using System.IO;

namespace UltimateUpdater.Services.App;

/// <summary>Resolves data/log/cache locations; installed vs. portable is decided by the Inno installer's marker
/// file.</summary>
public static partial class AppPaths
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
