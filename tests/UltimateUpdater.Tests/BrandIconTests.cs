using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The bundled marks are matched from an explicit table, never by similarity: the wrong company's
/// logo on a row is worse than no logo. These cover both directions, the matches that must work,
/// and the rows that must stay without one.
/// </summary>
public class BrandIconTests
{
    [Theory]
    [InlineData("Mozilla Firefox", "winget", "Mozilla.Firefox")]   // vendor-prefixed name and a winget id
    [InlineData("Firefox 129.0.1", "winget", "Mozilla.Firefox")]   // a trailing version must not defeat it
    [InlineData("eslint", "npm", "eslint")]                        // a package with no application on disk
    [InlineData("Node.js", "winget", "OpenJS.NodeJS")]             // punctuation in the name
    [InlineData("7-Zip", "winget", "7zip.7zip")]
    [InlineData("VLC media player", "winget", "VideoLAN.VLC")]
    [InlineData("Steam", "steam", "steam")]
    public void A_known_product_gets_its_mark(string name, string sourceId, string itemId) =>
        Assert.NotNull(BrandIcons.Resolve(name, sourceId, itemId));

    /// <summary>
    /// Windows updates are the reason the letter tile was replaced by a category symbol in the first
    /// place, a cumulative update showed up as the letter "2". Nothing may match them either.
    /// </summary>
    [Theory]
    [InlineData("2024-08 Cumulative Update for Windows 11 (KB5041585)", "windowsupdate", "KB5041585")]
    [InlineData("Some Internal Line-Of-Business Tool", "chocolatey", "acme-internal")]
    public void An_unknown_product_gets_no_mark(string name, string sourceId, string itemId) =>
        Assert.Null(BrandIcons.Resolve(name, sourceId, itemId));

    /// <summary>
    /// Marks their owners asked to have removed from the upstream set are not bundled, and must not
    /// quietly resolve to something else that happens to share a word.
    /// </summary>
    [Fact]
    public void A_mark_that_is_not_bundled_stays_absent()
    {
        Assert.Null(BrandIcons.Resolve("Microsoft Visual Studio Code", "winget", "Microsoft.VisualStudioCode"));
        Assert.Null(BrandIcons.Resolve("Microsoft Edge", "winget", "Microsoft.Edge"));
    }

    /// <summary>A brand mark replaces the letter tile, and the real icon still wins over both.</summary>
    [Fact]
    public void A_brand_mark_takes_the_place_of_the_letter()
    {
        var branded = new UpdateRowViewModel(Item("Mozilla Firefox", "winget", "Mozilla.Firefox"));
        var plain = new UpdateRowViewModel(Item("Acme Internal Tool", "chocolatey", "acme-internal"));

        Assert.True(branded.UsesBrandTile);
        Assert.False(branded.UsesLetterTile);
        Assert.False(branded.UsesGlyphTile);

        Assert.False(plain.UsesBrandTile);
        Assert.True(plain.UsesLetterTile);
    }

    private static UpdateItem Item(string name, string sourceId, string id) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.Software,
        Name = name,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
    };
}
