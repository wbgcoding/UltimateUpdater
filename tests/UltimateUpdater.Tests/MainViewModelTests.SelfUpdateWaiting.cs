using System.Reflection;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    private static AvailableUpdate OfferedUpdate() => new(
        "999.0.0",
        new ReleaseAsset("UltimateUpdater.exe", "https://github.com/x/y/a.exe", "https://api.github.com/a", 10),
        new ReleaseAsset("SHA256SUMS.txt", "https://github.com/x/y/s.txt", "https://api.github.com/s", 10),
        string.Empty);

    /// <summary>The download finishes while an install batch is running: applying would end the process and
    /// kill that batch half way, so the verified file waits and installs once the work is over.</summary>
    [Fact]
    public void A_finished_download_waits_for_a_running_install_and_then_applies_once()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var applied = new List<string>();
            var exited = new TaskCompletionSource();
            viewModel.OfferSelfUpdateForTest(OfferedUpdate());
            viewModel.SelfUpdateDownloadForTest = (_, _) =>
            {
                // the user started an install while the download was still running
                viewModel.IsInstalling = true;
                return Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
            };
            viewModel.SelfUpdateApplyForTest = path =>
            {
                applied.Add(path);
                return true;
            };
            viewModel.SelfUpdateExitForTest = () => exited.TrySetResult();

            Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));

            Assert.Empty(applied);
            Assert.True(viewModel.SelfUpdateWaitingForWork);
            Assert.False(exited.Task.IsCompleted);

            viewModel.IsInstalling = false;
            Pump(exited.Task);

            Assert.Equal(["verified.exe"], applied);
            Assert.False(viewModel.SelfUpdateWaitingForWork);

            // a second change notification must not apply again
            viewModel.IsInstalling = true;
            viewModel.IsInstalling = false;
            Assert.Single(applied);
        });
    }

    /// <summary>A solo activity (uninstall, rollback, interactive retry) clears the installing flag a moment
    /// before it hands rows queued meanwhile to the next batch. The waiting update must see that work too,
    /// or it would end the process just as that batch starts.</summary>
    [Fact]
    public void A_waiting_update_is_not_applied_while_rows_are_handed_to_the_next_batch()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var applied = new List<string>();
            var exited = new TaskCompletionSource();
            viewModel.OfferSelfUpdateForTest(OfferedUpdate());
            viewModel.SelfUpdateDownloadForTest = (_, _) =>
            {
                viewModel.IsInstalling = true;
                return Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
            };
            viewModel.SelfUpdateApplyForTest = path =>
            {
                applied.Add(path);
                return true;
            };
            viewModel.SelfUpdateExitForTest = () => exited.TrySetResult();

            Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));
            Assert.True(viewModel.SelfUpdateWaitingForWork);

            var pending = (List<UpdateRowViewModel>)typeof(MainViewModel)
                .GetField("_installPending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel)!;

            // The flag clears and, in the same step, a row queued meanwhile is waiting for the next batch.
            viewModel.IsInstalling = false;
            pending.Add(new UpdateRowViewModel(Item("vlc", "VLC", "winget")));
            DrainDispatcher();

            Assert.Empty(applied);
            Assert.True(viewModel.SelfUpdateWaitingForWork);

            // That batch ran and ended: now the update applies.
            pending.Clear();
            viewModel.IsInstalling = true;
            viewModel.IsInstalling = false;
            Pump(exited.Task);

            Assert.Equal(["verified.exe"], applied);
        });
    }

    /// <summary>Applying ends the process, so an install requested while it runs is not started at all
    /// rather than being killed half way.</summary>
    [Fact]
    public void No_install_starts_while_the_update_is_being_applied()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var exited = new TaskCompletionSource();
            using var releaseApply = new ManualResetEventSlim();
            viewModel.OfferSelfUpdateForTest(OfferedUpdate());
            viewModel.SelfUpdateDownloadForTest = (_, _) =>
                Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
            viewModel.SelfUpdateApplyForTest = _ =>
            {
                releaseApply.Wait(TimeSpan.FromSeconds(10));
                return true;
            };
            viewModel.SelfUpdateExitForTest = () => exited.TrySetResult();

            var applying = viewModel.InstallSelfUpdateCommand.ExecuteAsync(null);

            var row = new UpdateRowViewModel(Item("vlc", "VLC", "winget"));
            viewModel.Updates.Add(row);
            InvokeEnqueueInstall(viewModel, [row]);

            Assert.Equal(RowInstallState.Idle, row.InstallState);
            Assert.False(viewModel.IsInstalling);

            releaseApply.Set();
            Pump(applying);
            Pump(exited.Task);
        });
    }

    /// <summary>The user is told why nothing started, instead of the rows silently staying idle.</summary>
    [Fact]
    public void A_refused_install_tells_the_user_the_app_is_restarting()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var exited = new TaskCompletionSource();
            using var releaseApply = new ManualResetEventSlim();
            var notices = new List<string>();
            UltimateUpdater.Services.Dialogs.InfoObserverForTests = notices.Add;
            try
            {
                viewModel.OfferSelfUpdateForTest(OfferedUpdate());
                viewModel.SelfUpdateDownloadForTest = (_, _) =>
                    Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
                viewModel.SelfUpdateApplyForTest = _ =>
                {
                    releaseApply.Wait(TimeSpan.FromSeconds(10));
                    return true;
                };
                viewModel.SelfUpdateExitForTest = () => exited.TrySetResult();

                var applying = viewModel.InstallSelfUpdateCommand.ExecuteAsync(null);
                var row = new UpdateRowViewModel(Item("vlc", "VLC", "winget"));
                viewModel.Updates.Add(row);
                InvokeEnqueueInstall(viewModel, [row]);

                Assert.Equal([UltimateUpdater.Services.LocalizationService.Instance["SelfUpdateApplyingNoInstall"]], notices);

                releaseApply.Set();
                Pump(applying);
                Pump(exited.Task);
            }
            finally
            {
                UltimateUpdater.Services.Dialogs.InfoObserverForTests = null;
            }
        });
    }

    /// <summary>The pre-flight window of an install request is work too: while it is up, a download that
    /// finishes waits instead of applying and ending the app under the open dialog.</summary>
    [Fact]
    public void An_open_preflight_window_holds_back_a_finished_update()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var blockers = new UltimateUpdater.Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]);
                var viewModel = Create(installBlockers: blockers);
                var workDuringWindow = false;
                var workProperty = typeof(MainViewModel).GetProperty("WorkIsRunning", BindingFlags.NonPublic | BindingFlags.Instance)!;

                var row = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
                viewModel.Updates.Add(row);
                viewModel.InstallPreflightOverride = _ =>
                {
                    workDuringWindow = (bool)workProperty.GetValue(viewModel)!;
                    return false;
                };

                InvokeEnqueueInstall(viewModel, [row]);

                Assert.True(workDuringWindow);
                Assert.False((bool)workProperty.GetValue(viewModel)!);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    [Fact]
    public void A_finished_download_with_no_work_running_applies_at_once()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var applied = new List<string>();
            var exited = new TaskCompletionSource();
            viewModel.OfferSelfUpdateForTest(OfferedUpdate());
            viewModel.SelfUpdateDownloadForTest = (_, _) =>
                Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
            viewModel.SelfUpdateApplyForTest = path =>
            {
                applied.Add(path);
                return true;
            };
            viewModel.SelfUpdateExitForTest = () => exited.TrySetResult();

            Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));
            Pump(exited.Task);

            Assert.Equal(["verified.exe"], applied);
            Assert.False(viewModel.SelfUpdateWaitingForWork);
        });
    }

    /// <summary>A drive optimisation is not an install or a cleanup flag, yet quitting kills it: the update
    /// waits for it too and starts as soon as the last run ends.</summary>
    [Fact]
    public void A_finished_download_also_waits_for_a_running_drive_optimisation()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var applied = new List<string>();
            var exited = new TaskCompletionSource();
            var run = UltimateUpdater.Services.Cleanup.DefragRunState.Enter();
            try
            {
                viewModel.OfferSelfUpdateForTest(OfferedUpdate());
                viewModel.SelfUpdateDownloadForTest = (_, _) =>
                    Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
                viewModel.SelfUpdateApplyForTest = path =>
                {
                    applied.Add(path);
                    return true;
                };
                viewModel.SelfUpdateExitForTest = () => exited.TrySetResult();

                Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));

                Assert.Empty(applied);
                Assert.True(viewModel.SelfUpdateWaitingForWork);
            }
            finally
            {
                run.Dispose();
            }

            Pump(exited.Task);
            Assert.Equal(["verified.exe"], applied);
        });
    }
}
