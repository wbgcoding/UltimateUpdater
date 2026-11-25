using System.IO;

namespace UltimateUpdater.Services.App;

/// <summary>Resolves data/log/cache locations; installed vs. portable is decided by the Inno installer's marker
/// file.</summary>
public static class AppPaths
{
    private const string InstalledMarkerFileName = ".installed";
    private const string ProbeFileName = ".writetest";

    /// <summary>Left in the new folder when the legacy copy failed. The app creates that folder on every start,
    /// so "the folder exists" alone would end every retry; this file keeps the migration open until it
    /// worked.</summary>
    private const string MigrationPendingFileName = ".migration-pending";

    /// <summary>Written into the marker when it cannot be deleted after a successful retry, so no later start
    /// merges the legacy folder a second time.</summary>
    private const string MigrationDoneContent = "done";

    private const string MigrationReplacedSuffix = ".before-migration";

    /// <summary>
    /// The roaming folder name from before the rename to Ultimate Updater. Kept only so the one-time
    /// migration can find data left there; never written to.
    /// </summary>
    private const string LegacyProductFolderName = "WindowsUpdater";

    /// <summary>
    /// The small file that says "the data folder lives somewhere else". Never inside
    /// <see cref="DataDirectory"/> itself: that folder is what it points away from, and a pointer that moved
    /// with its target could not be found again.
    /// </summary>
    private const string PointerFileName = "datapath.txt";

    /// <summary>
    /// An explicit static constructor, so the whole static initializer runs once, at the first touch of any
    /// member, and reports it to <see cref="AppPathsTouchTracker"/>, a witness a test can read without
    /// causing the touch it observes.
    /// </summary>
    static AppPaths() => AppPathsTouchTracker.MarkTouched();

    private static readonly bool IsInstalledValue =
        File.Exists(Path.Combine(AppContext.BaseDirectory, InstalledMarkerFileName));

    /// <summary>Every read of a resolved path or flag on this type is counted here: the seam the
    /// elevated-worker audit test uses to prove that constructing the worker's <see cref="LoggingService"/>
    /// touches nothing on this type. Reading it is not itself a touch.</summary>
    internal static int AccessCount { get; private set; }

    public static bool IsInstalled
    {
        get { AccessCount++; return IsInstalledValue; }
    }

    /// <summary>The title bar's "Portable" badge reads this directly (an <c>x:Static</c> binding). Like
    /// <see cref="IsInstalled"/>, it is decided once at process start.</summary>
    public static bool IsPortable => !IsInstalled;

    private static readonly string DataDirectoryValue = ResolveDataDirectory();

    public static string DataDirectory
    {
        get { AccessCount++; return DataDirectoryValue; }
    }

    /// <summary>
    /// Where the logs, the icon cache, the saved error reports and the crash dumps live. They can be rebuilt
    /// or only matter on this PC, so they stay out of the roaming folder. Installed copies:
    /// <c>%LOCALAPPDATA%\UltimateUpdater</c>. A portable copy keeps everything in its own <c>data</c> folder,
    /// and so does a copy whose local folder cannot be written. Settings, history and the quarantine manifests
    /// stay in <see cref="DataDirectory"/>.
    /// </summary>
    private static readonly string LocalDirectoryValue = ResolveLocalDirectory();

    public static string LocalDirectory
    {
        get { AccessCount++; return LocalDirectoryValue; }
    }

    /// <summary>True when <see cref="LocalDirectory"/> is a folder of its own, apart from the data folder.</summary>
    internal static bool LocalDirectoryIsSeparate => !SamePath(LocalDirectoryValue, DataDirectoryValue);

    private const string LogsFolderName = "logs";
    private static readonly string LogsDirectoryValue = EnsureSubdirectory(LogsFolderName);

    public static string LogsDirectory
    {
        get { AccessCount++; return LogsDirectoryValue; }
    }

    private static readonly string IconCacheDirectoryValue = EnsureSubdirectory(Path.Combine("cache", "icons"));

    public static string IconCacheDirectory
    {
        get { AccessCount++; return IconCacheDirectoryValue; }
    }

    /// <summary>The pure choice behind <see cref="LocalDirectory"/>: an installed copy takes a folder under
    /// the local app data, everything else (portable, no local app data known) the data folder itself.</summary>
    internal static string PickLocalDirectory(bool installed, string? localAppData, string dataDirectory) =>
        installed && !string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(localAppData, DedicatedSubfolderName)
            : dataDirectory;

