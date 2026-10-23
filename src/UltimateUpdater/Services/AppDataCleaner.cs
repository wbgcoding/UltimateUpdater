using System.IO;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services;

/// <summary>
/// Deletes the files this app wrote, for the settings window's "delete data".
/// Deliberately narrow: only known file names and only directories under <see cref="AppPaths"/> are
/// touched, never a path that arrived from anywhere else. Anything still locked (the current log file is,
/// by definition) is skipped and reported rather than retried or forced.
/// </summary>
public static class AppDataCleaner
{
    /// <summary>Caches, the icon store, the logs and the saved error reports (ErrorReportBuilder): whole
    /// subdirectories of the data directory.</summary>
    internal static readonly string[] OwnedDirectories = ["cache", "logs", "reports"];

    /// <summary>
    /// Single files the app owns by name. Every writer of a file under the data directory belongs here, or
    /// its file quietly survives an action the settings window calls "delete my data": the rotated history
    /// (HistoryService.RotateIfNeeded) and the cleanup history (CleanupHistoryService) both did.
    /// </summary>
    internal static readonly string[] OwnedFiles =
    [
        "history.jsonl", "history.jsonl.1", "enrichment-cache.json", "cleanup-history.json",
        "last-scan.json", "last-scan.json.tmp", "restore-point.json",
    ];

    /// <summary>The caches, the history and the logs. Returns true if anything could not be removed.</summary>
    public static bool DeleteCachesAndHistory(LoggingService logging)
    {
        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.DedicatedSubfolderName);
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (!IsDeletableDataFolder(AppPaths.DataDirectory, roaming, portable))
        {
            logging.Warn($"Refusing to delete from '{AppPaths.DataDirectory}': it is not a folder this app created.");
            return true;
        }

        var failed = DeleteOwnedContent(
            AppPaths.DataDirectory, logging, Path.GetTempPath(), new AutostartService(logging));

        // AppPaths resolved these once, in a static initializer, and nothing else ever recreates them .
        // so without this the app silently stopped logging and caching icons for the rest of the session,
        // right after the action a user is most likely to want a log for.
        Recreate(AppPaths.LogsDirectory, logging);
        Recreate(AppPaths.IconCacheDirectory, logging);

