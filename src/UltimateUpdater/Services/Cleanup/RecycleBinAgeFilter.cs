namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Whether one recycled item is old enough to stay listed under the recycle bin card's age filter.
/// Pure and total, so the rule is provable without a real recycle bin.
/// </summary>
public static class RecycleBinAgeFilter
{
    /// <summary>
    /// True when the item passes the filter and should stay listed (and, later, be deleted). 0 or less
    /// means the filter is off: every age passes. A null <paramref name="deletedAt"/> stands for an item
    /// whose deletion timestamp the scan could not read at all, the shell's own whole-drive aggregate row
    /// carries no per-item date, and is treated as old rather than filtered out, so a scan never silently
    /// hides real disk usage only because it cannot prove an age for it.
    /// </summary>
    public static bool PassesAgeFilter(DateTimeOffset? deletedAt, int minAgeDays, DateTimeOffset now)
    {
        if (minAgeDays <= 0)
            return true;

        if (deletedAt is not { } value)
            return true;

        return (now - value).TotalDays >= minAgeDays;
    }
}
