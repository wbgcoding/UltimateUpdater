using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>Loads/saves settings.json: atomic write, UTF-8 no BOM. Fails closed to defaults on a corrupt file rather than crashing.</summary>
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

    /// <summary>The two system repair targets default to off. A saved
    /// tick from an earlier default would keep them on for everyone who ever closed the cleanup window, so
    /// their saved choice is dropped exactly once (the catalogue default then applies). Runs at load, before
    /// any restore of the selection, so every reader of it sees the result. Persists only when an entry was
    /// actually removed; otherwise the flag rides along with the next ordinary save.</summary>
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
    /// Puts every setting back to its factory default. The <see cref="Current"/>
    /// object is mutated in place rather than replaced, because view models, the scan orchestrator and the
    /// install queue all hold a reference to this very instance. Does not write; the caller saves.
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
            // Any section added to AppSettings has to be listed here AND in Normalize, or a factory
            // reset silently leaves that section's values untouched.
            Current.RegistryCleanup = defaults.RegistryCleanup;
            Current.Logs = defaults.Logs;
            Current.CleanupRecycleInsteadOfDelete = defaults.CleanupRecycleInsteadOfDelete;
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

    /// <summary>Throws IOException/UnauthorizedAccessException if all retries are exhausted (e.g. AV briefly locking the file); callers must handle it (fail closed, never let a save error crash the app).</summary>
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

        // Serialised BEFORE the lock below, never inside it: SerializeCurrentOnOwningThread can block this
        // thread on the UI dispatcher, and Reset() also takes _lock on the UI thread itself, so waiting on
        // the dispatcher while holding _lock could deadlock the two against each other.
        var (json, sequence) = SerializeCurrentOnOwningThread();

        lock (_lock)
        {
            // Two saves can overlap (the timer's and a Flush); the snapshot taken LATER must win, so an older
            // one that reaches this point second never overwrites a newer file.
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
    /// <see cref="Current"/>'s collections (Sources, Excludes, Snoozed, ...) are mutated straight from the
    /// UI thread by every view model that touches settings, with no lock of their own. Serialising them
    /// from <see cref="OnSaveTimer"/>'s thread-pool thread would race a live mutation and throw
    /// InvalidOperationException ("Collection was modified"), so the read and serialise step runs on the
    /// dispatcher that owns those mutations, where nothing else can run meanwhile. A caller already on that
    /// thread (<see cref="Flush"/> at app exit) or with no dispatcher at all (tests) runs inline;
    /// <see cref="OnSaveTimer"/>'s catch-all keeps a failure on that inline path from ending the process.
    /// </summary>
    /// <summary>
    /// Writes the settings as they are in memory right now to <paramref name="path"/>, for a data folder copy
    /// that ran in the background while the user could still change something. Does nothing while saves are
    /// suppressed: the file on disk is the truth then, and the copy already holds it. Throws like
    /// <see cref="Save"/> on a failed write.
    /// </summary>
    public void WriteCopyTo(string path)
    {
        if (_saveSuppressed)
            return;

        var (json, _) = SerializeCurrentOnOwningThread();
        AtomicFile.WriteAllText(path, json);
    }

    private (string Json, long Sequence) SerializeCurrentOnOwningThread()
    {
        // The sequence number is taken in the same step as the snapshot, so it orders snapshots exactly.
        (string, long) Snapshot() => (JsonSerializer.Serialize(Current, JsonOptions), Interlocked.Increment(ref _serializeSequence));

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess() || dispatcher.HasShutdownStarted)
            return Snapshot();

        // An Invoke the dispatcher aborts (shutdown racing this timer) yields null; writing that would
        // replace settings.json with an empty file, so it fails instead and the old file stays.
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

    /// <summary>How many times the file was really written. Test seam: the point of the deferred save is
    /// that a burst of calls collapses into one write, and only counting the writes proves it.</summary>
    internal int DiskWrites => Volatile.Read(ref _diskWrites);

    /// <summary>
    /// The save every UI click goes through. <see cref="Save"/> serialises, writes a temp file and then
    /// retries a locked move with a growing sleep; up to about three quarters of a second on the caller's
    /// thread, which on the UI thread is a visible freeze on something as small as ticking a checkbox.
    /// This marks the settings dirty and writes them on a thread-pool thread once the changes stop coming,
    /// so a burst of toggles costs one write instead of one per click. Never throws: a failed write is
    /// logged. Anything that needs the file to be current
    /// right now calls <see cref="Flush"/>.
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
    /// Writes a pending deferred save out NOW, on the calling thread, and does nothing when there is
    /// nothing pending. Called wherever the on-disk file has to be current: application exit, and before an
    /// elevated job starts (the worker is a separate process and reads what is on disk, not what is in this
    /// object). A timer save that is still writing counts as pending: it cleared the flag before it wrote,
    /// so returning on the flag alone let the caller go on (and the process exit) while that write was not
    /// on disk yet. Flush then writes the current state itself; that save queues behind the file lock the
    /// in-flight write holds, and the newer snapshot wins. It never waits on the timer thread, which may
    /// itself be waiting for the UI thread Flush is usually running on.
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
            // Nothing may escape a System.Threading.Timer callback: it runs on a thread-pool thread with
            // no caller to catch it, and an unhandled exception there tears down the whole process, not
            // just this one save. WriteAndLogFailure's own catch below handles every failure it knows to
            // expect; this is the backstop for whatever it does not (a mutation racing the no-dispatcher
            // fallback in SerializeCurrentOnOwningThread, or the dispatcher itself shutting down mid-save).
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
            // InvalidOperationException also covers "Collection was modified" when a live mutation races
            // the inline serialize used without a dispatcher (tests, or any host without a WPF Application).
            _logging.Error("Failed to save settings.json", ex);
        }
    }

    /// <summary>
    /// A legitimate settings.json is a few KB. Checked BEFORE reading: without this, a huge
    /// file (planted, or corrupted into a giant string) would be buffered whole before the parser ever got a
    /// chance to fail closed, which could exhaust memory before startup's own fallback logic ever ran.
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
            // Widest sensible net: this method's whole contract is to fall back to defaults, and an
            // exception escaping it took the app down at startup with no way to recover from the UI.
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
    /// Falling back to defaults means the next save overwrites the file, so a broken file is set aside first:
    /// whoever hand-edited it can still recover their settings. At most <see cref="MaxCorruptCopies"/> such
    /// files are kept, the newest ones. Best effort; keeping a copy must never block startup.
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
    /// An explicit <c>null</c> in the file deserialises SUCCESSFULLY into a null section; the property
    /// initialisers only run for members the JSON never mentions. `"scan": null` therefore took the app down
    /// with a NullReferenceException long after loading, in whichever view model read it first. Every section
    /// and every collection is put back to its default here, so nothing downstream has to null-check.
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
        // Added later than the sections above and never given the same treatment: a null here reached the
        // cleanup dialog's ctor (TryGetValue on the saved selection) and every scan's ApplyPermanentExclusions.
        settings.CleanupSelection ??= defaults.CleanupSelection;
        settings.CleanupPermanentlyExcludedPaths ??= defaults.CleanupPermanentlyExcludedPaths;

        // The two strings are compared against known values everywhere; null is not one of them.
        settings.Ui.Theme ??= defaults.Ui.Theme;
        settings.Ui.Language ??= defaults.Ui.Language;

        // The table layouts are read in MainViewModel's ctor: a null here ("tables": null, "main": null or
        // "hiddenColumns": null) failed every launch with a fatal error that no UI could ever repair.
        settings.Ui.Tables ??= defaults.Ui.Tables;
        foreach (var broken in settings.Ui.Tables.Where(e => e.Value is null).Select(e => e.Key).ToList())
            settings.Ui.Tables.Remove(broken);
        foreach (var table in settings.Ui.Tables.Values)
        {
            table.HiddenColumns ??= [];
            table.ShownColumns ??= [];
            table.PinnedColumns ??= [];
        }

        // The violet theme was renamed. Without this, a stored old id matches nothing and the user
        // would silently land back on the system theme.
        if (string.Equals(settings.Ui.Theme, "aipurple", StringComparison.OrdinalIgnoreCase))
            settings.Ui.Theme = "neon";

        // An earlier registry-backup default was the profile root itself, which put exported .reg
        // files directly in the user's home directory. Only a value
        // that matches that default EXACTLY is rewritten; a folder the user picked themselves (even one
        // that happens to sit inside the profile) is left untouched.
        if (string.Equals(
                settings.RegistryCleanup.BackupFolder,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.OrdinalIgnoreCase))
        {
            settings.RegistryCleanup.BackupFolder = RegistryCleanupSettings.DefaultBackupFolder;
        }

        // The app itself was later renamed from Windows Updater to Ultimate Updater, and the roaming data
        // folder followed; a settings file saved before that still points at the old folder name. Same
        // rule as above: only an exact match with the old DEFAULT is rewritten, never a folder the user
        // chose themselves.
        if (string.Equals(
                settings.RegistryCleanup.BackupFolder,
                RegistryCleanupSettings.LegacyDefaultBackupFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            settings.RegistryCleanup.BackupFolder = RegistryCleanupSettings.DefaultBackupFolder;
        }

        // A JSON array may hold nulls; every reader of this list treats an entry as a key.
        // IntervalMinutes is deliberately NOT clamped; 0 or less is how the interval scan is turned off.
        settings.Excludes.RemoveAll(string.IsNullOrWhiteSpace);

        // Same reasoning: a blank entry is compared against a real path on every scan and can never match.
        settings.CleanupPermanentlyExcludedPaths.RemoveAll(string.IsNullOrWhiteSpace);

        // Swept here too (not only lazily on lookup): an install that stays closed past a snooze's due
        // date must not resurrect it the moment the app is opened again days or weeks later.
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in settings.Snoozed.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            settings.Snoozed.Remove(expired);

        // A reference-typed dictionary value CAN be an explicit JSON null (unlike Excludes' plain
        // strings-in-a-list or Snoozed's struct values); a hand-edited or corrupted entry here must not
        // throw the first time a scan reads it back.
        foreach (var broken in settings.PersistentFailures.Where(e => e.Value is null).Select(e => e.Key).ToList())
            settings.PersistentFailures.Remove(broken);

        // MainViewModel.RowActions caps and redacts LastErrorText the same way
        // HistoryService.Append holds Detail to, but only at the moment it is NEWLY set; a value already
        // sitting in settings.json from before that cap existed, or from a larger cap that later shrank,
        // would otherwise survive on disk uncapped for ever, reloaded verbatim on every future launch.
        // CapDetail is idempotent, an already-capped value passes through unchanged.
        foreach (var record in settings.PersistentFailures.Values)
            record.LastErrorText = HistoryService.CapDetail(record.LastErrorText);

        return settings;
    }

    /// <summary>
    /// Drops settings.json keys of a source no longer in <see cref="SourceRegistry"/>, which would
    /// otherwise stay for ever: <see cref="Normalize"/> only fills in missing sections, it never prunes stale keys out of
    /// existing ones, and being `private static` it has no logger to name what it drops anyway. This runs
    /// once per load, after Normalize, and only ever removes keys; the cleaned dictionary reaches disk the
    /// next time something calls <see cref="Save"/> or <see cref="SaveDeferred"/>, same as any other
    /// in-memory change made here.
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
