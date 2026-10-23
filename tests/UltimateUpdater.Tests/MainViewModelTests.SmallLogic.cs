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
}
