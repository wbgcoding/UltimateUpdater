using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_overall_install_percent_counts_every_finished_row_as_done()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var failed = new UpdateRowViewModel(Item("a", "A", "npm")) { InstallState = RowInstallState.Failed, InstallPercent = 10 };
            var handedOff = new UpdateRowViewModel(Item("b", "B", "steam")) { InstallState = RowInstallState.HandedOff, InstallPercent = 0 };
            var running = new UpdateRowViewModel(Item("c", "C", "npm")) { InstallState = RowInstallState.Running, InstallPercent = 40 };
            viewModel.InstallBatch.Add(failed);
            viewModel.InstallBatch.Add(handedOff);
            viewModel.InstallBatch.Add(running);

            Assert.Equal((100 + 100 + 40) / 3.0, viewModel.InstallOverallPercent, 5);

            running.InstallState = RowInstallState.Cancelled;
            Assert.Equal(100.0, viewModel.InstallOverallPercent, 5);
        });
    }

    [Fact]
    public void The_scan_command_is_always_available_and_is_not_requeried_when_installs_start()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var raised = 0;
            viewModel.ScanCommand.CanExecuteChanged += (_, _) => raised++;

            viewModel.IsInstalling = true;
            Assert.True(viewModel.ScanCommand.CanExecute(null));
            viewModel.IsInstalling = false;

            Assert.Equal(0, raised);
        });
    }

    /// <summary>Closing an owned window hands activation to the next window in z-order, which can be another
    /// application's; the owner is raised again only while it is shown, restored and the app is not exiting.</summary>
    [Theory]
    [InlineData(true, System.Windows.WindowState.Normal, false, true)]
    [InlineData(true, System.Windows.WindowState.Maximized, false, true)]
    [InlineData(false, System.Windows.WindowState.Normal, false, false)]
    [InlineData(true, System.Windows.WindowState.Minimized, false, false)]
    [InlineData(true, System.Windows.WindowState.Normal, true, false)]
    public void An_owned_window_closing_reactivates_only_a_visible_restored_owner(
        bool ownerVisible, System.Windows.WindowState state, bool appExiting, bool expected)
    {
        Assert.Equal(expected, MainViewModel.ShouldActivateOwnerOnClose(ownerVisible, state, appExiting));
    }

    [Fact]
    public void An_independent_window_opens_centred_over_the_main_window()
    {
        var origin = MainViewModel.CenterOver(new System.Windows.Rect(100, 50, 1200, 900), new System.Windows.Size(980, 880));

        Assert.Equal(210, origin.X, 5);
        Assert.Equal(60, origin.Y, 5);
    }

    /// <summary>Starting a scan in the main window used to close the cleanup window, because it was an owned
    /// popup that any click in its owner dismissed. It is a window of its own now; this pins both halves.</summary>
    [Fact]
    public void The_cleanup_window_is_not_closed_by_working_in_the_main_window()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UltimateUpdater.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        var views = Path.Combine(root!.FullName, "src", "UltimateUpdater");
        var window = File.ReadAllText(Path.Combine(views, "Views", "CleanupWindow.xaml.cs"));
        var dialogs = File.ReadAllText(Path.Combine(views, "ViewModels", "MainViewModel.Dialogs.cs"));

        Assert.DoesNotContain("DismissOnDeactivate.Attach", window);
        Assert.Contains("new Views.CleanupWindow(cleanupViewModel), openStarted: openStarted, independent: true", dialogs);
    }
}
