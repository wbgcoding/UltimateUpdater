using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UltimateUpdater.Models;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The update table's scrollbar runs down to the bottom of the rounded results card. A Border rounds
    /// only its own fill and stroke, never its child, so the square bar painted over the card's lower right
    /// corner and the corner looked cut off. The card's content is clipped to the card's inner radius: the
    /// bar's own square corner has to fall outside that clip.
    /// </summary>
    [Fact]
    public void The_table_scrollbar_stays_inside_the_cards_rounded_corner()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            for (var i = 0; i < 40; i++)
            {
                viewModel.Updates.Add(new UpdateRowViewModel(Item(
                    $"row-{i}", $"Contoso Tool {i}", "winget", UpdateCategory.Software, "1.0", "1.1")));
            }

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-results-card-corner", 1100, 700);

            var card = FindByName<Grid>(content, "ResultsCardContent");
            var scroller = FindByName<ScrollViewer>(content, "ResultsScrollViewer");
            Assert.NotNull(card);
            Assert.NotNull(scroller);
            Assert.True(scroller!.ScrollableHeight > 0, "40 rows should overflow the card, so the bar shows");

            var bar = VerticalBars(scroller).Single();
            Assert.True(bar.ActualHeight > 0, "the vertical bar was never laid out");

            var clip = card!.Clip;
            Assert.NotNull(clip);

            // The bar's own lower right corner, a hair inside so rounding cannot put it on the edge.
            var corner = bar.TranslatePoint(new Point(bar.ActualWidth - 0.5, bar.ActualHeight - 0.5), card);
            Assert.False(clip!.FillContains(corner),
                $"the scrollbar's square corner at {corner} is drawn over the card's rounded corner");

            // and the clip still keeps the rest of the bar: a point in its middle stays visible
            var middle = bar.TranslatePoint(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2), card);
            Assert.True(clip.FillContains(middle), "the clip cuts away the scrollbar itself");
        });

        AssertRendered(result);
    }

    /// <summary>The scroll viewer's own vertical bar, from its template; the rows' content is not searched.</summary>
    private static IEnumerable<ScrollBar> VerticalBars(ScrollViewer scroller)
    {
        var pending = new Stack<DependencyObject>([scroller]);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is ScrollBar { Orientation: Orientation.Vertical } bar)
            {
                yield return bar;
                continue;
            }

            if (node is ScrollContentPresenter)
                continue;

            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
    }
}
