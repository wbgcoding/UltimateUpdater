using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>Lays a Settings window out at the given width by hand (an unshown window has no layout
    /// yet) and returns it with its scroll viewer and section list.</summary>
    private static (SettingsWindow Window, System.Windows.Controls.ScrollViewer Scroll, ListBox Nav) LayOutSettingsWindow(
        double width)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging);
        LocalizationService.Instance.Apply("en");

        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
        var window = new SettingsWindow(viewModel);

        // Laid out in a host of its own, as WpfHarness.RenderWindow does: left inside the unshown window,
        // a layout pass could reach the window first, which measured the page at no size at all in a
        // long test run (extent 0, viewport 0). The names stay registered on the window.
        var root = (FrameworkElement)window.Content;
        var dataContext = window.DataContext;
        window.Content = null;
        var host = new Border { Width = width, Height = window.Height, Child = root, DataContext = dataContext };
        host.Measure(new Size(width, window.Height));
        host.Arrange(new Rect(0, 0, width, window.Height));
        host.UpdateLayout();

        return (window,
            (System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll")!,
            (ListBox)window.FindName("SettingsNav")!);
    }

    private static double HeaderTopInViewport(
        SettingsWindow window, System.Windows.Controls.ScrollViewer scroll, string headerName)
    {
        var header = (FrameworkElement)window.FindName(headerName)!;
        return header.TransformToAncestor((UIElement)scroll.Content).Transform(new Point(0, 0)).Y - scroll.VerticalOffset;
    }

    /// <summary>Picking a section in the list scrolls that section's header to the top of the page.</summary>
    [Fact]
    public void Settings_window_navigation_scrolls_the_selected_header_to_the_top()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, scroll, nav) = LayOutSettingsWindow(800);
            Assert.Equal(Visibility.Visible, nav.Visibility);
            Assert.True(scroll.ScrollableHeight > 0,
                $"the settings page does not scroll (extent {scroll.ExtentHeight:0.#}, viewport {scroll.ViewportHeight:0.#})");

            var entry = nav.Items.OfType<ListBoxItem>().Single(i => (string)i.Tag == "NotifyHeader");
            nav.SelectedItem = entry;

            // Wait for the page to arrive instead of reading it after one layout pass: the tolerance stays
            // 1.5 px, only the moment of reading is polled (dispatcher pumped, 3 s at most).
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                scroll.UpdateLayout();
                if (scroll.VerticalOffset > 0 && Math.Abs(HeaderTopInViewport(window, scroll, "NotifyHeader")) < 1.5)
                    break;
                if (DateTime.UtcNow >= deadline)
                    break;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
                System.Threading.Thread.Sleep(20);
            }

            Assert.True(scroll.VerticalOffset > 0, "the page did not scroll at all");
            Assert.True(Math.Abs(HeaderTopInViewport(window, scroll, "NotifyHeader")) < 1.5,
                $"the Notifications header sits {HeaderTopInViewport(window, scroll, "NotifyHeader"):0.#}px from the top "
                + $"(offset {scroll.VerticalOffset:0.#} of {scroll.ScrollableHeight:0.#})");

            window.Close();
        });
    }

    /// <summary>Scrolling the page moves the highlight to the section at the top of it.</summary>
    [Fact]
    public void Settings_window_navigation_highlights_the_section_at_the_top_of_the_page()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, scroll, nav) = LayOutSettingsWindow(800);

            // Install: a header in the middle of the page, well before the sections that never reach the top.
            var install = (FrameworkElement)window.FindName("InstallHeader")!;
            var offset = install.TransformToAncestor((UIElement)scroll.Content).Transform(new Point(0, 0)).Y;
            scroll.ScrollToVerticalOffset(offset);
            scroll.UpdateLayout();

            var selected = Assert.IsType<ListBoxItem>(nav.SelectedItem);
            Assert.Equal("InstallHeader", (string)selected.Tag);

            scroll.ScrollToVerticalOffset(0);
            scroll.UpdateLayout();
            Assert.Equal("ScanHeader", (string)((ListBoxItem)nav.SelectedItem).Tag);

            window.Close();
        });
    }

    /// <summary>A header scrolled to the top of the page is the highlighted one, also for the sections near the
    /// end of the page; the last sections, too short to reach the top, are reached by the end of the page.</summary>
    [Fact]
    public void Settings_window_navigation_highlights_every_header_that_can_reach_the_top()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, scroll, nav) = LayOutSettingsWindow(800);
            Assert.True(scroll.ScrollableHeight > 0);

            var reached = 0;
            foreach (var entry in nav.Items.OfType<ListBoxItem>())
            {
                var target = (FrameworkElement)window.FindName((string)entry.Tag)!;
                FrameworkElement anchor = target is System.Windows.Controls.Expander && target.Parent is Border card ? card : target;
                var top = anchor.TransformToAncestor((UIElement)scroll.Content).Transform(new Point(0, 0)).Y;
                if (top > scroll.ScrollableHeight)
                    continue;

                scroll.ScrollToVerticalOffset(top);
                scroll.UpdateLayout();

                Assert.True(
                    ReferenceEquals(entry, nav.SelectedItem),
                    $"header {entry.Tag} at the top (offset {top:0} of {scroll.ScrollableHeight:0}) highlights "
                    + $"{((ListBoxItem)nav.SelectedItem).Tag}");
                reached++;
            }

            // Notifications and Appearance sit on the last screen of the page, the part the old reading line got wrong.
            Assert.True(reached >= 6, $"only {reached} headers can reach the top");

            scroll.ScrollToEnd();
            scroll.UpdateLayout();
            Assert.Equal("AboutExpander", (string)((ListBoxItem)nav.SelectedItem).Tag);

            window.Close();
        });
    }

    /// <summary>Choosing About also opens its card; below 760 px the list is hidden.</summary>
    [Fact]
    public void Settings_window_navigation_opens_about_and_hides_below_760_pixels()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, scroll, nav) = LayOutSettingsWindow(800);
            var about = (System.Windows.Controls.Expander)window.FindName("AboutExpander")!;
            Assert.False(about.IsExpanded);

            nav.SelectedItem = nav.Items.OfType<ListBoxItem>().Single(i => (string)i.Tag == "AboutExpander");
            Assert.True(about.IsExpanded);
            window.Close();

            var (narrow, _, narrowNav) = LayOutSettingsWindow(759);
            Assert.Equal(Visibility.Collapsed, narrowNav.Visibility);
            narrow.Close();
        });
    }
}
