using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The pie chart's arithmetic: grouping across runs, the six-plus-other cut and whole percentages.</summary>
public class CleanupHistoryOverviewTests
{
    private static CleanupHistoryArea Area(string key, long bytes, params string[] args) => new()
    {
        Id = key,
        TitleKey = key,
        TitleArguments = [.. args],
        FreedBytes = bytes,
    };

    private static CleanupHistoryEntry Run(DateTimeOffset when, params CleanupHistoryArea[] areas) => new()
    {
        Timestamp = when,
        FreedBytes = areas.Sum(a => a.FreedBytes),
        Areas = [.. areas],
    };

    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(2);

    [Fact]
    public void An_area_is_summed_over_runs_with_its_run_count_and_latest_date()
    {
        var slices = CleanupHistoryOverview.BuildSlices(
        [
            Run(Day1, Area("A", 300), Area("B", 100)),
            Run(Day2, Area("A", 100)),
        ]);

        Assert.Equal(2, slices.Count);
        Assert.Equal("A", slices[0].TitleKey);
        Assert.Equal(400, slices[0].Bytes);
        Assert.Equal(2, slices[0].RunCount);
        Assert.Equal(Day2, slices[0].LastRun);
        Assert.Equal(1, slices[1].RunCount);
        Assert.Equal(Day1, slices[1].LastRun);
    }

    [Fact]
    public void The_same_key_with_different_arguments_stays_apart()
    {
        var slices = CleanupHistoryOverview.BuildSlices([Run(Day1, Area("Drive", 50, "C:"), Area("Drive", 70, "D:"))]);

        Assert.Equal(2, slices.Count);
        Assert.Equal(["D:"], slices[0].TitleArguments);
        Assert.Equal(["C:"], slices[1].TitleArguments);
    }

    [Fact]
    public void An_area_that_freed_nothing_gets_no_slice_and_no_run_count()
    {
        var slices = CleanupHistoryOverview.BuildSlices([Run(Day1, Area("A", 10), Area("Empty", 0)), Run(Day2, Area("A", 10), Area("Empty", 0))]);

        var only = Assert.Single(slices);
        Assert.Equal("A", only.TitleKey);
        Assert.Equal(100, only.Percent);
    }

    [Fact]
    public void Nothing_freed_gives_no_slices()
    {
        Assert.Empty(CleanupHistoryOverview.BuildSlices([]));
        Assert.Empty(CleanupHistoryOverview.BuildSlices([Run(Day1, Area("A", 0))]));
    }

    [Fact]
    public void More_than_six_areas_become_six_slices_plus_other()
    {
        var areas = Enumerable.Range(1, 9).Select(i => Area($"K{i}", i * 100)).ToArray();
        var slices = CleanupHistoryOverview.BuildSlices([Run(Day1, areas[..5]), Run(Day2, [.. areas[5..], Area("K1", 100)])]);

        Assert.Equal(7, slices.Count);
        Assert.Equal(["K9", "K8", "K7", "K6", "K5", "K4"], slices.Take(6).Select(s => s.TitleKey));
        var other = slices[6];
        Assert.True(other.IsOther);
        Assert.Equal(700, other.Bytes);
        Assert.Equal(2, other.RunCount);
        Assert.Equal(Day2, other.LastRun);
        Assert.Equal(100, slices.Sum(s => s.Percent));
    }

    [Fact]
    public void Exactly_six_areas_have_no_other_slice()
    {
        var areas = Enumerable.Range(1, 6).Select(i => Area($"K{i}", 10)).ToArray();

        var slices = CleanupHistoryOverview.BuildSlices([Run(Day1, areas)]);

        Assert.Equal(6, slices.Count);
        Assert.DoesNotContain(slices, s => s.IsOther);
    }

    [Theory]
    [InlineData(1L, 1L, 1L)]
    [InlineData(1L, 2L, 4L)]
    [InlineData(7L, 11L, 13L)]
    [InlineData(5L, 5L, 5L)]
    [InlineData(999L, 1L, 1L)]
    public void Percentages_are_whole_numbers_that_sum_to_100(long a, long b, long c)
    {
        long[] values = [a, b, c];

        var percents = CleanupHistoryOverview.WholePercents(values, values.Sum());

        Assert.Equal(100, percents.Sum());
    }

    [Fact]
    public void Three_equal_shares_read_34_33_33()
    {
        Assert.Equal([34, 33, 33], CleanupHistoryOverview.WholePercents([10, 10, 10], 30));
    }
}
