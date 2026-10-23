using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    private static UpdateRowViewModel FailedVisualStudioRow()
    {
        var row = new UpdateRowViewModel(Item("Vs.Community", "Visual Studio", "winget"));
        row.ApplyInstallStatus(new InstallItemStatus("winget", row.Item.Id, 0, "failed", false, "failed")
        {
            Detail = "Installer failed with exit code: 8006",
        });
        return row;
    }

    [Fact]
    public void An_interactive_retry_during_other_install_activity_explains_why_it_does_not_start()
    {
        App.ForceAttendedForTest = true;
        var warnings = new List<string>();
        Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create([new Services.Sources.WingetSource(new LoggingService())]);
                var row = FailedVisualStudioRow();
                Assert.True(row.ShowRetryInteractiveButton);
                viewModel.Updates.Add(row);
                SetInstallActivityRunning(viewModel, true);

                Pump(viewModel.RetryInteractiveAsync(row));

                Assert.Equal([LocalizationService.Instance["RetryBlockedByActivity"]], warnings);
                SetInstallActivityRunning(viewModel, false);
            });
        }
        finally
        {
            Dialogs.WarnObserverForTests = null;
            App.ForceAttendedForTest = false;
        }
    }

    [Fact]
    public void An_interactive_retry_in_an_unattended_run_stays_silent()
    {
        var warnings = new List<string>();
        Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create([new Services.Sources.WingetSource(new LoggingService())]);
                var row = FailedVisualStudioRow();
                viewModel.Updates.Add(row);
                SetInstallActivityRunning(viewModel, true);

                Pump(viewModel.RetryInteractiveAsync(row));

                Assert.Empty(warnings);
                SetInstallActivityRunning(viewModel, false);
            });
        }
        finally
        {
            Dialogs.WarnObserverForTests = null;
        }
    }
}
