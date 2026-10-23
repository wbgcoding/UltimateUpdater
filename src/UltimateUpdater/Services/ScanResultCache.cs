using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services;

/// <summary>
/// Persists the last finished scan (<see cref="ScanOrchestrator"/>) to disk so the table can show
/// something the instant the window opens instead of starting empty for however long the first real
/// scan of the session takes. Read once at startup, before the first scan runs; a cached row is replaced the moment its own source
/// reports a live result and is otherwise left dimmed, still installable (install re-fetches the item
/// by id at install time regardless, per <see cref="UpdateItem"/>'s own summary, so serving a cached
/// row is safe). Same atomic-write/fail-open shape as <see cref="EnrichmentCache"/>: a corrupt,
/// too-old or wrong-version file is simply discarded, never thrown from <see cref="TryLoad"/>.
/// </summary>
public sealed class ScanResultCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The running app's own version, exactly as written into a freshly saved cache; a cache
    /// written by any other version is discarded unread rather than risk replaying a shape the current
    /// model classes no longer agree with.</summary>
    public static string CurrentAppVersion { get; } = SelfUpdate.SelfUpdateService.CurrentVersion;

    /// <summary>The real, non-portable-mode file this cache reads and writes in a normal run. Internal so a
    /// test that needs to reach the cache's own file (rather than an injected test path) can name it
    /// without duplicating the file name as a second literal that could drift from this one.</summary>
    internal static string DefaultPath { get; } = Path.Combine(AppPaths.DataDirectory, "last-scan.json");

    /// <summary>Test-only override of the path <see cref="DefaultPath"/> would otherwise resolve to, same
    /// style as <see cref="Cleanup.CleanupService.RecycleBinDriveRootsOverride"/>. Null (the only value
    /// ever set in production) means the real machine's own <see cref="DefaultPath"/>. <see
    /// cref="UltimateUpdater.ViewModels.MainViewModel"/> owns exactly one <see cref="ScanResultCache"/>, built through the
    /// parameterless constructor with no way for a test to inject a different one, and finishing a scan
    /// SAVES it: without this override, any MainViewModel test that completes a scan writes the real
    /// user's own %APPDATA%\UltimateUpdater\last-scan.json and a later test can read that pollution back.
    /// Pointed at a fresh, per-test-name temp file by the test project's WpfHarness on every call into it,
    /// never assigned directly by an individual test.</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>The path a parameterless <see cref="ScanResultCache"/> actually resolves to right now:
    /// <see cref="PathOverride"/> during tests, <see cref="DefaultPath"/> otherwise. Internal so a test
    /// that needs the exact file its own unparameterized instances read and write does not have to
    /// duplicate this fallback logic.</summary>
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

    /// <summary>A real scan cache is a few hundred KB; anything past this is damaged or foreign and is not loaded.</summary>
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
    /// Serialised through a temporary file and a single move, same as <see cref="EnrichmentCache.Save"/>:
    /// a write is never observed half-finished. Every failure is swallowed; this cache is an
    /// optimisation, never a correctness dependency.
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

            // A literal null where a collection (or an item's identity) belongs is a damaged file, and the
            // mapping below would throw out of startup. Nothing is deleted here: the next saved scan
            // replaces the file, and the empty result is the same "nothing usable" every other path gives.
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
