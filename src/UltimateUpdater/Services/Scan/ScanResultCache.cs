using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Scan;

/// <summary>
/// Persists the last finished scan (<see cref="ScanOrchestrator"/>) so the table shows something the
/// instant the window opens. Read once at startup, before the first scan; a cached row is replaced when
/// its own source reports a live result and is otherwise left dimmed, still installable (install
/// re-fetches the item by id regardless). Same atomic-write, fail-open shape as
/// <see cref="EnrichmentCache"/>: a corrupt, too-old or wrong-version file is discarded, never thrown
/// from <see cref="TryLoad"/>.
/// </summary>
public sealed class ScanResultCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The running app's own version, as written into a freshly saved cache; a cache from any other
    /// version is discarded unread rather than replayed against changed model classes.</summary>
    public static string CurrentAppVersion { get; } = SelfUpdate.SelfUpdateService.CurrentVersion;

    /// <summary>The real file this cache reads and writes in a normal run; internal so tests can name it
    /// without a second literal.</summary>
    internal static string DefaultPath { get; } = Path.Combine(AppPaths.DataDirectory, "last-scan.json");

    /// <summary>Test-only override of <see cref="DefaultPath"/>; null in production. The main view model
    /// builds its own cache through the parameterless constructor and saves after every scan, so without this
    /// any test that completes a scan would write the real user's last-scan file.</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>The path a parameterless <see cref="ScanResultCache"/> resolves to right now:
    /// <see cref="PathOverride"/> during tests, <see cref="DefaultPath"/> otherwise.</summary>
    internal static string EffectivePath => PathOverride ?? DefaultPath;

    private sealed class CachedChild
    {
        public string Id { get; set; } = "";
        public string SourceId { get; set; } = "";
        public UpdateCategory Category { get; set; }
        public string Name { get; set; } = "";
        public string? InstalledVersion { get; set; }
        public string? AvailableVersion { get; set; }
        public bool IsCritical { get; set; }
        public SourceTier Tier { get; set; }
        public string? OpenTarget { get; set; }
        public List<CachedChild> Children { get; set; } = [];
        public Dictionary<string, string> Meta { get; set; } = [];
        public long? DownloadSize { get; set; }
        public DateTimeOffset? PublishDate { get; set; }
    }

    private sealed class CachedScan
    {
        public string AppVersion { get; set; } = "";
        public long SavedAtUnix { get; set; }
        public List<CachedChild> Items { get; set; } = [];
    }

    /// <summary>A real scan cache is a few hundred KB; anything past this is damaged or foreign and is not
    /// loaded.</summary>
    internal const int MaxFileBytes = 20 * 1024 * 1024;

    private readonly string _path;
    private readonly LoggingService _logging;

    public ScanResultCache(LoggingService logging)
        : this(logging, EffectivePath)
    {
    }

    /// <summary>Testing seam: inject an explicit file path instead of the AppPaths-derived default.</summary>
    public ScanResultCache(LoggingService logging, string path)
    {
        _logging = logging;
        _path = path;
    }

    public readonly record struct LoadResult(IReadOnlyList<UpdateItem> Items, DateTimeOffset SavedAt);

    /// <summary>
    /// Written through a temporary file and a single move, like <see cref="EnrichmentCache.Save"/>. Every
    /// failure is swallowed: this cache is an optimisation, never a correctness dependency.
    /// </summary>
    public void Save(IReadOnlyList<UpdateItem> items)
    {
        try
        {
            var cached = new CachedScan
            {
                AppVersion = CurrentAppVersion,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Items = items.Select(ToCached).ToList(),
            };
            var json = JsonSerializer.Serialize(cached, JsonOptions);
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logging.Warn($"Scan result cache could not be saved: {ex.Message}");
        }
    }

    /// <summary>
    /// Null when there is nothing usable: no file, a corrupt file, a file older than seven days, or one
    /// written by a different app version. The stale/mismatched cases delete the file so the next save
    /// starts clean instead of re-checking a file this build will only ever reject.
    /// </summary>
    public LoadResult? TryLoad()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            // Over the cap reads as null: a cache that big is not ours, so it is dropped like a corrupt one.
            var text = AtomicFile.TryReadAllText(_path, MaxFileBytes);
            var cached = text is null ? null : JsonSerializer.Deserialize<CachedScan>(text, JsonOptions);
            if (cached is null)
            {
                TryDelete();
                return null;
            }

            var savedAt = DateTimeOffset.FromUnixTimeSeconds(cached.SavedAtUnix);
            if (DateTimeOffset.UtcNow - savedAt > Ttl)
            {
                TryDelete();
                return null;
            }

            if (!string.Equals(cached.AppVersion, CurrentAppVersion, StringComparison.Ordinal))
            {
                TryDelete();
                return null;
            }

            // A literal null where a collection or an item's identity belongs is a damaged file, and the mapping
            // below would throw out of startup. Nothing is deleted: the next saved scan replaces the file.
            if (!IsStructurallyValid(cached.Items))
            {
                _logging.Warn("Scan result cache holds an empty required value and was ignored.");
                return null;
            }

            return new LoadResult(cached.Items.Select(ToItem).ToList(), savedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or FormatException or OverflowException)
        {
            _logging.Warn($"Scan result cache could not be loaded: {ex.Message}");
            TryDelete();
            return null;
        }
    }

    private static bool IsStructurallyValid(List<CachedChild>? items) =>
        items is not null
        && items.All(item => item is not null
            && item.Id is not null && item.SourceId is not null && item.Name is not null
            && item.Meta is not null
            && IsStructurallyValid(item.Children));

    private static CachedChild ToCached(UpdateItem item) => new()
    {
        Id = item.Id,
        SourceId = item.SourceId,
        Category = item.Category,
        Name = item.Name,
        InstalledVersion = item.InstalledVersion,
        AvailableVersion = item.AvailableVersion,
        IsCritical = item.IsCritical,
        Tier = item.Tier,
        OpenTarget = item.OpenTarget,
        Children = item.Children.Select(ToCached).ToList(),
        Meta = new Dictionary<string, string>(item.Meta),
        DownloadSize = item.DownloadSize,
        PublishDate = item.PublishDate,
    };

    private static UpdateItem ToItem(CachedChild cached) => new()
    {
        Id = cached.Id,
        SourceId = cached.SourceId,
        Category = cached.Category,
        Name = cached.Name,
        InstalledVersion = cached.InstalledVersion,
        AvailableVersion = cached.AvailableVersion,
        IsCritical = cached.IsCritical,
        Tier = cached.Tier,
        OpenTarget = cached.OpenTarget,
        Children = cached.Children.Select(ToItem).ToList(),
        Meta = cached.Meta,
        DownloadSize = cached.DownloadSize,
        PublishDate = cached.PublishDate,
    };

    private void TryDelete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort housekeeping only.
        }
    }
}
