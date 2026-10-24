using System.Net.Http;
using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>
    /// An interactive retry used to set only <c>IsInstalling</c>, not the
    /// shared activity flag the normal batch loop guards itself with, so a per-row Install click on a
    /// DIFFERENT row while the retry was still running found "no loop running" and started a second,
    /// concurrent one that reset the same shared queue/progress state out from under the first. Proven
    /// here without a real winget process: <see cref="MainViewModel.RetryInteractiveOverride"/> is a
    /// test-only seam that replaces just the OS-level call, so the retry can be held "running" on demand
    /// while everything around it (the gate check, the shared state) is the real production code.
    /// </summary>
    [Fact]
    public void A_per_row_install_click_during_an_interactive_retry_queues_instead_of_racing_a_second_loop()
    {
        // WpfHarness sets Dialogs.SuppressInteractiveDialogs process-wide, which makes App.IsUnattended
        // true for the whole test run, including the interactive retry's own real refusal gate, which
        // this test needs OFF to exercise the real code. Reset in finally: every other test in this
        // (serial) process must keep seeing the normal, actually-unattended behavior.
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var quickSource = new InstantSource();
                var viewModel = Create([new Services.Sources.WingetSource(new LoggingService()), quickSource]);

                var failedRow = new UpdateRowViewModel(Item("Vs.Community", "Visual Studio", "winget"));
                failedRow.ApplyInstallStatus(new InstallItemStatus("winget", failedRow.Item.Id, 0, "failed", false, "failed")
                {
                    Detail = "Installer failed with exit code: 8006",
                });
                Assert.True(failedRow.ShowRetryInteractiveButton); // sanity: the row the retry itself requires

                var otherRow = new UpdateRowViewModel(Item("Other.Tool", "Other Tool", quickSource.Id));
                viewModel.Updates.Add(failedRow);
                viewModel.Updates.Add(otherRow);

                var gate = new TaskCompletionSource();
                viewModel.RetryInteractiveOverride = (_, _, token) => gate.Task.WaitAsync(token);

                var retryTask = viewModel.RetryInteractiveAsync(failedRow);
                Assert.True(viewModel.IsInstalling);

                // The click that used to race: a per-row Install on a completely different row.
                ((IRowActions)viewModel).InstallSingle(otherRow);

                // Blocker detection takes its process snapshot off the UI thread (a real thread pool hop,
                // so a real timed wait beats a tight spin here), and this does not resolve the gated retry
                // above, which is stuck on its own separate gate.Task.
                Pump(WaitUntilAsync(() => otherRow.InstallState != RowInstallState.Idle, TimeSpan.FromSeconds(5)));

                // The bug: this used to start a second RunInstallLoopAsync that installed otherRow
                // instantly (InstantSource has no delay) and then reset IsInstalling to false in ITS OWN
                // finally, while the interactive retry above was still gated and had not finished at all.
                Assert.True(viewModel.IsInstalling, "the interactive retry is still running and must still own IsInstalling");
                Assert.Equal(RowInstallState.Queued, otherRow.InstallState);

                gate.SetResult();
                Pump(retryTask);

                // Hand-off: the queued row must still get installed once the retry is out of the way, not
                // sit forever as "queued".
                for (var i = 0; i < 20 && otherRow.InstallState is RowInstallState.Queued or RowInstallState.Running; i++)
                    DrainDispatcher();
                Assert.False(viewModel.IsInstalling);
                Assert.Equal(RowInstallState.Succeeded, otherRow.InstallState);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>An install source with no delay at all, exists to make a queued row resolve to
    /// "succeeded" within a single dispatcher pump, so the race window above is deterministic.</summary>
    private sealed class InstantSource : IUpdateSource
    {
        public string Id => "instant-test-source";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>An install source whose outcome a test controls between calls: it either throws a
    /// message shaped like a real tool's generic exit-code wording (classifiable by
    /// <see cref="InstallErrorExplainer"/>, so it produces a real signature, not raw unclassified text)
    /// or succeeds, and the exit code baked into the message can change from call to call so a test can
    /// flip the classified signature without swapping the source instance the row is wired to.</summary>
    private sealed class FailingSource : IUpdateSource
    {
        public string Id => "failing-test";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public bool Fail { get; set; } = true;

        public string ExitCode { get; set; } = "424242";

        /// <summary>Off by default: most tests here drive installs directly and never scan. The restart
        /// test turns this on so a fresh view model's scan re-creates the same row from scratch, the way
        /// a real source re-reports a still-available package after an app restart.</summary>
        public bool ReportOnScan { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (ReportOnScan)
                yield return new UpdateItem
                {
                    Id = "Persist.Tool",
                    SourceId = Id,
                    Category = UpdateCategory.Software,
                    Name = "Persistent Tool",
                    InstalledVersion = "1.0",
                    AvailableVersion = "2.0",
                };
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Fail ? throw new Exception($"mock tool (exit code {ExitCode})") : Task.CompletedTask;
    }

    /// <summary>Same wiring as <see cref="Create"/>, but hands back the SettingsService too, needed by
    /// any test that must inspect PersistentFailures/Snoozed directly rather than only through row state.</summary>
    private static MainViewModel CreateWithSettings(IReadOnlyList<IUpdateSource> sources, out SettingsService settings)
    {
        var logging = new LoggingService();
        settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";

        var history = new HistoryService(logging);
        return new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);
    }

    private static void RunInstallToTerminal(MainViewModel viewModel, UpdateRowViewModel row)
    {
        // Blocker detection now takes its process snapshot off the UI thread, so the row may still show
        // its PRE-call state for a while before BeginInstall actually runs; the wait covers that gap too,
        // not just Queued/Running, or it could bail out before the row ever left Idle. A real timed wait,
        // not a tight spin, since that snapshot is a genuine thread pool hop a busy loop can outrun.
        var stateBeforeCall = row.InstallState;
        ((IRowActions)viewModel).InstallSingle(row);
        Pump(WaitForRowToSettleAsync(row, stateBeforeCall));
    }

    /// <summary>Best effort, same as the bounded loop it replaces: gives up quietly after the timeout
    /// rather than failing the test, since some scenarios here legitimately end back at their own starting
    /// state (a row failing the same classified way twice).</summary>
    private static async Task WaitForRowToSettleAsync(UpdateRowViewModel row, RowInstallState stateBeforeCall)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (row.InstallState == stateBeforeCall || row.InstallState is RowInstallState.Queued or RowInstallState.Running)
        {
            if (DateTime.UtcNow > deadline)
                return;

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Three installs that fail the SAME classified way in a row flag the row as needing manual work
    /// instead of leaving it as an ordinary retryable failure forever. The first two
    /// failures must NOT flag it, only the third crosses the threshold.
    /// </summary>
    [Fact]
    public void Three_identical_classified_failures_in_a_row_flag_the_row_as_needing_manual_work()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource();
            var viewModel = Create([source]);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);

            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState);

            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState);

            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.NeedsManualWork, row.InstallState);
        });
    }

    /// <summary>A failure that classifies DIFFERENTLY from the previous one resets the streak
    /// rather than adding to it, two failures of one kind followed by one of another kind must not
    /// combine into a false "three strikes".</summary>
    [Fact]
    public void A_differently_classified_failure_resets_the_streak_instead_of_accumulating()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource { ExitCode = "111111" };
            var viewModel = Create([source]);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);

            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState); // 2 of a kind, not yet flagged

            source.ExitCode = "222222";
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState); // streak reset by the new signature

            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState); // 2 of the NEW kind, still not flagged
        });
    }

    /// <summary>A success clears the record entirely: an update that once failed repeatedly but now
    /// installs cleanly is not still treated as broken.</summary>
    [Fact]
    public void A_success_clears_the_persistent_failure_record_entirely()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource();
            var viewModel = CreateWithSettings([source], out var settings);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);

            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState);

            source.Fail = false;
            RunInstallToTerminal(viewModel, row);

            Assert.Equal(RowInstallState.Succeeded, row.InstallState);
            Assert.False(settings.Current.PersistentFailures.ContainsKey($"{source.Id}:Persist.Tool"));

            // and the streak really is gone, not just paused: two more failures (as if a later version of
            // the same package failed twice, InstallState reset the way a fresh scan would leave it,
            // since EnqueueInstallAsync only re-installs an Idle or Failed row) must not instantly flag it
            source.Fail = true;
            row.InstallState = RowInstallState.Idle;
            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
        });
    }

    /// <summary>The row's own "try normally again" action clears both the flag on the row and the
    /// settings record behind it, leaving only the settings record would silently re-flag the row on the
    /// very next scan.</summary>
    [Fact]
    public void ResetPersistentFailure_clears_both_the_rows_state_and_the_settings_record()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource();
            var viewModel = CreateWithSettings([source], out var settings);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);

            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.NeedsManualWork, row.InstallState);
            Assert.True(row.ShowResetPersistentFailureEntry);

            ((IRowActions)viewModel).ResetPersistentFailure(row);

            Assert.Equal(RowInstallState.Idle, row.InstallState);
            Assert.False(settings.Current.PersistentFailures.ContainsKey($"{source.Id}:Persist.Tool"));
        });
    }

    /// <summary>The visible retry on a NeedsManualWork row clears the flag AND reruns the install in one
    /// click; the row ends Failed (a fresh streak of one) instead of idling after the reset. A plain Failed
    /// row keeps its ordinary Retry and never shows this one.</summary>
    [Fact]
    public void ResetAndRetry_clears_the_needs_manual_work_flag_and_reruns_the_install()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource();
            var viewModel = CreateWithSettings([source], out var settings);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id), actions: viewModel);
            viewModel.Updates.Add(row);

            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
            Assert.False(row.ShowResetAndRetryButton);
            Assert.True(row.ShowRetryButton);

            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.NeedsManualWork, row.InstallState);
            Assert.True(row.ShowResetAndRetryButton);
            Assert.False(row.ShowRetryButton);

            row.ResetAndRetryCommand.Execute(null);
            Pump(WaitForRowToSettleAsync(row, RowInstallState.Idle));

            Assert.Equal(RowInstallState.Failed, row.InstallState);
            Assert.Equal(1, settings.Current.PersistentFailures[$"{source.Id}:Persist.Tool"].Count);
        });
    }

    /// <summary>Regression for a review finding: <see cref="InstallItemStatus"/> carries no action, so the
    /// shared status dispatch cannot itself tell an install's terminal status from an uninstall's, the fix
    /// is a package-key set (populated only while an uninstall is in flight) that TrackPersistentFailure
    /// checks first. Before the fix, three failed UNINSTALLS of the same package could flag it
    /// NeedsManualWork and then refuse to let it be re-installed, and one successful UNINSTALL could
    /// silently wipe out a real install-failure streak. TrackPersistentFailure is private and only reached
    /// through UI paths gated by Dialogs.Confirm (unavailable headless), so the guard is exercised directly
    /// via reflection, exactly what UninstallRowAsync itself does to the same field.</summary>
    [Fact]
    public void TrackPersistentFailure_ignores_a_row_whose_key_is_marked_as_currently_uninstalling()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource();
            var viewModel = CreateWithSettings([source], out var settings);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);
            var key = $"{source.Id}:Persist.Tool";

            // Two real install failures build a streak that must survive the guarded calls below.
            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
            Assert.Equal(2, settings.Current.PersistentFailures[key].Count);

            var uninstallingKeys = (HashSet<string>)typeof(MainViewModel)
                .GetField("_uninstallingKeys", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(viewModel)!;
            var trackPersistentFailure = typeof(MainViewModel)
                .GetMethod("TrackPersistentFailure", BindingFlags.NonPublic | BindingFlags.Instance)!;

            // A third failure while the package is marked as mid-uninstall must NOT push the streak to
            // 3/NeedsManualWork.
            uninstallingKeys.Add(key);
            trackPersistentFailure.Invoke(viewModel, [row]);
            Assert.Equal(2, settings.Current.PersistentFailures[key].Count);
            Assert.Equal(RowInstallState.Failed, row.InstallState);

            // A successful uninstall must NOT wipe the real install-failure record either.
            row.InstallState = RowInstallState.Succeeded;
            trackPersistentFailure.Invoke(viewModel, [row]);
            Assert.True(settings.Current.PersistentFailures.ContainsKey(key));

            uninstallingKeys.Remove(key);
        });
    }

    /// <summary><see cref="PersistentFailureRecord.LastErrorText"/> used to store
    /// <c>row.InstallErrorDetail</c> verbatim (up to 10 M chars, raw tool output) in settings.json, past its
    /// 5 MB limit that silently drops back to defaults, with no redaction of a token or a profile path that
    /// happened to be in there. <see cref="HistoryService.CapDetail"/> already caps and redacts the exact
    /// same kind of text for history entries; this proves TrackPersistentFailure routes through it too.
    /// Reached the same way as the guard test just above: TrackPersistentFailure is private.</summary>
    [Fact]
    public void Persistent_failure_LastErrorText_is_capped_and_redacted_before_it_reaches_settings()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource();
            var viewModel = CreateWithSettings([source], out var settings);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);
            var key = $"{source.Id}:Persist.Tool";

            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.Failed, row.InstallState);

            // Same classifiable shape FailingSource already produces (CliExitCodeRegex only needs to find
            // "(exit code N)" somewhere in the text), padded to 10 M chars with a real profile path mixed
            // in, exactly the two things this fix flags: size and an unredacted path.
            var profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var hugeDetail = "(exit code 424242) " + profilePath + @"\secrets.txt " + new string('x', 10_000_000);
            row.InstallErrorDetail = hugeDetail;

            var trackPersistentFailure = typeof(MainViewModel)
                .GetMethod("TrackPersistentFailure", BindingFlags.NonPublic | BindingFlags.Instance)!;
            trackPersistentFailure.Invoke(viewModel, [row]);

            var stored = settings.Current.PersistentFailures[key].LastErrorText;
            Assert.NotNull(stored);
            Assert.True(stored!.Length < 4100, $"expected the stored detail to be capped, was {stored.Length} chars");
            Assert.DoesNotContain(profilePath, stored, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", stored);
        });
    }

    /// <summary>The flag is not just an in-memory row property, it is derived from the settings
    /// record on load, so it survives a restart the same way Ignore and Snooze do.</summary>
    [Fact]
    public void The_needs_manual_work_state_is_restored_from_settings_after_a_restart()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settingsPath = Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json");
            var settings = new SettingsService(logging, settingsPath);
            settings.Current.Ui.Language = "en";

            var source = new FailingSource();
            var sources = new List<IUpdateSource> { source };
            var history = new HistoryService(logging);
            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);

            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.NeedsManualWork, row.InstallState);

            // A fresh app start: new settings + view model reading the SAME file, then a scan that
            // (re-)constructs the row from scratch, the same way DrainPendingItems does after a restart.
            // Persist() defers to a background write now - flush it before reading the file back, or
            // the reload races the still-pending save.
            settings.Flush();
            var reloadedSettings = new SettingsService(logging, settingsPath);
            var reloadedViewModel = new MainViewModel(
                reloadedSettings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, reloadedSettings, history, logging), history,
                new AutostartService(logging), new NotificationService(reloadedSettings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            source.ReportOnScan = true;
            Pump(reloadedViewModel.ScanCommand.ExecuteAsync(null));

            var reloadedRow = Assert.Single(reloadedViewModel.Updates);
            Assert.Equal(RowInstallState.NeedsManualWork, reloadedRow.InstallState);
        });
    }

    /// <summary>NeedsManualWork is terminal exactly like Succeeded/Failed/HandedOff/Stuck, a row that hits
    /// its third strike mid-batch must still count as "done", or the install strip's "X of Y" counter gets
    /// stuck short for the rest of that batch.</summary>
    [Fact]
    public void InstallDoneCount_counts_a_row_that_reached_needs_manual_work()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FailingSource();
            var viewModel = Create([source]);
            var row = new UpdateRowViewModel(Item("Persist.Tool", "Persistent Tool", source.Id));
            viewModel.Updates.Add(row);

            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            RunInstallToTerminal(viewModel, row);
            Assert.Equal(RowInstallState.NeedsManualWork, row.InstallState);

            Assert.Equal(1, viewModel.InstallDoneCount);
        });
    }

    /// <summary>A row the user stopped is terminal too; leaving it out kept the install strip at "2 of 3"
    /// after the last row of a batch had been stopped.</summary>
    [Fact]
    public void InstallDoneCount_counts_a_cancelled_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var row = new UpdateRowViewModel(Item("Stopped.Tool", "Stopped Tool", "winget"));
            row.ApplyInstallStatus(new InstallItemStatus("winget", "Stopped.Tool", 0, null, null, null) { IsCancelled = true });
            viewModel.InstallBatch.Add(row);

            Assert.Equal(1, viewModel.InstallDoneCount);
        });
    }

    /// <summary>Interactive retry is a separate, standalone entry point that never goes through the normal
    /// batch loop's status dispatch (Install.cs's ReportInstallStatus), that dispatch is the only other
    /// place TrackPersistentFailure was wired in, so an interactively-retried package could fail the exact
    /// same classified way forever without ever reaching NeedsManualWork.</summary>
    [Fact]
    public void RetryInteractiveAsync_failures_count_toward_the_persistent_failure_streak()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create([new Services.Sources.WingetSource(new LoggingService())]);
                var row = new UpdateRowViewModel(Item("Vs.Community", "Visual Studio", "winget"));
                row.ApplyInstallStatus(new InstallItemStatus("winget", row.Item.Id, 0, "failed", false, "failed")
                {
                    Detail = "Installer failed with exit code: 8006",
                });
                viewModel.Updates.Add(row);
                Assert.True(row.ShowRetryInteractiveButton); // sanity: the gate the retry itself requires

                viewModel.RetryInteractiveOverride = (_, _, _) =>
                    throw new Exception("Installer failed with exit code: 8006");

                Pump(viewModel.RetryInteractiveAsync(row));
                Pump(viewModel.RetryInteractiveAsync(row));
                Assert.Equal(RowInstallState.Failed, row.InstallState); // 2 of a kind, not yet flagged

                Pump(viewModel.RetryInteractiveAsync(row));
                Assert.Equal(RowInstallState.NeedsManualWork, row.InstallState);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>SelectionSummary/CanInstallSelected/AllVisibleSelected/HasSelection are all computed from
    /// VisibleRows, but a filter change (search text, a category toggle, hide-ignored) does not touch any
    /// row's own properties, only ApplyFilter's own RefreshSelectionState call notifies these; nothing
    /// about the filter change itself does. Without it, selecting rows and then typing a search that
    /// changes what is visible left the summary text/count showing the stale, pre-filter selection until
    /// some unrelated event happened to refresh it.</summary>
    [Fact]
    public void Changing_the_search_filter_refreshes_the_selection_summary_properties()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new InstantSource()]);
            var row = new UpdateRowViewModel(Item("Some.Tool", "Some Tool", "instant"));
            viewModel.Updates.Add(row);
            row.IsSelected = true;

            var raised = new List<string>();
            viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName ?? string.Empty);

            viewModel.SearchQuery = "no-such-package";

            Assert.Contains(nameof(MainViewModel.SelectionSummary), raised);
        });
    }
}
