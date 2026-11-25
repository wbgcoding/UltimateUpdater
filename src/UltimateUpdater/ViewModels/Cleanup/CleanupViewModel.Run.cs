using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Cleanup;

// Performing the cleanup: the run loop and its elevated half, pause/stop, and the phase, ring,
// progress and result figures that follow from it.
public partial class CleanupViewModel
{
    /// <summary>Whether the dialog is on screen: the second gate on per-entry marking, set by the window and cleared by <see cref="WindowClosed"/>. Defaults to true so the view model works without a view.</summary>
    [ObservableProperty]
    private bool isWindowVisible = true;

    partial void OnIsWindowVisibleChanged(bool value)
    {
        // A report kept for a dialog that has gone says nothing worth applying.
        if (!value)
            _pendingEntryProgress.Clear();
    }

    /// <summary>The newest progress report per target, waiting for the tick (the third gate): a pass deleting hundreds of files a second has nothing to say that its newest report does not.</summary>
    private readonly Dictionary<CleanupItemViewModel, CleanupProgress> _pendingEntryProgress = [];

    private readonly System.Windows.Threading.DispatcherTimer _entryProgressTimer;

    [ObservableProperty]
    private CleanupPhase phase = CleanupPhase.Choosing;

    /// <summary>Progress of the cleaning run, 0-100. Drives the ring while <see cref="Phase"/> is Cleaning.</summary>
    [ObservableProperty]
    private double cleanPercent;

    /// <summary>What is being deleted right now.</summary>
    [ObservableProperty]
    private string currentItemText = string.Empty;

    [ObservableProperty]
    private long freedBytes;

    public string FreedText => FormatFreed(FreedBytes);

    /// <summary>The result figure, formatted. Shared with the dialog, which counts the number up to it at the end of a run.</summary>
    public static string FormatFreed(long bytes) => $"+{ByteSize.Format(bytes)}";

    /// <summary>The overall percentage, shown under the broom so the number is never lost behind it.</summary>
    public string CleanPercentText => PercentFormat.Format(CleanPercent);

    /// <summary>The ring's fill, 0-100: the selected share of the total while choosing, the run's progress while cleaning, full when done. Never NaN, and a tiny selection still shows a minimum sliver.</summary>
    public double RingPercent => Phase switch
    {
        CleanupPhase.Cleaning => CleanPercent,
        CleanupPhase.Finished => 100,
        _ => TotalBytes <= 0 || SelectedBytes <= 0
            ? 0
            : Math.Clamp(SelectedBytes * 100.0 / TotalBytes, 2, 100),
    };

    public bool IsChoosing => Phase == CleanupPhase.Choosing;
    public bool IsCleaning => Phase == CleanupPhase.Cleaning;

    /// <summary>The detail lists can be ticked only before a run is confirmed.</summary>
    public bool CanEditDetails => !IsCleaning && !_runClaimed;
    public bool IsFinished => Phase == CleanupPhase.Finished;

    /// <summary>Rescan stays on offer after a run: the dialog would otherwise end with no button at all.</summary>
    public bool ShowRescanButton => Phase is CleanupPhase.Choosing or CleanupPhase.Finished;

    /// <summary>A run in progress can be held or stopped. Stop ends the local pass between files and reaches the elevated helper; only a repair step already under way (sfc, DISM) finishes first, because killing it mid-write can damage the component store.</summary>
    [ObservableProperty]
    private bool isPaused;

    /// <summary>Which of the two hold buttons is up. Two buttons because <c>ui:Button.Icon</c> holds an element, not a bindable glyph name, so one button could not change its icon with its word.</summary>
    public bool ShowPauseButton => IsCleaning && !IsPaused;

