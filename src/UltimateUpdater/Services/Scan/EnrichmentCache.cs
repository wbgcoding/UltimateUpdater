using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Scan;

/// <summary>
/// Remembers background-enrichment results (release date, download size) keyed by source, item and version,
/// so a rescan does not hit PyPI/npm again for a package whose available version has not moved. Entries
/// expire after a week. Persisted as one small JSON file in the data directory; every failure is
/// swallowed: the cache is an optimisation, never a correctness dependency.
/// </summary>
public sealed class EnrichmentCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>
    /// Hard cap with oldest-first eviction on write. Expiry is passive (a key is only dropped when looked up
    /// again), so without a cap a long-running instance could grow the dictionary and its file without bound.
    /// </summary>
    private const int MaxEntries = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Entry
    {
        public DateTimeOffset? Date { get; set; }
        public long? Size { get; set; }
        public string? Url { get; set; }

        /// <summary>The newest version, for the sources whose own listing cannot report one.</summary>
        public string? Version { get; set; }

        /// <summary>Release notes text or link, fetched together with the date and size, so it shares their key
        /// and expiry.</summary>
        public string? ChangelogBody { get; set; }
        public string? ChangelogUrl { get; set; }

        /// <summary>The vendor or author name a registry reported, where it did; same fetch, key and expiry as the
        /// rest of the entry.</summary>
        public string? Publisher { get; set; }

        public long FetchedAtUnix { get; set; }
    }

    private readonly string _path;
    private readonly LoggingService _logging;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Set by every change to the entries, cleared by a save that wrote them. A scan whose lookups
    /// were all hits changes nothing, and serialising up to <see cref="MaxEntries"/> entries per source for
    /// that is pure waste.</summary>
    private volatile bool _dirty;

    /// <summary>A real enrichment cache is well under a few MB; anything past this is damaged or foreign.</summary>
    internal const int MaxFileBytes = 20 * 1024 * 1024;

    public EnrichmentCache(LoggingService logging)
        : this(logging, Path.Combine(AppPaths.DataDirectory, "enrichment-cache.json"))
    {
    }

    /// <summary>Testing seam: an explicit file path instead of the AppPaths-derived default. Nothing is read
    /// here; see <see cref="LoadAsync"/>.</summary>
    internal EnrichmentCache(LoggingService logging, string path, Action? beforeRead = null)
    {
        _logging = logging;
        _path = path;
        _beforeReadForTest = beforeRead;
    }

    private readonly object _loadLock = new();
    private Task? _loadTask;
    private volatile bool _loaded;

    /// <summary>Test seam: runs right before the file is read, on the thread that reads it.</summary>
    private readonly Action? _beforeReadForTest;

    /// <summary>
    /// Reads the cache file on a pool thread (up to <see cref="MaxFileBytes"/>) so it never blocks the
    /// window's start. Started once, later calls return the same task. Lookups and writes wait for it
    /// themselves, so nothing runs against a half-loaded cache.
    /// </summary>
    public Task LoadAsync()
    {
        lock (_loadLock)
            return _loadTask ??= Task.Run(Load);
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
            LoadAsync().GetAwaiter().GetResult();
    }

    public static string Key(string sourceId, string itemId, string? version) => $"{sourceId}:{itemId}:{version}";

    public bool TryGet(string key, out UpdateEnrichment enrichment)
    {
        EnsureLoaded();
        enrichment = default;
        if (!_entries.TryGetValue(key, out var entry))
            return false;

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(entry.FetchedAtUnix);
        if (age > Ttl)
        {
            _entries.TryRemove(key, out _);
            _dirty = true;
            return false;
        }

        enrichment = new UpdateEnrichment(
            entry.Date, entry.Size, entry.Url, entry.Version, entry.ChangelogBody, entry.ChangelogUrl,
            entry.Publisher);
        return true;
    }

    public void Set(string key, UpdateEnrichment enrichment)
    {
        EnsureLoaded();
        _entries[key] = new Entry
        {
            Date = enrichment.PublishDate,
            Size = enrichment.DownloadSize,
            Url = enrichment.ProjectUrl,
            Version = enrichment.AvailableVersion,
            ChangelogBody = enrichment.ChangelogBody,
            ChangelogUrl = enrichment.ChangelogUrl,
            Publisher = enrichment.Publisher,
            FetchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        _dirty = true;

        if (_entries.Count > MaxEntries)
            EvictOldest();
    }

    /// <summary>Drops the oldest-fetched entries down to 90 % of <see cref="MaxEntries"/>, so a full cache
    /// does not re-sort on every following Set. Best-effort: a key removed concurrently just means one fewer
    /// eviction this round, not a torn cache.</summary>
    private void EvictOldest()
    {
        var excess = _entries.Count - MaxEntries / 10 * 9;
        if (excess <= 0)
            return;

        foreach (var key in _entries.OrderBy(e => e.Value.FetchedAtUnix).Take(excess).Select(e => e.Key).ToList())
            _entries.TryRemove(key, out _);
    }

    /// <summary>Forgets every entry, for "delete my data". Loads first, so a lazy read that has not happened
    /// yet cannot bring back entries from a file the delete has not reached.</summary>
    public void Clear()
    {
        EnsureLoaded();
        lock (_saveLock)
        {
            _entries.Clear();
            _dirty = false;
        }
    }

    /// <summary>
    /// Serialised and written through a temporary file. Two saves can overlap (the scan's own and the one on
    /// exit); concurrent writes to one file would throw or leave a half-written file. One writer at a time,
    /// and the replacement is a single move, so the file on disk is always complete.
    /// </summary>
    public void Save()
    {
        // A save before the file was read would replace it with whatever few entries exist so far.
        EnsureLoaded();
        lock (_saveLock)
        {
            if (!_dirty)
                return;

            // Cleared before the snapshot is taken, so a Set that lands during the write marks the cache
            // dirty again instead of being lost; a failed write puts the mark back.
            _dirty = false;
            try
            {
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _dirty = true;
                _logging.Warn($"Enrichment cache could not be saved: {ex.Message}");
            }
        }
    }

    private readonly System.Threading.Lock _saveLock = new();

    private void Load()
    {
        try
        {
            _beforeReadForTest?.Invoke();
            if (!File.Exists(_path))
                return;

            // Over the cap reads as null: treated as no cache and overwritten by the next save.
            var text = AtomicFile.TryReadAllText(_path, MaxFileBytes);
            var loaded = text is null ? null : JsonSerializer.Deserialize<Dictionary<string, Entry>>(text, JsonOptions);
            if (loaded is null)
                return;

            var now = DateTimeOffset.UtcNow;
            foreach (var (key, entry) in loaded)
            {
                if (entry is not null && now - DateTimeOffset.FromUnixTimeSeconds(entry.FetchedAtUnix) <= Ttl)
                    _entries[key] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A corrupt or unreadable cache is ignored and overwritten on the next save.
            _logging.Warn($"Enrichment cache could not be loaded: {ex.Message}");
        }
        finally
        {
            _loaded = true;
        }
    }
}
