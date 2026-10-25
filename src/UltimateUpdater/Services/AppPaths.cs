using System.IO;

namespace UltimateUpdater.Services;

/// <summary>Resolves data/log/cache locations; installed vs. portable is decided by the Inno installer's marker file.</summary>
public static class AppPaths
{
    private const string InstalledMarkerFileName = ".installed";
    private const string ProbeFileName = ".writetest";

    /// <summary>
    /// The roaming folder name used before the app was renamed to Ultimate Updater. Kept only so the
    /// one-time migration below can find data left there; never written to again.
    /// </summary>
    private const string LegacyProductFolderName = "WindowsUpdater";

    /// <summary>
    /// The name of the small file that says "the data folder actually lives somewhere else".
    /// Never inside <see cref="DataDirectory"/> itself, that folder is exactly what this points away from,
    /// and a pointer that moved with its own target could never be found again.
    /// </summary>
    private const string PointerFileName = "datapath.txt";

    /// <summary>
    /// An explicit static constructor, so this type's whole static initializer (this field plus
    /// every other one below) runs exactly once, at the first genuine touch of ANY member, and reports
    /// that touch to <see cref="AppPathsTouchTracker"/>, a witness a test can read without itself causing
    /// the very touch it is trying to observe (see that type's own remarks).
    /// </summary>
    static AppPaths() => AppPathsTouchTracker.MarkTouched();

    private static readonly bool IsInstalledValue =
        File.Exists(Path.Combine(AppContext.BaseDirectory, InstalledMarkerFileName));

    /// <summary>Every read of any resolved path or flag on this type is marked here, on every call, not
    /// just the first: this is the seam the elevated-worker audit test uses to prove that constructing the
    /// worker's own <see cref="LoggingService"/> touches nothing on this type. Reading it does not
    /// itself count as a touch.</summary>
    internal static int AccessCount { get; private set; }

    public static bool IsInstalled
    {
        get { AccessCount++; return IsInstalledValue; }
    }

    /// <summary>The title bar's own "Portable" badge reads this directly (an <c>x:Static</c> binding,
    /// never re-evaluated after the window loads, which matches <see cref="IsInstalled"/> itself: both
    /// are decided once, at process start, and never change for the life of the run).</summary>
    public static bool IsPortable => !IsInstalled;

    private static readonly string DataDirectoryValue = ResolveDataDirectory();

    public static string DataDirectory
    {
        get { AccessCount++; return DataDirectoryValue; }
    }

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

    /// <summary>
    /// Where the "moved elsewhere" pointer lives, itself always in a FIXED location so it can be
    /// found before anything knows where the real data folder is. Portable: beside the executable, the one
    /// part of a portable install that never moves on its own. Installed: the roaming folder this app
    /// would use by default anyway, kept as the one stable address even after the real folder moves out of
    /// it.
    /// </summary>
    private static string PointerFilePath => IsInstalled
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltimateUpdater", PointerFileName)
        : Path.Combine(AppContext.BaseDirectory, PointerFileName);

    /// <summary>
    /// Set at most once, when a saved pointer no longer resolves to somewhere usable: nothing here can log
    /// yet (this runs in a static initializer, see <see cref="TryPrepare"/>'s own remark), so the first
    /// caller that owns a <see cref="LoggingService"/> reads and clears this through <see
    /// cref="ConsumePointerFallbackReason"/> instead.
    /// </summary>
    public static string? PointerFallbackReason { get; private set; }

    /// <summary>Reads and clears <see cref="PointerFallbackReason"/> in one step, so the one line it
    /// describes is logged exactly once no matter how many windows ask.</summary>
    public static string? ConsumePointerFallbackReason()
    {
        var reason = PointerFallbackReason;
        PointerFallbackReason = null;
        return reason;
    }

