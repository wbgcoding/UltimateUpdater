using UltimateUpdater.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Tests;

/// <summary>
/// Walks every text-on-surface brush-key pair this app actually renders somewhere (the catalogue below was
/// built from a grep of every Foreground/Background/BorderBrush pairing across Views/*.xaml, not invented)
/// and reads the two brushes' LIVE resolved colours the same way <see cref="LiveButtonContrastTests"/>
/// already does for the accent-filled control family, rather than reasoning about which dictionary wins.
/// 4.5:1 is the floor for ordinary text; 3.0:1 for text 24px or larger and for focus rings/control borders,
/// matching WCAG's own large-text and non-text-contrast floors.
///
/// Light and dark run for real, through <see cref="ThemeService.ApplyAccentContrast"/> exactly as the app
/// applies them. High contrast cannot be turned on from a test: <see cref="System.Windows.SystemParameters.HighContrast"/>
/// is read-only and belongs to Windows, not to this process. The third case instead loads
/// <see cref="ThemeService.ApplyHighContrastSurfaceOverrides"/> directly, the exact method the theme
/// service itself calls once <c>SystemParameters.HighContrast</c> is true, proving the WIRING is right
/// (every one of these keys really does resolve to a <c>SystemColors</c> brush, and whatever this machine's
/// live desktop colours happen to be still clears the floor) without proving the real thing: on an ordinary
/// dev/CI machine this exercises the CURRENT desktop's everyday colours, not a genuine Windows high-contrast
/// scheme, which is why this is not considered done on this test alone; the real thing still needs a
/// human to turn Windows high contrast on and look.
/// </summary>
[Collection("wpf")]
public class ContrastSweepTests
{
    private sealed record Pair(string Foreground, string Background, double MinRatio, string Description);

    /// <summary>Every pair actually rendered somewhere in Views/*.xaml, grouped by the surface it sits on.
    /// AccentTextFillColorPrimaryBrush-on-card (the focus ring) is deliberately not repeated here:
    /// <see cref="LiveButtonContrastTests.The_cleanup_card_focus_ring_is_visible_against_the_card"/> already
    /// covers exactly that pair permanently.</summary>
    private static readonly Pair[] Pairs =
    [
        new("TextFillColorPrimaryBrush", "ApplicationBackgroundBrush", 4.5, "primary text on the window"),
        new("TextFillColorPrimaryBrush", "CardBackgroundFillColorDefaultBrush", 4.5, "primary text on a card"),
        new("TextFillColorSecondaryBrush", "ApplicationBackgroundBrush", 4.5, "secondary text on the window"),
        new("TextFillColorSecondaryBrush", "CardBackgroundFillColorDefaultBrush", 4.5, "secondary text on a card"),
        new("TextFillColorTertiaryBrush", "ApplicationBackgroundBrush", 4.5, "tertiary text on the window"),
        new("TextFillColorTertiaryBrush", "CardBackgroundFillColorDefaultBrush", 4.5, "tertiary text on a card"),
        new("TextOnAccentFillColorPrimaryBrush", "AccentFillColorDefaultBrush", 4.5, "text on an accent-filled badge"),
        new("SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", 4.5, "critical text on its tinted background"),
        new("SystemFillColorCriticalBrush", "CardBackgroundFillColorDefaultBrush", 4.5, "critical text on a card"),
        new("SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush", 4.5, "caution text on its tinted background"),
        new("SystemFillColorCautionBrush", "CardBackgroundFillColorDefaultBrush", 4.5, "caution text on a card"),
        new("SystemFillColorSuccessBrush", "CardBackgroundFillColorDefaultBrush", 4.5, "success text on a card"),
        new("CardStrokeColorDefaultBrush", "CardBackgroundFillColorDefaultBrush", 3.0, "card border against the card"),
        new("CardStrokeColorDefaultBrush", "ApplicationBackgroundBrush", 3.0, "card border against the window"),
        new("ControlStrokeColorDefaultBrush", "ControlFillColorDefaultBrush", 3.0, "control border against its own fill"),
    ];

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void Every_pair_clears_its_floor_in_the_named_theme(ApplicationTheme theme)
    {
        WpfHarness.OnUiThread(() =>
        {
            // WpfHarness.ApplyTheme covers the dark theme's own third override step (ThemeService.ApplyCore
            // applies it the same way, keyed off the resolved theme, not off a string) and clears whatever
            // a PREVIOUS theory case (or another test entirely, sharing WpfHarness's one Application for
            // the whole run) left behind in Application.Resources.
            WpfHarness.ApplyTheme(theme);

            AssertAllPairsClearTheirFloor($"{theme}");
        });
    }

    [Fact]
    public void Every_pair_clears_its_floor_in_the_high_contrast_dictionary()
    {
        WpfHarness.OnUiThread(() =>
        {
            // A representative base to start from, same as any other run - ApplyHighContrastSurfaceOverrides
            // below is what actually matters, it overwrites every key this sweep reads.
            WpfHarness.ApplyTheme(ApplicationTheme.Light);
            ThemeService.ApplyHighContrastSurfaceOverrides();
            ThemeService.ApplyAccentContrast(System.Windows.SystemColors.HighlightColor);

            AssertAllPairsClearTheirFloor("high contrast dictionary");
        });
    }

    private static void AssertAllPairsClearTheirFloor(string caseName)
    {
        var violations = new List<string>();

        foreach (var pair in Pairs)
        {
            var foreground = ResolvedColor(pair.Foreground);
            var background = ResolvedColor(pair.Background);

            if (foreground is null || background is null)
            {
                violations.Add($"{caseName}: '{(foreground is null ? pair.Foreground : pair.Background)}' " +
                    "is not a solid colour brush any more.");
                continue;
            }

            var ratio = AccentPalette.Contrast(foreground.Value, background.Value);
            if (ratio < pair.MinRatio)
            {
                violations.Add($"{caseName}: {pair.Description} ({pair.Foreground} on {pair.Background}) " +
                    $"is only {ratio:0.00}:1, needs {pair.MinRatio:0.0}:1.");
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private static Color? ResolvedColor(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) switch
        {
            System.Windows.Media.SolidColorBrush scb => scb.Color,
            _ => null,
        };
}
