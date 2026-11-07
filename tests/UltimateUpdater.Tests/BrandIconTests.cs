using System.Windows.Media;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

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
    [InlineData("Rustup: the Rust toolchain installer", "winget", "Rustlang.Rustup")]   // the tool shows the language mark
    [InlineData("uv", "winget", "astral-sh.uv")]
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

    /// <summary>A pip library must not wear the letter tile either, and no installed program's icon is looked for.</summary>
    [Theory]
    [InlineData("typer", "pip")]
    [InlineData("google-auth", "pip")]
    [InlineData("left-pad", "npm")]
    public void A_library_row_gets_its_ecosystem_mark_not_a_letter(string name, string sourceId)
    {
        var row = new UpdateRowViewModel(Item(name, sourceId, name));

        Assert.True(row.UsesBrandTile);
        Assert.False(row.UsesLetterTile);
        Assert.True(BrandIcons.IsLibrarySource(sourceId));
    }

    [Theory]
    [InlineData("chocolatey")]
    [InlineData("winget")]
    [InlineData("uv")]
    [InlineData("rustup")]
    public void Application_sources_are_not_library_sources(string sourceId) =>
        Assert.False(BrandIcons.IsLibrarySource(sourceId));

    /// <summary>The generated tables must stay in step: a mark without its colour would render unpainted.</summary>
    [Fact]
    public void Every_bundled_mark_has_a_well_formed_brand_colour()
    {
        Assert.Equal(BrandIconData.Paths.Keys.Order(), BrandIconData.Colors.Keys.Order());
        Assert.All(BrandIconData.Colors.Values, hex => Assert.Matches("^#[0-9A-F]{6}$", hex));
    }

    [Fact]
    public void A_mark_comes_with_its_own_brand_colour()
    {
        var mark = BrandIcons.Resolve("Python 3.12", "winget", "Python.Python.3.12");

        Assert.NotNull(mark);
        Assert.Equal(Color.FromRgb(0x37, 0x76, 0xAB), mark.Color);
    }

    [Fact]
    public void A_pale_brand_colour_is_deepened_on_a_light_tile_and_keeps_its_hue()
    {
        var orange = Color.FromRgb(0xFF, 0x98, 0x00);
        var lightTile = Colors.White;

        var drawn = BrandMark.LegibleOn(orange, lightTile);

        Assert.NotNull(drawn);
        Assert.True(AccentPalette.Contrast(drawn.Value, lightTile) >= BrandMark.MinimumContrast);
        Assert.True(drawn.Value.R > drawn.Value.G && drawn.Value.G > drawn.Value.B, "still an orange, not a grey");
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0x20, 0x20, 0x20)]   // black on a dark tile
    [InlineData(0xFF, 0xFF, 0xFF, 0xF5, 0xF5, 0xF5)]   // white on a light tile
    public void A_colour_that_cannot_be_made_legible_falls_back_to_the_text_colour(
        byte r, byte g, byte b, byte tileR, byte tileG, byte tileB) =>
        Assert.Null(BrandMark.LegibleOn(Color.FromRgb(r, g, b), Color.FromRgb(tileR, tileG, tileB)));

    /// <summary>
    /// A black mark must not vanish on the dark theme, and the paint must follow a live theme switch:
    /// the row only re-reads it when told the theme changed.
    /// </summary>
    [Fact]
    public void A_black_mark_follows_a_live_theme_switch()
    {
        WpfHarness.OnUiThread(() =>
        {
            var row = new UpdateRowViewModel(Item("Rust", "winget", "Rustlang.Rustup"));
            var changed = new List<string?>();
            row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            WpfHarness.ApplyTheme(ApplicationTheme.Dark);
            row.RefreshThemeBrushes();
            var inDark = ((SolidColorBrush)row.BrandBrush).Color;
            var darkText = ((SolidColorBrush)System.Windows.Application.Current.FindResource("TextFillColorPrimaryBrush")).Color;
            Assert.Equal(darkText, inDark);

            changed.Clear();
            WpfHarness.ApplyTheme(ApplicationTheme.Light);
            row.RefreshThemeBrushes();
            var inLight = ((SolidColorBrush)row.BrandBrush).Color;

            Assert.Contains(nameof(UpdateRowViewModel.BrandBrush), changed);
            Assert.Contains(nameof(UpdateRowViewModel.TileBackground), changed);
            Assert.NotEqual(inDark, inLight);
            Assert.True(AccentPalette.Contrast(inLight, Colors.White) >= BrandMark.MinimumContrast);

            WpfHarness.ApplyTheme(ApplicationTheme.Dark);
        });
    }

    [Fact]
    public void A_coloured_mark_keeps_its_brand_colour_in_the_dark_theme()
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(ApplicationTheme.Dark);
            var row = new UpdateRowViewModel(Item("Firefox", "winget", "Mozilla.Firefox"));

            Assert.Equal(Color.FromRgb(0xFF, 0x71, 0x39), ((SolidColorBrush)row.BrandBrush).Color);
        });
    }

    /// <summary>
    /// The letter and category-glyph tiles are washed with the accent and carry accent-coloured text: the
    /// letter must still read (4.5:1) on that wash in both themes, and so must a brand mark on its own.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_letter_on_its_accent_tile_stays_readable(bool dark)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
            var row = new UpdateRowViewModel(Item("Acme Internal Tool", "chocolatey", "acme-internal"));

            Color Resource(string key) => ((SolidColorBrush)System.Windows.Application.Current.FindResource(key)).Color;

            var page = Resource("ApplicationBackgroundBrush") is { A: 255 } opaque ? opaque : Colors.White;
            var surface = BrandMark.Composite(Resource("CardBackgroundFillColorDefaultBrush"), page);
            var tile = BrandMark.Composite(((SolidColorBrush)row.TileBackground).Color, surface);
            var letter = Resource("AccentTextFillColorPrimaryBrush");

            Assert.True(AccentPalette.Contrast(letter, tile) >= 4.5,
                $"letter {letter} on tile {tile} is only {AccentPalette.Contrast(letter, tile):0.0}:1");

            WpfHarness.ApplyTheme(ApplicationTheme.Dark);
        });
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
