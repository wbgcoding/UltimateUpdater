using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace UltimateUpdater.Tests;

/// <summary>
/// "Everything goes black when pressed" survived four fixes that each compared RESOURCES, so this one
/// compares PIXELS: every accent-carrying control is rendered at rest and again in its pressed state, and
/// the pressed image may not contain near-black pixels the resting one did not. That is the only check
/// that would have caught the real cause, WPF-UI declares the pressed label, tick, dot and knob as black
/// inside its own theme dictionary, where a StaticResource lookup finds them regardless of what the
/// application-level override says.
/// Renders through the same <see cref="WpfHarness"/> as the layout snapshots, so it carries the same
/// environment dependency and build.bat runs it in the warning-only step rather than as a gate.
/// </summary>
[Collection("wpf")]
public class PressedPixelTests
{
    /// <summary>Anything darker than this counts as "black", deep enough to exclude ordinary dark text
    /// on a light surface, which is what the resting baseline is there to subtract anyway.</summary>
    private const double BlackLuminance = 0.15;

    public static TheoryData<ApplicationTheme, string> Cases()
    {
        var data = new TheoryData<ApplicationTheme, string>();
        foreach (var theme in new[] { ApplicationTheme.Dark, ApplicationTheme.Light })
        {
            foreach (var control in new[]
                     {
                         "button-primary", "button-secondary", "checkbox", "togglebutton",
                         "toggleswitch", "radiobutton",
                     })
            {
                data.Add(theme, control);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void No_control_turns_black_when_it_is_pressed(ApplicationTheme theme, string control)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var atRest = BlackPixels(control, pressed: false);
            var whenPressed = BlackPixels(control, pressed: true);

            Assert.True(
                whenPressed <= atRest,
                $"{theme}/{control}: {whenPressed} near-black pixels while pressed against {atRest} at rest, "
                + "the pressed state is painting something black again.");
        });
    }

    private static int BlackPixels(string control, bool pressed)
    {
        var element = Create(control);
        var host = new Border
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            Child = element,
        };

        // Kept alive for the render: a collected window takes its inherited values with it.
        var window = WpfHarness.HostForStyling(host);
        element.ApplyTemplate();
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();

        if (element is ToggleButton toggle)
            toggle.IsChecked = true;

        if (pressed)
            GoToPressed(element);

        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        GC.KeepAlive(window);
        return CountDark(bitmap);
    }

    /// <summary>
    /// These are WPF styles: they react to <see cref="ButtonBase.IsPressed"/> through triggers, not to a
    /// WinUI-style visual state, and that property is read-only for everyone but the mouse. Setting it
    /// through its own property key is what a real press does internally. IsMouseOver goes with it, the
    /// pressed look sits behind a MULTI-trigger that wants both, and setting only the one changed no pixel
    /// at all, which would have made this whole test photograph the resting state twice.
    /// </summary>
    private static void GoToPressed(FrameworkElement element)
    {
        Assert.True(element is ButtonBase, $"{element.GetType().Name} is not a ButtonBase and has no pressed state.");

        element.SetValue(ReadOnlyKey(typeof(ButtonBase), "IsPressedPropertyKey"), true);
        element.SetValue(ReadOnlyKey(typeof(UIElement), "IsMouseOverPropertyKey"), true);
        element.UpdateLayout();
    }

    private static DependencyPropertyKey ReadOnlyKey(Type owner, string field)
    {
        var key = owner
            .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as DependencyPropertyKey;

        Assert.True(key is not null, $"{owner.Name}.{field} is gone, this test would prove nothing.");
        return key!;
    }

    private static FrameworkElement Create(string control) => control switch
    {
        "button-primary" => new Button { Appearance = ControlAppearance.Primary, Content = "Install" },
        "button-secondary" => new Button { Appearance = ControlAppearance.Secondary, Content = "Cancel" },
        "checkbox" => new CheckBox { Content = "Selected", IsChecked = true },
        "togglebutton" => new ToggleButton { Content = "Filter", IsChecked = true },
        "toggleswitch" => new ToggleSwitch { IsChecked = true },
        "radiobutton" => new RadioButton { Content = "Interval", IsChecked = true },
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "unknown control"),
    };

    private static int CountDark(RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var dark = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Pbgra32, and a fully transparent pixel is not painted at all.
            if (pixels[i + 3] < 128)
                continue;

            var luminance = ((0.2126 * pixels[i + 2]) + (0.7152 * pixels[i + 1]) + (0.0722 * pixels[i])) / 255.0;
            if (luminance < BlackLuminance)
                dark++;
        }

        return dark;
    }
}
