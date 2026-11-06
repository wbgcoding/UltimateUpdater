using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// Every right-click and drop-down menu takes the app's own menu look (rounded opaque card, flat items)
/// through implicit application styles, including the menus built in code, which no window resource reaches.
/// </summary>
[Collection("wpf")]
public class AppMenuStyleTests
{
    private static T? FindNamed<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name)
                return match;
            if (FindNamed<T>(child, name) is { } found)
                return found;
        }

        return null;
    }

    private static Border? MenuCard(ContextMenu menu) =>
        VisualTreeHelper.GetChildrenCount(menu) > 0 ? VisualTreeHelper.GetChild(menu, 0) as Border : null;

    [Fact]
    public void A_code_built_checkbox_menu_opens_as_an_app_card_with_visible_check_marks()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
            var anchor = new System.Windows.Controls.Button();
            window.Content = anchor;
            window.Show();

            var menu = FilterMenu.ShowFor(anchor,
            [
                new FilterEntry("a", () => "On", () => true, _ => { }),
                new FilterEntry("b", () => "Off", () => false, _ => { }),
            ])!;
            menu.UpdateLayout();

            var card = MenuCard(menu);
            Assert.NotNull(card);
            Assert.Equal(new CornerRadius(7), card!.CornerRadius);
            Assert.Equal(Application.Current.FindResource("SolidBackgroundFillColorSecondaryBrush"), menu.Background);

            var items = menu.Items.Cast<MenuItem>().ToList();
            Assert.Equal(Visibility.Visible, FindNamed<FrameworkElement>(items[0], "Check")!.Visibility);
            // hidden, not collapsed: the unticked label stays in line with the ticked one
            Assert.Equal(Visibility.Hidden, FindNamed<FrameworkElement>(items[1], "Check")!.Visibility);

            menu.IsOpen = false;
            window.Close();
        });
    }

    [Fact]
    public void A_plain_item_shows_no_check_column_and_a_shortcut_shows_its_text()
    {
        WpfHarness.OnUiThread(() =>
        {
            var plain = new MenuItem { Header = "Plain" };
            var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
            var menu = new ContextMenu();
            menu.Items.Add(plain);
            menu.Items.Add(new Separator());
            menu.Items.Add(copy);
            menu.IsOpen = true;
            menu.UpdateLayout();

            Assert.Equal(Visibility.Collapsed, FindNamed<FrameworkElement>(plain, "Check")!.Visibility);
            Assert.Equal(Visibility.Collapsed, FindNamed<FrameworkElement>(plain, "Gesture")!.Visibility);
            var gesture = FindNamed<TextBlock>(copy, "Gesture")!;
            Assert.Equal(Visibility.Visible, gesture.Visibility);
            Assert.Equal("Ctrl+C", gesture.Text);

            var separator = (Separator)menu.Items[1];
            Assert.Same(Application.Current.FindResource(MenuItem.SeparatorStyleKey), separator.Style);

            menu.IsOpen = false;
        });
    }

    [Fact]
    public void The_row_menu_keeps_its_own_keyed_styles()
    {
        WpfHarness.OnUiThread(() =>
        {
            foreach (var key in new[] { "RowContextMenu", "RowMenuItem", "RowSubmenuItem" })
                Assert.IsType<Style>(Application.Current.FindResource(key));
        });
    }
}
