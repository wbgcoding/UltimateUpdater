using System.IO;
using System.Globalization;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>One label and value line under the history total.</summary>
public sealed class CleanupHistoryStatViewModel(string label, string value)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    public override string ToString() => $"{Label}: {Value}";

    /// <summary>The lines under the total, in display order. The two system checks are always there (reading
    /// "never" until one ran); every other line is left out while it has nothing true to say.</summary>
    internal static IReadOnlyList<CleanupHistoryStatViewModel> Build(
        IReadOnlyCollection<CleanupHistoryEntry> entries, DateTimeOffset now, (string Drive, long FreeBytes)? systemDrive)
    {
        var loc = LocalizationService.Instance;
        var lines = new List<CleanupHistoryStatViewModel>
        {
            new(loc["CleanupHistoryStatSfc"], CheckText(CleanupHistoryStats.LastCheck(entries, "system-repair-sfc"))),
            new(loc["CleanupHistoryStatDism"], CheckText(CleanupHistoryStats.LastCheck(entries, "system-repair-dism"))),
        };

        if (entries.Count > 0)
            lines.Add(new(loc["CleanupHistoryStatRuns"], entries.Count.ToString(CultureInfo.CurrentUICulture)));

        if (CleanupHistoryStats.AverageFreedPerRun(entries) is > 0 and var average)
            lines.Add(new(loc["CleanupHistoryStatAverage"], ByteSize.Format(average)));

        if (CleanupHistoryStats.FreedInRecentDays(entries, now) is { } recent)
            lines.Add(new(loc["CleanupHistoryStatLast30"], ByteSize.Format(recent)));

        if (systemDrive is { } drive)
            lines.Add(new(string.Format(loc["CleanupHistoryStatFree"], drive.Drive), ByteSize.Format(drive.FreeBytes)));

        return lines;
    }

    private static string CheckText((DateTimeOffset When, RepairResult Result)? check)
    {
        var loc = LocalizationService.Instance;
        if (check is not { } found)
            return loc["CleanupHistoryRepairNever"];

        var date = found.When.LocalDateTime.ToString(
            CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentUICulture);
        var key = found.Result switch
        {
            RepairResult.Repaired => "CleanupHistoryRepairRepaired",
            RepairResult.ProblemsFound => "CleanupHistoryRepairProblems",
            _ => "CleanupHistoryRepairOk",
        };
        return string.Format(loc[key], date);
    }

    /// <summary>The system drive's name and live free space, or null when it cannot be read.</summary>
    internal static (string Drive, long FreeBytes)? ReadSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root))
                return null;

            var info = new DriveInfo(root);
            return info.IsReady ? (root.TrimEnd('\\'), info.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