    /// <summary>
    /// Set at most once, the first time this build's roaming folder is resolved and the legacy
    /// pre-rename folder was copied into it. Same deferred-logging shape as
    /// <see cref="PointerFallbackReason"/>, for the same reason: this runs in a static initializer,
    /// before any logger exists.
    /// </summary>
    public static string? LegacyDataFolderMigrationMessage { get; private set; }

    /// <summary>Reads and clears <see cref="LegacyDataFolderMigrationMessage"/> in one step.</summary>
    public static string? ConsumeLegacyDataFolderMigrationMessage()
    {
        var message = LegacyDataFolderMigrationMessage;
        LegacyDataFolderMigrationMessage = null;
        return message;
    }

    /// <summary>
    /// Picks the first location that is actually writable. This runs in a static initializer, so a
    /// throw here would surface as a TypeInitializationException before any logging or crash handler
    /// exists, the process would simply vanish. A portable exe dropped into Program Files or onto a
    /// read-only share is a perfectly normal thing for a user to do, so it falls back instead.
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

        // Deliberate, one-time exception to the "this app never migrates data on its own" rule above:
        // the rename to Ultimate Updater changed only the FOLDER NAME under %AppData%, never the
        // format of anything inside it, so a plain copy is safe here in a way an ordinary format
        // migration would not be. Runs before the pointer check below; an old installation that had moved
        // its data elsewhere has that folder copied instead of its default one.
        TryMigrateLegacyDataFolder(roaming);

        // A folder the user moved data to on purpose wins over every ordinary candidate below,
        // but only while it still resolves; a fallback here logs one line once LoggingService exists
        // rather than silently using the wrong folder or throwing this process away outright.
        if (TryReadPointer(PointerFilePath) is { } pointedTo)
        {
            // Only a dedicated subfolder is ever adopted: "delete my data" removes logs, cache and reports
            // beneath the data folder, which on a pointer to a folder the user already owns (hand-written,
            // restored from a backup, left by an older build) would hit their own folders.
            if (!IsDedicatedDataFolder(pointedTo))
            {
                PointerFallbackReason =
                    $"The data folder recorded in '{PointerFilePath}' ('{pointedTo}') is not a dedicated '{DedicatedSubfolderName}' folder; ignoring it and using the default folder instead.";
            }
            else
            {
                // Must still EXIST: TryPrepare creates a missing folder, so a data folder renamed away, or a USB
                // stick back under another letter, silently started the app on an empty folder with default
                // settings and no history, and without the fallback line in the log.
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

        // Last resort. If even this fails there is nowhere to write at all, and returning the path
        // anyway keeps the promise that this method does not throw, the logger already tolerates a
        // directory it cannot write to.
        var fallback = Path.Combine(Path.GetTempPath(), "UltimateUpdater");
        TryPrepare(fallback);
        return fallback;
    }

    /// <summary>
    /// Copies an old <c>%AppData%\WindowsUpdater</c> folder into the new <c>%AppData%\UltimateUpdater</c>
    /// one, exactly once: only when the new folder does not exist yet and the old one does. The old
    /// folder is left untouched (a copy, never a move), so a problem with the new folder never loses the
    /// user's actual data. Public overload uses the real, fixed roaming path; the internal one takes
    /// explicit paths so a test can prove this without touching the real <c>%AppData%</c>.
    /// </summary>
    private static void TryMigrateLegacyDataFolder(string roaming) =>
        TryMigrateLegacyDataFolder(
            roaming,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyProductFolderName));

