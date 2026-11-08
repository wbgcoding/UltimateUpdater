namespace UltimateUpdater.Services.Cleanup;

/// <summary>One slice of the history pie: an area summed over every run, or the rest of them lumped together.</summary>
/// <param name="TitleKey">Null exactly for the lumped-together "other" slice.</param>
public sealed record HistorySliceData(
    string? TitleKey, IReadOnlyList<string> TitleArguments, long Bytes, int Percent, int RunCount, DateTimeOffset LastRun)
{
    public bool IsOther => TitleKey is null;
}

/// <summary>
/// The history overview's arithmetic, kept apart from the view models so it can be tested without a window.
/// </summary>
public static class CleanupHistoryOverview
{
    /// <summary>The largest areas that get a slice of their own; everything smaller is one "other" slice.</summary>
    public const int MaxNamedSlices = 6;

    /// <summary>
    /// Areas summed across all runs (grouped by name key plus name arguments, so two drives of a per-drive
    /// area stay apart), the largest <paramref name="maxNamed"/> as their own slices and the rest as one last
    /// "other" slice. An area that freed nothing in any run is left out. Percentages are whole numbers that add
    /// up to exactly 100 (largest remainder), so a legend never reads 99 or 101. A run counts for an area only
    /// where that area freed something, the same rule the slice itself follows.
    /// </summary>
    public static IReadOnlyList<HistorySliceData> BuildSlices(IEnumerable<CleanupHistoryEntry> entries, int maxNamed = MaxNamedSlices)
    {
        var groups = entries
            .SelectMany(entry => entry.Areas.Where(a => a.FreedBytes > 0).Select(area => (entry, area)))
            .GroupBy(x => (x.area.TitleKey, Arguments: string.Join('\u001f', x.area.TitleArguments)))
            .Select(g => new
            {
                g.Key.TitleKey,
                Arguments = (IReadOnlyList<string>)g.First().area.TitleArguments.ToList(),
                Bytes = g.Sum(x => x.area.FreedBytes),
                Runs = g.Select(x => x.entry).Distinct().ToList(),
            })
            .OrderByDescending(g => g.Bytes)
            .ThenBy(g => g.TitleKey, StringComparer.Ordinal)
            .ToList();

        var total = groups.Sum(g => g.Bytes);
        if (total <= 0)
            return [];

        var slices = groups
            .Take(maxNamed)
            .Select(g => (Title: (string?)g.TitleKey, g.Arguments, g.Bytes, g.Runs))
            .ToList();

        var rest = groups.Skip(maxNamed).ToList();
        if (rest.Count > 0)
        {
            slices.Add((null, [], rest.Sum(g => g.Bytes), rest.SelectMany(g => g.Runs).Distinct().ToList()));
        }

        var percents = WholePercents(slices.Select(s => s.Bytes).ToList(), total);

        return slices
            .Select((s, i) => new HistorySliceData(
                s.Title, s.Arguments, s.Bytes, percents[i], s.Runs.Count, s.Runs.Max(r => r.Timestamp)))
            .ToList();
    }

    /// <summary>Largest-remainder rounding: floors every share, then hands the missing points to the
    /// biggest fractional parts, so the result always sums to 100.</summary>
    public static int[] WholePercents(IReadOnlyList<long> values, long total)
    {
        var result = new int[values.Count];
        if (total <= 0)
            return result;

        var remainders = new (int Index, double Fraction)[values.Count];
        var assigned = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var exact = values[i] * 100.0 / total;
            result[i] = (int)Math.Floor(exact);
            remainders[i] = (i, exact - result[i]);
            assigned += result[i];
        }

        foreach (var (index, _) in remainders.OrderByDescending(r => r.Fraction).ThenBy(r => r.Index).Take(100 - assigned))
            result[index]++;

        return result;
    }
}
