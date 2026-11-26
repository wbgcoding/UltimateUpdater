using System.Globalization;
using System.IO;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// Where the Setup that a self-update starts writes its install log, and the matching command-line argument.
/// The files sit in a <c>setup</c> folder below the app's logs folder, apart from the daily logs so their
/// archiving and retention never touch them, and only the newest few are kept.
/// </summary>
internal static class SetupLog
{
    internal const string FolderName = "setup";
    internal const string FilePrefix = "setup-";
    internal const string FileExtension = ".log";

    /// <summary>How many install logs stay: the one just started and the few before it.</summary>
    internal const int KeepCount = 5;

    /// <summary>The folder for install logs under the app's logs folder.</summary>
    internal static string FolderFor(string logsDirectory) => Path.Combine(logsDirectory, FolderName);

    /// <summary>The file for a Setup started at <paramref name="now"/>.</summary>
    internal static string PathFor(string logsDirectory, DateTime now) =>
        Path.Combine(
            FolderFor(logsDirectory),
            string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{now:yyyyMMdd-HHmmss}{FileExtension}"));

    /// <summary>
    /// The argument that makes Setup write its log to <paramref name="logPath"/>, or null when the path cannot be
    /// quoted safely. The path is app-derived, never user input, but it still goes onto a command line, so a
    /// quote, a control character, a wildcard or a switch-like start is refused instead of escaped: the update
    /// then runs without a log rather than with a mangled command line.
    /// </summary>
    internal static string? Argument(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !Path.IsPathFullyQualified(logPath))
            return null;

        foreach (var c in logPath)
        {
            if (c is '"' or '*' or '?' or '<' or '>' or '|' || char.IsControl(c))
                return null;
        }

        // A trailing backslash would escape the closing quote; a file path never ends in one.
        if (logPath.EndsWith('\\') || logPath.EndsWith('/'))
            return null;

        return $"/LOG=\"{logPath}\"";
    }

    /// <summary>Removes all but the newest <paramref name="keep"/> install logs. Never throws: housekeeping is
    /// not worth failing an update over.</summary>
    internal static void Prune(string logsDirectory, int keep = KeepCount)
    {
        try
        {
            var folder = FolderFor(logsDirectory);
            if (!Directory.Exists(folder))
                return;

            var files = new DirectoryInfo(folder)
                .EnumerateFiles(FilePrefix + "*" + FileExtension)
                .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(Math.Max(keep, 0));
            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next update.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next update.
        }
    }
}
