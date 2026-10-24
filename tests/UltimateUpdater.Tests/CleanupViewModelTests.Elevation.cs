using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    private static string FindViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Views directory from the test output path.");
    }

    /// <summary>No elevation service at all (the exact case originally reported) must leave
    /// a status line behind instead of the button silently doing nothing.</summary>
    [Fact]
    public async Task AnalyzeComponentStore_with_no_elevation_service_shows_the_unavailable_status()
    {
        var viewModel = Create(CreateSettings());

        await viewModel.AnalyzeComponentStoreCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance["CleanupComponentStoreUnavailable"], viewModel.ComponentStoreStatusText);
    }

    /// <summary>The status line is shown while Settings (non-modal) can switch the language; it must
    /// follow the switch like every other text in this dialog.</summary>
    [Fact]
    public async Task The_component_store_status_follows_a_language_switch()
    {
        LocalizationService.Instance.Apply("de");
        var viewModel = Create(CreateSettings());
        await viewModel.AnalyzeComponentStoreCommand.ExecuteAsync(null);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        LocalizationService.Instance.Apply("en");
        viewModel.RefreshLocalizedText();

        try
        {
            Assert.Contains(nameof(viewModel.ComponentStoreStatusText), raised);
            Assert.Equal(LocalizationService.Instance["CleanupComponentStoreUnavailable"], viewModel.ComponentStoreStatusText);
        }
        finally
        {
            LocalizationService.Instance.Apply("de");
        }
    }

    /// <summary>Declining the UAC prompt (headless tests always decline, see
    /// <c>Dialogs.SuppressInteractiveDialogs</c>) must leave the OTHER status line behind, distinct from
    /// "helper not running".</summary>
    [Fact]
    public async Task AnalyzeComponentStore_with_a_declined_prompt_shows_the_needs_admin_status()
    {
        // An elevated test runner would otherwise skip the prompt and never show the hint.
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        await viewModel.AnalyzeComponentStoreCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance["CleanupComponentStoreNeedsAdmin"], viewModel.ComponentStoreStatusText);
    }

    /// <summary>A worker launch that fails for a reason other than a declined prompt must end as the
    /// unavailable status and a cleared busy flag, never as an exception out of the fire-and-forget command.</summary>
    [Fact]
    public async Task AnalyzeComponentStore_swallows_a_launch_failure_and_shows_the_unavailable_status()
    {
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        elevated.SetRunningForTest(true);
        elevated.EnsureStartedOverrideForTest = _ => throw new InvalidOperationException("launch failed");
        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        await viewModel.AnalyzeComponentStoreCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance["CleanupComponentStoreUnavailable"], viewModel.ComponentStoreStatusText);
        Assert.False(viewModel.IsAnalyzingComponentStore);
    }

    /// <summary>A running component store analysis is work of this view model: reopening the dialog must
    /// reuse it rather than start a second analysis, so IsBusy has to cover it from start to end.</summary>
    [Fact]
    public async Task IsBusy_covers_a_running_component_store_analysis()
    {
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        elevated.SetRunningForTest(true);
        var release = new TaskCompletionSource<bool>();
        elevated.EnsureStartedOverrideForTest = _ => release.Task;
        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        var running = viewModel.AnalyzeComponentStoreCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBusy);

        release.SetResult(false);
        await running;

        Assert.False(viewModel.IsBusy);
    }

    /// <summary>An elevation-needing row must not say "needs admin rights" while the app-wide
    /// elevated helper is already running from an earlier action in the same session, and the state must
    /// be read live rather than only fixed once by the scan's own elevation step.</summary>
    [Fact]
    public void ShowsElevationHint_is_hidden_when_the_elevated_helper_is_already_running()
    {
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        elevated.SetRunningForTest(true);

        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        Assert.All(viewModel.Items.Where(i => i.NeedsElevation), row => Assert.False(row.ShowsElevationHint));
    }

    /// <summary>The same row must flip live: the helper stopping or starting after the dialog is already
    /// open changes the mark without a rescan.</summary>
    [Fact]
    public void ShowsElevationHint_follows_the_helper_starting_and_stopping_live()
    {
        // An elevated test runner would otherwise skip the prompt and never show the hint.
        ElevationService.IsElevatedForTest.Value = false;
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);

        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        var row = viewModel.Items.First(i => i.NeedsElevation);
        Assert.True(row.ShowsElevationHint);

        elevated.SetRunningForTest(true);
        WpfHarness.DrainViewModelDispatcher();
        Assert.False(row.ShowsElevationHint);

        elevated.SetRunningForTest(false);
        WpfHarness.DrainViewModelDispatcher();
        Assert.True(row.ShowsElevationHint);
    }

    /// <summary>A dialog reopened onto a still-busy, reused view model logged "first frame after 90898 ms":
    /// the open timer ran from the first open. Attaching a window starts it over.</summary>
    [Fact]
    public void Reattaching_a_window_restarts_the_open_timer()
    {
        var viewModel = Create(CreateSettings());
        WpfHarness.WaitForCleanupCatalog(viewModel);
        Thread.Sleep(300);

        viewModel.WindowAttached();

        Assert.True(viewModel.OpenElapsedMilliseconds < 200, $"open timer at {viewModel.OpenElapsedMilliseconds} ms");
    }

    /// <summary>The helper's pipe thread raises the state change while the UI thread may be waiting on that very
    /// thread (app exit shutting the helper down). The notification must be posted, not waited on.</summary>
    [Fact]
    public void A_helper_state_change_raised_off_thread_never_waits_for_the_blocked_ui_thread()
    {
        ElevationService.IsElevatedForTest.Value = false;
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        var row = viewModel.Items.First(i => i.NeedsElevation);

        // This thread plays the UI thread: it blocks right here while the other thread raises the change.
        // The block is the point of the test, so an awaiting async test would not reproduce it.
#pragma warning disable xUnit1031
        var raised = Task.Run(() => elevated.SetRunningForTest(true)).Wait(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1031

        Assert.True(raised, "raising the state change blocked on the UI thread");
        WpfHarness.DrainViewModelDispatcher();
        Assert.False(row.ShowsElevationHint);
    }

    /// <summary>Closing the dialog must not cut this instance off from
    /// the helper for good, because ShowCleanup() reuses the very same instance for the next window while
    /// a scan or cleanup is still busy; the badge has to keep following the helper start/stop for as long
    /// as this instance is the one still in play.</summary>
    [Fact]
    public void ShowsElevationHint_keeps_following_the_helper_after_the_window_closes()
    {
        // An elevated test runner would otherwise skip the prompt and never show the hint.
        ElevationService.IsElevatedForTest.Value = false;
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);

        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        var row = viewModel.Items.First(i => i.NeedsElevation);

        viewModel.WindowClosed();

        elevated.SetRunningForTest(true);
        WpfHarness.DrainViewModelDispatcher();
        Assert.False(row.ShowsElevationHint);
    }

    /// <summary>The flip side of the test above - once this instance is
    /// truly thrown away (a fresh one replacing it because the old one finished, see
    /// MainViewModel.CreateCleanupViewModel), it must stop reacting to the helper altogether, or it would
    /// stay registered on <see cref="ElevatedHost.StateChanged"/> for as long as the app-wide helper lives.</summary>
    [Fact]
    public void Discard_stops_the_view_model_from_reacting_to_the_helper()
    {
        // An elevated test runner would otherwise skip the prompt and never show the hint.
        ElevationService.IsElevatedForTest.Value = false;
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);

        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        var row = viewModel.Items.First(i => i.NeedsElevation);

        viewModel.Discard();

        elevated.SetRunningForTest(true);
        WpfHarness.DrainViewModelDispatcher();
        Assert.True(row.ShowsElevationHint);
    }

    /// <summary>The history row itself formats a defrag area's title with its stored drive
    /// name, instead of showing the raw "{0}" placeholder the resource key carries.</summary>
    [Fact]
    public void CleanupHistoryAreaViewModel_formats_a_defrag_titles_drive_argument()
    {
        var entry = new CleanupHistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Areas = [new CleanupHistoryArea { Id = "defrag-e", TitleKey = "CleanupDefragRowHdd", TitleArguments = ["Games E:\\"] }],
        };
        var row = new CleanupHistoryRowViewModel(entry, _ => { });

        var title = row.Areas.Single().Title;

        Assert.DoesNotContain("{0}", title);
        Assert.Contains("Games E:\\", title);
    }

    /// <summary>Selecting both component-store rows in the same run must only ever run
    /// ResetBase, so a whole second, often long, component-cleanup pass is never repeated for nothing
    /// right after it. Exercises the actual production rule
    /// (<see cref="CleanupViewModel.ApplyComponentCleanupSupersedeRule"/>), not a re-implementation of it.</summary>
    [Fact]
    public void Selecting_both_component_rows_runs_only_ResetBase()
    {
        var viewModel = Create(CreateSettings());
        var repairLane = viewModel.Items.Where(i => i.Target.Kind == CleanupKind.SystemRepair).ToList();

        var result = CleanupViewModel.ApplyComponentCleanupSupersedeRule(repairLane);

        Assert.Contains(result, i => i.Target.Id == "component-resetbase");
        Assert.DoesNotContain(result, i => i.Target.Id == "component-cleanup");
        Assert.Equal(repairLane.Count - 1, result.Count);
    }

    /// <summary>The rule must never touch the lane when ResetBase itself is not part of this run.</summary>
    [Fact]
    public void The_supersede_rule_leaves_the_lane_untouched_without_ResetBase()
    {
        var viewModel = Create(CreateSettings());
        var repairLane = viewModel.Items
            .Where(i => i.Target.Kind == CleanupKind.SystemRepair && i.Target.Id != "component-resetbase")
            .ToList();

        var result = CleanupViewModel.ApplyComponentCleanupSupersedeRule(repairLane);

        Assert.Equal(repairLane, result);
    }

    /// <summary>A run that genuinely reported no sizes at all shows a sentence, not the two
    /// dashes that used to read exactly like a parsing failure.</summary>
    [Fact]
    public void ComponentStoreSizeText_shows_a_sentence_when_DISM_reported_no_numbers()
    {
        var viewModel = Create(CreateSettings());
        viewModel.ComponentStoreResult = new ComponentStoreAnalysis(null, null, null, null);

        Assert.Equal(LocalizationService.Instance["CleanupComponentStoreNoNumbers"], viewModel.ComponentStoreSizeText);
    }

    /// <summary>The ordinary case is untouched: at least one real number still formats through the normal
    /// template, dashes only for the half that is missing.</summary>
    [Fact]
    public void ComponentStoreSizeText_still_formats_a_partial_result_normally()
    {
        var viewModel = Create(CreateSettings());
        viewModel.ComponentStoreResult = new ComponentStoreAnalysis(1_000_000_000, null, null, null);

        var text = viewModel.ComponentStoreSizeText;
        Assert.NotEqual(LocalizationService.Instance["CleanupComponentStoreNoNumbers"], text);
        Assert.Contains("-", text);
    }

    /// <summary>DISM being killed after its own deadline must show as a distinguishable error, not as
    /// the same silent "no numbers" wording a genuinely empty run produces.</summary>
    [Fact]
    public void ShowComponentStoreTimedOut_is_true_only_for_a_timed_out_result()
    {
        var viewModel = Create(CreateSettings());

        viewModel.ComponentStoreResult = ComponentStoreAnalysis.TimedOutResult;
        Assert.True(viewModel.ShowComponentStoreTimedOut);

        viewModel.ComponentStoreResult = new ComponentStoreAnalysis(null, null, null, null);
        Assert.False(viewModel.ShowComponentStoreTimedOut);
    }

    /// <summary>The tick already says "done"; the state text next to it must not repeat the same
    /// word, while a failure state still gets one.</summary>
    [Fact]
    public void RunStateText_is_empty_for_Done_but_not_for_Stopped()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First();

        item.RunState = CleanupTargetState.Done;
        Assert.Equal(string.Empty, item.RunStateText);

        item.RunState = CleanupTargetState.Stopped;
        Assert.NotEqual(string.Empty, item.RunStateText);
    }

    /// <summary>A target the run stopped short of finishing removed nothing more, so its open rows are
    /// genuinely still on disk and say skipped rather than claiming a tick.</summary>
    [Fact]
    public void A_stopped_target_leaves_its_open_entries_skipped()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(viewModel, CleanupKind.Directories, Folder(@"C:\A"), Folder(@"C:\B"));

        Report(viewModel, item, @"C:\A\one.log", freed: 400);

        item.RunState = CleanupTargetState.Stopped;
        item.ResolveEntryStates(completed: false);

        Assert.DoesNotContain(item.Entries, e => e.State is EntryState.Running or EntryState.Pending);
        Assert.All(item.Entries, e => Assert.Equal(EntryState.Skipped, e.State));
        Assert.All(item.Entries, e => Assert.Equal(0, e.FreedBytes));
    }

    /// <summary>An unticked row is handed to Clean as an exclusion, so it is settled before the run starts
    /// and stays settled through a target that finished perfectly well.</summary>
    [Fact]
    public void An_unticked_entry_stays_skipped_through_a_finished_target()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.SetEntries([Folder(@"C:\A"), Folder(@"C:\B")]);
        Entry(item, @"C:\B").IsSelected = false;

        item.ResetEntryStates();
        item.RunState = CleanupTargetState.Running;
        item.IsDetailsExpanded = true;
        Report(viewModel, item, @"C:\A\one.log", freed: 400);
        item.ResolveEntryStates(completed: true);

        Assert.Equal(EntryState.Cleaned, Entry(item, @"C:\A").State);
        Assert.Equal(EntryState.Skipped, Entry(item, @"C:\B").State);
        Assert.Equal(0, Entry(item, @"C:\B").FreedBytes);
    }

    /// <summary>
    /// The cleanup header's own "last restore point" line, read back from the same timestamp file
    /// InstallQueue and CleanupWorker write after actually creating one. The dialog seam proves the button
    /// never has to pop a real system window for this to be testable.
    /// </summary>
    [Fact]
    public void RestorePointText_reads_the_shared_timestamp_and_the_open_command_uses_its_test_seam()
    {
        var logging = new LoggingService();
        var timestampPath = Path.Combine(AppPaths.DataDirectory, "restore-point.json");
        if (File.Exists(timestampPath))
            File.Delete(timestampPath);

        var withoutOne = Create(CreateSettings());
        Assert.Equal(LocalizationService.Instance["RestorePointNone"], withoutOne.RestorePointText);

        RestorePointThrottle.TryWriteCreated(logging, DateTimeOffset.UtcNow);
        var withOne = Create(CreateSettings());

        // Whatever language happens to be active in this process (LocalizationService is a shared
        // singleton, and another test earlier in the same run may have switched it), not a hardcoded
        // English literal: the prefix up to the format placeholder is enough to prove the shared
        // timestamp actually reached the string, without pinning the test to one language.
        var expectedPrefix = LocalizationService.Instance["RestorePointLast"].Split("{0}")[0];
        Assert.StartsWith(expectedPrefix, withOne.RestorePointText);

        var opened = false;
        withOne.OpenRestorePointDialogOverride = () => opened = true;
        withOne.OpenRestorePointDialogCommand.Execute(null);

        Assert.True(opened, "the seam must run instead of the real rstrui.exe launch");
    }

    // ---- recycle bin age filter ----

    private static (CleanupViewModel ViewModel, Func<int> AnalysisStarts) CreateForAutoAnalysis(bool helperRunning)
    {
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        elevated.SetRunningForTest(helperRunning);
        elevated.EnsureStartedOverrideForTest = _ => Task.FromResult(false);
        var viewModel = new CleanupViewModel(
            new DelayedCleanupService(logging, TimeSpan.Zero), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        var starts = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanupViewModel.IsAnalyzingComponentStore) && viewModel.IsAnalyzingComponentStore)
                starts++;
        };
        return (viewModel, () => starts);
    }

    /// <summary>With the elevated helper already running, a finished scan measures the component store on
    /// its own, once.</summary>
    [Fact]
    public async Task A_finished_scan_analyses_the_component_store_once_when_the_helper_is_running()
    {
        var (viewModel, starts) = CreateForAutoAnalysis(helperRunning: true);

        await viewModel.StartScanAsync();

        Assert.Equal(1, starts());
    }

    /// <summary>Without the helper (and without an elevated test host) nothing may analyse on its own, and
    /// no prompt may be raised on the analysis' behalf.</summary>
    [Fact]
    public async Task A_finished_scan_does_not_analyse_the_component_store_without_the_helper()
    {
        if (ElevationService.IsElevated)
            return; // IsElevated is static; an elevated test host would always count as "has admin".

        Dialogs.SuppressInteractiveDialogs = true;
        var (viewModel, starts) = CreateForAutoAnalysis(helperRunning: false);

        await viewModel.StartScanAsync();

        Assert.Equal(0, starts());
        Assert.Null(viewModel.ComponentStoreStatusText);
    }

    /// <summary>A result that is already there is never re-measured automatically.</summary>
    [Fact]
    public async Task A_finished_scan_keeps_an_existing_component_store_result()
    {
        var (viewModel, starts) = CreateForAutoAnalysis(helperRunning: true);
        viewModel.ComponentStoreResult = new ComponentStoreAnalysis(1_000_000_000, null, null, null);

        await viewModel.StartScanAsync();

        Assert.Equal(0, starts());
    }

    /// <summary>The user declined administrator rights for this run in the dialog's own question. A
    /// restore point needs them too, so asking for one afterwards raised the UAC prompt the user had just
    /// said no to.</summary>
    [Fact]
    public async Task Declining_admin_for_a_run_asks_for_no_restore_point_afterwards()
    {
        if (ElevationService.IsElevated)
            return; // IsElevated is static; an elevated test host never asks.

        Dialogs.SuppressInteractiveDialogs = true;
        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        elevated.SetRunningForTest(false);
        elevated.EnsureStartedOverrideForTest = _ => Task.FromResult(false);
        var settings = CreateSettings();
        settings.Current.Install.CreateRestorePoint = true;
        var viewModel = new CleanupViewModel(new InertCleanupService(logging), logging, settings, elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        viewModel.ComponentStoreResult = new ComponentStoreAnalysis(1_000_000_000, null, null, null);
        var restorePoints = 0;
        viewModel.CreateRestorePointOverride = _ =>
        {
            Interlocked.Increment(ref restorePoints);
            return Task.FromResult(false);
        };

        var repair = viewModel.Items.First(i => i.Target.Kind == CleanupKind.SystemRepair);
        foreach (var item in viewModel.Items)
            item.IsSelected = ReferenceEquals(item, repair);

        var timestamp = Path.Combine(AppPaths.DataDirectory, "restore-point.json");
        if (File.Exists(timestamp))
            File.Delete(timestamp);

        // Dialogs.Confirm declines headlessly, the same answer as the user's "continue without".
        await viewModel.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await viewModel.RunCleanupAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, Volatile.Read(ref restorePoints));
    }

    /// <summary>Measures nothing and deletes nothing, so a run can be driven end to end.</summary>
    private sealed class InertCleanupService(LoggingService logging) : CleanupService(logging)
    {
        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
            Task.FromResult(new CleanupScanResult(0, 0, 0));

        public override Task<CleanupOutcome> CleanAsync(
            CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
            PauseGate? pause = null, Models.Settings.RegistryCleanupSettings? registrySettings = null,
            IReadOnlyCollection<string>? selectedLeftoverPaths = null,
            IReadOnlyList<RegistryFinding>? registryFindings = null,
            IReadOnlyCollection<string>? excludedEntryPaths = null,
            bool recycleInsteadOfDelete = false,
            int recycleBinMinAgeDays = 0,
            IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
            IReadOnlyCollection<string>? selectedDriverPackageNames = null,
            IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
            IReadOnlyCollection<string>? confirmedEntryPaths = null) =>
            Task.FromResult(new CleanupOutcome(0, 0, 0));
    }

    /// <summary>The scan decided elevation from what was ticked THEN. A target that needs administrator
    /// rights ticked after the scan must still get its elevation request when the run starts, instead of
    /// running unelevated without a word.</summary>
    [Fact]
    public async Task An_admin_target_ticked_after_the_scan_requests_elevation_when_the_run_starts()
    {
        if (ElevationService.IsElevated)
            return; // IsElevated is static; an elevated test host never asks.

        var logging = new LoggingService();
        var elevated = new ElevatedHost(logging);
        elevated.SetRunningForTest(true);
        var armed = false;
        CleanupPhase? phaseOfFirstRequestAfterScan = null;
        CleanupViewModel? viewModel = null;
        elevated.EnsureStartedOverrideForTest = _ =>
        {
            if (Volatile.Read(ref armed))
                phaseOfFirstRequestAfterScan ??= viewModel!.Phase;

            return Task.FromResult(true);
        };
        viewModel = new CleanupViewModel(new InertCleanupService(logging), logging, CreateSettings(), elevated: elevated);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        // A result that is already there keeps the finished scan from starting a component store analysis
        // of its own, which would ask the helper too.
        viewModel.ComponentStoreResult = new ComponentStoreAnalysis(1_000_000_000, null, null, null);

        var plain = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories && !i.Target.NeedsElevation);
        foreach (var item in viewModel.Items)
            item.IsSelected = ReferenceEquals(item, plain);

        await viewModel.StartScanAsync();
        Volatile.Write(ref armed, true);

        var admin = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories && i.Target.NeedsElevation);
        admin.IsSelected = true;
        await viewModel.RunCleanupAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // The request comes from the start of the run itself, before the dialog switches to cleaning.
        Assert.Equal(CleanupPhase.Choosing, phaseOfFirstRequestAfterScan);
    }
}