    internal static void TryMigrateLegacyDataFolder(string roaming, string legacy)
    {
        if (Directory.Exists(roaming) || !Directory.Exists(legacy))
            return;

        // The legacy folder can itself carry a "moved elsewhere" pointer, written by a
        // pre-rename build whose user had already moved their data folder; that pointer, not the legacy
        // default folder, is where the real data actually lives. An unusable pointer target falls back to
        // the legacy folder itself, logged so the fallback is visible rather than silently wrong.
        var source = legacy;
        string? fallbackReason = null;

        if (TryReadPointer(Path.Combine(legacy, PointerFileName)) is { } pointedTo)
        {
            if (Directory.Exists(pointedTo))
            {
                source = pointedTo;
            }
            else
            {
                fallbackReason =
                    $"The data folder recorded in the old installation ('{pointedTo}') no longer exists; migrating the old default folder '{legacy}' instead.";
            }
        }

        if (!TryMigrateInto(source, roaming))
            return;

        LegacyDataFolderMigrationMessage =
            (fallbackReason is null ? string.Empty : fallbackReason + " ")
            + $"Copied data from the old '{legacy}' folder to '{roaming}' after the app was renamed; the old folder was left in place.";
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a temp sibling of <paramref name="destination"/> and renames
    /// it into place only once the copy actually succeeded: a copy that fails midway (a locked
    /// file, a vanished source) never leaves <paramref name="destination"/> existing, so the caller's own
    /// "does the new folder already exist" check keeps seeing nothing there and the next launch retries
    /// from scratch instead of being stuck on a half-migrated folder forever.
    /// </summary>
    internal static bool TryMigrateInto(string source, string destination)
    {
        var staging = destination + ".migrating-" + Guid.NewGuid().ToString("N");

        try
        {
            // A skipped log costs nothing; any other skipped file would be lost for good, since the new
            // folder existing stops every later retry.
            if (!TryCopyDirectory(source, staging, out var skipped)
                || skipped.Any(file => !IsUnder(file, Path.Combine(source, LogsFolderName))))
            {
                TryDeleteBestEffort(staging);
                return false;
            }

            Directory.Move(staging, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteBestEffort(staging);
            return false;
        }
    }

    /// <summary>A 32,767-character long path still fits, even at 3 bytes per character in UTF-8.</summary>
    internal const long MaxPointerFileBytes = 128 * 1024;

    /// <summary>internal rather than private so a test can prove the round trip through an arbitrary temp
    /// path, never through the real, fixed <see cref="PointerFilePath"/> a test run must not touch.</summary>
    internal static string? TryReadPointer(string pointerPath)
    {
        try
        {
            if (!File.Exists(pointerPath))
                return null;

            // Runs during static initialisation: an unbounded read of a corrupt or huge file would
            // crash every start, so an oversized pointer reads like a missing one.
            var text = AtomicFile.TryReadAllText(pointerPath, MaxPointerFileBytes)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Records the new location so the NEXT launch resolves <see cref="DataDirectory"/>
    /// there; this run keeps using the folder it already started with (moving a folder out from under a
    /// running process is what the restart is for).</summary>
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
    /// of the folder (caches, logs, backups) is either rebuilt or tolerates a stale copy.</summary>
    private static readonly string[] LiveWrittenEntries = ["history.jsonl", "history.jsonl.1", "cleanup-history.json", "reports"];

    /// <summary>Brings the history, cleanup history and reports in the new data folder up to date with the
    /// folder this run writes to. Called right before the pointer is written, so the copy made earlier does not
    /// miss what happened meanwhile.</summary>
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

                    // A report the user deleted meanwhile must not come back with the restart.
                    TryDeleteBestEffort(to);
                    if (!TryCopyDirectory(from, to, out var skipped) || skipped.Count > 0)
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

    internal static bool WriteDataFolderPointer(string newPath, string pointerFilePath)
    {
        try
        {
            var pointerDirectory = Path.GetDirectoryName(pointerFilePath);
            if (!string.IsNullOrEmpty(pointerDirectory))
                Directory.CreateDirectory(pointerDirectory);

            // Atomic: a crash mid-write left a truncated path ("D:\Da") that the next start then used.
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
    /// The move never lands directly in the folder the user picked, always in a dedicated
    /// subfolder of it, so a folder that already holds the user's OWN unrelated files (a foreign
    /// <c>settings.json</c>, a <c>logs</c> subtree) is never overwritten or later swept up by
    /// <see cref="AppDataCleaner"/>, which only ever touches paths under this app's own data directory.
    /// </summary>
    public const string DedicatedSubfolderName = "UltimateUpdater";

    /// <summary>True when the last segment of <paramref name="path"/> is <see cref="DedicatedSubfolderName"/>,
    /// the only shape this app ever writes as a data folder, so it is the only one it accepts back.</summary>
    internal static bool IsDedicatedDataFolder(string path)
    {
        try
        {
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
    /// The rules for a new data folder: no reparse point on the way (reusing the cleanup dialog's own
    /// walk, <see cref="Cleanup.CleanupSafety.AncestryLeadsThroughLink"/>, rather than a second copy of the
    /// same check, so a junction is judged exactly the same way there and here), not a Windows system
    /// folder, not inside the program's own folder unless this build is portable (where that is already
    /// the normal place for data to live), and finally the dedicated <see cref="EffectiveDataFolderTarget"/>
    /// subfolder must not already exist with content in it and must be writable.
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

        // A target this app's own cleanup could later empty (CleanupSafety's own name-based
        // heuristic, reused rather than duplicated) is refused outright, same as %TEMP% itself.
        if (IsUnder(full, tempPath) || Cleanup.CleanupSafety.LooksLikeTempOrCacheLocation(full))
            return DataFolderRejection.TempOrCache;

        // The copy/pointer destination is always a dedicated subfolder, never the picked
        // folder itself, so a folder holding the user's own unrelated files is never touched.
        var effective = EffectiveDataFolderTarget(full);

        // Equal to, inside, or an ancestor of the CURRENT data folder would otherwise let
        // CopyRecursive copy the folder into itself (or write a pointer that points at itself).
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
    /// Copies everything under <see cref="DataDirectory"/> (settings, history, caches, logs and
    /// reports all live somewhere under here, so one walk carries all of them) into
    /// <paramref name="destination"/>, and reports whether the result is safe to switch to.
    /// A skipped file that is NOT under <see cref="LogsDirectory"/> means something real
    /// (settings, history, a cache entry) was silently left behind, so that fails the whole move: the
    /// partial copy at <paramref name="destination"/> is removed and nothing is switched, the same
    /// "nothing changed" contract a hard copy failure already had. A skipped file that IS a log (the file
    /// currently being written, by definition open right now) is fine to leave behind; its own copy simply
    /// starts fresh at the new location on the next launch, same as every other log rotation already
    /// works.
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
    /// the move like any other left-behind file. A "still under the source root" comparison of full paths was
    /// dropped here: <c>Path.GetFullPath</c> never resolves a link, and every folder entered is a child of the
    /// previous one by construction, so it was always true and could not catch a swapped folder.
    /// </summary>
    private static void CopyRecursive(
        string source, string destination, List<string> skipped, Action<string>? afterEnumerated)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Test seam for the window between listing an entry and using it, where the running app can delete it.
            afterEnumerated?.Invoke(file);
            var name = Path.GetFileName(file);
            // Neither belongs in a copy of the data itself: the pointer names a location (copying it would
            // point the new folder at itself) and the write probe is transient housekeeping.
            if (string.Equals(name, PointerFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ProbeFileName, StringComparison.OrdinalIgnoreCase))
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

        // The write is the proof. Deleting it is housekeeping, and a scanner holding the file for a
        // moment must not make a perfectly good directory look unusable, that would move the user's
        // settings and history somewhere else behind their back.
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

    /// <summary>Falls back to the data directory itself: a missing subfolder must not stop the app.</summary>
    private static string EnsureSubdirectory(string relative)
    {
        var dir = Path.Combine(DataDirectory, relative);

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
/// Where the test suite redirects the data folder. A separate type, not a member of <see cref="AppPaths"/>:
/// setting a static member of that class would run its initializer first, which is the very moment the
/// folder gets resolved. Production never sets it.
/// </summary>
internal static class AppPathsTestOverride
{
    internal static string? DataDirectory { get; set; }
}