        return failed;
    }

    /// <summary>
    /// The recursive deletes below assume the data folder holds only this app's files. That holds for the
    /// default folder, the portable <c>data</c> folder and a dedicated subfolder; any other folder may
    /// hold the user's own logs, cache or reports and is left alone.
    /// </summary>
    internal static bool IsDeletableDataFolder(string dataDirectory, string defaultFolder, string portableFolder)
    {
        static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            var full = Normalize(dataDirectory);
            return string.Equals(full, Normalize(defaultFolder), StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, Normalize(portableFolder), StringComparison.OrdinalIgnoreCase)
                || AppPaths.IsDedicatedDataFolder(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The deletion itself, against any data directory: so the list above can be proven against a
    /// throwaway folder instead of the machine's real one.
    /// </summary>
    internal static bool DeleteOwnedContent(
        string dataDirectory, LoggingService logging, string? tempDirectory = null, AutostartService? autostart = null)
    {
        var failed = false;

        foreach (var relative in OwnedDirectories)
            failed |= !TryDeleteDirectory(Path.Combine(dataDirectory, relative), logging);

        foreach (var name in OwnedFiles)
        {
            failed |= !TryDeleteFile(Path.Combine(dataDirectory, name), logging);
            failed |= !TryDeleteTempSiblings(dataDirectory, name, minAge: null, logging);
        }

        // Unreadable copies the app set aside itself; they hold the same history and settings the user is
        // deleting, so they go too.
        foreach (var pattern in CorruptCopyPatterns)
            failed |= !TryDeleteMatching(dataDirectory, pattern, logging);

        // Both are removed on a best-effort basis and never flag a failure: the caller reports a failure as
        // "files still in use", which neither of them is.
        if (tempDirectory is not null)
            TryDeleteFile(Path.Combine(tempDirectory, CrashNoteName), logging);

        autostart?.TrySet(false);

        return failed;
    }

    /// <summary>The note the app writes to the temp folder when it dies before logging works.</summary>
    internal const string CrashNoteName = "UltimateUpdater-crash.log";

    /// <summary>The names the app gives an unreadable cleanup history and an unreadable settings file when
    /// it keeps them aside: the original name, ".corrupt-" and a local timestamp.</summary>
    private static readonly Regex[] CorruptCopyPatterns =
    [
        new(@"^cleanup-history\.corrupt-[0-9]{8}-[0-9]{6}\.json$", RegexOptions.CultureInvariant),
        new(@"^settings\.json\.corrupt-[0-9]{8}-[0-9]{6}$", RegexOptions.CultureInvariant),
    ];

    private static bool TryDeleteMatching(string directory, Regex pattern, LoggingService logging)
    {
        var ok = true;
        try
        {
            if (!Directory.Exists(directory))
                return true;

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (!pattern.IsMatch(Path.GetFileName(path)))
                    continue;

                try
                {
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        continue;

                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ok = false;
                    logging.Warn($"Could not delete '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Could not list '{directory}': {ex.Message}");
            return false;
        }

        return ok;
    }

    /// <summary>
    /// Start-up housekeeping for the leftovers of an atomic write that was cut short (crash, power loss, a
    /// lock that outlived the write): every "name.guid.tmp" beside an owned file or the settings file that
    /// is older than <paramref name="minAge"/>. The age limit keeps a write that is still in flight safe.
    /// Only the settings file's own temp copies are included here, never the settings file itself.
    /// </summary>
    internal static void DeleteStaleTempSiblings(string dataDirectory, TimeSpan minAge)
    {
        foreach (var name in OwnedFiles.Append("settings.json"))
            TryDeleteTempSiblings(dataDirectory, name, minAge, logging: null);
    }

    /// <summary>
    /// The temp name <see cref="AtomicFile"/> gives a write: the target name, a 32-digit lowercase hex guid
    /// and ".tmp". Matched by that exact shape instead of a wildcard, so a file that merely starts the same
    /// way is never taken for one of ours.
    /// </summary>
    private static Regex TempSiblingPattern(string name) =>
        new($@"^{Regex.Escape(name)}\.[0-9a-f]{{32}}\.tmp$", RegexOptions.CultureInvariant);

    /// <summary>Non-recursive; links and anything that is not a plain file are skipped. Returns false when
    /// a sibling exists but could not be removed.</summary>
    private static bool TryDeleteTempSiblings(string dataDirectory, string name, TimeSpan? minAge, LoggingService? logging)
    {
        var pattern = TempSiblingPattern(name);
        var ok = true;
        try
        {
            if (!Directory.Exists(dataDirectory))
                return true;

            foreach (var path in Directory.EnumerateFiles(dataDirectory))
            {
                if (!pattern.IsMatch(Path.GetFileName(path)))
                    continue;

                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        continue;

                    if (minAge is { } age && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < age)
                        continue;

                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ok = false;
                    logging?.Warn($"Could not delete '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging?.Warn($"Could not list '{dataDirectory}': {ex.Message}");
            return false;
        }

        return ok;
    }

    private static void Recreate(string path, LoggingService logging)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Could not recreate '{path}': {ex.Message}");
        }
    }

    private static bool TryDeleteDirectory(string path, LoggingService logging)
    {
        try
        {
            if (!Directory.Exists(path))
                return true;

            // A reparse point could aim anywhere; deleting through one is exactly the mistake to avoid.
            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                logging.Warn($"Refusing to delete '{path}': it is a link, not a real directory.");
                return false;
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Expected: the running log file is open. Not an error worth interrupting the user over.
            logging.Warn($"Could not delete '{path}': {ex.Message}");
            return false;
        }
    }

    private static bool TryDeleteFile(string path, LoggingService logging)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Could not delete '{path}': {ex.Message}");
            return false;
        }
    }
}
