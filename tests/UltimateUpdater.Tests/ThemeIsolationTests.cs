using System.Windows.Media;
using UltimateUpdater.Services;
using Wpf.Ui.Appearance;

namespace UltimateUpdater.Tests;

/// <summary>
/// Regression test for the order-dependent leak <see cref="WpfHarness.ApplyTheme"/> exists to close:
/// <see cref="ThemeService.ApplyDarkPaletteOverrides"/> writes an OPAQUE dark card colour straight into
/// the one shared <c>Application.Resources</c> (see <see cref="LiveButtonContrastTests"/>'s own doc
/// comment), and nothing removed it again once a test called that method directly instead of going
/// through the theme service's own light/dark switch. A later light-theme test then read the previous
/// test's dark card colour and failed, only when the two ran in that order, never alone. This plants
/// exactly that leak and proves the harness helper cleans it up.
/// </summary>
[Collection("wpf")]
public class ThemeIsolationTests
{
    /// <summary>The exact opaque colour <c>ThemeService</c>'s own dark palette writes into
    /// <c>CardBackgroundFillColorDefaultBrush</c> (#FF13161B), measured directly from the failure this
    /// guards against, not re-derived, since the field it comes from is private to <c>ThemeService</c>.</summary>
    private static readonly Color LeakedDarkCard = Color.FromRgb(0x13, 0x16, 0x1B);

    [Fact]
    public void Applying_the_light_theme_clears_a_dark_override_a_previous_test_left_behind()
    {
        WpfHarness.OnUiThread(() =>
        {
            // Stands in for an earlier test that rendered the dark theme through ThemeService directly
            // (exactly what LiveButtonContrastTests and PressedPixelTests used to do) and left the shared
            // Application carrying its opaque dark surface colours.
            ThemeService.ApplyDarkPaletteOverrides();

            WpfHarness.ApplyTheme(ApplicationTheme.Light);

            var card = System.Windows.Application.Current!.TryFindResource("CardBackgroundFillColorDefaultBrush")
                as SolidColorBrush;
            Assert.NotNull(card);

            Assert.NotEqual(LeakedDarkCard, card!.Color);

            // The dark override is opaque (A=255); the Fluent light theme's own card brush is a
            // translucent overlay on the page behind it (every test that reads this key elsewhere in the
            // suite composites it over a page background for exactly that reason), so a fully opaque
            // result here would itself mean the leak, not just the wrong colour, survived.
            Assert.NotEqual(255, card.Color.A);
        });
    }

    [Fact]
    public void The_dark_palette_writes_the_zinc_page_and_text_colours_and_removes_them_again()
    {
        WpfHarness.OnUiThread(() =>
        {
            var resources = System.Windows.Application.Current!.Resources;
            try
            {
                ThemeService.ApplyDarkPaletteOverrides();

                Assert.Equal(Color.FromRgb(0x18, 0x18, 0x1B), ((SolidColorBrush)resources["ApplicationBackgroundBrush"]).Color);
                Assert.Equal(Color.FromRgb(0xF4, 0xF4, 0xF5), ((SolidColorBrush)resources["TextFillColorPrimaryBrush"]).Color);
            }
            finally
            {
                ThemeService.RemoveDarkPaletteOverrides();
            }

            // Removed means the lookup falls back to the Fluent theme's own entry, never the dark override.
            Assert.NotEqual(Color.FromRgb(0x18, 0x18, 0x1B), ((SolidColorBrush)resources["ApplicationBackgroundBrush"]).Color);
            Assert.NotEqual(Color.FromRgb(0xF4, 0xF4, 0xF5), ((SolidColorBrush)resources["TextFillColorPrimaryBrush"]).Color);
        });
    }

    /// <summary>
    /// High contrast and the dark palette share the page, card, control and text keys. The apply pass used
    /// to write the high-contrast brushes and then remove the dark palette afterwards, which deleted those
    /// shared high-contrast brushes again, so a high-contrast session showed the Fluent colours instead of
    /// the OS scheme. Driven through the same surface step the live apply pass uses, from both bases.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void High_contrast_surface_brushes_survive_the_dark_palette_step(bool dark)
    {
        WpfHarness.OnUiThread(() =>
        {
            var resources = System.Windows.Application.Current!.Resources;
            try
            {
                // The state a dark session leaves behind when high contrast is switched on.
                ThemeService.ApplySurfaceOverrides(highContrast: false, dark: true);

                ThemeService.ApplySurfaceOverrides(highContrast: true, dark: dark);

                Assert.Same(System.Windows.SystemColors.WindowBrush, resources["ApplicationBackgroundBrush"]);
                Assert.Same(System.Windows.SystemColors.WindowBrush, resources["CardBackgroundFillColorDefaultBrush"]);
                Assert.Same(System.Windows.SystemColors.WindowTextBrush, resources["TextFillColorPrimaryBrush"]);
                Assert.Same(System.Windows.SystemColors.WindowFrameBrush, resources["ControlStrokeColorDefaultBrush"]);
            }
            finally
            {
                WpfHarness.ApplyTheme(ApplicationTheme.Light);
            }
        });
    }
}
