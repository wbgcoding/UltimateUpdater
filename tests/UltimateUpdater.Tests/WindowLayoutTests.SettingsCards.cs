using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    private static (SettingsWindow Window, FrameworkElement Root) SettingsWindowForCards()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging);
        LocalizationService.Instance.Apply("de");

        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
        var window = new SettingsWindow(viewModel);
        foreach (var name in new[] { "SourcesExpander", "AboutExpander", "NetworkTargetsExpander" })
            ((Expander)window.FindName(name)!).IsExpanded = true;
        return (window, (FrameworkElement)window.Content);
    }

    private static T? AncestorOf<T>(DependencyObject start, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T found && (match is null || match(found)))
                return found;
        }

        return null;
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var inner in VisualDescendants<T>(child))
                yield return inner;
        }
    }

    /// <summary>
    /// The hairlines between the rows of a settings card use the main table's divider tone and run from
    /// the card's left edge to its right edge; one inside an expander body runs the width of that body.
    /// </summary>
    [Fact]
    public void Settings_separators_use_the_divider_tone_and_span_the_card()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, root) = SettingsWindowForCards();
            AssertRendered(WpfHarness.RenderWindow(window, "settings-separators-de", (int)window.Width, 4000));

            var separatorStyle = (Style)window.FindResource("CardSeparator");
            var cardStyle = (Style)window.FindResource("Card");
            var divider = ((SolidColorBrush)window.FindResource("DividerStrokeColorDefaultBrush")).Color;

            var separators = VisualDescendants<Border>(root)
                .Where(b => ReferenceEquals(b.Style, separatorStyle) && b.ActualWidth > 0)
                .ToList();
            Assert.True(separators.Count >= 10, $"expected the settings page to show its separators, found {separators.Count}");

            foreach (var separator in separators)
            {
                Assert.Equal(1, separator.ActualHeight);
                Assert.Equal(divider, ((SolidColorBrush)separator.Background).Color);

                var card = AncestorOf<Border>(separator, b => ReferenceEquals(b.Style, cardStyle))!;
                var expander = AncestorOf<Expander>(separator, _ => true);
                var reference = expander ?? (FrameworkElement)card;
                var left = separator.TransformToAncestor(card).Transform(new Point(0, 0)).X;
                var refLeft = reference.TransformToAncestor(card).Transform(new Point(0, 0)).X;

                // The card keeps a one pixel outline inside its own bounds; the line may end on it.
                Assert.InRange(left, refLeft - 0.5, refLeft + (expander is null ? 1.5 : 0.5));
                Assert.InRange(left + separator.ActualWidth, refLeft + reference.ActualWidth - (expander is null ? 1.5 : 0.5),
                    refLeft + reference.ActualWidth + 0.5);
            }
        });
    }

    /// <summary>
    /// A collapsible settings card draws no frame of its own inside the rounded card: next to the
    /// expander's edges the pixels are the card's background.
    /// </summary>
    [Fact]
    public void Expanded_settings_cards_show_no_inner_frame()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, root) = SettingsWindowForCards();
            AssertRendered(WpfHarness.RenderWindow(window, "settings-expanders-de", (int)window.Width, 4000));

            var host = (FrameworkElement)VisualTreeHelper.GetParent(root)!;
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);

            Color PixelAt(FrameworkElement element, double x, double y)
            {
                var p = element.TransformToAncestor(host).Transform(new Point(x, y));
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)p.X, (int)p.Y, 1, 1), pixel, 4, 0);
                return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
            }

            foreach (var name in new[] { "AboutExpander", "NetworkTargetsExpander" })
            {
                var expander = (Expander)window.FindName(name)!;
                Assert.True(expander.IsExpanded && expander.ActualHeight > 100, $"{name} was not laid out expanded");

                // Plain card surface: inside the card's padding, left of the expander.
                var surface = PixelAt(expander, -8, expander.ActualHeight - 10);
                foreach (var (x, y, what) in new[]
                {
                    (0.0, expander.ActualHeight - 10, "the left edge of the body"),
                    (expander.ActualWidth - 1, expander.ActualHeight - 10, "the right edge of the body"),
                    (10.0, expander.ActualHeight - 1, "the bottom edge of the body"),
                })
                {
                    var pixel = PixelAt(expander, x, y);
                    Assert.True(ColourDistance(pixel, surface) <= 2,
                        $"{name}: {what} is {pixel} but the card surface is {surface}.");
                }

            }
        });
    }
}
