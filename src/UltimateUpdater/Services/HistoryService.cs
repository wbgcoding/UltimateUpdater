using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UltimateUpdater.Services;

public sealed class HistoryEntry
{
    /// <summary>
    /// The one place an install or uninstall outcome becomes a history entry, so the queue, the blocked
    /// row paths and the interactive retry all record the same fields the same way.
    /// </summary>
    public static HistoryEntry ForItem(
        Models.UpdateItem item, Models.InstallAction action, bool success, string? error,
        bool isManualHandoff = false, bool isNotApplicable = false, string? detail = null,
        bool isCancelled = false, bool versionUnchanged = false) => new()
    {
        Timestamp = DateTimeOffset.Now,
        SourceId = item.SourceId,
        ItemId = item.Id,
        Name = item.Name,
        FromVersion = item.InstalledVersion,
        // An uninstall is not a move to a new version; only an install has a target.
        ToVersion = action == Models.InstallAction.Install ? item.AvailableVersion : null,
        SizeBytes = item.DownloadSize,
        Success = success,
        Error = error,
        Detail = detail,
        Action = action == Models.InstallAction.Uninstall ? "uninstall" : "install",
        IsManualHandoff = isManualHandoff,
        IsNotApplicable = isNotApplicable,
        IsCancelled = isCancelled,
        VersionUnchanged = versionUnchanged,
        // Read off the item that was actually run, never round-tripped through the elevated pipe.
        IsRollback = item.IsRollback,
    };

    public DateTimeOffset Timestamp { get; set; }
    public required string SourceId { get; set; }
    public required string ItemId { get; set; }
    public required string Name { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>The download size <see cref="Models.UpdateItem.DownloadSize"/> reported at install time,
    /// bytes. Null for every entry written before this field existed and for a source that never reports
    /// one (pre-release rule: no migration, an older entry simply has no value, the history window shows
    /// a hyphen).</summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// The failing tool's own captured output, same text the main table's error-detail button already
    /// shows for a live row; redacted and length-capped by <see cref="HistoryService.Append"/> before it
    /// ever reaches disk, never set directly. Null for a success, for a terminal non-failure, and for
    /// every entry written before this field existed; the history window falls back to a log excerpt and
    /// then to <see cref="Error"/> when it is missing.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>"install" (default, covers updates) or "uninstall". Absent in older files reads as install.</summary>
    public string Action { get; set; } = "install";

    /// <summary>
    /// The source could only open a vendor's own confirmation dialog and cannot see the real outcome
    /// (e.g. Steam's uninstall). Neither success nor failure; <see cref="Success"/> is meaningless when
    /// this is true. Absent in older files reads as false, which is the correct reading for every entry
    /// written before this existed.
    /// </summary>
    public bool IsManualHandoff { get; set; }

    /// <summary>
    /// The package manager offers no version that fits this installation (winget's 0x8A15002B). Terminal,
    /// but nothing went wrong and nothing changed; read like <see cref="IsManualHandoff"/>, not like a
    /// failure. Absent in older files reads as false.
    /// </summary>
    public bool IsNotApplicable { get; set; }

    /// <summary>
    /// The user stopped just this one row with its own stop button, the rest of the queue kept
    /// running. Terminal, but not a failure, read like <see cref="IsManualHandoff"/>. Absent in older
    /// files reads as false.
    /// </summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// The source's post-install re-check found the same version that was installed before this
    /// run, on an otherwise successful entry. Not a failure, read like a footnote on <see cref="Success"/>,
    /// not a replacement for it. Absent in older files reads as false.
    /// </summary>
    public bool VersionUnchanged { get; set; }

    /// <summary>
    /// This entry is a deliberate downgrade, not an ordinary update - <see cref="ToVersion"/> is
    /// OLDER than <see cref="FromVersion"/> on purpose. Absent in older files reads as false, the correct
    /// reading for every entry written before this existed.
    /// </summary>
    public bool IsRollback { get; set; }
}

/// <summary>
/// Append-only install history as JSONL. EVERY install attempt lands here,
/// successful or not. Rotates at 5 MB.
/// </summary>
public sealed class HistoryService
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly LoggingService _logging;

    /// <summary>The real file an explicit-path-less <see cref="HistoryService"/> reads and writes in a
    /// normal run. Internal for the same reason as <see cref="ScanResultCache.DefaultPath"/>: a test that
    /// needs to name this file exactly should not duplicate it as a second literal.</summary>
    internal static string DefaultPath { get; } = Path.Combine(AppPaths.DataDirectory, "history.jsonl");

