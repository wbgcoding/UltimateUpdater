using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>The filter menu is built like the column chooser: checkable entries, opened from a button,
/// a second call while it is open closes it again.</summary>
[Collection("wpf")]
public class FilterMenuTests
{
    private static System.Windows.Controls.Button MakeAnchor(Window window)
    {
        var anchor = new System.Windows.Controls.Button();
        window.Content = anchor;
        window.Show();
        return anchor;
    }

    /// <summary>What a real click does: IsChecked flips first, then Click is raised.</summary>
    private static void Click(MenuItem item)
    {
        item.IsChecked = !item.IsChecked;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    [Fact]
    public void Entries_become_checkable_items_that_show_and_set_their_state()
    {
        WpfHarness.OnUiThread(() =>
        {
            var on = false;
            var other = true;
            var menu = FilterMenu.Build(
            [
                new FilterEntry("a", () => "First", () => on, v => on = v),
                new FilterEntry("b", () => "Second", () => other, v => other = v),
            ]);

            var items = menu.Items.Cast<MenuItem>().ToList();
            Assert.Equal(["First", "Second"], items.Select(i => i.Header));
            Assert.All(items, i => Assert.True(i.IsCheckable));
            Assert.False(items[0].IsChecked);
            Assert.True(items[1].IsChecked);

            Click(items[0]);
            Assert.True(on);
            Click(items[1]);
            Assert.False(other);
        });
    }

    [Fact]
    public void The_menu_opens_from_the_button_and_a_second_call_closes_it()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
            var anchor = MakeAnchor(window);
            IReadOnlyList<FilterEntry> entries = [new FilterEntry("a", () => "First", () => false, _ => { })];

            var menu = FilterMenu.ShowFor(anchor, entries);
            Assert.True(menu?.IsOpen);

            Assert.Null(FilterMenu.ShowFor(anchor, entries));
            Assert.False(menu!.IsOpen);
        });
    }
}
