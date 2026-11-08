using System.Windows;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Xunit;

namespace UltimateUpdater.Tests;

[Collection("wpf")]
public class CleanupWindowHistoryTests
{
    /// <summary>The history button and the report button open the same window: asking again, by either
    /// route, brings the open one forward rather than stacking a second copy on top of it.</summary>
    [Fact]
    public void Asking_for_the_history_twice_opens_one_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(
                logging, Path.Combine(Directory.CreateTempSubdirectory("wu-hist-").FullName, "settings.json"));
            var history = new CleanupHistoryService(
                logging, Path.Combine(Directory.CreateTempSubdirectory("wu-hist-data-").FullName, "history.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings, history: history);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            var window = new CleanupWindow(viewModel) { ShowInTaskbar = false };
            window.Show();
            try
            {
                Click(window, "OnShowHistory");
                Click(window, "OnShowHistory");
                Click(window, "OnShowReport");

                Assert.Single(window.OwnedWindows.OfType<CleanupHistoryWindow>());
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The real click handlers, so the buttons' wiring is what is proven.</summary>
    private static void Click(CleanupWindow window, string handler) =>
        typeof(CleanupWindow)
            .GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()]);
}
