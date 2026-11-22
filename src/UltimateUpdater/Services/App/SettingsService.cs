using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.App;

/// <summary>Loads/saves settings.json: atomic write, UTF-8 no BOM. Fails closed to defaults on a corrupt file rather
/// than crashing.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // A hand-edited file is the likeliest way settings.json breaks; a stray trailing comma or a comment
        // is not worth losing every setting over.
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>How many unreadable settings files are kept beside the live one.</summary>
    private const int MaxCorruptCopies = 3;

    private readonly object _lock = new();
    private readonly string _settingsPath;
    private readonly LoggingService _logging;

    public AppSettings Current { get; }

    public SettingsService(LoggingService logging)
        : this(logging, Path.Combine(AppPaths.DataDirectory, "settings.json"))
    {
    }

    /// <summary>Testing seam: inject an explicit settings file path instead of the AppPaths-derived default.</summary>
    public SettingsService(LoggingService logging, string settingsPath)
    {
        _logging = logging;
        _settingsPath = settingsPath;
        // Created stopped; SaveDeferred is what ever schedules it.
        _saveTimer = new System.Threading.Timer(
            _ => OnSaveTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Current = Load();
        ApplyRepairDefaultsReset();
    }

    /// <summary>The two system repair targets default to off. A tick saved under an earlier default would keep
    /// them on, so their saved choice is dropped exactly once (the catalogue default then applies). Runs at
    /// load, before any restore of the selection. Persists only when an entry was removed; otherwise the flag
    /// rides along with the next ordinary save.</summary>
    private void ApplyRepairDefaultsReset()
    {
        if (Current.CleanupRepairDefaultsApplied)
            return;

        var removed = false;
        foreach (var id in RepairTargetIds)
            removed |= Current.CleanupSelection.Remove(id);

        Current.CleanupRepairDefaultsApplied = true;
        if (removed)
            WriteAndLogFailure();
    }

    private static readonly string[] RepairTargetIds = ["system-repair-dism", "system-repair-sfc"];

    /// <summary>
    /// Puts every setting back to its factory default. <see cref="Current"/> is mutated in place rather than
    /// replaced, because view models, the scan orchestrator and the install queue hold a reference to this
    /// instance. Does not write; the caller saves.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            var defaults = new AppSettings();

            Current.SchemaVersion = defaults.SchemaVersion;
            Current.Scan = defaults.Scan;
            Current.Notify = defaults.Notify;
            Current.Install = defaults.Install;
            Current.Ui = defaults.Ui;
            Current.Autostart = defaults.Autostart;
            Current.SelfUpdate = defaults.SelfUpdate;
            Current.Security = defaults.Security;
            // Any section added to AppSettings has to be listed here and in Normalize, or a factory reset leaves
            // that section untouched.
            Current.RegistryCleanup = defaults.RegistryCleanup;
            Current.Logs = defaults.Logs;
            Current.CleanupRecycleInsteadOfDelete = defaults.CleanupRecycleInsteadOfDelete;
            Current.CleanupPrescanAtStartup = defaults.CleanupPrescanAtStartup;
            Current.CleanupRecycleBinMinAgeDays = defaults.CleanupRecycleBinMinAgeDays;
            Current.CleanupReminderDays = defaults.CleanupReminderDays;
            Current.CleanupReminderOff = defaults.CleanupReminderOff;
            Current.HistoryRetentionDays = defaults.HistoryRetentionDays;
            // A factory reset should let the hardware auto-tune run again too.
            Current.ParallelismChosenByUser = defaults.ParallelismChosenByUser;
            Current.Sources.Clear();
            Current.Excludes.Clear();
            Current.Snoozed.Clear();
            Current.PersistentFailures.Clear();
            Current.CleanupSelection.Clear();
            Current.CleanupPermanentlyExcludedPaths.Clear();
        }
    }

    private const int MaxMoveAttempts = 5;

    /// <summary>Throws IOException/UnauthorizedAccessException if all retries are exhausted (e.g. AV briefly locking
    /// the file); callers must handle it (fail closed, never let a save error crash the app).</summary>
    public void Save()
    {
        if (_saveSuppressed)
        {
            lock (_lock)
            {
                if (!_suppressedSaveLogged)
                {
                    _suppressedSaveLogged = true;
                    _logging.Warn("settings.json could not be read at startup; changes are not saved this session so the file on disk stays untouched.");
                }
            }

            return;
        }

        // Serialised before the lock below, never inside it: SerializeCurrentOnOwningThread can block this
        // thread on the UI dispatcher, and Reset() also takes _lock on the UI thread, so waiting on the
        // dispatcher while holding _lock could deadlock.
        var (json, sequence) = SerializeCurrentOnOwningThread();

        lock (_lock)
        {
            // Two saves can overlap (the timer's and a Flush); the later snapshot must win, so an older one reaching
            // this point second never overwrites a newer file.
            if (sequence < _lastWrittenSequence)
                return;

            BeforeWriteForTest?.Invoke();

            for (var attempt = 1; attempt <= MaxMoveAttempts; attempt++)
            {
                try
                {
                    AtomicFile.WriteAllText(_settingsPath, json);
                    _lastWrittenSequence = sequence;
                    Interlocked.Increment(ref _diskWrites);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == MaxMoveAttempts)
                        throw;
                    Thread.Sleep(50 * attempt); // transient lock (e.g. AV on-access scan); back off and retry
                }
            }
        }
    }

    /// <summary>
    /// Writes the settings as they are in memory now to <paramref name="path"/>, for a data folder copy that
    /// ran in the background while the user could still change something. Does nothing while saves are
    /// suppressed: the file on disk is the truth then. Throws like <see cref="Save"/> on a failed write.
    /// </summary>
    public void WriteCopyTo(string path)
    {
        if (_saveSuppressed)
            return;

        var (json, _) = SerializeCurrentOnOwningThread();
        AtomicFile.WriteAllText(path, json);
    }

    /// <summary>
    /// <see cref="Current"/>'s collections are mutated straight from the UI thread by every view model, with
    /// no lock. Serialising them from <see cref="OnSaveTimer"/>'s thread-pool thread would race a live
    /// mutation and throw InvalidOperationException ("Collection was modified"), so the read and serialise
    /// step runs on the dispatcher that owns those mutations. A caller already on that thread
    /// (<see cref="Flush"/> at app exit) or with no dispatcher (tests) runs inline; <see cref="OnSaveTimer"/>'s
    /// catch-all keeps a failure on that inline path from ending the process.
    /// </summary>
    private (string Json, long Sequence) SerializeCurrentOnOwningThread()
    {
        // The sequence number is taken with the snapshot, so it orders snapshots exactly.
        (string, long) Snapshot() => (JsonSerializer.Serialize(Current, JsonOptions), Interlocked.Increment(ref _serializeSequence));

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess() || dispatcher.HasShutdownStarted)
            return Snapshot();

        // An Invoke the dispatcher aborts (shutdown racing this timer) yields null; writing that would replace
        // settings.json with an empty file, so it fails and the old file stays.
        var snapshot = dispatcher.Invoke(Snapshot);
        return snapshot.Item1 is null
            ? throw new InvalidOperationException("Settings were not serialised: the UI dispatcher shut down.")
            : snapshot;
    }

    private long _serializeSequence;
    private long _lastWrittenSequence;

    /// <summary>Test seam, called inside the file lock right before a write: lets a test hold a write open.</summary>
    internal Action? BeforeWriteForTest { get; set; }

    /// <summary>How long a deferred save waits for the next change before it actually writes.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(250);

    private readonly object _pendingLock = new();
    private readonly System.Threading.Timer _saveTimer;
    private bool _savePending;

    /// <summary>Timer saves that cleared <see cref="_savePending"/> and have not finished writing yet.</summary>
    private int _timerWritesInFlight;

    private int _diskWrites;

    /// <summary>How many times the file was really written. Test seam: counting writes is the only way to
    /// prove a burst of deferred saves collapses into one.</summary>
    internal int DiskWrites => Volatile.Read(ref _diskWrites);

    /// <summary>
    /// The save every UI click goes through. <see cref="Save"/> serialises, writes a temp file and retries a
    /// locked move with a growing sleep, up to about three quarters of a second on the caller's thread, a
    /// visible freeze on the UI thread. This marks the settings dirty and writes them on a thread-pool thread
    /// once the changes stop coming, so a burst of toggles costs one write. Never throws: a failed write is
    /// logged. Anything that needs the file current right now calls <see cref="Flush"/>.
    /// </summary>
    public void SaveDeferred()
    {
        lock (_pendingLock)
        {
            _savePending = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Writes a pending deferred save out now, on the calling thread; does nothing when none is pending.
    /// Called wherever the on-disk file has to be current: application exit, and before an elevated job
    /// starts (the worker is a separate process and reads the disk). A timer save that is still writing
    /// counts as pending, since it cleared the flag before writing; Flush then writes the current state
    /// itself, queues behind the in-flight write's file lock, and the newer snapshot wins. It never waits on
    /// the timer thread, which may be waiting for the UI thread Flush usually runs on.
    /// </summary>
    public void Flush()
    {
        lock (_pendingLock)
        {
            if (!_savePending && _timerWritesInFlight == 0)
                return;

            _savePending = false;
            _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        WriteAndLogFailure();
    }

    private void OnSaveTimer()
    {
        try
        {
            lock (_pendingLock)
            {
                // Flush got there first; its write already carries these changes.
                if (!_savePending)
                    return;

                _savePending = false;
                _timerWritesInFlight++;
            }

            try
            {
                WriteAndLogFailure();
            }
            finally
            {
                lock (_pendingLock)
                    _timerWritesInFlight--;
            }
        }
        catch (Exception ex)
        {
            // Nothing may escape a System.Threading.Timer callback: it runs on a thread-pool thread with no caller,
            // and an unhandled exception there tears down the whole process. WriteAndLogFailure handles the failures
            // it expects; this is the backstop for the rest (a mutation racing the no-dispatcher fallback, or the
            // dispatcher shutting down mid-save).
            _logging.Error("Unhandled exception in the settings deferred-save timer", ex);
        }
    }

    private void WriteAndLogFailure()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException)
        {
            // InvalidOperationException also covers "Collection was modified" when a live mutation races the inline
            // serialize used without a dispatcher.
            _logging.Error("Failed to save settings.json", ex);
        }
    }

    /// <summary>
    /// A legitimate settings.json is a few KB. Checked before reading, so a huge file (planted or corrupted)
    /// cannot exhaust memory before the parser gets a chance to fail closed.
    /// </summary>
    private const long MaxSettingsFileBytes = 5L * 1024 * 1024;

    private AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return new AppSettings();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var length = new FileInfo(_settingsPath).Length;
                if (length > MaxSettingsFileBytes)
                {
                    _logging.Error($"settings.json is {length} bytes, over the {MaxSettingsFileBytes}-byte limit; falling back to defaults.");

                    // Moved, not copied: the file may be arbitrarily large, and the next save recreates the live one.
                    KeepUnreadableFile(move: true);
                    return new AppSettings();
                }

                using var stream = File.OpenRead(_settingsPath);
                var settings = Normalize(JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions));
                DropUnknownSourceKeys(settings);
                return settings;
            }
            // A lock held by a scanner, backup or sync tool is not a broken file: wait it out first.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < LoadAttempts)
            {
                _ = ex;
                Thread.Sleep(LoadRetryDelay);
            }
            // Still locked or unreadable after the retries: the good file on disk is not known to be broken, so
            // defaults are used for this session only and every save is switched off. Writing the defaults
            // back would overwrite the user's real settings.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logging.Error("settings.json could not be read; using defaults for this session and not saving", ex);
                _saveSuppressed = true;
                return new AppSettings();
            }
            // Widest sensible net: this method's contract is to fall back to defaults, and an exception escaping it
            // would take the app down at startup with no way to recover from the UI.
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                _logging.Error("settings.json unreadable/corrupt; falling back to defaults", ex);
                KeepUnreadableFile(move: false);
                return new AppSettings();
            }
        }
    }

    private const int LoadAttempts = 5;
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Set when settings.json existed but could not be read for an IO or access reason: every save
    /// of this session is then a no-op, so the file on disk is never overwritten with defaults.</summary>
    private volatile bool _saveSuppressed;
    private bool _suppressedSaveLogged;

    /// <summary>
    /// Falling back to defaults means the next save overwrites the file, so a broken file is set aside first
    /// and a hand-editor can recover their settings. At most <see cref="MaxCorruptCopies"/> such files are
    /// kept, the newest ones. Best effort; keeping a copy must never block startup.
    /// </summary>
    private void KeepUnreadableFile(bool move)
    {
        try
        {
            var kept = $"{_settingsPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            if (move)
                File.Move(_settingsPath, kept, overwrite: true);
            else
                File.Copy(_settingsPath, kept, overwrite: true);

            var directory = Path.GetDirectoryName(_settingsPath);
            if (string.IsNullOrEmpty(directory))
                return;

            var stale = Directory.EnumerateFiles(directory, Path.GetFileName(_settingsPath) + ".corrupt-*")
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .Skip(MaxCorruptCopies);
            foreach (var old in stale)
                FileDeletion.TryDelete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"Could not keep a copy of the unreadable settings.json: {ex.Message}");
        }
    }

    /// <summary>
    /// An explicit <c>null</c> in the file deserialises successfully into a null section; the property
    /// initialisers only run for members the JSON never mentions. Every section and collection is put back
    /// to its default here, so nothing downstream has to null-check.
    /// </summary>
    private static AppSettings Normalize(AppSettings? settings)
    {
        var defaults = new AppSettings();
        if (settings is null)
            return defaults;

        settings.Scan ??= defaults.Scan;
        settings.Sources ??= defaults.Sources;
        settings.Notify ??= defaults.Notify;
        settings.Install ??= defaults.Install;
        settings.Ui ??= defaults.Ui;
        settings.SelfUpdate ??= defaults.SelfUpdate;
        settings.Security ??= defaults.Security;
        settings.Excludes ??= defaults.Excludes;
        settings.Snoozed ??= defaults.Snoozed;
        settings.PersistentFailures ??= defaults.PersistentFailures;
        settings.Logs ??= defaults.Logs;
        settings.RegistryCleanup ??= defaults.RegistryCleanup;

        // A hand-edited or out-of-range value must never reach the elevated worker unchecked.
        settings.Install.MaxParallelInstalls = Math.Clamp(settings.Install.MaxParallelInstalls, 1, 5);
        // A null here would reach the cleanup dialog's constructor and every scan's ApplyPermanentExclusions.
        settings.CleanupSelection ??= defaults.CleanupSelection;
        settings.CleanupPermanentlyExcludedPaths ??= defaults.CleanupPermanentlyExcludedPaths;

        // The two strings are compared against known values everywhere; null is not one of them.
        settings.Ui.Theme ??= defaults.Ui.Theme;
        settings.Ui.Language ??= defaults.Ui.Language;

        // The table layouts are read in MainViewModel's constructor: a null here ("tables": null, "main": null or
        // "hiddenColumns": null) would fail every launch with an error no UI could repair.
        settings.Ui.Tables ??= defaults.Ui.Tables;
        foreach (var broken in settings.Ui.Tables.Where(e => e.Value is null).Select(e => e.Key).ToList())
            settings.Ui.Tables.Remove(broken);
        foreach (var table in settings.Ui.Tables.Values)
        {
            table.HiddenColumns ??= [];
            table.ShownColumns ??= [];
            table.PinnedColumns ??= [];
        }

        // The violet theme id "aipurple" is a legacy name; without this a stored old id matches nothing and the
        // user silently lands on the system theme.
        if (string.Equals(settings.Ui.Theme, "aipurple", StringComparison.OrdinalIgnoreCase))
            settings.Ui.Theme = "neon";

        // A null or blank folder would make every registry backup refuse as "outside the user profile".
        if (string.IsNullOrWhiteSpace(settings.RegistryCleanup.BackupFolder))
            settings.RegistryCleanup.BackupFolder = RegistryCleanupSettings.DefaultBackupFolder;

        // An earlier registry-backup default was the profile root itself. Only a value that matches that default
        // exactly is rewritten; a folder the user picked (even one inside the profile) is left alone.
        if (string.Equals(
                settings.RegistryCleanup.BackupFolder,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.OrdinalIgnoreCase))
        {
            settings.RegistryCleanup.BackupFolder = RegistryCleanupSettings.DefaultBackupFolder;
        }

        // A settings file saved under the legacy roaming folder name still points there. Same rule: only an exact
        // match with the old default is rewritten, never a folder the user chose.
        if (string.Equals(
                settings.RegistryCleanup.BackupFolder,
                RegistryCleanupSettings.LegacyDefaultBackupFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            settings.RegistryCleanup.BackupFolder = RegistryCleanupSettings.DefaultBackupFolder;
        }

        // A JSON array may hold nulls; every reader of this list treats an entry as a key.
        // IntervalMinutes is deliberately not clamped: 0 or less turns the interval scan off.
        settings.Excludes.RemoveAll(string.IsNullOrWhiteSpace);

        // Same reasoning: a blank entry is compared against a real path on every scan and can never match.
        settings.CleanupPermanentlyExcludedPaths.RemoveAll(string.IsNullOrWhiteSpace);

        // Swept here too, not only on lookup: an install closed past a snooze's due date must not resurrect it
        // when the app is opened days later.
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in settings.Snoozed.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            settings.Snoozed.Remove(expired);

        // A reference-typed dictionary value can be an explicit JSON null (unlike Excludes' strings or Snoozed's
        // structs); a corrupted entry must not throw the first time a scan reads it back.
        foreach (var broken in settings.PersistentFailures.Where(e => e.Value is null).Select(e => e.Key).ToList())
            settings.PersistentFailures.Remove(broken);

        // LastErrorText is capped and redacted when newly set, but a value already in settings.json from before
        // the cap, or from a larger cap, would otherwise survive uncapped and reload verbatim on every launch.
        // CapDetail is idempotent.
        foreach (var record in settings.PersistentFailures.Values)
            record.LastErrorText = HistoryService.CapDetail(record.LastErrorText);

        MigrateSchema(settings);
        return settings;
    }

    /// <summary>
    /// Version 2 shows the publisher column by default. A profile saved earlier had it hidden by default
    /// (silence in the file meant hidden), so its choice is written out explicitly: hidden unless the file
    /// recorded an opt-in. Only a fresh install (no file, already the current version) shows the column.
    /// </summary>
    private static void MigrateSchema(AppSettings settings)
    {
        if (settings.SchemaVersion >= 2)
            return;

        if (!settings.Ui.Tables.TryGetValue("main", out var main))
        {
            main = new TableLayoutState();
            settings.Ui.Tables["main"] = main;
        }

        if (!main.ShownColumns.Contains("publisher") && !main.HiddenColumns.Contains("publisher"))
            main.HiddenColumns.Add("publisher");

        main.ShownColumns.Remove("publisher");
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    /// <summary>
    /// Drops settings.json keys of a source no longer in <see cref="SourceRegistry"/>, which would otherwise
    /// stay for ever: <see cref="Normalize"/> only fills in missing sections and never prunes stale keys. Runs
    /// once per load, after Normalize, and only removes keys; the cleaned dictionary reaches disk with the
    /// next <see cref="Save"/> or <see cref="SaveDeferred"/>.
    /// </summary>
    private void DropUnknownSourceKeys(AppSettings settings)
    {
        var unknown = settings.Sources.Keys.Where(key => !SourceRegistry.KnownIds.Contains(key)).ToList();
        if (unknown.Count == 0)
            return;

        foreach (var key in unknown)
            settings.Sources.Remove(key);

        _logging.Info($"settings.json: dropped {unknown.Count} unknown source id(s) not in SourceRegistry.");
    }
}
