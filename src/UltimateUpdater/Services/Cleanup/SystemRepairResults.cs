using System.IO;
using System.Text;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Turns what sfc and DISM left behind into a <see cref="RepairResult"/>. The tools print localised text, so
/// nothing here reads their console output: sfc is judged from the component servicing log, which is always
/// English, and DISM from its exit codes. Pure functions plus one small file reader so each rule can be
/// tested without running either tool.
/// </summary>
internal static class SystemRepairResults
{
    private const string CannotRepair = "Cannot repair member file";
    private const string RepairingCorrupted = "Repairing corrupted file";
    private const string RepairedFile = "Repaired file";

    /// <summary>A line that cannot be repaired wins over everything else: a run that fixed three files and
    /// could not fix a fourth still has a problem. A fix without a failure is "repaired". Neither means the
    /// scan found nothing to do.</summary>
    internal static RepairResult ClassifyCbsLines(IEnumerable<string> lines)
    {
        var repaired = false;
        foreach (var line in lines)
        {
            if (line.Contains(CannotRepair, StringComparison.OrdinalIgnoreCase))
                return RepairResult.ProblemsFound;

            if (line.Contains(RepairingCorrupted, StringComparison.OrdinalIgnoreCase)
                || line.Contains(RepairedFile, StringComparison.OrdinalIgnoreCase))
                repaired = true;
        }

        return repaired ? RepairResult.Repaired : RepairResult.Ok;
    }

    /// <summary>
    /// The image check from the exit codes of its three steps, in catalogue order (CheckHealth, ScanHealth,
    /// RestoreHealth). A step that did not run (tool missing, timed out) is null and leaves the result open.
    /// All zero: fine. A RestoreHealth that succeeded after a ScanHealth that complained: it repaired
    /// something. Any other non-zero code: a problem.
    /// </summary>
    internal static RepairResult? ClassifyDism(int? checkHealth, int? scanHealth, int? restoreHealth)
    {
        if (checkHealth is null || scanHealth is null || restoreHealth is null)
            return null;

        if (checkHealth == 0 && scanHealth == 0 && restoreHealth == 0)
            return RepairResult.Ok;

        if (scanHealth != 0 && restoreHealth == 0)
            return RepairResult.Repaired;

        return RepairResult.ProblemsFound;
    }

    internal static string DefaultCbsLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");

    /// <summary>Where the log ends right now, to read only what a step adds afterwards. Null when the log
    /// is missing or unreadable.</summary>
    internal static long? CbsLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The lines written to the log after <paramref name="startOffset"/>. A log that is now shorter than the
    /// offset was rotated while sfc ran, so it is read from the top. Read-only and shared with the servicing
    /// stack that keeps writing it. Null when it cannot be read, which the caller treats as "unknown".
    /// </summary>
    internal static List<string>? ReadCbsLinesSince(string path, long startOffset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(startOffset <= stream.Length ? startOffset : 0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
