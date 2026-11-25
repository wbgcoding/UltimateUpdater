using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The queue shrinks whenever an item starts or is removed. Every remaining card's accessible name
    /// ("name, position X of N") must follow, not keep announcing the position it had when it was created.
    /// </summary>
    [Fact]
    public void The_queue_cards_announce_their_new_position_once_the_queue_shrinks()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, content, viewModel) = QueuePanelWindow("en");
            AssertRendered(WpfHarness.RenderWindow(window, "main-queue-names-en", 1100, 700));
            var host = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(content)!;
            host.UpdateLayout();

            List<string> Names() => FindAllByName<Border>(content, "QueueRowCard")
                .Select(AutomationProperties.GetName).ToList();

            viewModel.QueueRows.RemoveAt(0);
            host.UpdateLayout();
            // The refresh is posted at Loaded priority; the harness never shows a window, so run it by hand.
            host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);

            var after = Names();
            Assert.Equal(2, after.Count);
            Assert.All(after.Select((name, i) => (name, i)), t => Assert.EndsWith($"{t.i + 1} of 2", t.name));
        });
    }
}
