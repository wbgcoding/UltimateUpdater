using System.Windows.Documents;
using System.Windows.Media;
using UltimateUpdater.Services;
using UltimateUpdater.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Tests;

/// <summary>
/// Live companion to <see cref="ButtonStyleTests"/>: that class checks App.xaml's structure (the
/// property is set, the style derives correctly), which is the fast, order-independent check. This one
/// reads the ACTUAL resolved pressed-foreground colour a real button ends up with (the "buttons go
/// black" defect), a styling check alone is not enough to catch this. Every case applies its own theme
/// first, so it does not depend on run order.
/// Deliberately does NOT construct a `SymbolIcon`: an ambient FontSize-keyed resource left behind by
/// another test in a full suite run can resolve to an empty string, and `FontIcon`'s own constructor
/// throws reading it, passing in isolation but failing in the full run. Icon-Foreground inheritance is
/// covered separately and does not need re-proving here at the cost of suite-wide reliability.
/// `ApplicationThemeManager.Apply` below still goes through `WpfHarness`'s own "pack:" URI registration
/// (the same one `WindowLayoutTests` needs), which is environment-dependent and NOT guaranteed on every
/// machine. `build.bat` therefore treats this class the same as `WindowLayoutTests`: excluded
/// from the gating test step, run separately as warning-only.
/// </summary>
[Collection("wpf")]
public class LiveButtonContrastTests
{
    [Theory]
    [InlineData(ApplicationTheme.Dark, ControlAppearance.Secondary)]
    [InlineData(ApplicationTheme.Dark, ControlAppearance.Primary)]
    [InlineData(ApplicationTheme.Light, ControlAppearance.Secondary)]
    [InlineData(ApplicationTheme.Light, ControlAppearance.Primary)]
    public void A_pressed_button_stays_readable_against_its_own_fill(ApplicationTheme theme, ControlAppearance appearance)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var button = new Button { Appearance = appearance, Content = "x" };
            // kept alive for the length of this assertion - a collected window takes its inherited values with it
            var window = WpfHarness.HostForStyling(button);
            button.ApplyTemplate();

            var pressedForeground = ResolvedColor(button.GetValue(Button.PressedForegroundProperty));
            Assert.NotNull(pressedForeground);

