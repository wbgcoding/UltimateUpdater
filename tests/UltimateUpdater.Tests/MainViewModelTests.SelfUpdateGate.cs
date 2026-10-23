using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Self_update_cannot_start_while_updates_are_installing_and_flips_back_after()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var raised = 0;
            viewModel.InstallSelfUpdateCommand.CanExecuteChanged += (_, _) => raised++;

            Assert.True(viewModel.InstallSelfUpdateCommand.CanExecute(null));

            viewModel.IsInstalling = true;
            Assert.False(viewModel.InstallSelfUpdateCommand.CanExecute(null));

            viewModel.IsInstalling = false;
            Assert.True(viewModel.InstallSelfUpdateCommand.CanExecute(null));
            Assert.True(raised >= 2);
        });
    }

    [Fact]
    public void Self_update_cannot_start_while_a_cleanup_runs()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();

            viewModel.IsCleanupRunning = true;
            Assert.False(viewModel.InstallSelfUpdateCommand.CanExecute(null));

            viewModel.IsCleanupRunning = false;
            Assert.True(viewModel.InstallSelfUpdateCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Self_update_cannot_start_while_it_is_already_in_progress()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();

            viewModel.SelfUpdateInProgress = true;
            Assert.False(viewModel.InstallSelfUpdateCommand.CanExecute(null));

            viewModel.SelfUpdateInProgress = false;
            Assert.True(viewModel.InstallSelfUpdateCommand.CanExecute(null));
        });
    }
}
