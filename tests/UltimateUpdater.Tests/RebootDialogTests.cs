using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// One collected restart prompt at the end of a batch, never per row and never mid-batch.
/// <see cref="MainViewModel.ShowRebootDialogIfNeeded"/> is called directly (see its own doc comment) since
/// <see cref="MockUpdateSource"/> has no way to report <c>RebootRequired</c> through a real batch run, and
/// <see cref="MainViewModel.MainWindowVisibleOverride"/> stands in for a real window (constructing one
/// against the shared test-process <c>Application</c> proved unsafe, see that seam's own doc comment).
/// </summary>
[Collection("wpf")]
public class RebootDialogTests
{
    private static MainViewModel Create()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-reboot-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";

        var sources = MockUpdateSource.CreateAll();
        var history = new HistoryService(logging);

        return new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new Services.SelfUpdate.SelfUpdateService(
                new System.Net.Http.HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
            sources);
    }

    [Fact]
    public void The_names_are_collected_regardless_of_window_visibility()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.MainWindowVisibleOverride = () => false;
            viewModel.ShowRebootDialogIfNeeded(["Git", "Node.js"]);

            Assert.Equal(["Git", "Node.js"], viewModel.RebootRequestedBy);
        });
    }

    /// <summary>Hidden to tray: the dialog seam must never fire (nobody is watching for it), the
    /// balloon-equivalent notification is what carries the news instead.</summary>
    [Fact]
    public void A_hidden_window_means_no_dialog_and_a_notification_instead()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.MainWindowVisibleOverride = () => false;

            var dialogShown = false;
            viewModel.RebootDialogOverride = _ => { dialogShown = true; return false; };

            var notified = false;
            var handler = new Action<string, string>((_, _) => notified = true);
            var notifications = GetNotificationService(viewModel);
            notifications.NotificationRequested += handler;
            try
            {
                viewModel.ShowRebootDialogIfNeeded(["Git"]);
            }
            finally
            {
                notifications.NotificationRequested -= handler;
            }

            Assert.False(dialogShown);
            Assert.True(notified);
        });
    }

    /// <summary>A visible window: the dialog seam fires with exactly the names collected, and its answer
    /// (here, "later") is read back without starting a real restart.</summary>
    [Fact]
    public void A_visible_window_shows_the_dialog_with_every_collected_name()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.MainWindowVisibleOverride = () => true;

            IReadOnlyList<string>? seen = null;
            viewModel.RebootDialogOverride = names => { seen = names; return false; };

            viewModel.ShowRebootDialogIfNeeded(["Git", "Node.js", "7-Zip"]);

            Assert.Equal(["Git", "Node.js", "7-Zip"], seen);
        });
    }

    private static NotificationService GetNotificationService(MainViewModel viewModel)
    {
        var field = typeof(MainViewModel).GetField("_notifications",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (NotificationService)field.GetValue(viewModel)!;
    }

    /// <summary>
    /// The VM's own "install all" run put both windowsupdate mock rows (a Defender KB and a
    /// Windows KB, same source, same sourceId, both reboot-required) through ONE real batch (select all,
    /// one InstallSelected click) and got TWO restart dialogs instead of one. This drives the real
    /// MainViewModel/InstallQueue path (not the direct-call seam the tests above use) with --mock-sources'
    /// own isMockSession flag (App.xaml.cs), the same flag the VM run itself was started with, and counts
    /// how many times the dialog seam actually fires.
    /// </summary>
    [Fact]
    public void A_single_select_all_install_batch_shows_one_dialog_for_both_reboot_rows()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-reboot-batch-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            // Only the reboot-requiring source, so the batch has nothing else to wait on.
            var sources = MockUpdateSource.CreateAll().Where(s => s.Id == "windowsupdate").ToList();
            var history = new HistoryService(logging);
            var installQueue = new InstallQueue(sources, settings, history, logging, isMockSession: true);

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), installQueue, history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new Services.SelfUpdate.SelfUpdateService(
                    new System.Net.Http.HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
                sources)
            {
                MainWindowVisibleOverride = () => true,
            };

            var dialogCalls = new List<IReadOnlyList<string>>();
            viewModel.RebootDialogOverride = names =>
            {
                dialogCalls.Add(names);
                return false;
            };

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            Assert.Equal(2, viewModel.Updates.Count);

            // Both mock rows are critical, so a fresh scan pre-selects them on its own
            // (Settings.Install.PreselectCritical, on by default) - no separate "select all" click needed,
            // and the batch below is exactly what a real "install all" run puts through in one go.
            Assert.All(viewModel.Updates, r => Assert.True(r.IsSelected));

            viewModel.InstallSelectedCommand.Execute(null);

            // The install loop itself is fire-and-forget (Background.Run); wait for it on the real
            // dispatcher rather than the enqueue step alone, which returns long before either row finishes.
            Pump(WaitForFullInstallCycleAsync(viewModel, TimeSpan.FromSeconds(10)));

            Assert.All(viewModel.Updates, r => Assert.Equal(RowInstallState.Succeeded, r.InstallState));
            Assert.Single(dialogCalls);
            Assert.Equal(2, dialogCalls[0].Count);
        });
    }

    /// <summary>
    /// Same as above but with the FULL --mock-sources roster (matching the VM run exactly): the winget
    /// rows land in MainViewModel's own admin-classified set alongside the two windowsupdate rows (both
    /// use the static <c>InstallQueue.NeedsElevation</c> check), while the pip rows land in its user-scope
    /// set - RunInstallLoopAsync fires TWO concurrent <c>InstallQueue.RunAsync</c> calls for one batch
    ///. This is the "sub-batch" split the plan item's own Umsetzung note calls out as a
    /// suspect; if that split were what causes the extra dialog, this test would show more than one.
    /// </summary>
    [Fact]
    public void A_mixed_admin_and_user_scope_batch_still_shows_one_dialog()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-reboot-mixed-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            var sources = MockUpdateSource.CreateAll();
            var history = new HistoryService(logging);
            var installQueue = new InstallQueue(sources, settings, history, logging, isMockSession: true);

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), installQueue, history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new Services.SelfUpdate.SelfUpdateService(
                    new System.Net.Http.HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
                sources)
            {
                MainWindowVisibleOverride = () => true,
            };

            var dialogCalls = new List<IReadOnlyList<string>>();
            viewModel.RebootDialogOverride = names =>
            {
                dialogCalls.Add(names);
                return false;
            };

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            // winget (4) + windowsupdate (2) + pip (2) = 8 installable rows; npm is empty, scoop fails,
            // steam/msstore/battlenet are Tier C (CanInstall false, never selected).
            var installable = viewModel.UpdatesView.Cast<UpdateRowViewModel>().Where(u => u.CanInstall).ToList();
            Assert.Equal(8, installable.Count);
            foreach (var row in installable.Where(r => !r.IsSelected))
                row.IsSelected = true;

            viewModel.InstallSelectedCommand.Execute(null);

            Pump(WaitForFullInstallCycleAsync(viewModel, TimeSpan.FromSeconds(15)));

            var rebootRows = viewModel.Updates.Where(r => r.NeedsReboot).ToList();
            Assert.Equal(2, rebootRows.Count);
            Assert.Single(dialogCalls);
            Assert.Equal(2, dialogCalls[0].Count);
        });
    }

    /// <summary>
    /// This fix's other own interaction shape: two per-row Installieren clicks, the second issued only
    /// once the first row's own install has fully finished (and its own solo dialog already fired). Locks
    /// in that this is legitimately TWO batches by the current "once per batch" rule (_installActivityRunning
    /// has already gone back to false by the time the second click lands), distinct from the two tests
    /// above, which prove a single batch (select-all-then-install, admin+user-scope mixed) always still
    /// shows exactly one dialog with every collected name.
    /// </summary>
    [Fact]
    public void Two_sequential_non_overlapping_row_clicks_are_two_separate_batches_by_design()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-reboot-sequential-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            var sources = MockUpdateSource.CreateAll().Where(s => s.Id == "windowsupdate").ToList();
            var history = new HistoryService(logging);
            var installQueue = new InstallQueue(sources, settings, history, logging, isMockSession: true);

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), installQueue, history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new Services.SelfUpdate.SelfUpdateService(
                    new System.Net.Http.HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
                sources)
            {
                MainWindowVisibleOverride = () => true,
            };

            var dialogCalls = new List<IReadOnlyList<string>>();
            viewModel.RebootDialogOverride = names =>
            {
                dialogCalls.Add(names);
                return false;
            };

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var rowA = viewModel.Updates[0];
            var rowB = viewModel.Updates[1];

            ((IRowActions)viewModel).InstallSingle(rowA);
            Pump(WaitForFullInstallCycleAsync(viewModel, TimeSpan.FromSeconds(10)));
            Assert.Single(dialogCalls);

            ((IRowActions)viewModel).InstallSingle(rowB);
            Pump(WaitForFullInstallCycleAsync(viewModel, TimeSpan.FromSeconds(10)));

            // Two dialogs, one per row - each one's own solo click, fully finished before the next
            // started. Not the bug the plan item describes (that is a SINGLE batch showing two), but the
            // shape the VM run may actually have driven if it clicked Installieren per row rather than
            // using select-all.
            Assert.Equal(2, dialogCalls.Count);
        });
    }

    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was never met.");

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits for a whole install click to run its course: blocker detection takes its process snapshot off
    /// the UI thread now, so <c>IsInstalling</c> can still read false for a moment right after the click,
    /// and polling for "not installing" alone could read that gap as already finished. This instead waits
    /// for the flag to have been seen true at some point, then waits for it to go false again.
    /// </summary>
    private static async Task WaitForFullInstallCycleAsync(MainViewModel viewModel, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var seenRunning = false;
        while (true)
        {
            if (viewModel.IsInstalling)
                seenRunning = true;
            else if (seenRunning)
                return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The install never started, or never finished.");

            await Task.Delay(2);
        }
    }
}
