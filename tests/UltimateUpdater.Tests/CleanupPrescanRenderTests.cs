using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>Renders the two pieces of the startup prescan a person sees: the switch in the settings window
/// and the "As of" stamp in the cleanup window.</summary>
[Collection("wpf")]
public class CleanupPrescanRenderTests
{
    private const int MinimumDistinctColours = 20;

    private static void AssertRendered(RenderResult? result)
    {
        Assert.NotNull(result);
        Assert.True(File.Exists(result.Path), $"No image was written to {result.Path}.");
        Assert.True(
            result.DistinctColours >= MinimumDistinctColours,
            $"{Path.GetFileName(result.Path)} came out effectively blank, only {result.DistinctColours} distinct colours.");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void The_settings_window_shows_the_prescan_switch_with_its_hint(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(
                logging, Path.Combine(Directory.CreateTempSubdirectory("wu-prescan-ui-").FullName, "settings.json"));
            LocalizationService.Instance.Apply(language);

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);

            var toggle = window.FindName("CleanupPrescanToggle") as FrameworkElement;
            Assert.NotNull(toggle);

            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();
            toggle!.BringIntoView();
            scroll.UpdateLayout();

            result = WpfHarness.RenderWindow(window, $"settings-cleanup-prescan-{language}", (int)window.Width, (int)window.Height + 140);

            Assert.True(toggle.ActualHeight > 0, "the prescan switch was never laid out");
            Assert.Equal(Visibility.Visible, toggle.Visibility);
            Assert.True(viewModel.CleanupPrescanAtStartup);
            Assert.False(string.IsNullOrWhiteSpace(LocalizationService.Instance["CleanupPrescanAtStartupHint"]));
        });

        AssertRendered(result);
    }

    [Fact]
    public void The_cleanup_window_shows_the_stamp_after_a_prescan_and_drops_it_after_a_rescan()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            ElevationService.IsElevatedForTest.Value = false;
            Dialogs.SuppressInteractiveDialogs = true;
            LocalizationService.Instance.Apply("en");

            var logging = new LoggingService();
            var settings = new SettingsService(
                logging, Path.Combine(Directory.CreateTempSubdirectory("wu-prescan-stamp-").FullName, "settings.json"));
            var viewModel = new CleanupViewModel(
                new CleanupPrescanTests.RecordingCleanupService(logging), logging, settings,
                elevated: new ElevatedHost(logging))
            {
                Clock = () => new DateTime(2026, 3, 4, 14, 5, 0),
            };
            WpfHarness.WaitForCleanupCatalog(viewModel);
            viewModel.PrescanAsync().GetAwaiter().GetResult();

            var window = new CleanupWindow(viewModel);
            var label = window.FindName("MeasuredAtLabel") as TextBlock;
            Assert.NotNull(label);

            result = WpfHarness.RenderWindow(window, "cleanup-prescan-stamp-en", 760, 620);

            Assert.Equal(Visibility.Visible, label!.Visibility);
            Assert.True(label.ActualHeight > 0, "the stamp was never laid out");
            Assert.Equal(viewModel.MeasuredAtText, label.Text);
            Assert.Contains(
                new DateTime(2026, 3, 4, 14, 5, 0).ToString("t", System.Globalization.CultureInfo.CurrentUICulture),
                label.Text);

            viewModel.RescanCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(Visibility.Collapsed, label.Visibility);
        });

        AssertRendered(result);
    }
}
