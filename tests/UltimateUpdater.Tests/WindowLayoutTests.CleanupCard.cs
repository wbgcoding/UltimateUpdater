using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The list card is rounded, but the scrollbar's own square surface and its left hairline used to
    /// paint over the right-hand corners. Rendered at the window's minimum height so the list truly
    /// scrolls; the pixel one step in from each corner (inside the card's box, outside its rounded outline) must be the page colour.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Cleanup_window_list_card_keeps_its_rounded_corners(string themeName)
    {
        try
        {
            RenderResult? result = null;
            double scrollableHeight = 0;
            var failures = new List<string>();

            WpfHarness.OnUiThread(() =>
            {
                LocalizationService.Instance.Apply("de");
                ApplyTheme(themeName);

                var logging = new LoggingService();
                var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
                var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
                WpfHarness.WaitForCleanupCatalog(viewModel);

                long size = 96L * 1024 * 1024;
                var index = 0;
                foreach (var item in viewModel.Items)
                {
                    item.IsScanning = false;
                    item.Bytes = size / (index + 1);
                    item.Files = 12 * (index + 1);
                    index++;
                }

                var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
                var content = (FrameworkElement)window.Content;
                result = WpfHarness.RenderWindow(window, $"cleanup-card-corners-{themeName}", (int)window.MinWidth, (int)window.MinHeight);

                var card = FindByName<Border>(content, "CleanupTargetsCard")!;
                var scroll = FindFirstDescendant<ScrollViewer>(card)!;
                scrollableHeight = scroll.ScrollableHeight;

                var host = (FrameworkElement)VisualTreeHelper.GetParent(content)!;
                host.UpdateLayout();
                var width = (int)host.ActualWidth;
                var height = (int)host.ActualHeight;
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);

                Color PixelAt(double x, double y)
                {
                    var p = card.TransformToAncestor(host).Transform(new Point(x, y));
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect((int)Math.Floor(p.X), (int)Math.Floor(p.Y), 1, 1), pixel, 4, 0);
                    return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
                }

                var w = card.ActualWidth;
                var h = card.ActualHeight;
                var corners = new (string Name, double X, double Y, double RefX)[]
                {
                    ("top left", 1, 1, -6),
                    ("top right", w - 2, 1, w + 5),
                    ("bottom left", 1, h - 2, -6),
                    ("bottom right", w - 2, h - 2, w + 5),
                };
                foreach (var (name, x, y, refX) in corners)
                {
                    var corner = PixelAt(x, y);
                    var page = PixelAt(refX, y);
                    if (ColourDistance(corner, page) > 3)
                        failures.Add($"{name}: corner {corner} but page {page}");
                }
            });

            AssertRendered(result);
            Assert.True(scrollableHeight > 0, "the list has to scroll for the scrollbar to be on show");
            Assert.True(failures.Count == 0, $"{themeName}: the list card's corners are painted over: {string.Join("; ", failures)}");
        }
        finally
        {
            WpfHarness.OnUiThread(() => ApplyTheme("dark"));
        }
    }

    /// <summary>
    /// A category is a flat section of the list card, like a group of the main window's update table: no
    /// fill of its own (a second card fill on the card made the list look inverted against the main
    /// window), a hairline below it, and a header surface (hover, press) that runs square from edge to
    /// edge, open or collapsed. The header surface is painted red here on purpose, because the real hover
    /// colour is a faint tint a pixel check could not tell from the card.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Cleanup_window_category_is_a_flat_section_of_the_list_card(string themeName)
    {
        try
        {
            RenderResult? result = null;
            var failures = new List<string>();

            WpfHarness.OnUiThread(() =>
            {
                LocalizationService.Instance.Apply("de");
                ApplyTheme(themeName);

                var logging = new LoggingService();
                var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
                var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
                WpfHarness.WaitForCleanupCatalog(viewModel);

                foreach (var item in viewModel.Items)
                    item.IsScanning = false;

                var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
                target.Bytes = 340L * 1024 * 1024;
                target.Files = 812;
                target.SetEntries(
                    [
                        new CleanupEntry(@"C:\Temp\Spotify", "Spotify", 210L * 1024 * 1024, 640,
                            new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
                            new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache),
                    ],
                    omittedCount: 0, omittedBytes: 0);

                var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
                var content = (FrameworkElement)window.Content;
                result = WpfHarness.RenderWindow(window, $"cleanup-group-card-expanded-{themeName}", (int)window.Width, (int)window.Height);

                var cards = FindAllByName<Expander>(content, "GroupCard");
                var open = cards.First(c => c.IsExpanded);
                var shut = cards.First(c => !c.IsExpanded);
                var cardBorder = (Border)VisualTreeHelper.GetChild(open, 0);
                var hit = FindByName<Border>(open, "Hit")!;
                var shutHit = FindByName<Border>(shut, "Hit")!;

                if (hit.CornerRadius != new CornerRadius(0))
                    failures.Add($"expanded header radius is {hit.CornerRadius}, want 0");
                if (shutHit.CornerRadius != new CornerRadius(0))
                    failures.Add($"collapsed header radius is {shutHit.CornerRadius}, want 0");
                if (cardBorder.Background is not SolidColorBrush { Color.A: 0 })
                    failures.Add($"a category paints its own fill ({cardBorder.Background}) on the list card");
                if (cardBorder.BorderThickness != new Thickness(0, 0, 0, 1))
                    failures.Add($"a category's outline is {cardBorder.BorderThickness}, want a hairline below only");

                var host = (FrameworkElement)VisualTreeHelper.GetParent(content)!;

                // The header surface in a colour that cannot be mistaken for the card or the page.
                hit.Background = System.Windows.Media.Brushes.Red;
                host.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);

                Color PixelAt(FrameworkElement within, double x, double y)
                {
                    var p = within.TransformToAncestor(host).Transform(new Point(x, y));
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect((int)Math.Floor(p.X), (int)Math.Floor(p.Y), 1, 1), pixel, 4, 0);
                    return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
                }

                bool IsRed(Color c) => c.R > 200 && c.G < 80 && c.B < 80;

                // The header surface: square at all four corners, a full-width row.
                var hw = hit.ActualWidth;
                var hh = hit.ActualHeight;
                foreach (var (name, x, y) in new (string, double, double)[]
                         { ("top left", 2, 2), ("top right", hw - 3, 2), ("bottom left", 2, hh - 3), ("bottom right", hw - 3, hh - 3) })
                {
                    if (!IsRed(PixelAt(hit, x, y)))
                        failures.Add($"the header surface is notched at its {name} corner");
                }
            });

            AssertRendered(result);
            Assert.True(failures.Count == 0, $"{themeName}: {string.Join("; ", failures)}");
        }
        finally
        {
            WpfHarness.OnUiThread(() => ApplyTheme("dark"));
        }
    }
}
