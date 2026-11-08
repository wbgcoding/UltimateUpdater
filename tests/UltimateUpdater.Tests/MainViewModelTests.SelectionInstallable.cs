using UltimateUpdater.Models;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>A ticked row that cannot be installed right now (it is queued or running) must not keep
    /// "Install selected" on or count towards the summary.</summary>
    [Fact]
    public void A_ticked_row_that_cannot_install_does_not_enable_install_selected()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var row = new UpdateRowViewModel(Item("busy.app", "Busy", "winget"));
            viewModel.Updates.Add(row);
            row.IsSelected = true;
            viewModel.RefreshSelectionState();
            Assert.True(viewModel.CanInstallSelected);
            Assert.NotEqual(string.Empty, viewModel.SelectionSummary);

            row.BeginInstall();
            viewModel.RefreshSelectionState();

            Assert.False(row.CanInstall);
            Assert.False(viewModel.CanInstallSelected);
            Assert.Equal(string.Empty, viewModel.SelectionSummary);
        });
    }

    /// <summary>A failed uninstall leaves a row that is installable again; it must not stay ticked, or
    /// "Install selected" would reinstall what the user just tried to remove.</summary>
    [Fact]
    public void A_failed_uninstall_leaves_the_row_unticked_so_install_selected_stays_off()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, source) = CreateGated();
            var row = AddGatedRow(viewModel, "remove.app");
            row.IsSelected = true;
            viewModel.RefreshSelectionState();
            Assert.True(viewModel.CanInstallSelected);

            var uninstall = StartGatedUninstall(viewModel, source, row);
            source.Fail("uninstall", "remove.app");
            RunPumped(uninstall);

            Assert.False(row.IsSelected);
            Assert.False(viewModel.CanInstallSelected);
        });
    }
}
