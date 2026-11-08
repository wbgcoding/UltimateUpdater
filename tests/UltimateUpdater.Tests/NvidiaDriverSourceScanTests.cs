using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class NvidiaDriverSourceScanTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("offline");
        }
    }

    private static async Task<List<UpdateItem>> Scan(string? wmiVersion, CountingHandler handler)
    {
        var source = new NvidiaDriverSource(
            new LoggingService(),
            new HttpClient(handler),
            () => [new GpuInfo(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", wmiVersion)]);

        var rows = new List<UpdateItem>();
        await foreach (var row in source.ScanAsync(CancellationToken.None))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task A_driver_version_that_cannot_be_decoded_yields_no_row_and_no_lookup()
    {
        var handler = new CountingHandler();

        var rows = await Scan("12", handler);

        Assert.Empty(rows);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_readable_driver_version_still_reaches_the_lookup()
    {
        var handler = new CountingHandler();

        var rows = await Scan("32.0.15.6094", handler);

        Assert.True(handler.Calls > 0);
        Assert.Single(rows);
        Assert.Equal(SourceTier.C, rows[0].Tier);
    }
}
