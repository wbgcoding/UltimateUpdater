using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A winget uninstall that ends with success but leaves the package installed must not count as removed:
/// a reinstall then reported the old version as gone and failed its fresh install for the same reason.
/// </summary>
public class WingetUninstallCheckTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task A_package_still_listed_after_the_settle_time_counts_as_not_removed()
    {
        var asked = 0;
        var stays = await WingetSource.StaysListedAsync(
            _ => { asked++; return Task.FromResult(true); },
            TimeSpan.FromMilliseconds(40), Step, CancellationToken.None);

        Assert.True(stays);
        Assert.True(asked > 1, "the list was asked only once, so a slow uninstaller gets no time to finish");
    }

    [Fact]
    public async Task A_package_gone_at_once_counts_as_removed_after_one_look()
    {
        var asked = 0;
        var stays = await WingetSource.StaysListedAsync(
            _ => { asked++; return Task.FromResult(false); },
            TimeSpan.FromSeconds(30), Step, CancellationToken.None);

        Assert.False(stays);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_package_that_leaves_the_list_while_waiting_counts_as_removed()
    {
        var asked = 0;
        var stays = await WingetSource.StaysListedAsync(
            _ => Task.FromResult(++asked < 3),
            TimeSpan.FromSeconds(30), Step, CancellationToken.None);

        Assert.False(stays);
        Assert.Equal(3, asked);
    }
}