            // A Secondary button's own fill is intentionally translucent (WPF-UI design: it sits on
            // whatever surface is behind it), so its raw Background is not what a label is actually read
            // against - composite it over a representative page background for the theme, the same way
            // it would look on a real window, rather than over nothing.
            var pageBackground = theme == ApplicationTheme.Dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xFA, 0xFA, 0xFA);
            var fill = CompositeOver(ResolvedColor(button.Background) ?? Colors.Transparent, pageBackground);

            Assert.True(
                AccentPalette.Contrast(pressedForeground!.Value, fill) >= 3.0,
                $"{theme}/{appearance}: pressed label {pressedForeground} on fill {fill} (composited over " +
                $"page background {pageBackground}) is only {AccentPalette.Contrast(pressedForeground.Value, fill):0.0}:1.");
        });
    }

    /// <summary>
    /// A disabled Primary button used to keep its on-accent label colour, meant for a saturated accent
    /// fill, while WPF-UI's own disabled trigger faded the FILL underneath it to a near-white grey: about
    /// 1.1:1 in the light theme, next to invisible. <see cref="ButtonLabelColour"/> now re-applies on
    /// IsEnabled changing too, switching a disabled Primary button's label to the same primary text
    /// colour every other disabled control already uses.
    /// </summary>
    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void A_disabled_primary_button_label_is_not_pinned_to_the_on_accent_colour(ApplicationTheme theme)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var button = new Button { Appearance = ControlAppearance.Primary, Content = "x" };
            ButtonLabelColour.SetKeep(button, true);
            var window = WpfHarness.HostForStyling(button);
            button.ApplyTemplate();

            button.IsEnabled = false;

            var disabledLabel = ResolvedColor(button.GetValue(TextElement.ForegroundProperty));
            var expected = ResolvedColor(System.Windows.Application.Current!.TryFindResource("TextFillColorPrimaryBrush"));
            Assert.NotNull(disabledLabel);
            Assert.NotNull(expected);
            Assert.Equal(expected, disabledLabel);
        });
    }

    /// <summary>
    /// The label colour follows an Appearance set after the Keep style landed, and IsEnabled turning
    /// back on, through the button's own binding and instance event rather than a
    /// DependencyPropertyDescriptor subscription (whose static tracker kept every button and its window
    /// alive for the life of the process).
    /// </summary>
    [Fact]
    public void The_label_colour_follows_a_later_appearance_change_and_re_enabling()
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(ApplicationTheme.Light);

            var button = new Button { Appearance = ControlAppearance.Secondary, Content = "x" };
            ButtonLabelColour.SetKeep(button, true);
            var window = WpfHarness.HostForStyling(button);
            button.ApplyTemplate();

            var app = System.Windows.Application.Current!;
            var primaryText = ResolvedColor(app.TryFindResource("TextFillColorPrimaryBrush"));
            var onAccent = ResolvedColor(app.TryFindResource("AccentButtonForeground"));
            Assert.NotEqual(primaryText, onAccent);

            Assert.Equal(primaryText, ResolvedColor(button.GetValue(TextElement.ForegroundProperty)));

            button.Appearance = ControlAppearance.Primary;
            Assert.Equal(onAccent, ResolvedColor(button.GetValue(TextElement.ForegroundProperty)));

            button.IsEnabled = false;
            Assert.Equal(primaryText, ResolvedColor(button.GetValue(TextElement.ForegroundProperty)));

            button.IsEnabled = true;
            Assert.Equal(onAccent, ResolvedColor(button.GetValue(TextElement.ForegroundProperty)));
        });
    }

    /// <summary>
    /// The rest of the accent-filled control family, beyond the button fix. WPF-UI's dark theme assumes
    /// Windows' accent is LIGHT and paints what sits ON it black: the knob of a switched-on toggle at
    /// #FF000000, the label of a pressed checked toggle at #80000000, the dot of a checked radio at
    /// #FF000000. Against this app's deepened accent that is a hole in the middle of a blue control.
    /// Asserts the exact colour rather than a contrast ratio, deliberately: black on WPF-UI's own pale
    /// blue scores 9:1 and would sail through a readability check while looking exactly like the defect
    /// this guards against. Read from the LIVE dictionary, reading the style alone has been unreliable
    /// here.
    /// </summary>
    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void Everything_sitting_on_the_accent_is_painted_in_the_on_accent_colour(ApplicationTheme theme)
    {
        string[] onAccentKeys =
        [
            "AccentButtonForeground",
            "AccentButtonForegroundPressed",
            "CheckBoxCheckGlyphForeground",
            "RadioButtonCheckGlyphFill",
            "ToggleButtonForegroundChecked",
            "ToggleButtonForegroundCheckedPointerOver",
            "ToggleButtonForegroundCheckedPressed",
            "ToggleSwitchKnobFillOn",
            "ToggleSwitchKnobFillOnPointerOver",
            "ToggleSwitchKnobFillOnPressed",
        ];

        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var accent = ApplicationAccentColorManager.SystemAccent;
            var expected = AccentPalette.For(accent).OnAccent;

            foreach (var key in onAccentKeys)
            {
                var actual = ResolvedColor(System.Windows.Application.Current!.TryFindResource(key));

                // A null here means the key no longer exists upstream, which would let this guard pass by
                // looking at nothing at all, the one failure mode a regression test must not have.
                Assert.True(actual is not null, $"{theme}: '{key}' is not a solid colour brush any more.");
                Assert.True(actual == expected, $"{theme}: {key} is {actual}, expected the on-accent {expected}.");
            }
        });
    }

    /// <summary>The cleanup group card's focus ring is a plain BorderBrush trigger (CleanupCardToggle,
    /// CleanupWindow.xaml) reading AccentTextFillColorPrimaryBrush, never proven visible against the
    /// card's own dark background, so read the two live and compare.</summary>
    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void The_cleanup_card_focus_ring_is_visible_against_the_card(ApplicationTheme theme)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var ring = ResolvedColor(System.Windows.Application.Current!.TryFindResource("AccentTextFillColorPrimaryBrush"));
            var rawCard = ResolvedColor(System.Windows.Application.Current!.TryFindResource("CardBackgroundFillColorDefaultBrush"));
            Assert.NotNull(ring);
            Assert.NotNull(rawCard);

            // The card fill is a translucent overlay (Fluent design: it tints whatever sits behind it),
            // so compare against how it actually looks composited over the page, same page background
            // the pressed-button check above uses.
            var pageBackground = theme == ApplicationTheme.Dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xFA, 0xFA, 0xFA);
            var card = CompositeOver(rawCard!.Value, pageBackground);

            Assert.True(
                AccentPalette.Contrast(ring!.Value, card) >= 3.0,
                $"{theme}: focus ring {ring} on card background {card} (raw {rawCard}, composited over " +
                $"page background {pageBackground}) is only {AccentPalette.Contrast(ring.Value, card):0.0}:1.");
        });
    }

    /// <summary>
    /// The factory reset button. WPF-UI's own Danger fill is a fixed #F44336 in both themes and its own
    /// ControlTemplate flips the CONTENT to near-black in the light theme regardless of a local
    /// Button.Foreground override (measured directly: setting Foreground="White" on the button itself
    /// still read back #E4000000 in the light theme once templated), and white-on-#F44336 is only 3.7:1
    /// in dark besides. Views/SettingsWindow.xaml works around both problems the same way: a fixed
    /// Background (the same saturated red Fluent's own light-theme critical colour uses, measured 5.7:1
    /// for white text against it) and the label as its OWN TextBlock carrying its own local Foreground,
    /// which no trigger on the button above it can override through inheritance. This constructs that
    /// exact shape and reads the label's actual resolved colour, not the button's.
    /// </summary>
    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void The_danger_buttons_text_is_readable_on_its_fill(ApplicationTheme theme)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var label = new System.Windows.Controls.TextBlock { Text = "x", Foreground = Brushes.White };
            var button = new Button
            {
                Appearance = ControlAppearance.Danger,
                Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
                Content = label,
            };
            var window = WpfHarness.HostForStyling(button);
            button.ApplyTemplate();

            var foreground = ResolvedColor(label.Foreground);
            Assert.NotNull(foreground);

            var pageBackground = theme == ApplicationTheme.Dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xFA, 0xFA, 0xFA);
            var fill = CompositeOver(ResolvedColor(button.Background) ?? Colors.Transparent, pageBackground);

            Assert.True(
                AccentPalette.Contrast(foreground!.Value, fill) >= 4.5,
                $"{theme}: danger button label {foreground} on fill {fill} (composited over page " +
                $"background {pageBackground}) is only {AccentPalette.Contrast(foreground.Value, fill):0.0}:1.");
        });
    }

    /// <summary>The factory reset card's description and icon switch to the critical foreground once the
    /// card reads as dangerous; read against the card background the same way the focus ring check above
    /// already does.</summary>
    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void The_reset_cards_critical_text_is_readable_on_the_card(ApplicationTheme theme)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var critical = ResolvedColor(System.Windows.Application.Current!.TryFindResource("SystemFillColorCriticalBrush"));
            var rawCard = ResolvedColor(System.Windows.Application.Current!.TryFindResource("CardBackgroundFillColorDefaultBrush"));
            Assert.NotNull(critical);
            Assert.NotNull(rawCard);

            var pageBackground = theme == ApplicationTheme.Dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xFA, 0xFA, 0xFA);
            var card = CompositeOver(rawCard!.Value, pageBackground);

            Assert.True(
                AccentPalette.Contrast(critical!.Value, card) >= 4.5,
                $"{theme}: critical text {critical} on card {card} (raw {rawCard}, composited over page " +
                $"background {pageBackground}) is only {AccentPalette.Contrast(critical.Value, card):0.0}:1.");
        });
    }

    private static Color? ResolvedColor(object? value) => value switch
    {
        SolidColorBrush scb => scb.Color,
        _ => null,
    };

    /// <summary>Standard alpha-over-opaque-base compositing, source-over.</summary>
    private static Color CompositeOver(Color source, Color baseColor)
    {
        var a = source.A / 255.0;
        byte Mix(byte s, byte b) => (byte)((s * a) + (b * (1 - a)));
        return Color.FromRgb(Mix(source.R, baseColor.R), Mix(source.G, baseColor.G), Mix(source.B, baseColor.B));
    }
}
