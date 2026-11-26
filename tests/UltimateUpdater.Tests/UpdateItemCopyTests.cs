using UltimateUpdater.Models;
using Xunit;

namespace UltimateUpdater.Tests;

public class UpdateItemCopyTests
{
    private static UpdateItem Item() => new()
    {
        Id = "a",
        SourceId = "pip",
        Category = UpdateCategory.DeveloperTools,
        Name = "a",
        InstallScope = "user",
        PublishDate = DateTimeOffset.UnixEpoch,
        DownloadSize = 5,
    };

    [Fact]
    public void A_pinned_copy_carries_every_other_field_including_the_install_scope()
    {
        var pinned = Item().WithPinnedVersion("1.0");

        Assert.Equal("user", pinned.InstallScope);
        Assert.Equal(DateTimeOffset.UnixEpoch, pinned.PublishDate);
        Assert.Equal(5, pinned.DownloadSize);
        Assert.Equal("1.0", pinned.PinnedVersion);
        Assert.Equal("1.0", pinned.AvailableVersion);
        Assert.True(pinned.IsRollback);
    }

    [Fact]
    public void Two_items_with_the_same_content_are_still_different_rows()
    {
        var first = Item();
        var second = first with { };

        Assert.NotEqual(first, second);
        Assert.Equal(first, first);
        Assert.Equal(2, new HashSet<UpdateItem> { first, second }.Count);
    }
}
