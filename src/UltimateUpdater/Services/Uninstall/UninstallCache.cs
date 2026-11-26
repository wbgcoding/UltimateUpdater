using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateUpdater.Services.Support;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>The last full listing as read back from <see cref="UninstallCache"/>: the entries and when they were read.</summary>
public sealed record UninstallCacheContent(IReadOnlyList<InstalledProgramEntry> Entries, DateTimeOffset SavedAt);

/// <summary>
/// The uninstaller's last full listing on disk (<c>uninstall-cache.json</c> in the local data folder), so the window
/// can show rows at once while the real read runs. Entries only: plans and risk ratings are recomputed from them, so
/// a changed rule never shows a stale rating. Written atomically by the unelevated app (never by the elevated worker),
/// size-capped on both sides, and a file that cannot be read back is simply no cache.
/// </summary>
public sealed class UninstallCache(string path, Func<DateTimeOffset>? clock = null)
{
    /// <summary>The file name inside the local data folder.</summary>
    public const string FileName = "uninstall-cache.json";

    /// <summary>The largest file written or read: a listing of a few thousand entries is a few MB, anything beyond is not ours.</summary>
    internal const long MaxBytes = 16 * 1024 * 1024;

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>The cache of this installation, in <see cref="AppPaths.LocalDirectory"/>.</summary>
    public static UninstallCache Default() => new(Path.Combine(AppPaths.LocalDirectory, FileName));

    private sealed class Document
    {
        public int Version { get; set; }

        public DateTimeOffset SavedAt { get; set; }

        public List<InstalledProgramEntry>? Entries { get; set; }
    }

    /// <summary>Writes the listing; false when it was too large or the file could not be replaced (logged by the caller, never fatal).</summary>
    public bool Save(IReadOnlyList<InstalledProgramEntry> entries)
    {
        try
        {
            var text = JsonSerializer.Serialize(
                new Document { Version = CurrentVersion, SavedAt = _clock(), Entries = [.. entries] }, Options);
            if (text.Length > MaxBytes / 2)   // UTF-16 chars; the bytes stay well under the cap a reader enforces
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The saved listing, or null when there is none, it is too large, from another format version or damaged in any way.</summary>
    public UninstallCacheContent? Load()
    {
        try
        {
            var text = AtomicFile.TryReadAllText(path, MaxBytes);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var document = JsonSerializer.Deserialize<Document>(text, Options);
            if (document is not { Version: CurrentVersion, Entries: { } entries })
                return null;

            // An entry without a key or a name cannot be matched or shown: the file is damaged, not partly usable.
            return entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Name))
                ? null
                : new UninstallCacheContent(entries, document.SavedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
