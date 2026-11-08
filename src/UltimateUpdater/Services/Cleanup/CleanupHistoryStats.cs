namespace UltimateUpdater.Services.Cleanup;

/// <summary>The figures under the history total, kept apart from the view model so they can be tested alone.</summary>
public static class CleanupHistoryStats
{
    public const int RecentDays = 30;

    /// <summary>The newest run in which the given repair area ended with a known result. Runs from before
    /// results were recorded, and runs where the tool never finished, have none and are skipped.</summary>
    public static (DateTimeOffset When, RepairResult Result)? LastCheck(IEnumerable<CleanupHistoryEntry> entries, string areaId)
    {
        (DateTimeOffset When, RepairResult Result)? best = null;
        foreach (var entry in entries)
        {
            foreach (var area in entry.Areas)
            {
                if (area.Id == areaId && area.RepairResult is { } result && (best is null || entry.Timestamp > best.Value.When))
                    best = (entry.Timestamp, result);
            }
        }

        return best;
    }

    /// <summary>What an average run freed; null when there is no run at all.</summary>
    public static long? AverageFreedPerRun(IReadOnlyCollection<CleanupHistoryEntry> entries) =>
        entries.Count == 0 ? null : entries.Sum(e => e.FreedBytes) / entries.Count;

    /// <summary>What the runs of the last <see cref="RecentDays"/> days freed in total; null when none ran in
    /// that time, so the row can stay hidden instead of claiming "0 B".</summary>
    public static long? FreedInRecentDays(IEnumerable<CleanupHistoryEntry> entries, DateTimeOffset now)
    {
        var since = now - TimeSpan.FromDays(RecentDays);
        var recent = entries.Where(e => e.Timestamp >= since && e.Timestamp <= now).ToList();
        return recent.Count == 0 ? null : recent.Sum(e => e.FreedBytes);
    }
}
