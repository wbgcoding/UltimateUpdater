using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// NVIDIA's Tier A/C hybrid source has no per-driver changelog feed, so EnrichAsync just reuses the same
/// vendor download page already used as the row's OpenTarget, trivial, synchronous, no network call.
/// This test only proves that reuse, not any I/O. The link only AMD/Intel/Dell/Lenovo driver page sources
/// that used to be covered here were removed entirely: a row that can only open a vendor web page is not
/// an update.
/// </summary>
public class DriverSourcesChangelogTests
{
    private static UpdateItem Item(string id) => new()
    {
        Id = id, SourceId = id, Category = UpdateCategory.SystemAndDrivers, Name = id,
    };

    [Fact]
    public async Task Nvidia_reuses_its_own_drivers_page()
    {
        var source = new NvidiaDriverSource(new LoggingService(), new HttpClient());
        var enrichment = await source.EnrichAsync(Item("nvidia-graphics-driver"), CancellationToken.None);

        Assert.Equal("https://www.nvidia.com/download/index.aspx", enrichment?.ChangelogUrl);
    }
}
