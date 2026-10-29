using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>The first <paramref name="queued"/> rows of the sample set installing: one running, the
    /// rest waiting in the queue panel, which is opened.</summary>
    private static (MainWindow Window, FrameworkElement Content, MainViewModel ViewModel) QueuePanelWindow(
        string language, int queued = 3)
    {
        var (viewModel, theme, settings) = CreateMainViewModel(language);
        Populate(viewModel);

        var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(queued + 1).ToList();
        foreach (var row in rows)
        {
            row.BeginInstall();
            viewModel.InstallBatch.Add(row);
        }

        rows[0].ApplyInstallStatus(new InstallItemStatus(
            rows[0].Item.SourceId, rows[0].Item.Id, 40, "Installing", null, null));
        foreach (var row in rows.Skip(1))
            viewModel.QueueRows.Add(row);

        viewModel.IsInstalling = true;
        viewModel.IsQueuePanelExpanded = true;

        var window = new MainWindow(viewModel, theme, settings);
        return (window, (FrameworkElement)window.Content, viewModel);
    }

    /// <summary>
    /// The queue panel lives on the strip's own surface: beside the header text, and in the gap between
    /// two row cards, the pixels are the strip's background, with no second panel, band or border.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void The_queue_panel_sits_on_the_strips_own_surface(string themeName)
    {
        WpfHarness.OnUiThread(() =>
        {
            ApplyTheme(themeName);
            try
            {
                var (window, content, _) = QueuePanelWindow("en");
                ApplyTheme(themeName);
                AssertRendered(WpfHarness.RenderWindow(window, $"main-queue-surface-{themeName}", 1100, 700));

                var host = (FrameworkElement)VisualTreeHelper.GetParent(content)!;
                host.UpdateLayout();
                var width = (int)host.ActualWidth;
                var height = (int)host.ActualHeight;
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);

                Color PixelAt(FrameworkElement element, double x, double y)
                {
                    var p = element.TransformToAncestor(host).Transform(new Point(x, y));
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect((int)p.X, (int)p.Y, 1, 1), pixel, 4, 0);
                    return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
                }

                var header = FindByName<Border>(content, "QueueHeaderHover")!;
                var cards = FindAllByName<Border>(content, "QueueRowCard");
                Assert.True(cards.Count >= 2, "expected at least two queue rows");

                // The strip's surface: just left of the header, inside the strip's own left padding.
                var surface = PixelAt(header, -6, header.ActualHeight / 2);
                var fill = PixelAt(cards[0], cards[0].ActualWidth - 6, cards[0].ActualHeight / 2);
                Assert.True(ColourDistance(surface, fill) > 3, "the row cards show no fill of their own");

                void AssertSurface(Color c, string what) =>
                    Assert.True(ColourDistance(c, surface) <= 2,
                        $"{themeName}: {what} is {c} but the strip's surface is {surface}.");

                AssertSurface(PixelAt(header, header.ActualWidth * 0.55, header.ActualHeight / 2), "beside the header text");
                AssertSurface(PixelAt(header, header.ActualWidth * 0.55, 1), "the top of the header row");
                AssertSurface(PixelAt(header, header.ActualWidth * 0.55, header.ActualHeight - 1), "the bottom of the header row");

                var gap = cards[1].TransformToAncestor(host).Transform(new Point(0, 0)).Y
                    - cards[0].TransformToAncestor(host).Transform(new Point(0, 0)).Y - cards[0].ActualHeight;
                Assert.InRange(gap, 2, 8);
                AssertSurface(PixelAt(cards[0], cards[0].ActualWidth / 2, cards[0].ActualHeight + 1), "between two cards");
                AssertSurface(PixelAt(cards[0], -6, cards[0].ActualHeight / 2), "left of a card");
            }
            finally
            {
                ApplyTheme("dark");
            }
        });
    }

    /// <summary>
    /// The queue rows carry their version lane in a column of its own, built like the main table's:
    /// the arrow stands at the same x in every row, whatever the row shows (short or long versions, no
    /// installed version, a critical mark), and the critical mark and the lane never overlap.
    /// </summary>
    [Fact]
    public void Queue_version_lane_arrow_lines_up_across_rows_regardless_of_what_each_row_shows()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);

            var running = viewModel.Updates.First(r => r.Item.CanInstall);
            running.BeginInstall();
            viewModel.InstallBatch.Add(running);

            var queued = new[]
            {
                new UpdateRowViewModel(Item("q.short", "Short versions", "winget", UpdateCategory.Software, "1.0.0", "1.1.0")),
                new UpdateRowViewModel(Item("q.critical", "Critical row", "winget", UpdateCategory.Software, "10.2.3", "10.2.4", critical: true)),
                new UpdateRowViewModel(Item("q.long", "Long version numbers", "winget", UpdateCategory.Software,
                    "2024.11.30-preview.987654321", "2026.08.22-preview.123456789")),
                new UpdateRowViewModel(Item("q.none", "No installed version", "winget", UpdateCategory.Software, null, "3.0.0")),
            };
            foreach (var row in queued)
            {
                viewModel.Updates.Add(row);
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
                viewModel.QueueRows.Add(row);
            }

            viewModel.IsInstalling = true;
            viewModel.IsQueuePanelExpanded = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            AssertRendered(WpfHarness.RenderWindow(window, "main-queue-version-lane-en", 1100, 700));

            var scroll = FindByName<ScrollViewer>(content, "QueueRowsScroll")!;
            T In<T>(string name, UpdateRowViewModel vm) where T : FrameworkElement =>
                FindAllByName<T>(scroll, name).First(e => ReferenceEquals(e.DataContext, vm));
            double X(FrameworkElement e, double fraction) =>
                e.TransformToAncestor(scroll).Transform(new Point(e.ActualWidth * fraction, 0)).X;

            var withArrow = queued.Take(3).ToList();
            var arrowXs = withArrow.Select(vm => Math.Round(X(In<TextBlock>("QueueVersionArrow", vm), 0.5), 1)).ToList();
            Assert.True(arrowXs.Max() - arrowXs.Min() <= 0.5, $"the arrow moved between queue rows: {string.Join(", ", arrowXs)}");

            var laneLefts = queued.Select(vm => Math.Round(X(In<Grid>("QueueVersionLane", vm), 0), 1)).ToList();
            Assert.True(laneLefts.Max() - laneLefts.Min() <= 0.5, $"the lane moved between queue rows: {string.Join(", ", laneLefts)}");

            var noInstalled = queued[3];
            Assert.NotEqual(Visibility.Visible, In<TextBlock>("QueueVersionArrow", noInstalled).Visibility);
            var lone = In<TextBlock>("QueueAvailableVersionText", noInstalled);
            Assert.True(Math.Abs(X(lone, 0.5) - arrowXs[0]) <= 1.0,
                $"a lone version is centred at {X(lone, 0.5):0.0}, the arrows stand at {arrowXs[0]:0.0}");

            // The critical mark sits in its own column, left of the lane, vertically centred in the card.
            var critical = queued[1];
            var icon = In<Wpf.Ui.Controls.SymbolIcon>("QueueRowCriticalIcon", critical);
            Assert.Equal(Visibility.Visible, icon.Visibility);
            var lane = In<Grid>("QueueVersionLane", critical);
            Assert.True(X(icon, 1) <= X(lane, 0) + 0.5, "the critical mark overlaps the version lane");
            var card = FindAllByName<Border>(scroll, "QueueRowCard").First(c => ReferenceEquals(c.DataContext, critical));
            double MidY(FrameworkElement e) => e.TransformToAncestor(card).Transform(new Point(0, e.ActualHeight / 2)).Y;
            Assert.True(Math.Abs(MidY(icon) - card.ActualHeight / 2) <= 1.5, "the critical mark is not centred vertically");
            Assert.True(Math.Abs(MidY(lane) - card.ActualHeight / 2) <= 1.5, "the version lane is not centred vertically");
        });
    }

    /// <summary>
    /// A long list must still leave a thumb big enough to grab, and scrolled to the end the thumb must sit
    /// at the very bottom of the track (a thumb forced bigger than the track's share would stop short).
    /// </summary>
    [Fact]
    public void The_scrollbar_thumb_keeps_a_minimum_length_and_reaches_the_end_of_a_long_list()
    {
        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            var entries = Enumerable.Range(0, 800).Select(i => new HistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 7, 19, 21, 0, 0, TimeSpan.Zero).AddMinutes(i),
                SourceId = "winget", ItemId = $"Vendor.App{i}", Name = $"Application {i}",
                FromVersion = "1.0.0", ToVersion = "1.0.1", Success = true,
            }).ToList();
            var viewModel = new HistoryViewModel(HistoryWith(entries));
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;
            AssertRendered(WpfHarness.RenderWindow(window, "history-scrollbar-thumb-en", 820, 520));

            var list = FindFirstDescendant<ListBox>(content)!;
            var scroll = FindFirstDescendant<ScrollViewer>(list)!;
            Assert.True(scroll.ScrollableHeight > 0, "expected a scrollable list");
            var bar = FindAllByName<ScrollBar>(scroll, "PART_VerticalScrollBar").FirstOrDefault()
                      ?? FindFirstDescendant<ScrollBar>(scroll)!;
            var thumb = bar.Track!.Thumb!;

            Assert.True(thumb.ActualHeight >= 47.5, $"the thumb is only {thumb.ActualHeight:0.0} points tall, expected at least 48");

            scroll.ScrollToEnd();
            window.UpdateLayout();
            bar.UpdateLayout();
            var bottom = thumb.TransformToAncestor(bar).Transform(new Point(0, thumb.ActualHeight)).Y;
            Assert.True(bar.ActualHeight - bottom <= 1.5,
                $"scrolled to the end, the thumb stops {bar.ActualHeight - bottom:0.0} points short of the track's end");
        });
    }
}
