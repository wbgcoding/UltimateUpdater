using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    private static byte[] RenderPixels(FrameworkElement host, int width, int height)
    {
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static double ColourDistance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    /// <summary>
    /// The fill of every hand-drawn bar is clipped to the track's pill, so the corners of the track and
    /// the rim right above and below the fill's rounded end stay the page colour at any value, including
    /// the narrow ones where a plain two-Border bar showed a square block.
    /// </summary>
    [Theory]
    [InlineData("dark", 3)]
    [InlineData("dark", 50)]
    [InlineData("dark", 97)]
    [InlineData("light", 3)]
    [InlineData("light", 50)]
    [InlineData("light", 97)]
    public void The_pill_bar_fill_is_rounded_at_its_end_and_at_the_track_corners(string theme, int percent)
    {
        WpfHarness.OnUiThread(() =>
        {
            ApplyTheme(theme);
            try
            {
                const int Pad = 6, W = 200, H = 6;
                var page = theme == "dark" ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
                var fillColour = theme == "dark" ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);

                var bar = new PillProgressBar
                {
                    Width = W,
                    Height = H,
                    Value = percent,
                    FillBrush = new SolidColorBrush(fillColour),
                    TrackBrush = new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xD8, 0xD8, 0xD8)),
                };
                var host = new Border
                {
                    Width = W + 2 * Pad,
                    Height = H + 2 * Pad,
                    Background = new SolidColorBrush(page),
                    Child = bar,
                    Padding = new Thickness(Pad),
                };
                var pixels = RenderPixels(host, W + 2 * Pad, H + 2 * Pad);
                var stride = (W + 2 * Pad) * 4;

                Color At(int x, int y)
                {
                    var i = (y + Pad) * stride + (x + Pad) * 4;
                    return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                }

                // "Is not fill" = clearly nearer to the page than to the fill colour.
                void AssertNotFill(int x, int y, string what)
                {
                    var c = At(x, y);
                    Assert.True(
                        ColourDistance(c, page) < ColourDistance(c, fillColour) && ColourDistance(c, fillColour) > 90,
                        $"{theme} {percent} %: {what} at ({x},{y}) is {c}, expected the page colour, not the fill.");
                }

                AssertNotFill(0, 0, "top-left track corner");
                AssertNotFill(W - 1, 0, "top-right track corner");
                AssertNotFill(0, H - 1, "bottom-left track corner");
                AssertNotFill(W - 1, H - 1, "bottom-right track corner");

                var end = (int)Math.Round(W * percent / 100.0);
                AssertNotFill(end - 1, -1, "pixel above the fill's end");
                AssertNotFill(end - 1, H, "pixel below the fill's end");
                AssertNotFill(end - 1, 0, "top of the fill's rounded end");
                AssertNotFill(end - 1, H - 1, "bottom of the fill's rounded end");

                // The fill itself is there.
                var body = At(Math.Max(end - 5, 1), H / 2);
                Assert.True(ColourDistance(body, fillColour) < 40, $"{theme} {percent} %: no fill inside the bar, found {body}.");
            }
            finally
            {
                ApplyTheme("dark");
            }
        });
    }

    /// <summary>
    /// The compact running list is a strict table: every row's bar starts at the same x and has the same
    /// width, whatever the row's name or percentage, and at a normal window width the bar is long enough
    /// to read.
    /// </summary>
    [Fact]
    public void Running_rows_share_one_bar_column_and_the_bar_is_wide()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(3).ToList();
            var percentages = new double[] { 3, 48, 97 };
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].BeginInstall();
                viewModel.InstallBatch.Add(rows[i]);
                rows[i].ApplyInstallStatus(new InstallItemStatus(
                    rows[i].Item.SourceId, rows[i].Item.Id, percentages[i], "Installing", null, null));
            }

            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            var result = WpfHarness.RenderWindow(window, "main-installing-running-bars-en", 1100, 700);
            AssertRendered(result);

            var list = FindByName<ItemsControl>(content, "RunningRowsList")!;
            var bars = FindAllByName<PillProgressBar>(content, "RunningRowBar");
            Assert.Equal(3, bars.Count);

            var lefts = bars.Select(b => b.TransformToAncestor(list).Transform(new Point(0, 0)).X).ToList();
            Assert.All(lefts, x => Assert.Equal(lefts[0], x, 0.5));
            Assert.All(bars, b => Assert.Equal(bars[0].ActualWidth, b.ActualWidth, 0.5));
            Assert.All(bars, b => Assert.Equal(6, b.ActualHeight, 0.5));
            Assert.True(bars[0].ActualWidth >= 200, $"the running bars are only {bars[0].ActualWidth:0} px wide at 1100 px");
        });
    }
}
