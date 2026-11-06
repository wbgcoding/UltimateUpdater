using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Wpf.Ui.Appearance;

namespace UltimateUpdater.Tests;

/// <summary>
/// The settings cards open and close with one eased height animation of the same length. A snapshot cannot
/// show motion, so these check what can be proven: that closing is a real in-flight move (the body stays
/// there and the page shrinks step by step instead of dropping at once), that it ends with the body
/// collapsed and the page back at its closed height, and that the About card shows the app logo.
/// </summary>
[Collection("wpf")]
public class SettingsExpanderTests : IDisposable
{
    public SettingsExpanderTests()
    {
        MotionPreference.AnimationsEnabledForTest = true;
        RevealDecorator.AnimateOutsideWindowForTest = true;
    }

    public void Dispose()
    {
        MotionPreference.AnimationsEnabledForTest = null;
        RevealDecorator.AnimateOutsideWindowForTest = null;
    }

    private static SettingsWindow CreateWindow(string language, string snapshot)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging);
        LocalizationService.Instance.Apply(language);
        var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
        var window = new SettingsWindow(viewModel);
        _ = WpfHarness.RenderWindow(window, snapshot, (int)window.Width, (int)window.Height);
        return window;
    }

    private static T? FindFirst<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
            return match;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindFirst<T>(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Drives WPF's own time manager one step; nothing composites a window here, so the clock
    /// that would run the animation never ticks by itself.</summary>
    private static void Tick()
    {
        var mediaContextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
        var context = mediaContextType
            .GetMethod("From", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Dispatcher.CurrentDispatcher])!;
        var timeManager = context.GetType()
            .GetField("_timeManager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(context)!;
        timeManager.GetType()
            .GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(timeManager, null);
    }

    /// <summary>Ticks and lays out until the move has arrived, handing every page height seen on the way
    /// to <paramref name="observe"/>.</summary>
    private static void SettleWhileObserving(RevealDecorator reveal, ScrollViewer scroll, Action<double> observe)
    {
        for (var attempt = 0; attempt < 100 && reveal.IsSettling; attempt++)
        {
            Tick();
            scroll.UpdateLayout();
            observe(scroll.ExtentHeight);
            Thread.Sleep(10);
        }

        Assert.False(reveal.IsSettling, "the move never arrived");
        scroll.UpdateLayout();
    }

    [Fact]
    public void Closing_a_card_is_an_in_flight_move_that_ends_collapsed_at_the_closed_page_height()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = CreateWindow("en", "settings-expander-close");
            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var expander = (Expander)window.FindName("AboutExpander")!;
            var closedHeight = scroll.ExtentHeight;

            expander.IsExpanded = true;
            scroll.UpdateLayout();
            var reveal = FindFirst<RevealDecorator>(expander)!;
            Assert.True(reveal.IsSettling, "opening jumped instead of animating");
            SettleWhileObserving(reveal, scroll, _ => { });
            var openHeight = scroll.ExtentHeight;
            Assert.True(openHeight > closedHeight + 50, "the opened card added no height");
            Assert.Equal(Visibility.Visible, reveal.Visibility);

            expander.IsExpanded = false;
            scroll.UpdateLayout();

            // The old template collapsed the body by timer and left the page at full height meanwhile;
            // here the body is still there and the move has not arrived at the first frame.
            Assert.True(reveal.IsSettling, "closing jumped instead of animating");
            Assert.Equal(Visibility.Visible, reveal.Visibility);

            var heights = new List<double>();
            SettleWhileObserving(reveal, scroll, heights.Add);

            // the page only ever shrinks on the way down, with no step back up
            for (var i = 1; i < heights.Count; i++)
                Assert.True(heights[i] <= heights[i - 1] + 0.5, $"page grew while closing at step {i}: {heights[i - 1]} -> {heights[i]}");

            Assert.Equal(Visibility.Collapsed, reveal.Visibility);
            Assert.Equal(0, reveal.Progress);
            Assert.Equal(closedHeight, scroll.ExtentHeight, 1);
            window.Close();
        });
    }

    [Fact]
    public void Without_window_animations_a_card_closes_at_once()
    {
        MotionPreference.AnimationsEnabledForTest = false;

        WpfHarness.OnUiThread(() =>
        {
            var window = CreateWindow("en", "settings-expander-instant");
            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var expander = (Expander)window.FindName("AboutExpander")!;
            var closedHeight = scroll.ExtentHeight;

            expander.IsExpanded = true;
            scroll.UpdateLayout();
            var reveal = FindFirst<RevealDecorator>(expander)!;
            Assert.False(reveal.IsSettling);
            Assert.Equal(1, reveal.Progress);

            expander.IsExpanded = false;
            scroll.UpdateLayout();
            Assert.False(reveal.IsSettling);
            Assert.Equal(Visibility.Collapsed, reveal.Visibility);
            Assert.Equal(closedHeight, scroll.ExtentHeight, 1);
            window.Close();
        });
    }

    [Fact]
    public void A_closed_card_never_builds_its_content()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = CreateWindow("en", "settings-expander-closed");
            var expander = (Expander)window.FindName("AboutExpander")!;

            Assert.Equal(Visibility.Collapsed, FindFirst<RevealDecorator>(expander)!.Visibility);
            Assert.Null(FindFirst<Image>(expander));
            window.Close();
        });
    }

    [Fact]
    public void Jumping_to_the_About_section_follows_the_growing_card_to_its_place()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = CreateWindow("en", "settings-expander-nav");
            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var nav = (ListBox)window.FindName("SettingsNav")!;
            var entry = nav.Items.OfType<ListBoxItem>().First(i => (string)i.Tag == "AboutExpander");
            var expander = (Expander)window.FindName("AboutExpander")!;

            window.ScrollToSection(entry);
            var reveal = FindFirst<RevealDecorator>(expander)!;
            SettleWhileObserving(reveal, scroll, _ => { });
            Tick();
            scroll.UpdateLayout();

            // the card ended in view: either at the top, or the page is at its very end
            var card = (FrameworkElement)expander.Parent;
            var top = card.TransformToAncestor((UIElement)scroll.Content).Transform(new Point(0, 0)).Y;
            Assert.True(
                Math.Abs(scroll.VerticalOffset - top) < 1 || scroll.VerticalOffset >= scroll.ScrollableHeight - 1,
                $"the page stopped at {scroll.VerticalOffset} with the About card at {top} (scrollable {scroll.ScrollableHeight})");
            window.Close();
        });
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_About_card_shows_the_app_logo_beside_the_name(string language)
    {
        WpfHarness.OnUiThread(() =>
        {
            MotionPreference.AnimationsEnabledForTest = false;
            var window = CreateWindow(language, $"settings-about-logo-{language}");
            var expander = (Expander)window.FindName("AboutExpander")!;
            expander.IsExpanded = true;
            ((ScrollViewer)window.FindName("SettingsScroll")!).UpdateLayout();

            var logo = FindFirst<Image>(expander);
            Assert.NotNull(logo);
            Assert.NotNull(logo!.Source);
            Assert.Equal(64, logo.ActualWidth, 0.5);
            Assert.Equal(64, logo.ActualHeight, 0.5);
            window.Close();
        });
    }

    [Theory]
    [InlineData("de", "light")]
    [InlineData("de", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public void About_expanded_renders_with_the_logo(string language, string theme)
    {
        MotionPreference.AnimationsEnabledForTest = false;
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light);
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply(language);
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            ((Expander)window.FindName("AboutExpander")!).IsExpanded = true;

            // the About card sits at the foot of the page: scroll there so it is what the picture shows
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();
            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            scroll.ScrollToEnd();
            scroll.UpdateLayout();

            result = WpfHarness.RenderWindow(window, $"settings-about-open-{language}-{theme}", (int)window.Width, (int)window.Height);
        });

        WpfHarness.OnUiThread(() => WpfHarness.ApplyTheme(ApplicationTheme.Dark));

        Assert.NotNull(result);
        Assert.True(File.Exists(result.Path));
        Assert.True(result.DistinctColours > 50, "the snapshot came out blank");
    }
}
