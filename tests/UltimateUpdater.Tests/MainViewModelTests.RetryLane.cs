using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>
    /// An interactive retry runs winget in-process, outside the install queue. A side uninstall of ANOTHER
    /// package of the same manager would run beside it and collide on the manager's single-operation lock,
    /// so it is refused for as long as the retry runs; a package of another manager is not affected.
    /// </summary>
    [Fact]
    public void A_side_uninstall_in_the_same_manager_is_refused_while_an_interactive_retry_runs()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create([new Services.Sources.WingetSource(new LoggingService()), new InstantSource()]);

                var failedRow = new UpdateRowViewModel(Item("Vs.Community", "Visual Studio", "winget"));
                failedRow.ApplyInstallStatus(new InstallItemStatus("winget", failedRow.Item.Id, 0, "failed", false, "failed")
                {
                    Detail = "Installer failed with exit code: 8006",
                });
                var sameManager = new UpdateRowViewModel(Item("Other.Tool", "Other Tool", "winget"));
                var otherManager = new UpdateRowViewModel(Item("Quick.Tool", "Quick Tool", new InstantSource().Id));
                viewModel.Updates.Add(failedRow);
                viewModel.Updates.Add(sameManager);
                viewModel.Updates.Add(otherManager);

                var blocked = typeof(MainViewModel).GetMethod(
                    "UninstallBlockedForRow", BindingFlags.NonPublic | BindingFlags.Instance)!;
                bool IsBlocked(UpdateRowViewModel row) => (bool)blocked.Invoke(viewModel, [row])!;

                Assert.False(IsBlocked(sameManager));

                var gate = new TaskCompletionSource();
                viewModel.RetryInteractiveOverride = (_, _, token) => gate.Task.WaitAsync(token);
                var retryTask = viewModel.RetryInteractiveAsync(failedRow);

                Assert.True(IsBlocked(sameManager), "winget is busy with the retry");
                Assert.False(IsBlocked(otherManager), "another manager has its own lane");

                gate.SetResult();
                Pump(retryTask);

                Assert.False(IsBlocked(sameManager), "the retry is over");
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }
}
