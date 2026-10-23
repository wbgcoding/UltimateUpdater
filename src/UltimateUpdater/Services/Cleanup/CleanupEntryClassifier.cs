using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Pure name/extension judgment behind the detail list's "by type" grouping. Never opens
/// or reads a file; only the path text itself, and the extensions already gathered while measuring it.
/// </summary>
public static class CleanupEntryClassifier
{
    private static readonly string[] CrashHints = ["crash", "crashdump", "crashpad", "minidump", "errorreport"];
    private static readonly string[] ThumbnailHints = ["thumb", "thumbnail"];
    private static readonly string[] CacheHints = ["cache"];
    private static readonly string[] LogHints = ["log", "logs"];
    private static readonly string[] LeftoverHints = ["leftover", "uninstall"];

    /// <summary>
    /// Judged from the folder's own name first; a folder literally named "Logs" or "Cache" almost always
    /// is exactly that; and only from the dominant extension among its files when the name says nothing.
    /// </summary>
    public static CleanupEntryType Classify(string path, IReadOnlyCollection<string>? sampleExtensions = null)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (ContainsAny(name, CrashHints))
            return CleanupEntryType.CrashReports;
        if (ContainsAny(name, ThumbnailHints))
            return CleanupEntryType.Thumbnails;
        if (ContainsAny(name, CacheHints))
            return CleanupEntryType.Cache;
        if (ContainsAny(name, LogHints))
            return CleanupEntryType.Logs;
        if (ContainsAny(name, LeftoverHints))
            return CleanupEntryType.InstallLeftovers;

        if (sampleExtensions is { Count: > 0 })
        {
            if (sampleExtensions.Any(ext => ext is ".dmp" or ".mdmp"))
                return CleanupEntryType.CrashReports;
            if (sampleExtensions.All(ext => ext is ".log" or ".txt"))
                return CleanupEntryType.Logs;
        }

        return CleanupEntryType.Other;
    }

    private static bool ContainsAny(string name, string[] hints) =>
        hints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
