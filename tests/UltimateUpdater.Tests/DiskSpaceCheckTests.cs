using UltimateUpdater.Services.Install;

namespace UltimateUpdater.Tests;

/// <summary>
/// Pure function, no drive or file system touched: every free-space figure is handed in directly.
/// </summary>
public class DiskSpaceCheckTests
{
    [Fact]
    public void Enough_space_is_not_short()
    {
        var verdict = DiskSpaceCheck.Evaluate([100L, 200L], freeSystemBytes: 10_000L, freeTempBytes: 10_000L);

        Assert.False(verdict.IsShort);
        Assert.Equal(600L, verdict.RequiredBytes); // (100 + 200) * 2
        Assert.Equal(0, verdict.UnknownCount);
        Assert.Equal(10_000L, verdict.FreeBytes);
    }

    [Fact]
    public void Not_enough_space_is_short()
    {
        var verdict = DiskSpaceCheck.Evaluate([1_000L], freeSystemBytes: 500L, freeTempBytes: 900L);

        Assert.True(verdict.IsShort);
        Assert.Equal(2_000L, verdict.RequiredBytes);
        Assert.Equal(500L, verdict.FreeBytes); // the smaller of the two free values
    }

    /// <summary>Never warn on a guess: with no known size at all there is nothing to compare against.</summary>
    [Fact]
    public void Every_size_unknown_is_never_short_no_matter_how_little_is_free()
    {
        var verdict = DiskSpaceCheck.Evaluate([null, null], freeSystemBytes: 1L, freeTempBytes: 1L);

        Assert.False(verdict.IsShort);
        Assert.Equal(0L, verdict.RequiredBytes);
        Assert.Equal(2, verdict.UnknownCount);
    }

    /// <summary>A mix of known and unknown sizes: the known ones alone decide IsShort, and the unknown
    /// count is reported separately rather than folded into the required-bytes figure.</summary>
    [Fact]
    public void A_mix_of_known_and_unknown_sizes_only_counts_the_known_ones_toward_the_requirement()
    {
        var verdict = DiskSpaceCheck.Evaluate([1_000L, null, 500L], freeSystemBytes: 100L, freeTempBytes: 100L);

        Assert.True(verdict.IsShort);
        Assert.Equal(3_000L, verdict.RequiredBytes); // (1000 + 500) * 2, the null contributes nothing
        Assert.Equal(1, verdict.UnknownCount);
    }

    [Fact]
    public void The_smaller_of_the_two_free_values_is_what_is_checked_against()
    {
        var verdict = DiskSpaceCheck.Evaluate([100L], freeSystemBytes: 5_000L, freeTempBytes: 50L);

        Assert.Equal(50L, verdict.FreeBytes);
        Assert.True(verdict.IsShort); // 200 required > 50 free
    }

    /// <summary>A size at or below zero is not a size: a bogus negative figure must neither cancel a real one
    /// out nor turn the sum negative (which read as "plenty of room").</summary>
    [Fact]
    public void A_negative_or_zero_size_is_ignored_and_counted_as_unknown()
    {
        var verdict = DiskSpaceCheck.Evaluate([1_000L, -5_000L, 0L], freeSystemBytes: 1_500L, freeTempBytes: 1_500L);

        Assert.Equal(2_000L, verdict.RequiredBytes);
        Assert.Equal(2, verdict.UnknownCount);
        Assert.True(verdict.IsShort);
    }

    [Fact]
    public void A_batch_of_only_non_positive_sizes_is_never_flagged_short()
    {
        var verdict = DiskSpaceCheck.Evaluate([-1L, 0L], freeSystemBytes: 1L, freeTempBytes: 1L);

        Assert.Equal(0L, verdict.RequiredBytes);
        Assert.False(verdict.IsShort);
    }

    /// <summary>Doubling used to wrap around to a negative number, which compared as "fits".</summary>
    [Fact]
    public void Huge_sizes_saturate_instead_of_overflowing()
    {
        var verdict = DiskSpaceCheck.Evaluate([long.MaxValue / 2, long.MaxValue / 2], freeSystemBytes: 1_000L, freeTempBytes: 1_000L);

        Assert.Equal(long.MaxValue, verdict.RequiredBytes);
        Assert.True(verdict.IsShort);
    }

    [Fact]
    public void A_sum_past_the_long_range_saturates_instead_of_throwing()
    {
        var verdict = DiskSpaceCheck.Evaluate([long.MaxValue, long.MaxValue, 5L], freeSystemBytes: 1_000L, freeTempBytes: 1_000L);

        Assert.Equal(long.MaxValue, verdict.RequiredBytes);
        Assert.True(verdict.IsShort);
    }
}
