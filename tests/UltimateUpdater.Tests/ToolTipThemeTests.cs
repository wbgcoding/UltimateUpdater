using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Services;
using Wpf.Ui.Appearance;

namespace UltimateUpdater.Tests;

/// <summary>
/// A window-local implicit ToolTip style without <c>BasedOn</c> replaces WPF-UI's themed one outright,
/// so the tooltip falls back to the system's light InfoBrush and shows up white in the dark theme. Walks
/// every window, resolves the ToolTip style that window would actually apply and checks the surface it
/// paints is dark with light text.
/// </summary>
[Collection("wpf")]
public class ToolTipThemeTests
{
    [Fact]
    public void Every_window_paints_its_tooltips_dark_in_the_dark_theme()
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(ApplicationTheme.Dark);
            try
            {
                var failures = new List<string>();

                foreach (var (name, window) in AccessibilityWalkTests.BuildWindows())
                {
                    var style = window.TryFindResource(typeof(ToolTip)) as Style;
                    var tip = new ToolTip { Content = "x", Style = style };

                    var background = (tip.Background as SolidColorBrush)?.Color;
                    var foreground = (tip.Foreground as SolidColorBrush)?.Color;

                    if (background is not { } bg || Luminance(bg) > 0.3)
                        failures.Add($"{name}: background {background?.ToString() ?? "none"}");
                    if (foreground is not { } fg || Luminance(fg) < 0.6)
                        failures.Add($"{name}: foreground {foreground?.ToString() ?? "none"}");

                    // Left open, these windows stay in Application.Windows and break later tests that
                    // count a window type. MainWindow stays: closing it ends the shared app.
                    if (window is not Views.MainWindow)
                        window.Close();
                }

                Assert.Empty(failures);
            }
            finally
            {
                WpfHarness.ApplyTheme(ApplicationTheme.Light);
            }
        });
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
