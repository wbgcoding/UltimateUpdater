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
}
