using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// WindowsUpdateSource.EnrichAsync makes no COM call of its own, Description/MoreInfoUrls are captured
/// into Meta during the scan (Search/TryMapUpdate), so EnrichAsync only ever reads a dictionary. That
/// keeps these tests free of any real WUA session, exactly like every other test in this suite never
/// exercises the live COM search.
/// </summary>
public class WindowsUpdateSourceChangelogTests
{
    private static UpdateItem Item(IReadOnlyDictionary<string, string> meta) => new()
    {
        Id = "kb1:1", SourceId = "windowsupdate", Category = UpdateCategory.SystemAndDrivers,
        Name = "Test Update", Meta = meta,
    };

    [Fact]
    public async Task EnrichAsync_returns_null_when_neither_description_nor_url_was_captured()
    {
        var source = new WindowsUpdateSource(new LoggingService());
        var enrichment = await source.EnrichAsync(Item(new Dictionary<string, string>()), CancellationToken.None);

        Assert.Null(enrichment);
    }

    [Fact]
    public async Task EnrichAsync_strips_the_description_and_carries_the_more_info_url()
    {
        var source = new WindowsUpdateSource(new LoggingService());
        var meta = new Dictionary<string, string>
        {
            ["description"] = "**Fixes** a `bug`.",
            ["moreInfoUrl"] = "https://support.microsoft.com/kb1",
        };

        var enrichment = await source.EnrichAsync(Item(meta), CancellationToken.None);

        Assert.NotNull(enrichment);
        Assert.Equal("https://support.microsoft.com/kb1", enrichment!.Value.ChangelogUrl);
        Assert.Equal(ChangelogFormatter.Strip("**Fixes** a `bug`."), enrichment.Value.ChangelogBody);
    }

    [Fact]
    public async Task EnrichAsync_carries_the_url_alone_when_no_description_was_captured()
    {
        var source = new WindowsUpdateSource(new LoggingService());
        var meta = new Dictionary<string, string> { ["moreInfoUrl"] = "https://support.microsoft.com/kb2" };

        var enrichment = await source.EnrichAsync(Item(meta), CancellationToken.None);

        Assert.NotNull(enrichment);
        Assert.Equal("https://support.microsoft.com/kb2", enrichment!.Value.ChangelogUrl);
        Assert.Null(enrichment.Value.ChangelogBody);
    }
}
