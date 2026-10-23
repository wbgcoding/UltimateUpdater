using System.IO;
using System.Text;
using System.Text.Json;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>What one area gave up during one run.</summary>
public sealed class CleanupHistoryArea
{
    public required string Id { get; set; }

    /// <summary>The resource key of the area's name, so the record follows the UI language.</summary>
    public required string TitleKey { get; set; }

    /// <summary>Formatting arguments for <see cref="TitleKey"/>, the same shape as
    /// <see cref="CleanupTarget.TitleArguments"/> (a per-drive defragmentation row's own drive display
    /// name). Empty for every ordinary, statically-titled target. Kept as data rather than baking the
    /// resolved text in, so the record still follows the UI language like every other area name here; a
    /// history file written before this field existed deserializes it as an empty list, same as before.</summary>
    public List<string> TitleArguments { get; set; } = [];

    public long FreedBytes { get; set; }
    public long SkippedBytes { get; set; }

    /// <summary>The refused share of <see cref="SkippedBytes"/>; zero in entries written before it existed.</summary>
    public long ProtectedBytes { get; set; }
}

/// <summary>One cleanup run.</summary>
public sealed class CleanupHistoryEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public long FreedBytes { get; set; }
    public int SkippedFiles { get; set; }
    public long SkippedBytes { get; set; }

    /// <summary>True when the user stopped the run before it finished.</summary>
    public bool Stopped { get; set; }

    public List<CleanupHistoryArea> Areas { get; set; } = [];
}

/// <summary>
/// What has been cleaned, and when. A cleanup is irreversible, so a record of what it removed is not a
/// nicety, it is the only way to answer "what happened to that folder" afterwards.
/// A whole file rather than the append-only JSONL the install history uses: this one is READ every time the
/// dialog opens and is bounded to the most recent runs, so rewriting it costs nothing and there is no
/// rotation to get wrong. Every failure is swallowed, losing the record of a cleanup must never break the
/// cleanup itself.
/// </summary>
public sealed class CleanupHistoryService
{
    /// <summary>Enough to cover months of ordinary use without the file ever needing attention.</summary>
    private const int MaxEntries = 50;

    /// <summary>Upper bound for the history file read back from disk.</summary>
    internal const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly LoggingService _logging;

    public CleanupHistoryService(LoggingService logging, string? path = null)
    {
        _logging = logging;
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "cleanup-history.json");
    }

    /// <summary>Newest first, the order the dialog shows them in.</summary>
    public IReadOnlyList<CleanupHistoryEntry> Load()
    {
        lock (_lock)
        {
            try
            {
                return ReadEntries();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logging.Warn($"The cleanup history could not be read: {ex.Message}");
                return [];
            }
        }
    }

    public void Append(CleanupHistoryEntry entry)
    {
        lock (_lock)
        {
            try
            {
                var entries = ReadForRewrite();
                entries.Insert(0, entry);

                if (entries.Count > MaxEntries)
                    entries = entries.Take(MaxEntries).ToList();

                SaveAtomic(entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logging.Warn($"The cleanup history could not be written: {ex.Message}");
            }
        }
    }

    private List<CleanupHistoryEntry> ReadEntries()
    {
        if (!File.Exists(_path))
            return [];

        // Fifty entries are a few tens of kilobytes; anything past the cap is not a history this app wrote,
        // and reading it whole would only spend memory. Oversize takes the same path as a parse failure.
        var text = AtomicFile.TryReadAllText(_path, MaxFileBytes);
        if (text is null)
        {
            if (!File.Exists(_path))
                return [];

            throw new JsonException($"The file is larger than {MaxFileBytes} bytes.");
        }

        var entries = JsonSerializer.Deserialize<List<CleanupHistoryEntry>>(text, JsonOptions);

        return entries?.OrderByDescending(e => e.Timestamp).ToList() ?? [];
    }

    /// <summary>
    /// The current entries for a rewrite of the file. A file that cannot be READ throws, so the rewrite is
    /// skipped instead of replacing the whole history with one entry. A file that reads but does not parse
    /// is moved aside first, so its runs stay recoverable by hand; if even that fails, this throws too.
    /// </summary>
    private List<CleanupHistoryEntry> ReadForRewrite()
    {
        try
        {
            return ReadEntries();
        }
        catch (JsonException ex)
        {
            var directory = Path.GetDirectoryName(_path) ?? string.Empty;
            var aside = Path.Combine(
                directory, $"{Path.GetFileNameWithoutExtension(_path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(_path, aside);
            _logging.Warn($"The cleanup history could not be parsed ({ex.Message}); kept it as '{aside}' and started a new one.");
            return [];
        }
    }

    /// <summary>Removes one run by its timestamp, unique enough in practice (set from
    /// <see cref="DateTimeOffset.Now"/> at the moment a real run finishes) that two entries colliding is
    /// not a real risk. Silently does nothing if no entry matches, so a double-click cannot throw.</summary>
    public void Remove(DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            try
            {
                var entries = ReadForRewrite();
                entries.RemoveAll(e => e.Timestamp == timestamp);
                SaveAtomic(entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logging.Warn($"The cleanup history entry could not be removed: {ex.Message}");
            }
        }
    }

    /// <summary>Same temp-file-then-move pattern as SettingsService/HistoryService: a crash or an AV lock
    /// mid-write must never truncate the only record of what a cleanup removed.</summary>
    private void SaveAtomic(List<CleanupHistoryEntry> entries)
    {
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(entries, JsonOptions));
    }

    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logging.Warn($"The cleanup history could not be cleared: {ex.Message}");
            }
        }
    }
}
