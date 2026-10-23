using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>The section list beside the settings page follows the scroll position; a click that
/// scrolls nowhere must not leave it waiting for an arrival that never comes.</summary>
[Collection("wpf")]
public class SettingsNavigationTests
{
    private static SettingsWindow CreateWindow()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging);
        LocalizationService.Instance.Apply("en");
        var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
        var window = new SettingsWindow(viewModel);
        _ = WpfHarness.RenderWindow(window, "settings-navigation", (int)window.Width, (int)window.Height);
        return window;
    }

    private static double TopOf(ScrollViewer scroll, FrameworkElement section) =>
        section.TransformToAncestor((UIElement)scroll.Content).Transform(new Point(0, 0)).Y;

    [Fact]
    public void Click_on_the_section_already_at_the_top_lets_a_later_scroll_move_the_highlight()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = CreateWindow();
            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var nav = (ListBox)window.FindName("SettingsNav")!;
            var entries = nav.Items.OfType<ListBoxItem>().ToList();

            // the first click lands the page on the first section; the second one is already there,
            // scrolls nowhere and raises no scroll event
            window.ScrollToSection(entries[0]);
            scroll.UpdateLayout();
            var landed = scroll.VerticalOffset;
            window.ScrollToSection(entries[0]);
            scroll.UpdateLayout();
            Assert.Equal(landed, scroll.VerticalOffset);

            // a scrollbar drag afterwards must move the highlight
            var later = entries[4];
            scroll.ScrollToVerticalOffset(TopOf(scroll, (FrameworkElement)window.FindName((string)later.Tag)!));
            scroll.UpdateLayout();

            Assert.NotSame(entries[0], nav.SelectedItem);
            window.Close();
        });
    }

    [Fact]
    public void Mouse_press_on_the_page_hands_the_highlight_back_to_the_scroll_position()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = CreateWindow();
            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var nav = (ListBox)window.FindName("SettingsNav")!;
            var entries = nav.Items.OfType<ListBoxItem>().ToList();

            // a navigation scroll is under way (its scroll event has not been raised yet) ...
            window.ScrollToSection(entries[6]);

            // ... and the user grabs the scrollbar and drags somewhere else
            scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent,
            });
            var other = entries[2];
            scroll.ScrollToVerticalOffset(TopOf(scroll, (FrameworkElement)window.FindName((string)other.Tag)!));
            scroll.UpdateLayout();

            Assert.Same(other, nav.SelectedItem);
            window.Close();
        });
    }

    [Fact]
    public void Scrolling_to_the_bottom_highlights_the_last_section()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = CreateWindow();
            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var nav = (ListBox)window.FindName("SettingsNav")!;
            var entries = nav.Items.OfType<ListBoxItem>().ToList();

            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            scroll.UpdateLayout();
            scroll.ScrollToEnd();
            scroll.UpdateLayout();

            Assert.Same(entries[^1], nav.SelectedItem);
            window.Close();
        });
    }

    [Theory]
    [InlineData(0, 0)]       // top of the page: first section
    [InlineData(510, 1)]     // second header passed the top line
    [InlineData(1000, 2)]    // at the bottom: the short last section is visible, so it wins
    [InlineData(999.8, 2)]   // within a pixel of the bottom counts as the bottom
    [InlineData(900, 1)]     // on the last screen the line glides down, the last header not crossed yet
    [InlineData(960, 2)]     // ... and crosses it before the very bottom
    public void The_highlight_follows_the_top_line_and_the_bottom_of_the_page(double offset, int expected) =>
        Assert.Equal(expected, SettingsWindow.HighlightedSectionIndex([0, 500, 1300], offset, viewportHeight: 400, scrollableHeight: 1000.5));

    [Fact]
    public void No_sections_means_nothing_to_highlight() =>
        Assert.Equal(-1, SettingsWindow.HighlightedSectionIndex([], 0, 400, 0));
}