    private static bool SamePath(string a, string b)
    {
        try
        {
            static string Normalize(string path) =>
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Picks the local folder and, the first time an installed copy uses it, moves the logs, cache and reports
    /// over from the data folder. This runs in a static initializer, so nothing may throw: whatever goes
    /// wrong, the answer is the data folder, where those folders have always been.
    /// </summary>
    private static string ResolveLocalDirectory()
    {
        // The test suite points everything at one throwaway folder; the real local folder is never touched.
        if (AppPathsTestOverride.DataDirectory is { Length: > 0 })
            return DataDirectoryValue;

        try
        {
            var local = PickLocalDirectory(
                IsInstalledValue, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryValue);

            if (SamePath(local, DataDirectoryValue) || !TryPrepare(local))
                return DataDirectoryValue;

            var (moved, skipped) = MigrateToLocalDirectory(DataDirectoryValue, local);
            if (moved > 0)
                LocalFolderMigrationMessage = $"Moved {moved} log, cache and report file(s) from '{DataDirectoryValue}' to '{local}'.";

            if (skipped > 0)
                LocalFolderMigrationFailure = $"{skipped} log, cache or report entr(ies) in '{DataDirectoryValue}' could not be moved to '{local}' and stay where they are.";

            return local;
        }
        catch (Exception)
        {
            return DataDirectoryValue;
        }
    }

    /// <summary>The folders that moved from the data folder to the local one. A property, not a field: it is read
    /// from the static initializer, before any later field would have been assigned.</summary>
    internal static string[] LocalFolderNames => ["logs", "cache", "reports"];

    /// <summary>Set when the one-time move of the logs, cache and reports carried something over; deferred
    /// logging like <see cref="PointerFallbackReason"/>.</summary>
    public static string? LocalFolderMigrationMessage { get; private set; }

    /// <summary>Reads and clears <see cref="LocalFolderMigrationMessage"/> in one step.</summary>
    public static string? ConsumeLocalFolderMigrationMessage()
    {
        var message = LocalFolderMigrationMessage;
        LocalFolderMigrationMessage = null;
        return message;
    }

    /// <summary>Set when part of the move had to be skipped; those files stay where they were.</summary>
    public static string? LocalFolderMigrationFailure { get; private set; }

    /// <summary>Reads and clears <see cref="LocalFolderMigrationFailure"/> in one step.</summary>
    public static string? ConsumeLocalFolderMigrationFailure()
    {
        var message = LocalFolderMigrationFailure;
        LocalFolderMigrationFailure = null;
        return message;
    }

    /// <summary>
    /// Moves the logs, cache and reports folders from <paramref name="dataDirectory"/> into
    /// <paramref name="localDirectory"/>, file by file. Nothing is ever lost or blocked: a file that cannot be
    /// moved (in use, a name that already exists there, a link) stays where it was, any error only skips that
    /// entry, and a folder with nothing left in it is removed. It runs on every start but finds nothing to do
    /// once the old folders are gone. Never throws; returns how many files moved and how many entries were skipped.
    /// </summary>
    internal static (int Moved, int Skipped) MigrateToLocalDirectory(string dataDirectory, string localDirectory)
    {
        var moved = 0;
        var skipped = 0;

        foreach (var name in LocalFolderNames)
        {
            try
            {
                MoveFolderContents(Path.Combine(dataDirectory, name), Path.Combine(localDirectory, name), ref moved, ref skipped);
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        return (moved, skipped);
    }

    private static void MoveFolderContents(string from, string to, ref int moved, ref int skipped)
    {
        // Gone: nothing to do. A link is never followed, in the source or at the target.
        var kind = ClassifyEntry(from);
        if (kind == EntryKind.Gone || !Directory.Exists(from))
            return;

        if (kind == EntryKind.Link || ClassifyEntry(to) == EntryKind.Link)
        {
            skipped++;
            return;
        }

        Directory.CreateDirectory(to);

        foreach (var entry in Directory.EnumerateFileSystemEntries(from).ToList())
        {
            var target = Path.Combine(to, Path.GetFileName(entry));
            try
            {
                if (ClassifyEntry(entry) != EntryKind.Plain)
                {
                    skipped++;
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    MoveFolderContents(entry, target, ref moved, ref skipped);
                    continue;
                }

                if (File.Exists(target) || Directory.Exists(target))
                {
                    skipped++;
                    continue;
                }

                File.Move(entry, target);
                moved++;
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        try
        {
            // only an empty folder goes; Directory.Delete without recursion refuses anything else
            Directory.Delete(from, recursive: false);
        }
        catch (Exception)
        {
            // still in use or not empty: stays
        }
    }

    /// <summary>
    /// Where the "moved elsewhere" pointer lives, always in a fixed location so it can be found before
    /// anything knows where the real data folder is. Portable: beside the executable. Installed: the default
    /// roaming folder, kept as the stable address even after the real folder moves out of it.
    /// </summary>
    private static string PointerFilePath => IsInstalled
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltimateUpdater", PointerFileName)
        : Path.Combine(AppContext.BaseDirectory, PointerFileName);

    /// <summary>
    /// Set at most once, when a saved pointer no longer resolves to somewhere usable. Nothing can log yet
    /// (this runs in a static initializer), so the first caller that owns a <see cref="LoggingService"/> reads
    /// and clears it through <see cref="ConsumePointerFallbackReason"/>.
    /// </summary>
    public static string? PointerFallbackReason { get; private set; }

    /// <summary>Reads and clears <see cref="PointerFallbackReason"/> in one step, so its line is logged once.</summary>
    public static string? ConsumePointerFallbackReason()
    {
        var reason = PointerFallbackReason;
        PointerFallbackReason = null;
        return reason;
    }

    /// <summary>
    /// Set at most once, when the legacy pre-rename folder was copied into this build's roaming folder.
    /// Deferred logging like <see cref="PointerFallbackReason"/>, for the same reason.
    /// </summary>
    public static string? LegacyDataFolderMigrationMessage { get; private set; }

    /// <summary>Reads and clears <see cref="LegacyDataFolderMigrationMessage"/> in one step.</summary>
    public static string? ConsumeLegacyDataFolderMigrationMessage()
    {
        var message = LegacyDataFolderMigrationMessage;
        LegacyDataFolderMigrationMessage = null;
        return message;
    }

    /// <summary>Set when the legacy copy failed; same deferred-logging shape as
    /// <see cref="LegacyDataFolderMigrationMessage"/>.</summary>
    public static string? LegacyDataFolderMigrationFailure { get; private set; }

    /// <summary>Reads and clears <see cref="LegacyDataFolderMigrationFailure"/> in one step.</summary>
    public static string? ConsumeLegacyDataFolderMigrationFailure()
    {
        var message = LegacyDataFolderMigrationFailure;
        LegacyDataFolderMigrationFailure = null;
        return message;
    }

    /// <summary>
    /// Picks the first location that is actually writable. This runs in a static initializer, so a throw
    /// would surface as a TypeInitializationException before any logging or crash handler exists and the
    /// process would vanish. A portable exe in Program Files or on a read-only share is normal, so it
    /// falls back instead.
    /// </summary>
    private static string ResolveDataDirectory()
    {
        // The test suite points the whole data folder at a throwaway one. No pointer and no migration then:
        // neither may read or write the developer's real folder.
        if (AppPathsTestOverride.DataDirectory is { Length: > 0 } forced)
        {
            TryPrepare(forced);
            return forced;
        }

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltimateUpdater");

        // A deliberate one-time exception to "this app never migrates data on its own": the rename changed
        // only the folder name under %AppData%, never the format inside, so a plain copy is safe. Runs before
        // the pointer check; an old installation that had moved its data has that folder copied instead.
        TryMigrateLegacyDataFolder(roaming);

        // A folder the user moved data to on purpose wins over every ordinary candidate, but only while it
        // still resolves; a fallback logs one line once LoggingService exists.
        if (TryReadPointer(PointerFilePath) is { } pointedTo)
        {
            // Only a dedicated subfolder is adopted: "delete my data" removes logs, cache and reports beneath the
            // data folder, which on a pointer to a folder the user already owns would hit their own folders.
            if (!IsDedicatedDataFolder(pointedTo))
            {
                PointerFallbackReason =
                    $"The data folder recorded in '{PointerFilePath}' ('{pointedTo}') is not a dedicated '{DedicatedSubfolderName}' folder; ignoring it and using the default folder instead.";
            }
            else
            {
                // Must still exist: TryPrepare creates a missing folder, so a renamed-away folder or a USB stick under
                // another letter would start the app on an empty folder with default settings and no fallback line.
                if (Directory.Exists(pointedTo) && TryPrepare(pointedTo))
                    return pointedTo;

                PointerFallbackReason =
                    $"The data folder recorded in '{PointerFilePath}' ('{pointedTo}') is no longer usable; using the default folder instead.";
            }
        }

        var candidates = IsInstalled
            ? new[] { roaming }
            : [Path.Combine(AppContext.BaseDirectory, "data"), roaming];

        foreach (var candidate in candidates)
        {
            if (TryPrepare(candidate))
                return candidate;
        }

        // Last resort. Returning the path anyway keeps the promise that this method does not throw; the logger
        // tolerates a directory it cannot write to.
        var fallback = Path.Combine(Path.GetTempPath(), "UltimateUpdater");
        TryPrepare(fallback);
        return fallback;
    }

    /// <summary>
    /// Copies an old <c>%AppData%\WindowsUpdater</c> folder into the new <c>%AppData%\UltimateUpdater</c>
    /// one, exactly once: only when the new folder does not exist yet and the old one does. The old folder is
    /// left untouched (a copy, never a move), so a problem with the new folder never loses the user's data.
    /// The internal overload takes explicit paths so a test does not touch the real <c>%AppData%</c>.
    /// </summary>
    private static void TryMigrateLegacyDataFolder(string roaming) =>
        TryMigrateLegacyDataFolder(
            roaming,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyProductFolderName));

    internal static void TryMigrateLegacyDataFolder(string roaming, string legacy)
    {
        // An existing new folder normally ends the migration for good. Only the marker of a failed copy
        // reopens it. The legacy copy then wins, and each file it replaces is kept with a suffix.
        var retry = Directory.Exists(roaming);
        if (retry && !IsMigrationPending(Path.Combine(roaming, MigrationPendingFileName)))
            return;

        if (!Directory.Exists(legacy))
            return;

        // The legacy folder can itself carry a "moved elsewhere" pointer from a pre-rename build; that pointer,
        // not the legacy default folder, is where the data lives. An unusable target falls back to the legacy
        // folder itself, logged.
        var source = legacy;
        string? fallbackReason = null;

        if (TryReadPointer(Path.Combine(legacy, PointerFileName)) is { } pointedTo)
        {
            // The whole target gets copied, so only a dedicated app folder qualifies: a pointer at a user's own
            // folder (or a relative one) must not make the first start copy arbitrary data.
            if (!IsDedicatedLegacyDataFolder(pointedTo))
            {
                fallbackReason =
                    $"The data folder recorded in the old installation ('{pointedTo}') is not a dedicated app folder; migrating the old default folder '{legacy}' instead.";
            }
            else if (Directory.Exists(pointedTo))
            {
                source = pointedTo;
            }
            else
            {
                fallbackReason =
                    $"The data folder recorded in the old installation ('{pointedTo}') no longer exists; migrating the old default folder '{legacy}' instead.";
            }
        }

        if (!TryMigrateInto(source, roaming, out var failure))
        {
            LegacyDataFolderMigrationFailure =
                $"Could not copy data from the old '{source}' folder to '{roaming}' ({failure}); the next start tries again.";
            MarkMigrationPending(roaming);
            return;
        }

        if (retry)
            CloseMigrationMarker(Path.Combine(roaming, MigrationPendingFileName));

        LegacyDataFolderMigrationMessage =
            (fallbackReason is null ? string.Empty : fallbackReason + " ")
            + $"Copied data from the old '{legacy}' folder to '{roaming}' after the app was renamed; the old folder was left in place.";
    }

    /// <summary>
    /// A fully qualified path whose last segment is the current or the pre-rename dedicated folder name.
    /// </summary>
    private static bool IsDedicatedLegacyDataFolder(string path)
    {
        if (IsDedicatedDataFolder(path))
            return true;

        try
        {
            return Path.IsPathFullyQualified(path)
                && string.Equals(
                    Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    LegacyProductFolderName,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a temp sibling of <paramref name="destination"/> and renames it
    /// into place only once the copy succeeded. A copy that fails midway never leaves
    /// <paramref name="destination"/> existing, so the next launch retries instead of being stuck on a
    /// half-migrated folder.
    /// </summary>
    internal static bool TryMigrateInto(string source, string destination) =>
        TryMigrateInto(source, destination, out _);

    /// <summary>When <paramref name="destination"/> already exists (a retry after a failed copy) the staged
    /// copy is laid over it: the failed start only wrote defaults there, while the legacy folder holds the
    /// user's real settings and history.</summary>
    internal static bool TryMigrateInto(string source, string destination, out string failure)
    {
        var staging = destination + ".migrating-" + Guid.NewGuid().ToString("N");

        try
        {
            // A skipped log costs nothing; any other skipped file would be lost for good, since the new
            // folder existing stops every later retry.
            if (!TryCopyDirectory(source, staging, out var skipped))
            {
                failure = "the copy failed part way";
                TryDeleteBestEffort(staging);
                return false;
            }

            if (skipped.FirstOrDefault(file => !IsUnder(file, Path.Combine(source, LogsFolderName))) is { } left)
            {
                failure = $"'{Path.GetFileName(left)}' could not be read";
                TryDeleteBestEffort(staging);
                return false;
            }

            if (Directory.Exists(destination))
            {
                MergeOver(staging, destination);
                TryDeleteBestEffort(staging);
            }
            else
            {
                Directory.Move(staging, destination);
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message;
            TryDeleteBestEffort(staging);
            return false;
        }
    }

    private static void MergeOver(string from, string into)
    {
        // The app may have run for a while on the new folder before this retry, so what the legacy copy
        // replaces is kept beside it instead of being lost.
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(into, Path.GetFileName(file));
            if (File.Exists(target))
                File.Move(target, target + MigrationReplacedSuffix, overwrite: true);
            File.Move(file, target);
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            var target = Path.Combine(into, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            MergeOver(directory, target);
        }
    }

    /// <summary>Best effort: a folder that cannot even take the marker is retried anyway while it does not
    /// exist, and the failure is already reported.</summary>
    private static void MarkMigrationPending(string roaming)
    {
        try
        {
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(roaming, MigrationPendingFileName), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsMigrationPending(string marker)
    {
        try
        {
            return File.Exists(marker) && File.ReadAllText(marker).Trim() != MigrationDoneContent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable marker must not reopen a merge that may already have run.
            return false;
        }
    }

    private static void CloseMigrationMarker(string marker)
    {
        FileDeletion.TryDelete(marker);
        if (!File.Exists(marker))
            return;

        try
        {
            File.WriteAllText(marker, MigrationDoneContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LegacyDataFolderMigrationFailure =
                $"The migration marker '{marker}' could not be removed ({ex.Message}); delete it so the next start does not copy the old data again.";
        }
    }

    /// <summary>A 32,767-character long path still fits, even at 3 bytes per character in UTF-8.</summary>
    internal const long MaxPointerFileBytes = 128 * 1024;

    /// <summary>Internal so a test can prove the round trip through an arbitrary temp path, never through
    /// the real <see cref="PointerFilePath"/>.</summary>
    internal static string? TryReadPointer(string pointerPath)
    {
        try
        {
            if (!File.Exists(pointerPath))
                return null;

            // Runs during static initialisation: an unbounded read of a corrupt or huge file would crash every
            // start, so an oversized pointer reads like a missing one.
            var text = AtomicFile.TryReadAllText(pointerPath, MaxPointerFileBytes)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Records the new location so the next launch resolves <see cref="DataDirectory"/> there; this
    /// run keeps using the folder it started with.</summary>
    public static bool WriteDataFolderPointer(string newPath)
    {
        var written = WriteDataFolderPointer(newPath, PointerFilePath);
        if (written)
            _pendingDataFolder = newPath;

        return written;
    }

    /// <summary>The folder the pointer now names while this run still writes to the old one; null when no move
    /// happened in this run.</summary>
    private static volatile string? _pendingDataFolder;

    /// <summary>The files the running app keeps appending to or replacing after a data folder copy. The rest
    /// of the folder (logs, the rebuildable caches) tolerates a stale copy.</summary>
    private static readonly string[] LiveWrittenEntries =
    [
        "history.jsonl", "history.jsonl.1", "cleanup-history.json", "reports",
        "driver-backups", "restore-point.json", "pip-blocked.json", "last-scan.json", "enrichment-cache.json",
    ];

    /// <summary>Brings the live-written entries (history, cleanup history, reports, driver backups, the
    /// restore point, the pip block list, the last scan and the enrichment cache) in the new data folder up
    /// to date with the folder this run writes to. Called right before the pointer is written, so the earlier
    /// copy does not miss what happened meanwhile.</summary>
    public static bool SyncLiveDataTo(string destination) => SyncLiveData(DataDirectory, destination, includeSettings: false);

    /// <summary>At exit, once the delayed settings write has landed: everything written after the pointer was
    /// set goes to the new folder too, so the restart finds nothing missing. Best effort, an exit never fails
    /// on it.</summary>
    public static void SyncPendingDataFolder()
    {
        if (_pendingDataFolder is { } pending)
            SyncLiveData(DataDirectory, pending, includeSettings: true);
    }

    /// <summary>Makes each live-written entry in <paramref name="destination"/> a copy of the one in
    /// <paramref name="source"/>, overwriting what is there and removing what the source no longer has (the
    /// history pruning deletes its rotated file). Links are never followed. False when something could not be
    /// written.</summary>
    internal static bool SyncLiveData(string source, string destination, bool includeSettings)
    {
        var entries = includeSettings ? LiveWrittenEntries.Append("settings.json") : LiveWrittenEntries;
        try
        {
            foreach (var entry in entries)
            {
                var from = Path.Combine(source, entry);
                var to = Path.Combine(destination, entry);

                if (Directory.Exists(from))
                {
                    if (IsReparsePoint(from))
                        return false;

                    // Mirrored file by file: a report the user deleted meanwhile must not come back with the
                    // restart, and a large folder (driver backups) is not copied over again in full.
                    if (!MirrorDirectory(from, to))
                        return false;
                }
                else if (File.Exists(from))
                {
                    if (IsReparsePoint(from))
                        return false;

                    File.Copy(from, to, overwrite: true);
                }
                else
                {
                    if (Directory.Exists(to))
                        Directory.Delete(to, recursive: true);
                    else if (File.Exists(to))
                        File.Delete(to);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Makes <paramref name="to"/> hold exactly what <paramref name="from"/> holds, recursively: files
    /// that are missing or differ are copied, files and folders the source no longer has are removed. A link
    /// anywhere in the source fails it, like every other copy of the data folder. False when something could
    /// not be written.</summary>
    private static bool MirrorDirectory(string from, string to)
    {
        // A link in the new folder would carry the copies and the deletions below to wherever it points.
        if (ClassifyEntry(to) == EntryKind.Link)
            return false;

        if (File.Exists(to))
            File.Delete(to);

        Directory.CreateDirectory(to);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var kind = ClassifyEntry(file);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
                return false;

            var name = Path.GetFileName(file);
            var target = Path.Combine(to, name);
            if (ClassifyEntry(target) == EntryKind.Link)
                return false;

            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);

                var source = new FileInfo(file);
                var existing = new FileInfo(target);

                // Copy keeps the write time, so equal length and write time means the file did not change.
                if (!existing.Exists || existing.Length != source.Length || existing.LastWriteTimeUtc != source.LastWriteTimeUtc)
                    File.Copy(file, target, overwrite: true);

                names.Add(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted by the running app after it was listed: nothing left to carry over.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            var kind = ClassifyEntry(directory);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
                return false;

            var name = Path.GetFileName(directory);
            names.Add(name);
            if (!MirrorDirectory(directory, Path.Combine(to, name)))
                return false;
        }

        foreach (var stale in Directory.EnumerateFileSystemEntries(to))
        {
            if (names.Contains(Path.GetFileName(stale)))
                continue;

            if (Directory.Exists(stale))
                Directory.Delete(stale, recursive: true);
            else
                File.Delete(stale);
        }

        return true;
    }

    internal static bool WriteDataFolderPointer(string newPath, string pointerFilePath)
    {
        try
        {
            var pointerDirectory = Path.GetDirectoryName(pointerFilePath);
            if (!string.IsNullOrEmpty(pointerDirectory))
                Directory.CreateDirectory(pointerDirectory);

            // Atomic, so a crash mid-write cannot leave a truncated path.
            AtomicFile.WriteAllText(pointerFilePath, newPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Why a folder the user picked in the change-data-folder dialog was refused.</summary>
    public enum DataFolderRejection
    {
        None,
        Link,
        NotWritable,
        SystemFolder,
        InsideProgramFolder,
        NonEmptyTarget,
        InsideCurrentDataFolder,
        TempOrCache,

        /// <summary>Only for the registry backup folder, which the elevated cleanup refuses outside the
        /// user profile.</summary>
        OutsideUserProfile,
    }

    /// <summary>
    /// The move never lands directly in the folder the user picked, always in a dedicated subfolder, so a
    /// folder that holds the user's own unrelated files (a foreign <c>settings.json</c>, a <c>logs</c>
    /// subtree) is never overwritten or swept up by <see cref="AppDataCleaner"/>, which only touches paths
    /// under this app's own data directory.
    /// </summary>
    public const string DedicatedSubfolderName = "UltimateUpdater";

    /// <summary>True when the last segment of <paramref name="path"/> is <see cref="DedicatedSubfolderName"/>,
    /// the only shape this app ever writes as a data folder, so it is the only one it accepts back.</summary>
    internal static bool IsDedicatedDataFolder(string path)
    {
        try
        {
            // A relative pointer would resolve against whatever the current directory is at the time.
            if (!Path.IsPathFullyQualified(path))
                return false;

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetFileName(trimmed), DedicatedSubfolderName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The actual copy/pointer destination for a folder the user picked, always
    /// <c>&lt;picked&gt;\UltimateUpdater</c>.</summary>
    public static string EffectiveDataFolderTarget(string picked) =>
        Path.Combine(Path.GetFullPath(picked), DedicatedSubfolderName);

    private static readonly Environment.SpecialFolder[] SystemFolders =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonProgramFilesX86,
    ];

    /// <summary>
    /// The rules for a new data folder: no reparse point on the way (the cleanup dialog's own walk,
    /// <see cref="Cleanup.CleanupSafety.AncestryLeadsThroughLink"/>, so a junction is judged the same way
    /// there and here), not a Windows system folder, not inside the program's own folder unless this build
    /// is portable, and the dedicated <see cref="EffectiveDataFolderTarget"/> subfolder must not already exist
    /// with content in it and must be writable.
    /// </summary>
    public static DataFolderRejection ValidateNewDataFolder(string path) =>
        ValidateNewDataFolder(path, IsInstalled, AppContext.BaseDirectory, DataDirectory, Path.GetTempPath());

    internal static DataFolderRejection ValidateNewDataFolder(
        string path, bool isInstalled, string baseDirectory, string currentDataDirectory, string tempPath)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DataFolderRejection.NotWritable;
        }

        if (Cleanup.CleanupSafety.AncestryLeadsThroughLink(full))
            return DataFolderRejection.Link;

        if (IsUnderAny(full, SystemFolders))
            return DataFolderRejection.SystemFolder;

        if (isInstalled && IsUnder(full, baseDirectory))
            return DataFolderRejection.InsideProgramFolder;

        // A target this app's own cleanup could later empty (CleanupSafety's name-based heuristic) is refused,
        // like %TEMP% itself.
        if (IsUnder(full, tempPath) || Cleanup.CleanupSafety.LooksLikeTempOrCacheLocation(full))
            return DataFolderRejection.TempOrCache;

        // The destination is always a dedicated subfolder, never the picked folder itself.
        var effective = EffectiveDataFolderTarget(full);

        // Equal to, inside, or an ancestor of the current data folder would let CopyRecursive copy the folder
        // into itself (or write a pointer that points at itself).
        if (RelatesToCurrentDataFolder(effective, currentDataDirectory))
            return DataFolderRejection.InsideCurrentDataFolder;

        if (Directory.Exists(effective) && Directory.EnumerateFileSystemEntries(effective).Any())
            return DataFolderRejection.NonEmptyTarget;

        return TryPrepare(effective) ? DataFolderRejection.None : DataFolderRejection.NotWritable;
    }

    /// <summary>
    /// The rules for the registry backup folder the user types or picks. Not the data-folder rules: the
    /// default backup folder lies inside the current data folder, so those would reject it. What decides
    /// is the boundary the elevated backup itself enforces (inside the user profile, no link on the way),
    /// plus the system, program and temp/cache folders no backup should ever land in.
    /// </summary>
    public static DataFolderRejection ValidateRegistryBackupFolder(string path) =>
        ValidateRegistryBackupFolder(
            path, IsInstalled, AppContext.BaseDirectory, Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static DataFolderRejection ValidateRegistryBackupFolder(
        string path, bool isInstalled, string baseDirectory, string tempPath, string userProfile)
    {
        if (!Cleanup.RegistryBackupService.IsWithinUserProfile(path, userProfile))
            return DataFolderRejection.OutsideUserProfile;

        var full = Path.GetFullPath(path);

        if (Cleanup.CleanupSafety.AncestryLeadsThroughLink(full))
            return DataFolderRejection.Link;

        if (IsUnderAny(full, SystemFolders))
            return DataFolderRejection.SystemFolder;

        if (isInstalled && IsUnder(full, baseDirectory))
            return DataFolderRejection.InsideProgramFolder;

        if (IsUnder(full, tempPath) || Cleanup.CleanupSafety.LooksLikeTempOrCacheLocation(full))
            return DataFolderRejection.TempOrCache;

        return DataFolderRejection.None;
    }

    /// <summary>True when <paramref name="effective"/> equals, sits inside, or is an ancestor of
    /// <paramref name="currentDataDirectory"/>.</summary>
    private static bool RelatesToCurrentDataFolder(string effective, string currentDataDirectory)
    {
        string trimmedCurrent;
        try
        {
            trimmedCurrent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDataDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (trimmedCurrent.Length == 0)
            return false;

        var trimmedEffective = Path.TrimEndingDirectorySeparator(effective);

        return string.Equals(trimmedEffective, trimmedCurrent, StringComparison.OrdinalIgnoreCase)
            || trimmedEffective.StartsWith(trimmedCurrent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || trimmedCurrent.StartsWith(trimmedEffective + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderAny(string full, IEnumerable<Environment.SpecialFolder> folders)
    {
        foreach (var special in folders)
        {
            string specialPath;
            try
            {
                specialPath = Environment.GetFolderPath(special);
            }
            catch (PlatformNotSupportedException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(specialPath) && IsUnder(full, specialPath))
                return true;
        }

        return false;
    }

    private static bool IsUnder(string full, string ancestor)
    {
        string trimmedAncestor;
        try
        {
            trimmedAncestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (trimmedAncestor.Length == 0)
            return false;

        return string.Equals(full, trimmedAncestor, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(trimmedAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies everything under <see cref="DataDirectory"/> into <paramref name="destination"/>, and reports
    /// whether the result is safe to switch to. A skipped file that is not under <see cref="LogsDirectory"/>
    /// means something real (settings, history, a cache entry) was left behind, so the whole move fails: the
    /// partial copy is removed and nothing is switched. A skipped log (the file currently being written)
    /// is fine to leave behind; its copy starts fresh at the new location on the next launch.
    /// </summary>
    public static bool TryMoveDataFolder(string destination, out IReadOnlyList<string> blockingSkippedFiles) =>
        TryMoveDataFolder(DataDirectory, LogsDirectory, destination, out blockingSkippedFiles);

    internal static bool TryMoveDataFolder(
        string source, string logsDirectory, string destination, out IReadOnlyList<string> blockingSkippedFiles,
        Action<string>? afterEnumerated = null)
    {
        if (!TryCopyDirectory(source, destination, out var skipped, afterEnumerated))
        {
            blockingSkippedFiles = [];
            TryDeleteBestEffort(destination);
            return false;
        }

        var blocking = skipped.Where(file => !IsUnder(file, logsDirectory)).ToList();
        if (blocking.Count == 0)
        {
            blockingSkippedFiles = [];
            return true;
        }

        TryDeleteBestEffort(destination);
        blockingSkippedFiles = blocking;
        return false;
    }

    /// <summary>
    /// Undoes a copy whose later step (final sync, settings copy, pointer write) failed: the dedicated
    /// subfolder is removed so a retry to the same target is not refused as non-empty. Only a folder this
    /// very move created is touched, never one that was already there, and a link in its place is left alone.
    /// Never throws.
    /// </summary>
    public static void DiscardCreatedDataFolder(string destination, bool createdByThisMove)
    {
        if (!createdByThisMove || ClassifyEntry(destination) != EntryKind.Plain)
            return;

        TryDeleteBestEffort(destination);
    }

    /// <summary>Cleanup after a failed copy/move; never throws, never blocks the caller from reporting or
    /// retrying its own failure.</summary>
    private static void TryDeleteBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool TryCopyDirectory(
        string source, string destination, out IReadOnlyList<string> skippedFiles, Action<string>? afterEnumerated = null)
    {
        var skipped = new List<string>();
        try
        {
            Directory.CreateDirectory(destination);
            CopyRecursive(source, destination, skipped, afterEnumerated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            skippedFiles = skipped;
            return false;
        }

        skippedFiles = skipped;
        return true;
    }

    /// <summary>
    /// A junction or symlink inside the folder being moved would make the copy read from wherever it points
    /// (another user's folder, a system directory) and write that into the new data folder. Every reparse
    /// point, file or directory, is therefore skipped and reported in <paramref name="skipped"/>, which fails
    /// the move like any other left-behind file. No "still under the source root" comparison of full paths:
    /// <c>Path.GetFullPath</c> never resolves a link, so it could not catch a swapped folder.
    /// </summary>
    private static void CopyRecursive(
        string source, string destination, List<string> skipped, Action<string>? afterEnumerated)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Test seam for the window between listing an entry and using it, where the running app can delete it.
            afterEnumerated?.Invoke(file);
            var name = Path.GetFileName(file);
            // Neither belongs in a copy of the data: the pointer names a location (copying it would point the new
            // folder at itself), the write probe and the migration marker are transient.
            if (string.Equals(name, PointerFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ProbeFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, MigrationPendingFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = ClassifyEntry(file);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
            {
                skipped.Add(file);
                continue;
            }

            try
            {
                File.Copy(file, Path.Combine(destination, name), overwrite: true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted by the running app after it was listed: nothing left to carry over, not a locked file.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(file);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            afterEnumerated?.Invoke(directory);

            var kind = ClassifyEntry(directory);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
            {
                skipped.Add(directory);
                continue;
            }

            var target = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            try
            {
                CopyRecursive(directory, target, skipped, afterEnumerated);
            }
            catch (DirectoryNotFoundException)
            {
                // The folder was deleted while it was being copied: drop the half-made copy of it.
                TryDeleteBestEffort(target);
            }
        }
    }

    private enum EntryKind { Plain, Link, Gone }

    /// <summary>An entry whose attributes cannot be read is treated as a link: fail closed, never copy it.
    /// One that no longer exists is told apart, since a deleted entry is nothing to carry over.</summary>
    private static EntryKind ClassifyEntry(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ? EntryKind.Link : EntryKind.Plain;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return EntryKind.Gone;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return EntryKind.Link;
        }
    }

    private static bool IsReparsePoint(string path) => ClassifyEntry(path) != EntryKind.Plain;

    /// <summary>Existing but read-only directories are common, so writability is proven, not assumed.</summary>
    private static bool TryPrepare(string directory)
    {
        var probe = Path.Combine(directory, ProbeFileName);

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(probe, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }

        // The write is the proof. A scanner holding the file for a moment must not make a good directory look
        // unusable and move the user's settings and history somewhere else.
        try
        {
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot log: this runs in a static initializer, before LoggingService exists. Deliberate.
        }

        return true;
    }

    /// <summary>A subfolder of <see cref="LocalDirectory"/>. Falls back to the data directory itself: a missing
    /// subfolder must not stop the app.</summary>
    private static string EnsureSubdirectory(string relative)
    {
        var dir = Path.Combine(LocalDirectoryValue, relative);

        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return DataDirectory;
        }
    }
}

/// <summary>
/// Where the test suite redirects the data folder. A separate type: setting a static member of
/// <see cref="AppPaths"/> would run its initializer first, which resolves the folder. Production never
/// sets it.
/// </summary>
internal static class AppPathsTestOverride
{
    internal static string? DataDirectory { get; set; }
}
