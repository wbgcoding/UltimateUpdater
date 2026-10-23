using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The recycle bin card's age filter, proven as the pure function it is.</summary>
public class RecycleBinAgeFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_item_older_than_the_minimum_is_kept()
    {
        var deletedAt = Now.AddDays(-31);

        Assert.True(RecycleBinAgeFilter.PassesAgeFilter(deletedAt, minAgeDays: 30, Now));
    }

    [Fact]
    public void An_item_exactly_at_the_minimum_is_kept()
    {
        var deletedAt = Now.AddDays(-30);

        Assert.True(RecycleBinAgeFilter.PassesAgeFilter(deletedAt, minAgeDays: 30, Now));
    }

    [Fact]
    public void An_item_younger_than_the_minimum_is_skipped()
    {
        var deletedAt = Now.AddDays(-29);

        Assert.False(RecycleBinAgeFilter.PassesAgeFilter(deletedAt, minAgeDays: 30, Now));
    }

    [Fact]
    public void Zero_keeps_everything_however_young()
    {
        var deletedAt = Now;

        Assert.True(RecycleBinAgeFilter.PassesAgeFilter(deletedAt, minAgeDays: 0, Now));
    }

    [Fact]
    public void A_negative_minimum_also_keeps_everything()
    {
        Assert.True(RecycleBinAgeFilter.PassesAgeFilter(Now, minAgeDays: -1, Now));
    }

    /// <summary>The shell's own whole-drive aggregate row (a bin that could not be enumerated) carries no
    /// per-item date at all; it must never be hidden just because the filter cannot prove its age.</summary>
    [Fact]
    public void An_unreadable_timestamp_is_treated_as_old_and_therefore_listed()
    {
        Assert.True(RecycleBinAgeFilter.PassesAgeFilter(deletedAt: null, minAgeDays: 30, Now));
    }

    [Fact]
    public void An_unreadable_timestamp_is_listed_even_when_the_filter_is_off()
    {
        Assert.True(RecycleBinAgeFilter.PassesAgeFilter(deletedAt: null, minAgeDays: 0, Now));
    }
}
