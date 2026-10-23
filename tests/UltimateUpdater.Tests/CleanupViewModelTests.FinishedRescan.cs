using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    /// <summary>A finished run leaves the dialog with nothing to press: Rescan has to be there too, and a
    /// fresh measurement takes the dialog back to choosing, with the old run's capsules gone.</summary>
    [Fact]
    public async Task Rescan_is_available_once_a_run_is_finished_and_returns_to_choosing()
    {
        var viewModel = Create(CreateSettings(), CreateHistory(), new InstantCleanupService(new LoggingService()));
        await viewModel.StartScanAsync();

        var item = viewModel.Items.First();
        item.RunState = CleanupTargetState.Done;
        viewModel.Phase = CleanupPhase.Finished;

        Assert.True(viewModel.RescanCommand.CanExecute(null));
        Assert.True(viewModel.ShowRescanButton);
        Assert.False(viewModel.IsChoosing);

        await viewModel.RescanCommand.ExecuteAsync(null);

        Assert.Equal(CleanupPhase.Choosing, viewModel.Phase);
        Assert.Equal(CleanupTargetState.Idle, item.RunState);
        Assert.False(viewModel.IsFinished);
    }

    [Fact]
    public async Task Rescan_is_not_available_while_a_run_is_going()
    {
        var viewModel = Create(CreateSettings(), CreateHistory(), new InstantCleanupService(new LoggingService()));
        await viewModel.StartScanAsync();

        viewModel.Phase = CleanupPhase.Cleaning;

        Assert.False(viewModel.RescanCommand.CanExecute(null));
        Assert.False(viewModel.ShowRescanButton);
    }
}