    /// <summary>Test-only override of the path an explicit-path-less <see cref="HistoryService"/> falls
    /// back to, same shape as <see cref="ScanResultCache.PathOverride"/>. Null (the only value ever set in
    /// production) means <see cref="DefaultPath"/>. Every <c>new HistoryService(logging)</c> in
    /// MainViewModelTests.cs hits this same fallback and, via the MainViewModel it feeds, gets appended to
    /// by a real install/uninstall: without this override those tests write the real user's own
    /// %APPDATA%\UltimateUpdater\history.jsonl. Pointed at a fresh, per-test-name temp file by the test
    /// project's WpfHarness on every call into it, never assigned directly by an individual test.</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>The path an explicit-path-less <see cref="HistoryService"/> actually resolves to right
    /// now: <see cref="PathOverride"/> during tests, <see cref="DefaultPath"/> otherwise. Internal so a
    /// test that pre-clears "the" history file before using one does not clear the real file while every
    /// unparameterized instance in the same run actually reads and writes the redirected one.</summary>
    internal static string EffectivePath => PathOverride ?? DefaultPath;

    public HistoryService(LoggingService logging, string? path = null)
    {
        _logging = logging;
        _path = path ?? EffectivePath;
    }

    public void Append(HistoryEntry entry)
    {
        // Detail already went through this, but Error itself (InstallQueue.cs, RowActions.cs,
        // MainViewModel.Install.cs all set it straight from a tool's own output) never did, so it reached
        // disk unredacted and uncapped, the profile-path redaction the log itself always gets, missing on
        // the one field a shared history file also carries.
        entry.Detail = CapDetail(entry.Detail);
        entry.Error = CapDetail(entry.Error);

        lock (_lock)
        {
            try
            {
                try
                {
                    RotateIfNeeded();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A rotation that cannot happen right now must not cost the entry itself; it is tried
                    // again on the next append.
                    _logging.Warn($"History rotation failed: {ex.Message}");
                }

                File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, Utf8NoBom);

                // The new entry is the newest of all, so it IS somebody's newest successful install when it
                // qualifies. Patched in place instead of dropped: dropping it made the next reader re-parse the
                // whole file, and that reader is the scan's drain on the UI thread. An index nobody has built
                // yet is left alone; its first build reads this entry from the file anyway. A rotation moves the
                // older half to the .1 file without deleting rows the index already holds, which only means the
                // index can briefly know a package the files no longer do, until the next start.
                if (_rollbackIndex is { } index && QualifiesForRollback(entry))
                    index[RollbackKey(entry)] = entry;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // one unwritable history file must not abort the install queue mid-batch
                _logging.Error("Failed to append history entry", ex);
            }
        }
    }

    /// <summary>
    /// Every package's own newest successful "install" entry, keyed <c>"{SourceId}:{ItemId}"</c>, built from
    /// the WHOLE history (both files, no row cap) in one pass and then kept current by <see cref="Append"/> and
    /// <see cref="CleanupOldEntries"/> without re-reading anything. Rollback eligibility only ever needs the
    /// latest entry per package, so a manual, cancelled, not-applicable or handed-off entry, or one missing
    /// the from-version a rollback would target, is never a candidate.
    /// This call BUILDS the index when it does not exist yet, which parses the file: never call it on the UI
    /// thread. Use <see cref="EnsureRollbackIndexAsync"/> to build it elsewhere and <see cref="RollbackIndex"/>
    /// to read what is there.
    /// </summary>
    public IReadOnlyDictionary<string, HistoryEntry> LastSuccessfulInstallByItem()
    {
        if (_rollbackIndex is { } built)
            return built;

        lock (_lock)
        {
            if (_rollbackIndex is { } again)
                return again;

            var index = BuildRollbackIndex(out var complete);
            if (complete)
                _rollbackIndex = index;
            return index;
        }
    }

    /// <summary>Drops the rollback index, for "delete my data": it would otherwise keep offering rollbacks for
    /// history that no longer exists until the app restarts.</summary>
    public void InvalidateRollbackIndex()
    {
        lock (_lock)
            _rollbackIndex = null;
    }

    /// <summary>The index as it stands now, or null while nothing has built it yet. Never parses.</summary>
    public IReadOnlyDictionary<string, HistoryEntry>? RollbackIndex => _rollbackIndex;

    /// <summary>Builds the index on a pool thread (a no-op once it exists), so the UI thread never pays for
    /// the parse.</summary>
    public Task<IReadOnlyDictionary<string, HistoryEntry>> EnsureRollbackIndexAsync() =>
        Task.Run(LastSuccessfulInstallByItem);

    /// <summary>Test seam: how many times the files were read to build the index.</summary>
    internal int RollbackIndexBuilds => Volatile.Read(ref _rollbackIndexBuilds);

    /// <summary>Test seam: runs on the thread that builds the index.</summary>
    internal Action? RollbackIndexBuildObserver { get; set; }

    private int _rollbackIndexBuilds;

    /// <summary>Written only under <see cref="_lock"/>; the dictionary itself is concurrent because the drain
    /// reads it on the UI thread while an install thread appends.</summary>
    private volatile ConcurrentDictionary<string, HistoryEntry>? _rollbackIndex;

    private ConcurrentDictionary<string, HistoryEntry> BuildRollbackIndex(out bool indexIsComplete)
    {
        Interlocked.Increment(ref _rollbackIndexBuilds);
        RollbackIndexBuildObserver?.Invoke();

        var index = new ConcurrentDictionary<string, HistoryEntry>(StringComparer.Ordinal);

        // Oldest file first and each file in order, so a later qualifying entry for the same key replaces an
        // earlier one: the newest wins, exactly as the newest-first scan it replaces had it.
        void Visit(HistoryEntry entry)
        {
            if (QualifiesForRollback(entry))
                index[RollbackKey(entry)] = entry;
        }

        var failed = !ReadEach(_path + ".1", Visit);
        failed |= !ReadEach(_path, Visit);

        // An index built from a failed read would miss rows that are really there, and a missing row hides
        // a rollback. Hand it to this caller but leave it uncached, so the next ensure reads again.
        indexIsComplete = !failed;
        return index;
    }

    private static string RollbackKey(HistoryEntry entry) => $"{entry.SourceId}:{entry.ItemId}";

    private static bool QualifiesForRollback(HistoryEntry entry) =>
        entry.Action == "install" && entry.Success && !entry.IsManualHandoff
        && !entry.IsNotApplicable && !entry.IsCancelled && !string.IsNullOrEmpty(entry.FromVersion);

    /// <summary>The history file rotates at 5 MB, so one unbounded detail could push real history out;
    /// same cap the main table's own error output is held to. Redacted the same way the log itself is.</summary>
    private const int MaxDetailChars = 4000;

    /// <summary>
    /// Keeps the head and the tail when the text is too long, with one marker line between them: the
    /// first lines usually carry the command context, the last lines carry the actual failure.
    /// </summary>
    internal static string? CapDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return detail;

        var redacted = LoggingService.Redact(detail);
        if (redacted.Length <= MaxDetailChars)
            return redacted;

        const string marker = "\n[... truncated ...]\n";
        var budget = MaxDetailChars - marker.Length;
        var headLength = budget * 2 / 3;
        var tailLength = budget - headLength;
        return redacted[..headLength] + marker + redacted[^tailLength..];
    }

    /// <summary>
    /// Newest first. Malformed lines are skipped rather than failing the whole read.
    /// The rotated file is read too, oldest part first. Without it the history window fell from
    /// hundreds of entries to a single one the moment the active file crossed the rotation size;
    /// the entries were never lost, they were simply not being looked at.
    /// </summary>
    public IReadOnlyList<HistoryEntry> Read(int maxEntries = 500)
    {
        lock (_lock)
        {
            var entries = new List<HistoryEntry>();
            ReadInto(_path + ".1", entries);
            ReadInto(_path, entries);

            entries.Reverse();
            return entries.Count > maxEntries ? entries[..maxEntries] : entries;
        }
    }

    /// <summary>How many earlier installs of this package ended "succeeded, version unchanged" for exactly
    /// this from/to pair. Reads the whole history, so call it off the UI thread.</summary>
    public int CountUnchangedInstalls(string sourceId, string itemId, string? fromVersion, string? toVersion) =>
        CountUnchangedInstalls(Read(int.MaxValue), sourceId, itemId, fromVersion, toVersion);

    internal static int CountUnchangedInstalls(
        IEnumerable<HistoryEntry> entries, string sourceId, string itemId, string? fromVersion, string? toVersion) =>
        entries.Count(e =>
            e.Action == "install" && e.Success && e.VersionUnchanged
            && string.Equals(e.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ItemId, itemId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.FromVersion, fromVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ToVersion, toVersion, StringComparison.OrdinalIgnoreCase));

    /// <summary>A real history line is a small JSON object; generous headroom over anything legitimate,
    /// tight enough that a corrupt file with no newlines at all cannot buffer arbitrarily.</summary>
    private const int MaxLineChars = 64 * 1024;

    private bool ReadInto(string path, List<HistoryEntry> entries) => ReadEach(path, entries.Add);

    /// <summary>False when the file exists but could not be read to the end; a missing file is a complete,
    /// empty read.</summary>
    private bool ReadEach(string path, Action<HistoryEntry> visit)
    {
        if (!File.Exists(path))
            return true;

        try
        {
            foreach (var line in ReadLinesBounded(path, Encoding.UTF8, MaxLineChars))
            {
                // null marks a line that ran past the length cap; skipped without ever holding the whole
                // thing in memory, same "corrupt line, keep going" outcome as a JSON parse failure below.
                if (line is null || string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                    if (entry is not null)
                        visit(entry);
                }
                catch (JsonException)
                {
                    // truncated/corrupt line; skip it, keep the rest of the history readable
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Error($"Failed to read history from '{Path.GetFileName(path)}'", ex);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Line-by-line like <see cref="File.ReadLines(string, Encoding)"/>, but a line over
    /// <paramref name="maxLineChars"/> comes back as <c>null</c> instead of ever being fully materialised;
    /// scanning still continues character by character to find the next newline, so the file stays in sync
    /// and every following line reads normally.
    /// </summary>
    internal static IEnumerable<string?> ReadLinesBounded(string path, Encoding encoding, int maxLineChars)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, encoding);

        var buffer = new StringBuilder();
        var overflowed = false;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            if (ch == '\n')
            {
                yield return overflowed ? null : buffer.ToString().TrimEnd('\r');
                buffer.Clear();
                overflowed = false;
                continue;
            }

            if (overflowed)
                continue;

            buffer.Append((char)ch);
            if (buffer.Length > maxLineChars)
                overflowed = true;
        }

        if (buffer.Length > 0 || overflowed)
            yield return overflowed ? null : buffer.ToString().TrimEnd('\r');
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
            return;

        // One replacing move: deleting the archive first lost all of it whenever the move then failed (a
        // scanner or an editor holding history.jsonl).
        File.Move(_path, _path + ".1", overwrite: true);
    }

    /// <summary>
    /// Discards rows older than the retention window. Meant to run once at start-up
    /// (App.xaml.cs, next to LoggingService.CleanupOldLogs); same contract as the log's own housekeeping,
    /// a shorter value takes effect on the next launch, not immediately. Reads both the active and the
    /// rotated file and consolidates the survivors back into one, since keeping two files around after
    /// pruning serves no purpose; the 5 MB rotation itself is untouched and keeps working as a second,
    /// independent cap.
    /// </summary>
    public void CleanupOldEntries(int retentionDays)
    {
        // A non-positive value puts the cutoff in the future and discards the WHOLE history, including the
        // row written seconds ago. settings.json is hand-editable, so this is not hypothetical; same guard
        // and same reason as LoggingService.CleanupOldLogs.
        if (retentionDays < 1)
            return;

        lock (_lock)
        {
            try
            {
                // Capped: a hand-edited huge value would make AddDays throw and the cleanup never run.
                var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Min(retentionDays, 36500));
                var entries = new List<HistoryEntry>();
                var complete = ReadInto(_path + ".1", entries);
                complete &= ReadInto(_path, entries);

                // Writing the survivors back after a failed read would delete every row the read could not
                // see: the active file is replaced and the rotated one removed.
                if (!complete)
                    return;

                // Rows present in both files (a previous prune whose delete of the rotated file failed) are
                // written back once, so the duplicate goes away instead of surviving every later prune.
                var kept = new List<HistoryEntry>();
                var survivors = new System.Text.StringBuilder();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries.Where(e => e.Timestamp >= cutoff))
                {
                    var line = JsonSerializer.Serialize(entry, JsonOptions);
                    if (!seen.Add(line))
                        continue;

                    kept.Add(entry);
                    survivors.AppendLine(line);
                }

                if (kept.Count == entries.Count)
                    return;

                // Survivors go to a temp file first and replace the active one in a single move, the same
                // way SettingsService and EnrichmentCache save. Deleting the rotated file and THEN
                // overwriting the active one in place left a window in which a crash or an AV lock lost
                // both at once. The rotated file goes last on purpose: interrupted after the move, its rows
                // are read twice rather than lost, until the next prune folds them back into one.
                AtomicFile.WriteAllText(_path, survivors.ToString());
                File.Delete(_path + ".1");

                // Some rows just left the file for good. The survivors are already in memory, in file order, so
                // an index that exists is rebuilt from them without another read; one that does not exist yet
                // stays unbuilt. This runs on the caller's thread, which at start-up is a background task.
                if (_rollbackIndex is not null)
                {
                    var index = new ConcurrentDictionary<string, HistoryEntry>(StringComparer.Ordinal);
                    foreach (var entry in kept.Where(QualifiesForRollback))
                        index[RollbackKey(entry)] = entry;
                    _rollbackIndex = index;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logging.Warn($"The install history could not be pruned: {ex.Message}");
            }
        }
    }
}