    public bool ShowResumeButton => IsCleaning && IsPaused;

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPauseButton));
        OnPropertyChanged(nameof(ShowResumeButton));
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        // A copy: a job can finish and drop out of the list while these are awaited.
        var jobs = _cleanJobs.ToList();

        // The gate decides, so the button follows at once. It is flipped before the elevated jobs are told, so a dead pipe still leaves the run resumable.
        try
        {
            if (IsPaused)
            {
                _pause.Resume();
                IsPaused = false;

                // Per job: one dead pipe must not stop the others from being told to resume.
                foreach (var job in jobs)
                    await TryAsync(job.ResumeAsync);
            }
            else
            {
                _pause.Pause();
                IsPaused = true;

                foreach (var job in jobs)
                    await TryAsync(job.PauseAsync);
            }
        }
        finally
        {
            IsPaused = _pause.IsPaused;
        }
    }

    /// <summary>Stops the run. The gate is opened first so a paused run notices the cancellation.</summary>
    [RelayCommand]
    private void Stop()
    {
        _pause.Resume();
        IsPaused = false;

        // The sfc and DISM steps are never killed (DISM mid-way through restoring the component store can leave it inconsistent), so the dialog says so. Only a repair that has really begun counts: a queued job is skipped when the stop reaches it.
        _stopRequested = true;
        IsStoppingRepair = Items.Any(i =>
            i.IsCleaning && i.Target.Kind == CleanupKind.SystemRepair && _startedRepairs.Contains(i));

        _cleanCts?.Cancel();
    }

    /// <summary>The repair rows whose tool has reported its first step: proof the repair began, as opposed to a row still waiting for the helper.</summary>
    private readonly HashSet<CleanupItemViewModel> _startedRepairs = [];

    /// <summary>Stop was pressed during the current run.</summary>
    private bool _stopRequested;

    /// <summary>A repair step is finishing after the user asked to stop, because it cannot be interrupted.</summary>
    [ObservableProperty]
    private bool isStoppingRepair;

    public string StoppingRepairText => LocalizationService.Instance["CleanupStoppingRepair"];

    /// <summary>Something is ticked that actually has something to free (or is Disk Cleanup).</summary>
    public bool CanClean =>
        Phase == CleanupPhase.Choosing
        && Items.Any(i => i.IsSelected && (i.SelectedEntryBytes > 0 || i.Target.Kind != CleanupKind.Directories))
        // While a ticked recycle bin is re-measured for a new age filter, Clean waits for the new figure.
        && !(!IsScanning && Items.Any(i => i.IsSelected && i.IsRecycleBin && i.IsScanning));

    /// <summary>How many files were skipped: in use, or refused (<see cref="ProtectedFiles"/>).</summary>
    [ObservableProperty]
    private int skippedFiles;

    /// <summary>And how much that was: a locked temp folder can hold a lot.</summary>
    [ObservableProperty]
    private long skippedBytes;

    /// <summary>The refused part of <see cref="SkippedFiles"/>: no access, typically without admin rights.</summary>
    [ObservableProperty]
    private int protectedFiles;

    [ObservableProperty]
    private long protectedBytes;

    public bool HasSkipped => SkippedFiles > 0;

    /// <summary>Two counts, never one: a file in use and a file refused for lack of rights need different actions.</summary>
    public string SkippedText => FormatSkipped(
        SkippedFiles - ProtectedFiles, SkippedBytes - ProtectedBytes, ProtectedFiles, ProtectedBytes);

    internal static string FormatSkipped(int inUseFiles, long inUseBytes, int protectedFiles, long protectedBytes)
    {
        var loc = LocalizationService.Instance;
        var parts = new List<string>(2);
        if (inUseFiles > 0)
        {
            parts.Add(inUseBytes > 0
                ? string.Format(loc[inUseFiles == 1 ? "CleanupSkippedFilesSizeOne" : "CleanupSkippedFilesSize"], inUseFiles, ByteSize.Format(inUseBytes))
                : string.Format(loc[inUseFiles == 1 ? "CleanupSkippedFilesOne" : "CleanupSkippedFiles"], inUseFiles));
        }

        if (protectedFiles > 0)
        {
            parts.Add(protectedBytes > 0
                ? string.Format(loc[protectedFiles == 1 ? "CleanupProtectedFilesSizeOne" : "CleanupProtectedFilesSize"], protectedFiles, ByteSize.Format(protectedBytes))
                : string.Format(loc[protectedFiles == 1 ? "CleanupProtectedFilesOne" : "CleanupProtectedFiles"], protectedFiles));
        }

        return string.Join(" ", parts);
    }

    partial void OnSkippedBytesChanged(long value) => OnPropertyChanged(nameof(SkippedText));

    partial void OnProtectedFilesChanged(int value) => OnPropertyChanged(nameof(SkippedText));

    partial void OnProtectedBytesChanged(long value) => OnPropertyChanged(nameof(SkippedText));

    partial void OnPhaseChanged(CleanupPhase value)
    {
        OnPropertyChanged(nameof(IsChoosing));
        OnPropertyChanged(nameof(IsCleaning));
        OnPropertyChanged(nameof(CanEditDetails));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(ShowRescanButton));
        OnPropertyChanged(nameof(RingPercent));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(ShowPauseButton));
        OnPropertyChanged(nameof(ShowResumeButton));
        OnPropertyChanged(nameof(HasFreeSpaceNow));
        OnPropertyChanged(nameof(HasLastCleanup));
        OnPropertyChanged(nameof(ShowsRunStatus));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(StatusCaptionText));
        OnPropertyChanged(nameof(StatusHeadlineText));
        OnPropertyChanged(nameof(StatusFreedText));

        // Back to choosing: the previous run's picture makes way for the idle facts.
        if (value == CleanupPhase.Choosing)
        {
            StepItems = [];
            CurrentGroupKey = null;
            CurrentAreaPercentText = string.Empty;
        }

        // The result reads per section once the run is over, not only as one figure in the ring.
        foreach (var group in Groups)
            group.RunFinished = value == CleanupPhase.Finished;

        NotifyRescanCanExecuteChanged();
    }

    partial void OnCleanPercentChanged(double value)
    {
        OnPropertyChanged(nameof(RingPercent));
        OnPropertyChanged(nameof(CleanPercentText));
    }

    partial void OnFreedBytesChanged(long value)
    {
        // Never steps back inside a run; the run start resets it.
        _liveFreedShown = Math.Max(_liveFreedShown, value);
        OnPropertyChanged(nameof(FreedText));
        OnPropertyChanged(nameof(LiveFreedText));
        OnPropertyChanged(nameof(StatusFreedText));
    }

    private long _liveFreedShown;

    /// <summary>The small "~2.3 GB" under the percentage while a run works. It only climbs within a run.</summary>
    public string LiveFreedText => $"~{ByteSize.Format(_liveFreedShown)}";

    partial void OnSkippedFilesChanged(int value)
    {
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(SkippedText));
    }

    /// <summary>Runs the cleanup. Only reached after the window showed the red confirmation; there is no automatic path to it.</summary>
    public async Task RunCleanupAsync()
    {
        // Phase turns to Cleaning only after the scan unwinds, so the claim is taken before the first await and held until the run is over.
        if (Phase != CleanupPhase.Choosing || _runClaimed)
            return;

        _runClaimed = true;
        OnRunClaimChanged();

        // Taken before the first await: the user confirmed exactly this selection.
        _runSnapshot = TakeRunSnapshot();

        // A run changes what the prescan measured, so its result is no longer one to reuse.
        SetPrescanFinishedAt(null);
        try
        {
            await RunCleanupClaimedAsync();
        }
        finally
        {
            _runSnapshot = null;
            _runClaimed = false;
            OnRunClaimChanged();
        }
    }

    /// <summary>What one ticked row hands to its clean, frozen when the run was confirmed.</summary>
    internal sealed record RunItemSelection(
        IReadOnlyList<string> LeftoverPaths,
        IReadOnlyList<RegistryFinding> RegistryFindings,
        IReadOnlyCollection<string> ExcludedPaths,
        IReadOnlyCollection<string>? ConfirmedEntryPaths,
        IReadOnlyList<string> DriverDeviceInstanceIds,
        IReadOnlyList<string> DriverPackageNames,
        IReadOnlyList<string> PythonVersionPaths);

    /// <summary>The ticked rows and their per-row selections as of the confirmed run; null outside a run.</summary>
    private Dictionary<CleanupItemViewModel, RunItemSelection>? _runSnapshot;

    private Dictionary<CleanupItemViewModel, RunItemSelection> TakeRunSnapshot() =>
        Items.Where(i => i.IsSelected).ToDictionary(i => i, SnapshotOf);

    private RunItemSelection SnapshotOf(CleanupItemViewModel item) => new(
        item.SelectedLeftoverPaths, item.RegistryFindings, ExcludedPathsForRun(item).ToList(),
        item.ConfirmedEntryPathsForRun?.ToList(), item.SelectedDriverDeviceInstanceIds,
        item.SelectedDriverPackageNames, item.SelectedOldPythonVersionPaths);

    /// <summary>The run's frozen selection for a row; a live read only when no run is active.</summary>
    private RunItemSelection SelectionFor(CleanupItemViewModel item) =>
        _runSnapshot is { } snapshot && snapshot.TryGetValue(item, out var frozen) ? frozen : SnapshotOf(item);

    /// <summary>Set from the first line of <see cref="RunCleanupAsync"/> to its last, on the UI thread.</summary>
    private bool _runClaimed;

    /// <summary>The claim covers the seconds before Phase turns to Cleaning: the dialog counts as busy then too, so Rescan cannot rewrite the rows about to be cleaned.</summary>
    private void OnRunClaimChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEditDetails));
        NotifyRescanCanExecuteChanged();
    }

    /// <summary>File targets go to the recycle bin instead of being deleted outright (the Settings switch as it stood at run start). A target that needs elevation ignores it.</summary>
    private bool _recycleInsteadOfDeleteForRun;

    private async Task RunCleanupClaimedAsync()
    {
        // A recycle-bin re-measure still in flight must not swap the row's list after the user confirmed what it showed.
        if (_recycleBinRescanCts is { } rescan)
        {
            rescan.Cancel();
            _recycleBinRescanCts = null;
            foreach (var bin in Items.Where(i => i.IsRecycleBin && i.IsScanning))
                bin.IsScanning = false;
        }

        // Cancelling the token is only a request: the scan loop can still be mid-await and write into an item as the reset below runs, so the run waits for the scan task to finish first.
        _scanCts?.Cancel();
        await AwaitScanUnwindAsync(_scanTask);

        if (Phase != CleanupPhase.Choosing)
            return;

        // Exactly what was ticked when the run was confirmed.
        var selected = Items.Where(i => _runSnapshot?.ContainsKey(i) ?? i.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        // Read once at run start; the Settings window can change it while the dialog is open.
        _recycleInsteadOfDeleteForRun = _settings.Current.CleanupRecycleInsteadOfDelete;

        _cleanCts = new CancellationTokenSource();
        _stopRequested = false;
        _startedRepairs.Clear();

        // Decided from what is ticked now: a target needing administrator rights ticked after the scan would otherwise run unelevated. Asks only when such a target is ticked and no helper runs.
        var elevationRefused = await EnsureElevationAsync(_cleanCts.Token);

        // A gate left closed by the previous run would stop this one.
        _pause.Resume();
        IsPaused = false;

        var runTime = System.Diagnostics.Stopwatch.StartNew();
        _logging.Event(ActivityLog.Cleanup, $"start {selected.Count} targets");

        Phase = CleanupPhase.Cleaning;
        CleanPercent = 0;
        _liveFreedShown = 0;
        FreedBytes = 0;

        Activity = NeutralActivity;
        _lastProgressTick = 0;

        SkippedFiles = 0;
        SkippedBytes = 0;
        ProtectedFiles = 0;
        ProtectedBytes = 0;

        var elevatedTargets = ElevatedItems(selected);

        // Everything the run does not touch drops its capsule.
        foreach (var item in Items)
            item.RunState = CleanupTargetState.Idle;

        // With ResetBase ticked, "component-cleanup" is a subset and is dropped from the repair lane, so it must not count toward the ring.
        var superseded = selected
            .Where(i => LaneFor(i.Target.Kind) == CleanupLane.Repair)
            .Except(ApplyComponentCleanupSupersedeRule(selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Repair).ToList()))
            .ToList();

        _runItems = selected.Except(superseded).ToList();
        StepItems = _runItems;
        WasStopped = false;
        StartRunClock();
        foreach (var item in _runItems)
        {
            item.IsWaiting = true;
            item.IsDone = false;
            item.Percent = 0;
            item.FreedBytes = 0;
            item.SkippedBytes = 0;
            item.ProtectedBytes = 0;
            item.RepairResult = null;
            item.SkipReason = null;
            item.DetailText = string.Empty;
            item.RunState = CleanupTargetState.Waiting;
            item.ResetEntryStates();
        }

        _pendingEntryProgress.Clear();
        _entryProgressTimer.Start();

        // Five lanes run concurrently. FILE: everything that only deletes files. REGISTRY: needs catalogue order but waits on the same restore-point task. REPAIR: DISM then sfc, strictly in that order (sfc restores from the store DISM repairs). DISK CLEANUP: separate because it walks the temp folders the file lane empties (see ShowsLaneOverlapNote). DEFRAG: drives one after another.
        var fileLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.File).ToList();
        var registryLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Registry).ToList();
        var repairLane = ApplyComponentCleanupSupersedeRule(
            selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Repair).ToList());
        var diskCleanupLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.DiskCleanup).ToList();
        var defragLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Defrag).ToList();

        // One restore-point decision for the run, started before any lane so it runs in parallel with the repair, disk-cleanup and defrag lanes. Only the file lane removes anything, so only it waits on the result.
        var restorePointTask = NeedsRestorePointForRun(selected, elevationRefused)
            ? CreateRestorePointAsync(_cleanCts.Token)
            : Task.FromResult(false);

        var cancelled = false;
        try
        {
            // WhenAll waits for every task even when one ends early. The restore-point task is always one of them, so a repair-only run still observes its failure instead of reporting Finished early.
            await RunLanesConcurrentlyAsync(BuildRunLanes(
                () => Task.WhenAll(fileLane.Select(item => CleanOneAsync(item, elevatedTargets, _cleanCts.Token, restorePointTask))),
                () => RunLaneInOrderAsync(registryLane, elevatedTargets, _cleanCts.Token, restorePointTask),
                () => RunLaneInOrderAsync(repairLane, elevatedTargets, _cleanCts.Token),
                () => RunLaneInOrderAsync(diskCleanupLane, elevatedTargets, _cleanCts.Token),
                () => RunLaneInOrderAsync(defragLane, elevatedTargets, _cleanCts.Token),
                restorePointTask));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            CurrentItemText = string.Empty;
            CurrentGroupKey = null;
            CurrentAreaPercentText = string.Empty;
            _cleanJobs.Clear();
            _runItems = null;
            _pause.Resume();
            IsPaused = false;
            IsStoppingRepair = false;
            _startedRepairs.Clear();

            _entryProgressTimer.Stop();
            _pendingEntryProgress.Clear();

            foreach (var item in selected)
            {
                item.IsWaiting = false;
                item.IsCleaning = false;

                if (item.RunState != CleanupTargetState.Skipped)
                    item.DetailText = string.Empty;

                if (item.RunState == CleanupTargetState.Waiting)
                {
                    item.RunState = CleanupTargetState.Skipped;
                    item.ResolveEntryStates(completed: false);
                }
            }

            // A cancelled run must not paint a full ring: it reports what it freed before it stopped.
            if (!cancelled)
                CleanPercent = 100;

            StopRunClock();
            WasStopped = cancelled;
            Phase = CleanupPhase.Finished;
            _logging.Event(
                ActivityLog.Cleanup,
                ActivityLog.CleanupFinished(FreedBytes, SkippedFiles, cancelled, runTime.Elapsed));

            RecordRun(selected, cancelled);
        }
    }

    /// <summary>The five concurrent lanes a selected target is dispatched into.</summary>
    public enum CleanupLane
    {
        File,
        Registry,
        Repair,
        DiskCleanup,
        Defrag,
    }

    /// <summary>ResetBase already does everything the plain component cleanup does, so selecting both repeats a long pass for nothing: ResetBase wins and the plain row drops out of this run's repair lane. Pure and static so it is directly testable.</summary>
    internal static List<CleanupItemViewModel> ApplyComponentCleanupSupersedeRule(List<CleanupItemViewModel> repairLane) =>
        repairLane.Any(i => i.Target.Id == "component-resetbase")
            ? repairLane.Where(i => i.Target.Id != "component-cleanup").ToList()
            : repairLane;
    /// <summary>Which lane a target's kind runs in. Every <see cref="CleanupKind"/> is covered explicitly; an unknown kind throws rather than silently going nowhere. Registry-like kinds get their own lane for catalogue order but still await the restore-point task.</summary>
    internal static CleanupLane LaneFor(CleanupKind kind) => kind switch
    {
        CleanupKind.Directories or CleanupKind.RecycleBin
            or CleanupKind.AppCacheSweep or CleanupKind.LeftoverInstalls
            or CleanupKind.PipInstallLeftovers => CleanupLane.File,
        CleanupKind.RegistryCleanup or CleanupKind.DriverCleanup or CleanupKind.OldPythonVersions => CleanupLane.Registry,
        CleanupKind.SystemRepair => CleanupLane.Repair,
        CleanupKind.DiskCleanup => CleanupLane.DiskCleanup,
        CleanupKind.Defragmentation => CleanupLane.Defrag,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every CleanupKind must map to a lane."),
    };

    /// <summary>Whether the current selection would start a rotational-disk optimisation ("can take hours"); read by the window before <see cref="RunCleanupAsync"/> so the warning sits beside the red confirmation.</summary>
    public bool HasSelectedHddDefragTarget => Items.Any(i =>
        i.IsSelected && i.Target.Kind == CleanupKind.Defragmentation && i.Target.MediaType == DriveMediaType.Hdd);

    /// <summary>Runs every lane at the same time. Pure orchestration with no service or pipe, so a test can prove the lanes overlap with fake delegates.</summary>
    internal static Task RunLanesConcurrentlyAsync(IReadOnlyList<Func<Task>> lanes) =>
        Task.WhenAll(lanes.Select(lane => lane()));

    /// <summary>Waits for a previous scan to stop touching <see cref="Items"/> before a run resets the same rows. Cancelling only requests it; the cancelled scan's <see cref="OperationCanceledException"/> is swallowed. Static so a bare fake task can test it.</summary>
    internal static async Task AwaitScanUnwindAsync(Task? scanTask)
    {
        if (scanTask is null)
            return;

        try
        {
            await scanTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The lane functions one run hands to <see cref="RunLanesConcurrentlyAsync"/>. The restore-point task is appended as its own lane: repair, Disk Cleanup and defrag lanes never wait on it, and without this a repair-only run could report Finished while it is still being created.</summary>
    internal static IReadOnlyList<Func<Task>> BuildRunLanes(
        Func<Task> fileLane, Func<Task> registryLane, Func<Task> repairLane, Func<Task> diskCleanupLane,
        Func<Task> defragLane, Task restorePointTask) =>
        [fileLane, registryLane, repairLane, diskCleanupLane, defragLane, () => restorePointTask];

    /// <summary>One lane's targets, strictly in the order given (catalogue order, which keeps DISM ahead of sfc). <paramref name="waitForRestorePoint"/> is passed through to every target; only the registry lane's caller supplies it.</summary>
    private async Task RunLaneInOrderAsync(
        IReadOnlyList<CleanupItemViewModel> lane, IReadOnlyCollection<CleanupItemViewModel> elevatedTargets,
        CancellationToken cancellationToken, Task<bool>? waitForRestorePoint = null)
    {
        foreach (var item in lane)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CleanOneAsync(item, elevatedTargets, cancellationToken, waitForRestorePoint);
        }
    }

    /// <summary>The targets this run is working through, so the ring can be recomputed from all of them.</summary>
    private IReadOnlyList<CleanupItemViewModel>? _runItems;

    /// <summary>The restore-point wait, a no-op when nothing is pending (so no waiting text when no restore point is due); only a genuinely pending task shows the text and is awaited.</summary>
    internal static async Task WaitForRestorePointAsync(
        Task<bool>? waitForRestorePoint, Action showWaitingText, CancellationToken cancellationToken)
    {
        if (waitForRestorePoint is not { IsCompleted: false })
            return;

        showWaitingText();
        await waitForRestorePoint.WaitAsync(cancellationToken);
    }

    /// <summary>One target, start to finish. Everything it reports lands on its own row; the ring is the mean of every row, so it is right whether targets run together or in turn. A failure is logged and the run carries on.</summary>
    private async Task CleanOneAsync(
        CleanupItemViewModel item,
        IReadOnlyCollection<CleanupItemViewModel> elevatedTargets,
        CancellationToken cancellationToken,
        Task<bool>? waitForRestorePoint = null)
    {
        // Only a target that removes something waits on the restore point.
        await WaitForRestorePointAsync(waitForRestorePoint, () => item.DetailText = Loc["CleanupRestorePointParallel"], cancellationToken);

        item.IsWaiting = false;
        item.IsCleaning = true;
        item.RunState = CleanupTargetState.Running;
        item.Percent = 0;
        item.FreedBytes = 0;

        // What this target has told the run total so far: the total is the finished targets' outcomes plus the live part of each running one.
        long liveFreed = 0;

        // Added to every report: a local pass taking over from a dropped helper reports from zero again, while the helper's confirmed part stays in liveFreed.
        long reportBase = 0;
        var settled = false;

        var progress = new Progress<CleanupProgress>(report =>
        {
            // The first report of a repair step is sent as the step begins, which tells a running repair from a queued one. Reports of a finished row are ignored.
            if (item.IsCleaning && item.Target.Kind == CleanupKind.SystemRepair
                && _startedRepairs.Add(item) && _stopRequested)
                IsStoppingRepair = true;

            var reported = report.FreedBytes + reportBase;

            // A report is posted, so a last one can arrive after the target settled its figure.
            if (!settled && reported > liveFreed)
            {
                FreedBytes += reported - liveFreed;
                liveFreed = reported;
            }

            // Never backwards: the worker forwards reports unchanged and a repair percentage resets between DISM phases. Percent is reset to 0 before the callback is wired for a fresh target.
            item.Percent = Math.Max(item.Percent, report.Percent);
            item.FreedBytes = reported;
            if (!string.IsNullOrEmpty(report.CurrentItem))
                item.DetailText = report.CurrentItem;

            // The bar, percentage and line follow every report; only the per-row marking in the detail list is held to one pass per tick.
            ReportEntryProgress(item, report);

            RefreshRunProgress();
        });

        string? failureText = null;

        // A target only an administrator can run is never started unelevated (DISM exit 740): it is skipped and the row says why.
        var frozen = SelectionFor(item);
        Task<CleanupOutcome> CleanLocallyAsync() => CleanupService.SkipsWithoutAdmin(item.Target, ElevationService.IsElevated)
            ? SkipForMissingAdmin()
            : _service.CleanAsync(
            item.Target, progress, cancellationToken, _pause, _settings.Current.RegistryCleanup,
            frozen.LeftoverPaths, frozen.RegistryFindings, frozen.ExcludedPaths, _recycleInsteadOfDeleteForRun,
            item.RecycleBinMinAgeDays, frozen.DriverDeviceInstanceIds, frozen.DriverPackageNames,
            frozen.PythonVersionPaths, frozen.ConfirmedEntryPaths);

        Task<CleanupOutcome> SkipForMissingAdmin()
        {
            _logging.Warn($"Cleanup: '{item.Target.Id}' skipped, admin rights are required.");
            return Task.FromResult(CleanupOutcome.AdminRequired);
        }

        try
        {
            // The elevated helper owns the targets that need admin; the rest run here. A helper that dropped out falls back to a local pass.
            var outcome = elevatedTargets.Contains(item)
                ? await CleanElevatedAsync(item, progress, cancellationToken) ?? await FallBackLocallyAsync()
                : await CleanLocallyAsync();

            // What the helper confirmed before it dropped stays counted once; the local pass reports from zero on top of it.
            async Task<CleanupOutcome> FallBackLocallyAsync()
            {
                // A job dropped because of the stop answers with no result. That is a stop, not a lost helper: redoing the target here would run it after Stop.
                cancellationToken.ThrowIfCancellationRequested();

                var freedByHelper = liveFreed;
                reportBase = freedByHelper;
                var local = await CleanLocallyAsync();
                return local with { FreedBytes = local.FreedBytes + freedByHelper };
            }

            // A per-user interpreter's uninstaller must never run in the elevated worker (HKCU is user-writable; see PythonVersionCleanupExecutor). The elevated pass acts only on machine-wide rows, so this local pass handles per-user ones. Skipped after a stop.
            if (item.Target.Kind == CleanupKind.OldPythonVersions && elevatedTargets.Contains(item) && !outcome.WasStopped)
            {
                var localOutcome = await CleanLocallyAsync();
                outcome = new CleanupOutcome(
                    outcome.FreedBytes + localOutcome.FreedBytes,
                    outcome.DeletedFiles + localOutcome.DeletedFiles,
                    outcome.SkippedFiles + localOutcome.SkippedFiles,
                    outcome.SkippedBytes + localOutcome.SkippedBytes,
                    outcome.WasStopped || localOutcome.WasStopped,
                    ProtectedFiles: outcome.ProtectedFiles + localOutcome.ProtectedFiles,
                    ProtectedBytes: outcome.ProtectedBytes + localOutcome.ProtectedBytes);
            }

            // The live part already counted is swapped for the outcome's own figure.
            settled = true;
            FreedBytes += outcome.FreedBytes - liveFreed;
            liveFreed = 0;
            SkippedFiles += outcome.SkippedFiles;
            SkippedBytes += outcome.SkippedBytes;
            ProtectedFiles += outcome.ProtectedFiles;
            ProtectedBytes += outcome.ProtectedBytes;
            item.FreedBytes = outcome.FreedBytes;
            item.ProtectedBytes = outcome.ProtectedBytes;
            item.SkippedBytes = outcome.SkippedBytes;
            item.RepairResult = outcome.RepairResult;

            // Driver cleanup exports into the elevated worker's own ProgramData staging folder (it must never resolve AppPaths); the copy into the data folder happens here, unelevated, best effort.
            if (outcome.DriverBackupFolderName is { Length: > 0 } folderName)
            {
                // Exported driver packages can be hundreds of MB: copied off the UI thread.
                var backupsRoot = Path.Combine(AppPaths.DataDirectory, "driver-backups");
                await Task.Run(() =>
                {
                    var copySource = DriverBackupStaging.ResolveExistingPath(folderName);
                    if (!AppPaths.TryCopyDirectory(copySource, Path.Combine(backupsRoot, folderName), out _))
                    {
                        _logging.Warn($"Driver cleanup: could not copy the backup folder '{folderName}' into the data folder.");
                        return;
                    }

                    // The data folder's copies are ours to trim; the ProgramData originals are trimmed by the elevated worker, the only side allowed to delete there.
                    DriverBackupStaging.PruneDataCopies(backupsRoot, folderName, _logging);
                });
            }

            // WasStopped (Disk Cleanup only) means the target swallowed its cancellation: it must not read "done" for a stopped run.
            if (outcome.NeedsAdmin)
            {
                // Never started: only an administrator can run it, and no helper took it over.
                item.RunState = CleanupTargetState.Skipped;
                item.SkipReason = new CleanupSkipReason("CleanupNeedsAdminSkipped");
                failureText = item.SkipReason.Text;
            }
            else if (outcome.TimedOut)
            {
                // Ended by the time limit: the row says why it freed nothing.
                item.RunState = CleanupTargetState.Skipped;
                item.SkipReason = TimedOutReason(item.Target.Kind);
                failureText = item.SkipReason.Text;
            }
            else if (outcome.Failed)
            {
                // A tool the target runs failed or never started: the row says why.
                item.RunState = CleanupTargetState.Skipped;
                item.SkipReason = FailedReason(outcome);
                failureText = item.SkipReason.Text;
            }
            else if (!outcome.WasStopped)
            {
                item.IsDone = true;
                item.Percent = 100;
                item.RunState = CleanupTargetState.Done;
            }
            else
            {
                item.RunState = CleanupTargetState.Stopped;
            }
        }
        catch (OperationCanceledException)
        {
            item.RunState = CleanupTargetState.Stopped;
            // A stop mid-target throws away the outcome, but what the reports confirmed is already in the total, so ring, history and the row's figure agree.
            settled = true;
            throw;
        }
        catch (Exception ex)
        {
            // One failing target must not stop the rest. Its row says "skipped", not "stopped", and does not read as finished.
            item.RunState = CleanupTargetState.Skipped;
            _logging.Error($"Cleanup failed for '{item.Target.Id}'", ex);

            // Its outcome never arrived: take its live part back out so the failed target adds nothing.
            settled = true;
            FreedBytes -= liveFreed;
            liveFreed = 0;
        }
        finally
        {
            // In the finally: a target that threw would otherwise leave its row showing a running progress bar.
            item.IsCleaning = false;
            item.DetailText = failureText ?? string.Empty;

            _pendingEntryProgress.Remove(item);
            item.ResolveEntryStates(item.RunState == CleanupTargetState.Done);

            RefreshRunProgress();
        }
    }

    /// <summary>The row text of a target ended by its time limit; each kind names its own limit.</summary>
    internal static string TimedOutText(CleanupKind kind) => TimedOutReason(kind).Text;

    internal static CleanupSkipReason TimedOutReason(CleanupKind kind) => kind switch
    {
        CleanupKind.SystemRepair => new("CleanupToolTimedOut", MinutesOf(CleanupService.RepairStepTimeout)),
        CleanupKind.Defragmentation => new("CleanupToolTimedOut", MinutesOf(CleanupService.DefragLimit)),
        _ => new("CleanupDiskCleanupTimedOut", MinutesOf(CleanupService.DiskCleanupLimit)),
    };

    private static string MinutesOf(TimeSpan limit) =>
        ((int)limit.TotalMinutes).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The row text of a target whose tool failed: its exit code, else that it could not be run. Codes outside the plain range (DISM's 0x800f0806 style) read as hex.</summary>
    internal static string FailedText(CleanupOutcome outcome) => FailedReason(outcome).Text;

    internal static CleanupSkipReason FailedReason(CleanupOutcome outcome)
    {
        if (outcome.ExitCode is not { } code)
            return new CleanupSkipReason("CleanupToolNotRun");

        var shown = code is < 0 or > 65535 ? $"0x{code:X8}" : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new CleanupSkipReason("CleanupToolFailed", shown);
    }

    /// <summary>Keeps the newest report for a target, and only when there is somewhere to show it. Public like <see cref="FlushEntryProgress"/>: the run that feeds it cannot be started in a test.</summary>
    public void ReportEntryProgress(CleanupItemViewModel item, CleanupProgress report)
    {
        // RunState, not IsCleaning: a Progress callback is posted, so a late report must not put a settled row back on "running".
        if (item.RunState != CleanupTargetState.Running || !IsWindowVisible || !item.IsDetailsExpanded
            || !item.HasEntries || string.IsNullOrEmpty(report.CurrentItem))
            return;

        _pendingEntryProgress[item] = report;
    }

    /// <summary>Applies at most one report per target per tick. Public because the tick is a DispatcherTimer and view-model tests have no message pump.</summary>
    public void FlushEntryProgress()
    {
        if (_pendingEntryProgress.Count == 0)
            return;

        foreach (var (item, report) in _pendingEntryProgress)
        {
            // Re-checked here: the list can be closed or the target can finish between the report and the tick.
            if (IsWindowVisible && item.IsDetailsExpanded && item.RunState == CleanupTargetState.Running)
                item.ApplyEntryProgress(report.CurrentItem, report.FreedBytes);
        }

        _pendingEntryProgress.Clear();
    }

    /// <summary>The ring and the line beside it, recomputed from the rows. Averaging rows rather than counting targets keeps the figure steady while several run at once.</summary>
    private void RefreshRunProgress()
    {
        if (_runItems is not { Count: > 0 } items)
            return;

        TrackActivity();

        CleanPercent = Math.Min(100, items.Sum(i => i.IsDone ? 100 : i.Percent) / items.Count);

        var running = items.Where(i => i.IsCleaning).ToList();

        // With several targets running, the one that has said something is the interesting one; the others fold into a "+n".
        var primary = running.FirstOrDefault(i => i.HasDetail) ?? running.FirstOrDefault();
        CurrentItemText = primary is null
            ? string.Empty
            : running.Count > 1 ? $"{primary.Title} +{running.Count - 1}" : primary.Title;
        CurrentGroupKey = primary is null ? null : CleanupGroups.For(primary.Target);
        CurrentAreaPercentText = primary is null ? string.Empty : PercentFormat.Format(primary.Percent);
        CurrentDetailText = primary?.DetailText ?? string.Empty;

        RefreshAreaProgress();
    }

    /// <summary>How lively the run is right now, 0 to 1, from how closely progress ticks follow each other. The dialog turns it into the speed of its sweep animation.</summary>
    [ObservableProperty]
    private double activity = NeutralActivity;

    /// <summary>Where a run starts: no ticks have been compared yet, so neither fast nor stalled.</summary>
    private const double NeutralActivity = 0.5;

    private long _lastProgressTick;

    /// <summary>Folds the gap since the previous progress tick into <see cref="Activity"/>; only published once it moved far enough to be worth a frame.</summary>
    private void TrackActivity()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var previous = _lastProgressTick;
        _lastProgressTick = now;

        if (previous == 0)
            return;

        var next = SmoothActivity(Activity, (now - previous) / (double)System.Diagnostics.Stopwatch.Frequency);
        if (Math.Abs(next - Activity) > 0.02)
            Activity = next;
    }

    /// <summary>The silence after which the run's timer starts pulling <see cref="Activity"/> down.</summary>
    private const double StalledGapSeconds = 1.0;

    /// <summary>Called by the run's timer: <see cref="TrackActivity"/> only runs when a report arrives, so a quiet step (a long DISM or sfc pass) would otherwise keep the last fast reading. The gap is measured from the last report.</summary>
    internal void DecayStalledActivity(long nowTimestamp)
    {
        if (Phase != CleanupPhase.Cleaning || _lastProgressTick == 0)
            return;

        var gap = (nowTimestamp - _lastProgressTick) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (gap < StalledGapSeconds)
            return;

        var next = SmoothActivity(Activity, gap);
        if (Math.Abs(next - Activity) > 0.02)
            Activity = next;
    }

    /// <summary>Exponential smoothing with the gap as the weight: a long silence weighs almost nothing against the low reading it produces, so a stalled step decays on its own. Pure and unit-tested.</summary>
    public static double SmoothActivity(double previous, double gapSeconds)
    {
        // Ticks this close together are as fast as the picture needs to get.
        const double FullSpeedGap = 0.25;

        // Roughly how long a silence takes to pull the value most of the way down.
        const double TimeConstant = 1.0;

        gapSeconds = double.IsFinite(gapSeconds) ? Math.Max(gapSeconds, 0) : 0;

        var instant = FullSpeedGap / Math.Max(gapSeconds, FullSpeedGap);
        var weight = Math.Exp(-gapSeconds / TimeConstant);

        return Math.Clamp(previous * weight + instant * (1 - weight), 0, 1);
    }

    /// <summary>The line under the current target's name: the command or file, verbatim.</summary>
    [ObservableProperty]
    private string currentDetailText = string.Empty;

    public bool HasCurrentDetail => CurrentDetailText.Length > 0;

    partial void OnCurrentDetailTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasCurrentDetail));
        OnPropertyChanged(nameof(CurrentPathText));
    }

    /// <summary>"4 von 10 erledigt": the run's own count, beside the ring while it works.</summary>
    public string DoneCountText => StepItems is { Count: > 0 } items
        ? string.Format(Loc[items.Count == 1 ? "CleanupDoneCountOne" : "CleanupDoneCount"], items.Count(i => i.IsDone), items.Count)
        : string.Empty;

    /// <summary>One target through the elevated helper. Returns null when the session broke, so the caller can fall back to a local pass.</summary>
    private async Task<CleanupOutcome?> CleanElevatedAsync(
        CleanupItemViewModel item, IProgress<CleanupProgress> progress, CancellationToken cancellationToken)
    {
        // Once "result" arrives for this target, later "progress" messages are ignored: progress callbacks run on pool threads, so a late one could move a finished figure back. ApplySingleTargetMessage carries the rule and is directly testable.
        var state = SingleTargetCleanupState.Initial;

        // Kept for as long as this target runs, so Pause and Stop can reach the elevated half of it.
        var handle = new ElevatedJobHandle();
        _cleanJobs.Add(handle);

        // A run that was already held before this target started has to hold this one too.
        if (IsPaused)
            _ = handle.PauseAsync();

        try
        {
            var completed = await RunElevatedAsync(
                "clean",
                [item],
                message =>
                {
                    var (nextState, progressToApply) = ApplySingleTargetMessage(state, message);
                    state = nextState;

                    if (progressToApply is { } applied)
                        progress.Report(applied);
                },
                cancellationToken,
                handle);

            return completed ? state.Outcome : null;
        }
        finally
        {
            // Out of the list as soon as this target is done, so a later Pause does not talk to a finished job.
            _cleanJobs.Remove(handle);
        }
    }

    /// <summary>One target's small state machine: "progress" updates the ring until "result" arrives, after which further "progress" is dropped. The handler serves one target at a time, so a plain sequence of calls reproduces the race without an elevated worker.</summary>
    internal static (SingleTargetCleanupState State, CleanupProgress? ProgressToApply) ApplySingleTargetMessage(
        SingleTargetCleanupState state, CleanupWorkerMessage message)
    {
        if (message.Type == "result")
        {
            var outcome = new CleanupOutcome(
                message.Freed, message.Files, message.Skipped, message.SkippedBytes,
                WasStopped: message.Stopped, DriverBackupFolderName: message.DriverBackupFolderName,
                TimedOut: message.TimedOut, ProtectedFiles: message.ProtectedFiles, ProtectedBytes: message.ProtectedBytes,
                RepairResult: message.RepairResult, Failed: message.Failed, ExitCode: message.ExitCode);
            return (state with { Finished = true, Outcome = outcome }, null);
        }

        if (message.Type == "progress" && !state.Finished)
            return (state, new CleanupProgress(message.Current ?? string.Empty, message.Percent, message.Freed));

        return (state, null);
    }

    internal readonly record struct SingleTargetCleanupState(bool Finished, CleanupOutcome? Outcome)
    {
        public static readonly SingleTargetCleanupState Initial = new(false, null);
    }

    /// <summary>One command to the shared elevated helper, replies decoded into cleanup messages. The helper belongs to the app, never shut down from here.</summary>
    private async Task<bool> RunElevatedAsync(
        string command,
        IReadOnlyList<CleanupItemViewModel> targets,
        Action<CleanupWorkerMessage> onMessage,
        CancellationToken cancellationToken,
        ElevatedJobHandle? handle = null,
        bool forceRestorePoint = false,
        bool allowStart = true,
        bool backgroundPriority = false)
    {
        if (_elevated is null)
            return false;

        var payload = new CleanupCommand
        {
            Command = command,
            TargetIds = targets.Select(i => i.Target.Id).ToList(),
            Language = LocalizationService.Instance.CurrentLanguage,
            CreateRestorePoint = forceRestorePoint,
            BackgroundPriority = command == "scan" && backgroundPriority,
            RegistryBackupFolder = _settings.Current.RegistryCleanup.BackupFolder,
            RegistryBackupRetentionCount = _settings.Current.RegistryCleanup.RetentionCount,
            // A "clean" command targets one item at a time; a scan needs no selection.
            SelectedLeftoverPaths = command == "clean" && targets is [{ Target.Kind: CleanupKind.LeftoverInstalls } only]
                ? SelectionFor(only).LeftoverPaths.ToList()
                : [],
            RegistryFindings = command == "clean" && targets is [{ Target.Kind: CleanupKind.RegistryCleanup } onlyRegistry]
                ? SelectionFor(onlyRegistry).RegistryFindings.Select(f => new RegistryFindingDto
                {
                    Category = f.Category,
                    KeyPath = f.KeyPath,
                    ValueName = f.ValueName,
                    DisplayName = f.DisplayName,
                    BrokenTarget = f.BrokenTarget,
                }).ToList()
                : [],
            // Not kind-restricted: a detail list can exist on a Directories, AppCacheSweep or RegistryCleanup row, so a single-item "clean" carries that row's deselections.
            ExcludedEntryPaths = command == "clean" && targets is [{ } onlyItem]
                ? SelectionFor(onlyItem).ExcludedPaths.ToList()
                : [],
            ConfirmedEntryPaths = command == "clean" && targets is [{ Target.Kind: CleanupKind.RecycleBin } onlyBin]
                ? SelectionFor(onlyBin).ConfirmedEntryPaths?.ToList()
                : null,
            SelectedDriverDeviceInstanceIds = command == "clean" && targets is [{ Target.Kind: CleanupKind.DriverCleanup } onlyDriver]
                ? SelectionFor(onlyDriver).DriverDeviceInstanceIds.ToList()
                : [],
            SelectedDriverPackageNames = command == "clean" && targets is [{ Target.Kind: CleanupKind.DriverCleanup } onlyDriverPkg]
                ? SelectionFor(onlyDriverPkg).DriverPackageNames.ToList()
                : [],
            SelectedPythonVersionPaths = command == "clean" && targets is [{ Target.Kind: CleanupKind.OldPythonVersions } onlyPython]
                ? SelectionFor(onlyPython).PythonVersionPaths.ToList()
                : [],
            CallerTempPaths = CleanupCatalog.OwnTempPaths().ToList(),
            CallerProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CallerProtectedFolders = CleanupSafety.ResolveOwnFolders().Protected.ToList(),
            CallerUserDataFolders = CleanupSafety.ResolveOwnFolders().UserData.ToList(),
            CallerSid = RecycleBinScanner.TryGetOwnSid(),
            CallerPythonOnPath = targets.Any(t => t.Target.Kind == CleanupKind.OldPythonVersions)
                ? new[] { "python", "pip" }.Select(ProcessRunner.Resolve).OfType<string>().ToList()
                : [],
        };

        // "cleanup" for a scan and for file-deletion "clean"; "cleanup-repair", "cleanup-diskcleanup", "cleanup-defrag" and "cleanup-registry" only for a single-target "clean" of that kind, so each gets its own lane in the elevated worker.
        var jobKind = command == "clean" && targets is [{ } onlyTarget]
            ? ElevatedJobKindFor(onlyTarget.Target.Kind)
            : "cleanup";

        return await _elevated.RunAsync(jobKind, payload, line =>
        {
            CleanupWorkerMessage? message;
            try
            {
                message = System.Text.Json.JsonSerializer.Deserialize<CleanupWorkerMessage>(
                    line, WorkerJson);
            }
            catch (System.Text.Json.JsonException)
            {
                return true;    // a malformed line is ignored, never fatal
            }

            if (message is null)
                return true;

            if (message.Type == "done")
                return false;

            if (message.Type == "log")
            {
                _logging.Info($"cleanup worker: {message.Text}");
                return true;
            }

            if (message.Type == "restorePointCreated")
            {
                RestorePointThrottle.TryWriteCreated(_logging, message.RestorePointCreatedAt ?? DateTimeOffset.UtcNow);
                return true;
            }

            // Onto the UI thread, synchronously: the "result" message assigns the outcome the caller reads after RunAsync returns. The UI thread awaits a TCS, so Invoke cannot deadlock.
            if (_dispatcher.CheckAccess())
                onMessage(message);
            else
                _dispatcher.Invoke(() => onMessage(message));

            return true;
        },
        cancellationToken,
        // Measuring only reads, so "scan" and "analyze" run alongside other work; treating them as exclusive would queue silently behind a cleanup and look like a hang. Cleaning takes the exclusive slot.
        exclusive: command is not ("scan" or "analyze"),
        handle,
        allowStart);
    }

    /// <summary>Whether this run as a whole warrants a restore point, checked once before any lane starts against the 24-hour throttle shared with the install page's toggle.</summary>
    private bool NeedsRestorePointForRun(IReadOnlyList<CleanupItemViewModel> selected, bool elevationRefused)
    {
        // Only a row with something to change asks: otherwise a no-op run would use up the 24-hour allowance, maybe with a UAC prompt and a minute's wait.
        var wantsRestorePoint = _settings.Current.Install.CreateRestorePoint
            && selected.Any(i => i.Target.Kind switch
            {
                CleanupKind.SystemRepair => true,
                CleanupKind.RegistryCleanup => SelectionFor(i).RegistryFindings.Count > 0,
                CleanupKind.DriverCleanup => SelectionFor(i).DriverDeviceInstanceIds.Count > 0
                    || SelectionFor(i).DriverPackageNames.Count > 0,
                CleanupKind.OldPythonVersions => SelectionFor(i).PythonVersionPaths.Count > 0,
                _ => false,
            });
        // A restore point needs administrator rights; asking again would raise the UAC prompt the user just declined.
        if (wantsRestorePoint && elevationRefused)
        {
            _logging.Info("Restore point skipped: administrator rights were declined for this run.");
            return false;
        }

        var shouldCreate = wantsRestorePoint
            && RestorePointThrottle.ShouldCreate(RestorePointThrottle.TryReadLastCreated(_logging), DateTimeOffset.UtcNow);
        if (wantsRestorePoint && !shouldCreate)
            _logging.Info("Restore point skipped: one was already created within the last 24 hours.");

        return shouldCreate;
    }

    /// <summary>Test seam: replaces the real elevated call with a fake awaitable.</summary>
    internal Func<CancellationToken, Task<bool>>? CreateRestorePointOverride { get; set; }

    /// <summary>Its own tiny elevated job (an empty "clean" command with <see cref="CleanupCommand.CreateRestorePoint"/> forced true), so it runs beside every lane. It uses the plain "cleanup" job kind, the same lane the file targets use, which gives the ordering the file lane needs.</summary>
    private Task<bool> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        CreateRestorePointOverride?.Invoke(cancellationToken)
        ?? RunElevatedAsync("clean", [], _ => { }, cancellationToken, forceRestorePoint: true);

    private static readonly System.Text.Json.JsonSerializerOptions WorkerJson =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    /// <summary>The elevated job kind a single "clean" target's kind maps to; pure so it is directly testable. Repair, DiskCleanup, Defragmentation and RegistryCleanup get their own lane; every other kind shares "cleanup", which lets file targets run together. Registry cleanup has its own kind so it neither serialises against the file lane nor blocks installs in <see cref="ElevatedWorker"/>.</summary>
    internal static string ElevatedJobKindFor(CleanupKind kind) => kind switch
    {
        CleanupKind.SystemRepair => "cleanup-repair",
        CleanupKind.DiskCleanup => "cleanup-diskcleanup",
        CleanupKind.Defragmentation => "cleanup-defrag",
        CleanupKind.RegistryCleanup or CleanupKind.DriverCleanup or CleanupKind.OldPythonVersions => "cleanup-registry",
        _ => "cleanup",
    };
}
