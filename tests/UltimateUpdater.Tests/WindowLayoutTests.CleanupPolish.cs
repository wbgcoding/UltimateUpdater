using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using Wpf.Ui.Controls;
using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    private static CleanupViewModel NewMeasuredCleanupViewModel()
    {
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

        return viewModel;
    }

    private static Color ColourOf(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : default;

    /// <summary>
    /// The status icon beside the headline is drawn in the same blue as the ring's arc. The icon is a font
    /// glyph, so the probe looks for the pixel in its box that comes closest to the ring colour: a glyph's
    /// body is fully covered somewhere, and it would be far from the ring colour in the lighter accent text
    /// brush the icon used to carry.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Cleanup_window_status_icon_is_the_ring_colour(string themeName)
    {
        try
        {
            RenderResult? result = null;
            var closest = int.MaxValue;
            Color ring = default;
            Color iconBrush = default;

            WpfHarness.OnUiThread(() =>
            {
                LocalizationService.Instance.Apply("en");
                ApplyTheme(themeName);

                var viewModel = NewMeasuredCleanupViewModel();
                var window = new Views.CleanupWindow(viewModel) { DisplayPercent = 47 };
                var content = (FrameworkElement)window.Content;

                var areas = viewModel.Items.Take(10).ToList();
                viewModel.StepItems = areas;
                var running = areas[3];
                running.IsCleaning = true;
                running.Percent = 40;
                viewModel.CurrentItemText = running.Title;
                viewModel.CurrentGroupKey = CleanupGroups.For(running.Target);
                viewModel.CleanPercent = 47;
                viewModel.RefreshAreaProgress();
                viewModel.Phase = CleanupPhase.Cleaning;

                result = WpfHarness.RenderWindow(window, $"cleanup-status-icon-{themeName}", (int)window.Width, (int)window.Height);

                var fill = FindByName<System.Windows.Shapes.Path>(content, "RingFill")!;
                var icon = FindByName<SymbolIcon>(content, "CleanupStatusIcon")!;
                ring = ColourOf(fill.Stroke);
                iconBrush = ColourOf(icon.Foreground);

                var host = (FrameworkElement)VisualTreeHelper.GetParent(content)!;
                host.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);

                var origin = icon.TransformToAncestor(host).Transform(new Point(0, 0));
                var left = (int)Math.Floor(origin.X);
                var top = (int)Math.Floor(origin.Y);
                var width = (int)Math.Ceiling(icon.ActualWidth);
                var height = (int)Math.Ceiling(icon.ActualHeight);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(new Int32Rect(left, top, width, height), pixels, width * 4, 0);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var distance = Math.Abs(pixels[i + 2] - ring.R) + Math.Abs(pixels[i + 1] - ring.G) + Math.Abs(pixels[i] - ring.B);
                    closest = Math.Min(closest, distance);
                }
            });

            AssertRendered(result);
            Assert.Equal(ring, iconBrush);
            Assert.True(closest <= 6, $"no pixel of the status icon matches the ring colour (closest distance {closest})");
        }
        finally
        {
            WpfHarness.OnUiThread(() => ApplyTheme("dark"));
        }
    }
}
