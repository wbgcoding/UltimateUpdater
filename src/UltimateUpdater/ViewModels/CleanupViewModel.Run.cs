using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// Performing the cleanup: the run loop and its elevated half, pause/stop, and the phase, ring,
// progress and result figures that follow from it.
public partial class CleanupViewModel
{
    /// <summary>
    /// Whether the dialog is actually on screen: the second gate on per-entry marking, set by the window
    /// (<c>IsVisibleChanged</c>/<c>StateChanged</c>) and cleared by <see cref="WindowClosed"/>. A run carries
    /// on with the dialog closed or minimized, and while it does, nothing is matched. Defaults to true so the
    /// view model is complete without a view; the window states the truth the moment it is shown.
    /// </summary>
    [ObservableProperty]
    private bool isWindowVisible = true;

    partial void OnIsWindowVisibleChanged(bool value)
    {
        // A report kept for a dialog that has since gone says nothing worth applying when it comes back.
        if (!value)
            _pendingEntryProgress.Clear();
    }

    /// <summary>
    /// The newest progress report per target, waiting for the tick: the third gate. Same shape the scan's
    /// own batching uses (<c>MainViewModel</c>'s 200 ms flush timer): a pass deleting hundreds of files a
    /// second has nothing to say that its newest report does not, and matching every one of them against
    /// every row of an open list is work nobody can see.
    /// </summary>
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

    /// <summary>The result figure, formatted. Shared with the dialog, which counts the number up to it at
    /// the end of a run instead of showing it at full size straight away.</summary>
    public static string FormatFreed(long bytes) => $"+{ByteSize.Format(bytes)}";

    /// <summary>The overall percentage, shown under the broom so the number is never lost behind it.</summary>
    public string CleanPercentText => PercentFormat.Format(CleanPercent);

    /// <summary>
    /// The ring's fill, 0-100: the selected share of the total while choosing, the run's own progress while
    /// cleaning, and full when done. Never NaN: an empty total reads as an empty ring. A selection that is
    /// a tiny fraction of the total still shows a minimum sliver, so "something is selected" is visible
    /// rather than looking like nothing (the converter refuses to draw anything smaller).
    /// </summary>
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
    public bool IsFinished => Phase == CleanupPhase.Finished;

    /// <summary>Rescan stays on offer after a run: the dialog would otherwise end with no button at all.</summary>
    public bool ShowRescanButton => Phase is CleanupPhase.Choosing or CleanupPhase.Finished;

    /// <summary>
    /// A run in progress can be held or stopped from the dialog. Stop is immediate: the
    /// token ends the local pass between files and travels to the elevated helper, which kills what it is
    /// running. The one thing it cannot cut short is a repair step already under way; killing sfc or DISM
    /// mid-write can damage the component store, so that stops after the current step.
    /// </summary>
    [ObservableProperty]
    private bool isPaused;

    /// <summary>
    /// Which of the two hold buttons is up. Two buttons rather than one whose label changes: the icon has to
    /// change with the word, and <c>ui:Button.Icon</c> holds an element, not a bindable glyph name: a single
    /// button kept the pause bars while reading "Fortsetzen".
    /// </summary>
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

        // The gate decides, so the button follows it immediately. Telling the elevated jobs comes
        // after; flipping the gate first means a dead pipe on the notify call still leaves the run in a
        // resumable state.
        try
        {
            if (IsPaused)
            {
                _pause.Resume();
                IsPaused = false;

                // Per job: one job's pipe having already died must not stop the
                // others from being told to resume: the gate itself already reflects the new state, so
                // leaving the rest untold would hold them for good.
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

    /// <summary>
    /// Stops the run. The gate is opened first, or a run that was paused would sit on it instead of noticing
    /// that it has been cancelled.
    /// </summary>
    [RelayCommand]
    private void Stop()
    {
        _pause.Resume();
        IsPaused = false;

        // Everything else ends within a moment; a repair step does not, and saying nothing about that is
        // what made a stopped run look like it was still going behind the user's back. The sfc and DISM steps are
        // deliberately never killed; DISM half way through restoring the component store can leave it
        // inconsistent, so the dialog says so instead of pretending.
        IsStoppingRepair = Items.Any(i => i.IsCleaning && i.Target.Kind == CleanupKind.SystemRepair);

        _cleanCts?.Cancel();
    }

    /// <summary>A repair step is finishing after the user asked to stop, because it cannot be interrupted.</summary>
    [ObservableProperty]
    private bool isStoppingRepair;

    public string StoppingRepairText => LocalizationService.Instance["CleanupStoppingRepair"];

    /// <summary>Something is ticked that actually has something to free (or is Disk Cleanup).</summary>
    public bool CanClean =>
        Phase == CleanupPhase.Choosing
        && Items.Any(i => i.IsSelected && (i.Bytes > 0 || i.Target.Kind != CleanupKind.Directories))
        // While a ticked recycle bin is being re-measured for a new age filter, the figure a confirmation
        // would show is about to be replaced: Clean waits for the new one.
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

    /// <summary>
    /// True once a run selected BOTH the file lane and Disk Cleanup: the two run at the same
    /// time, so the file lane's own counted bytes are not the whole story. Set once, before the
    /// run starts, from that run's own lane split, never merely because Disk Cleanup ran alone. Consumed
    /// only after the run ends, by <see cref="ShowsLaneOverlapExtra"/>.
    /// </summary>
    private bool _lanesOverlapped;

    /// <summary>
    /// Disk Cleanup never reports what it freed (cleanmgr says nothing, and guessing would be a lie), so
    /// its share is read back from the system drive's own before/after free space instead: whatever that
    /// delta holds beyond the file/registry lanes' own counted <see cref="FreedBytes"/> is what Disk
    /// Cleanup must have accounted for. Only meaningful once both readings exist and only shown for a run
    /// that actually had both lanes; a negative or zero result stays hidden rather than shown as a guess.
    /// </summary>
    private long? LaneOverlapExtraBytes => ComputeLaneOverlapExtraBytes(_freeSpaceBefore, _freeSpaceAfter, FreedBytes);

    /// <summary>Pure arithmetic, split out so it is testable without a real drive or a real run.</summary>
    internal static long? ComputeLaneOverlapExtraBytes(long? freeSpaceBefore, long? freeSpaceAfter, long freedBytes) =>
        freeSpaceBefore is null || freeSpaceAfter is null
            ? null
            : freeSpaceAfter.Value - freeSpaceBefore.Value - freedBytes;

    public bool ShowsLaneOverlapExtra => IsFinished && _lanesOverlapped && LaneOverlapExtraBytes is > 0;

    public string LaneOverlapExtraText => string.Format(
        Loc["CleanupLaneOverlapExtra"], ByteSize.Format(LaneOverlapExtraBytes ?? 0));

    public string LaneOverlapExtraTooltip => Loc["CleanupLaneOverlapExtraTooltip"];

    public bool HasSkipped => SkippedFiles > 0;

    /// <summary>Two counts, never one: a file in use and a file refused for lack of rights need different
    /// actions from the user (close a program, or run with admin rights).</summary>
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
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(ShowRescanButton));
        OnPropertyChanged(nameof(RingPercent));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(ShowPauseButton));
        OnPropertyChanged(nameof(ShowResumeButton));
        OnPropertyChanged(nameof(HasFreeSpaceResult));
        OnPropertyChanged(nameof(HasFreeSpaceNow));
        OnPropertyChanged(nameof(HasLastCleanup));
        OnPropertyChanged(nameof(ShowsLaneOverlapExtra));
        OnPropertyChanged(nameof(LaneOverlapExtraText));

        // The result reads per section once the run is over, not only as one figure in the ring.
        foreach (var group in Groups)
            group.ShowsFreed = value == CleanupPhase.Finished;

        RescanCommand.NotifyCanExecuteChanged();
    }

    partial void OnCleanPercentChanged(double value)
    {
        OnPropertyChanged(nameof(RingPercent));
        OnPropertyChanged(nameof(CleanPercentText));
    }

    partial void OnFreedBytesChanged(long value) => OnPropertyChanged(nameof(FreedText));

    partial void OnSkippedFilesChanged(int value)
    {
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(SkippedText));
    }

    /// <summary>
    /// Runs the cleanup. The caller (the window) must have shown the red confirmation first: this method is
    /// never reached from anywhere else, and there is no automatic path to it by design.
    /// </summary>
    public async Task RunCleanupAsync()
    {
        // Phase only turns to Cleaning after the scan has unwound, so two calls made in that window would
        // both pass a Phase check; the claim is taken before the first await and held until the run is over.
        if (Phase != CleanupPhase.Choosing || _runClaimed)
            return;

        _runClaimed = true;
        OnRunClaimChanged();

        // A run changes what the prescan measured; its result is no longer one to reuse or to stamp.
        SetPrescanFinishedAt(null);
        try
        {
            await RunCleanupClaimedAsync();
        }
        finally
        {
            _runClaimed = false;
            OnRunClaimChanged();
        }
    }

    /// <summary>Set from the first line of <see cref="RunCleanupAsync"/> to its last, on the UI thread.</summary>
    private bool _runClaimed;

    /// <summary>The claim covers the seconds before Phase turns to Cleaning (the scan unwinding, the UAC
    /// prompt): the dialog counts as busy then too, so Rescan cannot rewrite the rows about to be cleaned and
    /// closing the dialog cannot leave the run going unseen.</summary>
    private void OnRunClaimChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        RescanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>File targets go to the recycle bin instead of being deleted outright: the Settings window's
    /// switch, as it stood when this run started. A target that needs elevation ignores it regardless
    /// (CleanupService.CleanDirectories re-checks that itself).</summary>
    private bool _recycleInsteadOfDeleteForRun;

    private async Task RunCleanupClaimedAsync()
    {
        // A recycle-bin re-measure still in flight (an age filter committed by the very click on Clean) must
        // not swap the row's list after the user confirmed what it showed: the run cleans what was confirmed.
        if (_recycleBinRescanCts is { } rescan)
        {
            rescan.Cancel();
            _recycleBinRescanCts = null;
            foreach (var bin in Items.Where(i => i.IsRecycleBin && i.IsScanning))
                bin.IsScanning = false;
        }

        // The scan must not keep touching rows while they are being cleaned. Cancelling the token is only
        // a request: the scan's own loop can still be mid-await, one iteration from writing into an item's
        // Bytes/Files/entries right as the reset below runs, so the run also waits for the scan task itself
        // to actually finish before touching the same rows.
        _scanCts?.Cancel();
        await AwaitScanUnwindAsync(_scanTask);

        if (Phase != CleanupPhase.Choosing)
            return;

        // Exactly what is ticked in the list runs, every card is on screen.
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        // Read once when the run starts (the Settings window can change it while this dialog is open).
        _recycleInsteadOfDeleteForRun = _settings.Current.CleanupRecycleInsteadOfDelete;

        _cleanCts = new CancellationTokenSource();

        // Decided again from what is ticked NOW: the scan asked only for what was ticked then, and a target
        // that needs administrator rights ticked afterwards would otherwise run unelevated without a word.
        // Asks only when such a target is ticked and no helper runs; a declined prompt continues unelevated.
        var elevationRefused = await EnsureElevationAsync(_cleanCts.Token);

        // A gate left closed by the previous run would stop this one before it started.
        _pause.Resume();
        IsPaused = false;

        _freeSpaceBefore = FreeSpaceOnSystemDrive();
        var runTime = System.Diagnostics.Stopwatch.StartNew();
        _logging.Event(ActivityLog.Cleanup, $"start {selected.Count} targets");

        Phase = CleanupPhase.Cleaning;
        CleanPercent = 0;
        FreedBytes = 0;

        // A new run says nothing yet about its own pace; the first ticks decide.
        Activity = NeutralActivity;
        _lastProgressTick = 0;

        SkippedFiles = 0;
        SkippedBytes = 0;
        ProtectedFiles = 0;
        ProtectedBytes = 0;

        var elevatedTargets = ElevatedItems(selected);

        // Everything the run does not touch drops its capsule entirely rather than carrying a previous run's
        // word into this one.
        foreach (var item in Items)
            item.RunState = CleanupTargetState.Idle;

        // With ResetBase ticked too, "component-cleanup" is dropped from the repair lane (it is a subset of
        // it). It then never runs, so it must not count toward the ring or wait with a spinner all run long.
        var superseded = selected
            .Where(i => LaneFor(i.Target.Kind) == CleanupLane.Repair)
            .Except(ApplyComponentCleanupSupersedeRule(selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Repair).ToList()))
            .ToList();

        _runItems = selected.Except(superseded).ToList();
        foreach (var item in _runItems)
        {
            item.IsWaiting = true;
            item.IsDone = false;
            item.Percent = 0;
            item.FreedBytes = 0;
            item.SkippedBytes = 0;
            item.ProtectedBytes = 0;
            item.DetailText = string.Empty;
            item.RunState = CleanupTargetState.Waiting;
            item.ResetEntryStates();
        }

        _pendingEntryProgress.Clear();
        _entryProgressTimer.Start();

        // Five lanes, run CONCURRENTLY:
        // FILE: everything that only deletes files (also AppCacheSweep and LeftoverInstalls, which use the
        // same CleanOneAsync path and restore-point wait as Directories/RecycleBin). Each target owns its
        // own directories and the work waits on the disk, so running them one after another would only
        // make the dialog take the sum of all of them; they run all at once.
        // REGISTRY: its own lane because it needs catalogue order, but it deletes like the file lane, so
        // it waits on the SAME restore-point task (only deleting files and registry entries waits on it).
        // REPAIR: DISM then sfc, STRICTLY in that catalogue order, because sfc restores its replacement
        // files FROM the store DISM repairs.
        // DISK CLEANUP: on its own because it walks the very temp folders the file lane empties; the two
        // must not fight each other's file counts silently (see ShowsLaneOverlapNote below).
        // DEFRAG: one or more drives, one after another (a single elevated worker lane already keeps two
        // of them from overlapping), never blocking the other four.
        var fileLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.File).ToList();
        var registryLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Registry).ToList();
        var repairLane = ApplyComponentCleanupSupersedeRule(
            selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Repair).ToList());
        var diskCleanupLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.DiskCleanup).ToList();
        var defragLane = selected.Where(i => LaneFor(i.Target.Kind) == CleanupLane.Defrag).ToList();

        // A file one lane removed is counted by the other as skipped once they can genuinely overlap in
        // time; shown only when both lanes actually had something to run, never for Disk Cleanup (or the
        // file lane) running alone.
        _lanesOverlapped = fileLane.Count > 0 && diskCleanupLane.Count > 0;

        // ONE restore-point decision for the whole run (see CreateRestorePointAsync's own comment), made
        // and kicked off here, before any lane is dispatched, so it runs IN PARALLEL with the
        // repair/disk-cleanup/defrag lanes rather than ahead of them. Only the file lane removes anything,
        // so only it waits on the result (in CleanOneAsync); the restore-point throttle is untouched, and
        // when nothing is due this is a completed task and nobody waits at all.
        var restorePointTask = NeedsRestorePointForRun(selected, elevationRefused)
            ? CreateRestorePointAsync(_cleanCts.Token)
            : Task.FromResult(false);

        var cancelled = false;
        try
        {
            // WhenAll waits for every task even when one of them ends early, so a cancellation cannot leave
            // a lane running behind the dialog's back. The restore-point task is always one of the awaited
            // lanes, not only reachable through the file/registry lane's own wait: a repair/Disk-Cleanup/
            // defrag-only run must still wait for it and observe its failure, rather than reporting
            // Finished while it is still being created.
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
            _cleanJobs.Clear();
            _runItems = null;
            _pause.Resume();
            IsPaused = false;
            IsStoppingRepair = false;

            _entryProgressTimer.Stop();
            _pendingEntryProgress.Clear();

            // Anything the run never reached must stop claiming it is about to start.
            foreach (var item in selected)
            {
                item.IsWaiting = false;
                item.IsCleaning = false;
                item.DetailText = string.Empty;

                // Still queued when the run ended: it was never started, so nothing of it was removed.
                if (item.RunState == CleanupTargetState.Waiting)
                {
                    item.RunState = CleanupTargetState.Skipped;
                    item.ResolveEntryStates(completed: false);
                }
            }

            // A cancelled run must not paint a full ring: the figure it reports is what it actually freed
            // before it stopped, and claiming 100 % would be a different number's worth of confidence.
            if (!cancelled)
                CleanPercent = 100;

            _freeSpaceAfter = FreeSpaceOnSystemDrive();

            Phase = CleanupPhase.Finished;
            _logging.Event(
                ActivityLog.Cleanup,
                ActivityLog.CleanupFinished(FreedBytes, SkippedFiles, cancelled, runTime.Elapsed));

            RecordRun(selected, cancelled);
        }
    }

    /// <summary>The five concurrent lanes dispatch a selected target into.</summary>
    public enum CleanupLane
    {
        File,
        Registry,
        Repair,
        DiskCleanup,
        Defrag,
    }

    /// <summary>
    /// ResetBase already does everything the plain component cleanup does (the same DISM command
    /// plus <c>/ResetBase</c>), so selecting both would just repeat the same, often long, pass for nothing.
    /// ResetBase wins; the plain cleanup row simply drops out of THIS run's repair lane (it stays selected
    /// and ticked for next time, it just never starts, same as any other row the run does not reach).
    /// Pure and static so the decision is directly testable without building a whole run.
    /// </summary>
    internal static List<CleanupItemViewModel> ApplyComponentCleanupSupersedeRule(List<CleanupItemViewModel> repairLane) =>
        repairLane.Any(i => i.Target.Id == "component-resetbase")
            ? repairLane.Where(i => i.Target.Id != "component-cleanup").ToList()
            : repairLane;
    /// <summary>
    /// Which of the five lanes a target's kind runs in. Every named <see cref="CleanupKind"/> member is
    /// covered explicitly, so no selectable kind can match no lane and end "Skipped"; a new CleanupKind
    /// has to be added here too, or it fails loudly at runtime (the trailing throw) rather than silently
    /// going nowhere. The file kinds share the FILE lane because they take the same
    /// CleanOneAsync/CleanupService.CleanAsync path and differ only in where the directory list comes
    /// from. The registry-like kinds get their own lane because they need catalogue order (FILE runs all
    /// at once), but they still delete, so their caller awaits the same restore-point task FILE does.
    /// </summary>
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

    /// <summary>
    /// Whether the CURRENT selection would start a rotational-disk optimisation (HDD only,
    /// "this can take hours"): read by the window before <see cref="RunCleanupAsync"/>, since the
    /// warning belongs beside the existing red confirmation, not inside the run itself.
    /// </summary>
    public bool HasSelectedHddDefragTarget => Items.Any(i =>
        i.IsSelected && i.Target.Kind == CleanupKind.Defragmentation && i.Target.MediaType == DriveMediaType.Hdd);

    /// <summary>
    /// Runs every lane at the same time; a pure orchestration step with no <see
    /// cref="CleanupService"/> and no elevated pipe of its own, so a test can prove the lanes genuinely
    /// overlap using fake delegates instead of real files or a real elevated worker.
    /// </summary>
    internal static Task RunLanesConcurrentlyAsync(IReadOnlyList<Func<Task>> lanes) =>
        Task.WhenAll(lanes.Select(lane => lane()));

    /// <summary>
    /// Waits for a previous scan to actually stop touching <see cref="Items"/> before a run resets the
    /// same rows. Cancelling <c>_scanCts</c> is only a request: the scan loop can still be mid-await, one
    /// <c>ScanAsync</c> call from writing a fresh Bytes/Files figure into an item the run is about to
    /// reset to zero. No task, or a finished one, returns at once; the cancelled scan's own
    /// <see cref="OperationCanceledException"/> is expected and swallowed. Static and parameterised so a
    /// bare fake task can test it.
    /// </summary>
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

    /// <summary>
    /// The lane functions one run hands to <see cref="RunLanesConcurrentlyAsync"/>. The restore-point task
    /// is always one of them, appended as its own tiny lane: a run whose selection is repair, Disk
    /// Cleanup or defrag only never passes <paramref name="restorePointTask"/> into any of those lanes
    /// (they delete nothing, so they never wait on it), and without this the task would run unobserved and
    /// a repair-only run could report Finished while the restore point was still being created. Pure and
    /// static so fake lane delegates can test it.
    /// </summary>
    internal static IReadOnlyList<Func<Task>> BuildRunLanes(
        Func<Task> fileLane, Func<Task> registryLane, Func<Task> repairLane, Func<Task> diskCleanupLane,
        Func<Task> defragLane, Task restorePointTask) =>
        [fileLane, registryLane, repairLane, diskCleanupLane, defragLane, () => restorePointTask];

    /// <summary>One lane's own targets, strictly one after another in the order given (catalogue order,
    /// since <paramref name="lane"/> is filtered from <c>Items</c> without being reordered) - what keeps
    /// DISM ahead of sfc inside the repair lane while that lane runs beside the others.
    /// <paramref name="waitForRestorePoint"/> is passed straight through to every target in the lane, same
    /// as the file lane's own call - only the registry lane's caller actually supplies it, since it is the
    /// only OTHER lane that deletes anything (see <see cref="NeedsRestorePointForRun"/>).</summary>
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

    /// <summary>
    /// The restore-point wait, pulled out so it is directly testable with no real elevated worker: a no-op,
    /// not even a real suspension point, when there is nothing to wait for (no task at all, or one already
    /// finished, the common case once nothing is due), which is also why a run where no restore point is
    /// due never shows the waiting text at all. Only when the task is genuinely still pending does this
    /// show it and actually await it.
    /// </summary>
    internal static async Task WaitForRestorePointAsync(
        Task<bool>? waitForRestorePoint, Action showWaitingText, CancellationToken cancellationToken)
    {
        if (waitForRestorePoint is not { IsCompleted: false })
            return;

        showWaitingText();
        await waitForRestorePoint.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// One target, start to finish. Everything a target reports lands on its own row; the ring is the mean
    /// of every row, which is what makes it correct whether the targets run together or one after another.
    /// A failure is logged and the run carries on: one target must never take the rest with it.
    /// </summary>
    private async Task CleanOneAsync(
        CleanupItemViewModel item,
        IReadOnlyCollection<CleanupItemViewModel> elevatedTargets,
        CancellationToken cancellationToken,
        Task<bool>? waitForRestorePoint = null)
    {
        // Only a target that REMOVES something waits on the restore point (the file lane is
        // the only caller that ever passes this).
        await WaitForRestorePointAsync(waitForRestorePoint, () => item.DetailText = Loc["CleanupRestorePointParallel"], cancellationToken);

        item.IsWaiting = false;
        item.IsCleaning = true;
        item.RunState = CleanupTargetState.Running;
        item.Percent = 0;
        item.FreedBytes = 0;

        // What this target has told the run total so far. The total is always the finished targets' outcomes
        // plus this live part of every target still running, so it climbs while a target works and lands on
        // exactly the sum of the outcomes once they are all in.
        long liveFreed = 0;
        var settled = false;

        var progress = new Progress<CleanupProgress>(report =>
        {
            // A report is posted, so a last one can arrive after the target has settled its figure.
            if (!settled && report.FreedBytes > liveFreed)
            {
                FreedBytes += report.FreedBytes - liveFreed;
                liveFreed = report.FreedBytes;
            }

            // Never backwards, defense in depth: the elevated worker forwards its targets' own reports
            // over the pipe unchanged, and a repair step's percentage already resets between DISM phases
            // (CleanupService.RunSystemRepair guards its own figure too). item.Percent is reset to 0
            // explicitly before this callback is wired for a fresh target, so the guard never sticks a NEW
            // target at its predecessor's percentage.
            item.Percent = Math.Max(item.Percent, report.Percent);
            item.FreedBytes = report.FreedBytes;
            if (!string.IsNullOrEmpty(report.CurrentItem))
                item.DetailText = report.CurrentItem;

            // The bar, the percentage and the line under it follow EVERY report; only the per-row marking
            // in the detail list below is held back to one pass per tick.
            ReportEntryProgress(item, report);

            RefreshRunProgress();
        });

        string? failureText = null;

        Task<CleanupOutcome> CleanLocallyAsync() => _service.CleanAsync(
            item.Target, progress, cancellationToken, _pause, _settings.Current.RegistryCleanup,
            item.SelectedLeftoverPaths, item.RegistryFindings, ExcludedPathsForRun(item), _recycleInsteadOfDeleteForRun,
            item.RecycleBinMinAgeDays, item.SelectedDriverDeviceInstanceIds, item.SelectedDriverPackageNames,
            item.SelectedOldPythonVersionPaths, item.ConfirmedEntryPathsForRun);

        try
        {
            // The elevated helper owns the targets that need admin; everything else runs here, unelevated.
            // A helper that dropped out falls back to a local pass, which frees what it
            // can rather than skipping the target outright.
            var outcome = elevatedTargets.Contains(item)
                ? await CleanElevatedAsync(item, progress, cancellationToken)
                  ?? await CleanLocallyAsync()
                : await CleanLocallyAsync();

            // A per-user interpreter's own uninstaller must never run inside the elevated worker (HKCU is
            // user-writable, so a command read from it must never run with administrator rights; see
            // PythonVersionCleanupExecutor's doc comment). The elevated pass above therefore only acts on
            // machine-wide rows and leaves any per-user selection untouched; this always-local pass is that
            // row's real home. PythonVersionCleanupExecutor filters to per-user candidates itself when NOT
            // running as the elevated worker, so this is a no-op when nothing per-user was selected.
            // Skipped after a stop: the second pass would only throw on the cancelled token and throw away
            // the counts the elevated half already reported.
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

            // The live part already counted is swapped for the outcome's own figure, so the total ends
            // exactly where it did when only finished targets counted.
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

            // Driver cleanup exported its packages into the elevated worker's own ProgramData staging
            // folder (it must never resolve AppPaths itself); the copy into the data folder happens here,
            // unelevated, best effort, so "a backup of each package is kept in the data folder" stays
            // true without ever trusting this process's own data path with admin rights.
            if (outcome.DriverBackupFolderName is { Length: > 0 } folderName)
            {
                var copySource = DriverBackupStaging.ResolveExistingPath(folderName);
                var copyDestination = Path.Combine(AppPaths.DataDirectory, "driver-backups", folderName);
                if (!AppPaths.TryCopyDirectory(copySource, copyDestination, out _))
                    _logging.Warn($"Driver cleanup: could not copy the backup folder '{folderName}' into the data folder.");
            }

            // WasStopped (Disk Cleanup only, see CleanupOutcome) means this target swallowed its own
            // cancellation rather than letting it propagate: it must not read "done" for a run the user
            // just stopped.
            if (outcome.TimedOut)
            {
                // Ended by the time limit: neither done nor stopped by the user, so the row says why it
                // freed nothing.
                item.RunState = CleanupTargetState.Skipped;
                failureText = string.Format(
                    Loc["CleanupDiskCleanupTimedOut"], (int)CleanupService.DiskCleanupLimit.TotalMinutes);
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
            // A stop mid-target throws away the outcome, but what the progress reports already confirmed as
            // gone is gone: it is in the total already (the live part), so the ring and the history agree
            // with the row's own "+2 GB".
            settled = true;
            throw;
        }
        catch (Exception ex)
        {
            // One target failing must not stop the rest; it is reported in the log. Its row says "skipped"
            // rather than "stopped": nobody stopped it, and it must not read as finished either.
            item.RunState = CleanupTargetState.Skipped;
            _logging.Error($"Cleanup failed for '{item.Target.Id}'", ex);

            // Its outcome never arrived and counted nothing before live figures existed: take its live part
            // back out, so the failed target still adds nothing.
            settled = true;
            FreedBytes -= liveFreed;
            liveFreed = 0;
        }
        finally
        {
            // In the finally, not after the try: a target that threw, including the fallback pass
            // throwing, otherwise left its row stuck showing a running progress bar for ever.
            item.IsCleaning = false;
            item.DetailText = failureText ?? string.Empty;

            // Nothing of this target may still read "running" now that the target itself does not.
            _pendingEntryProgress.Remove(item);
            item.ResolveEntryStates(item.RunState == CleanupTargetState.Done);

            RefreshRunProgress();
        }
    }

    /// <summary>
    /// Keeps the newest report for a target, and only when there is somewhere to show it: a closed detail
    /// list or a dialog that is not on screen never even buffers one. Public for the same reason as
    /// <see cref="FlushEntryProgress"/>: the run that normally feeds this cannot be started in a test.
    /// </summary>
    public void ReportEntryProgress(CleanupItemViewModel item, CleanupProgress report)
    {
        // RunState, not IsCleaning: a Progress<T> callback is posted, so a last report can still arrive after
        // the target has settled its rows; applying it would put one of them back on "running" for good.
        if (item.RunState != CleanupTargetState.Running || !IsWindowVisible || !item.IsDetailsExpanded
            || !item.HasEntries || string.IsNullOrEmpty(report.CurrentItem))
            return;

        _pendingEntryProgress[item] = report;
    }

    /// <summary>
    /// Applies at most one report per target per tick. Public because the tick is a
    /// <see cref="System.Windows.Threading.DispatcherTimer"/>, which needs a message pump the view-model
    /// tests do not have: they drive this directly, exactly as the batching it mirrors is driven.
    /// </summary>
    public void FlushEntryProgress()
    {
        if (_pendingEntryProgress.Count == 0)
            return;

        foreach (var (item, report) in _pendingEntryProgress)
        {
            // Re-checked here, not only when the report was kept: the list can be closed, or the target can
            // finish, between the two.
            if (IsWindowVisible && item.IsDetailsExpanded && item.RunState == CleanupTargetState.Running)
                item.ApplyEntryProgress(report.CurrentItem, report.FreedBytes);
        }

        _pendingEntryProgress.Clear();
    }

    /// <summary>
    /// The ring and the line beside it, recomputed from the rows themselves. Averaging the rows rather than
    /// counting completed targets is what lets several of them run at once without the figure jumping.
    /// </summary>
    private void RefreshRunProgress()
    {
        if (_runItems is not { Count: > 0 } items)
            return;

        TrackActivity();

        CleanPercent = Math.Min(100, items.Sum(i => i.IsDone ? 100 : i.Percent) / items.Count);

        var running = items.Where(i => i.IsCleaning).ToList();
        CurrentItemText = running.Count > 0
            ? string.Join(" · ", running.Select(i => i.Title))
            : string.Empty;

        // Under the name: what that target is actually doing, and how far it has got. With several targets running the one that has said something
        // most recently is the interesting one; a repair step always reports, a file pass reports its path.
        var detailed = running.FirstOrDefault(i => i.HasDetail);
        CurrentDetailText = detailed?.DetailText ?? string.Empty;
        CurrentTargetPercentText = detailed is null
            ? string.Empty
            : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Loc["CleanupCurrentTargetPercent"], detailed.Title, detailed.Percent);

        OnPropertyChanged(nameof(DoneCountText));
        OnPropertyChanged(nameof(RemainingCountText));
    }

    /// <summary>
    /// How lively the run is right now, 0…1, taken from how closely the progress ticks follow each other:
    /// a pass deleting file after file sits near 1, a repair step that says nothing for seconds decays
    /// towards 0. The dialog turns it into the speed of its sweep animation, so the picture matches the
    /// pace of the work rather than running at one invented tempo.
    /// </summary>
    [ObservableProperty]
    private double activity = NeutralActivity;

    /// <summary>Where a run starts: no ticks have been compared yet, so neither fast nor stalled.</summary>
    private const double NeutralActivity = 0.5;

    private long _lastProgressTick;

    /// <summary>
    /// Folds the gap since the previous progress tick into <see cref="Activity"/>. Only published once it
    /// has moved far enough to be worth a frame; ticks can arrive many times a second, and a change of a
    /// hundredth is invisible.
    /// </summary>
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

    /// <summary>
    /// Exponential smoothing with the gap itself as the weight: closely spaced ticks pull the value up
    /// slowly, and a long silence weighs almost nothing against the low reading that silence produced, so
    /// a stalled step decays on its own instead of holding the last fast figure. Pure, and unit-tested.
    /// </summary>
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

    /// <summary>"Temporäre Windows-Dateien · 62 %"; how far the target itself has got.</summary>
    [ObservableProperty]
    private string currentTargetPercentText = string.Empty;

    public bool HasCurrentDetail => CurrentDetailText.Length > 0;

    partial void OnCurrentDetailTextChanged(string value) => OnPropertyChanged(nameof(HasCurrentDetail));

    /// <summary>"4 von 10 erledigt": the run's own count, beside the ring while it works.</summary>
    public string DoneCountText => _runItems is { Count: > 0 } items
        ? string.Format(Loc[items.Count == 1 ? "CleanupDoneCountOne" : "CleanupDoneCount"], items.Count(i => i.IsDone), items.Count)
        : string.Empty;

    /// <summary>What is still ahead, so the figure beside the ring is not only about the past.</summary>
    public string RemainingCountText => _runItems is { Count: > 0 } items
        ? string.Format(Loc["CleanupRemainingCount"], items.Count(i => !i.IsDone))
        : string.Empty;

    /// <summary>
    /// One target through the elevated helper. Returns null when the session broke, so the caller can fall
    /// back to a local pass instead of silently skipping the target.
    /// </summary>
    private async Task<CleanupOutcome?> CleanElevatedAsync(
        CleanupItemViewModel item, IProgress<CleanupProgress> progress, CancellationToken cancellationToken)
    {
        // Once "result" arrives for this target, every later "progress" is ignored.
        // CleanupWorker.CleanOneAsync's progress callbacks run on thread-pool threads while its "result" is
        // sent synchronously right after CleanAsync returns, so a progress line queued just before the end
        // can still be written to the pipe after the result and would otherwise move a finished figure back
        // to an earlier value on screen. ApplySingleTargetMessage carries the whole rule and is directly
        // testable on its own with a fabricated message sequence.
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
            // Out of the list as soon as this target is done, so a later Pause does not talk to a job that
            // has already ended.
            _cleanJobs.Remove(handle);
        }
    }

    /// <summary>
    /// One target's own little state machine: "progress" updates the ring until "result" arrives, after
    /// which the target is <see cref="SingleTargetCleanupState.Finished"/> and every further "progress"
    /// for it is dropped. <paramref name="message"/> is otherwise passed through unmatched ("log",
    /// "restorePointCreated" and "done" lines never reach this method, see <see cref="RunElevatedAsync"/>'s
    /// dispatch). Keyed by nothing but call order, because this handler only ever serves ONE target at a
    /// time (a "clean" command names exactly one, see <see cref="CleanElevatedAsync"/>), so a plain
    /// sequence of calls reproduces the race with no real elevated worker.
    /// </summary>
    internal static (SingleTargetCleanupState State, CleanupProgress? ProgressToApply) ApplySingleTargetMessage(
        SingleTargetCleanupState state, CleanupWorkerMessage message)
    {
        if (message.Type == "result")
        {
            var outcome = new CleanupOutcome(
                message.Freed, message.Files, message.Skipped, message.SkippedBytes,
                WasStopped: message.Stopped, DriverBackupFolderName: message.DriverBackupFolderName,
                TimedOut: message.TimedOut, ProtectedFiles: message.ProtectedFiles, ProtectedBytes: message.ProtectedBytes);
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

    /// <summary>
    /// One command to the shared elevated helper, with its replies decoded into cleanup messages. The
    /// helper belongs to the app, not to this dialog, so it is never shut down from here.
    /// </summary>
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
            // A "clean" command always targets one item at a time (see CleanElevatedAsync): a scan needs
            // no selection at all, and never mixing several items' selections into one list keeps this safe
            // even if that single-item assumption ever changes.
            SelectedLeftoverPaths = command == "clean" && targets is [{ Target.Kind: CleanupKind.LeftoverInstalls } only]
                ? only.SelectedLeftoverPaths
                : [],
            RegistryFindings = command == "clean" && targets is [{ Target.Kind: CleanupKind.RegistryCleanup } onlyRegistry]
                ? onlyRegistry.RegistryFindings.Select(f => new RegistryFindingDto
                {
                    Category = f.Category,
                    KeyPath = f.KeyPath,
                    ValueName = f.ValueName,
                    DisplayName = f.DisplayName,
                    BrokenTarget = f.BrokenTarget,
                }).ToList()
                : [],
            // Not kind-restricted like the two above: a detail list can exist on a
            // Directories/AppCacheSweep OR a RegistryCleanup row alike, so any single-item "clean" command
            // carries whatever that one row's own deselections are; empty for a row with no list at all.
            ExcludedEntryPaths = command == "clean" && targets is [{ } onlyItem]
                ? ExcludedPathsForRun(onlyItem).ToList()
                : [],
            ConfirmedEntryPaths = command == "clean" && targets is [{ Target.Kind: CleanupKind.RecycleBin } onlyBin]
                ? onlyBin.ConfirmedEntryPathsForRun?.ToList()
                : null,
            SelectedDriverDeviceInstanceIds = command == "clean" && targets is [{ Target.Kind: CleanupKind.DriverCleanup } onlyDriver]
                ? onlyDriver.SelectedDriverDeviceInstanceIds
                : [],
            SelectedDriverPackageNames = command == "clean" && targets is [{ Target.Kind: CleanupKind.DriverCleanup } onlyDriverPkg]
                ? onlyDriverPkg.SelectedDriverPackageNames
                : [],
            SelectedPythonVersionPaths = command == "clean" && targets is [{ Target.Kind: CleanupKind.OldPythonVersions } onlyPython]
                ? onlyPython.SelectedOldPythonVersionPaths
                : [],
        };

        // "cleanup" for a scan (never takes a lane at all, Exclusive is false below) and for every
        // file-deletion "clean"; "cleanup-repair"/"cleanup-diskcleanup"/"cleanup-defrag"/"cleanup-registry"
        // only for a single-target "clean" naming that kind, so the elevated worker's ONE-lane-per-job-kind
        // rule gives repair, Disk Cleanup, defrag and registry cleanup their own lanes instead of fighting
        // the file lane's jobs, and each other, over the same "cleanup" semaphore.
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

            // Onto the UI thread, and synchronously: the "result" message assigns the outcome the caller
            // reads once RunAsync returns, so it must have happened by then. Invoke cannot deadlock here;
            // the UI thread is awaiting a TCS, not blocking on the pipe.
            if (_dispatcher.CheckAccess())
                onMessage(message);
            else
                _dispatcher.Invoke(() => onMessage(message));

            return true;
        },
        cancellationToken,
        // Measuring only reads, so it runs alongside whatever else the helper is doing: both "scan" and
        // "analyze" (DISM's own AnalyzeComponentStore, never a delete) qualify. Treating an analysis as
        // exclusive would queue it silently behind a running cleanup with no wait indicator, which looks
        // like a hang. Cleaning changes the machine and takes the exclusive slot; emptying Temp while an
        // installer is using it would break both.
        exclusive: command is not ("scan" or "analyze"),
        handle,
        allowStart);
    }

    /// <summary>
    /// Whether THIS run, as a whole, warrants a restore point, checked once, before any lane
    /// starts, against the run's own throttle (one restore point per 24 hours, regardless of which feature
    /// asked for it, reusing the install page's own toggle, same rule already established, just
    /// moved from a per-target check to a per-run one).
    /// </summary>
    private bool NeedsRestorePointForRun(IReadOnlyList<CleanupItemViewModel> selected, bool elevationRefused)
    {
        // Only a row with something to change asks: a driver or Python row with no candidate ticked, or a
        // registry row with no finding, would otherwise create a restore point (and use up the 24-hour
        // allowance, maybe with a UAC prompt and a minute's wait) for a run that changes nothing.
        var wantsRestorePoint = _settings.Current.Install.CreateRestorePoint
            && selected.Any(i => i.Target.Kind switch
            {
                CleanupKind.SystemRepair => true,
                CleanupKind.RegistryCleanup => i.RegistryFindings.Count > 0,
                CleanupKind.DriverCleanup => i.SelectedDriverDeviceInstanceIds.Count > 0 || i.SelectedDriverPackageNames.Count > 0,
                CleanupKind.OldPythonVersions => i.SelectedOldPythonVersionPaths.Count > 0,
                _ => false,
            });
        // A restore point needs administrator rights; the user just declined them for this run, and
        // asking again would raise the very UAC prompt they said no to.
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

    /// <summary>
    /// Test seam: replaces the real elevated call below with a fake awaitable, the way it proves the
    /// repair/disk-cleanup/defrag lanes are dispatched before this resolves while a file target waits on
    /// it, with no real elevated worker and no real restore point.
    /// </summary>
    internal Func<CancellationToken, Task<bool>>? CreateRestorePointOverride { get; set; }

    /// <summary>
    /// Its own tiny elevated job, no real cleanup target (an empty "clean" command with
    /// <see cref="CleanupCommand.CreateRestorePoint"/> forced true), so creating the restore point is
    /// tied to no lane's first target and runs beside every lane instead of ahead of them. It falls into
    /// the plain "cleanup" job kind (ElevatedJobKindFor's default for an empty target list), the SAME
    /// per-kind lane the file lane's real targets use, which is the ordering the file lane needs, on top
    /// of the explicit await in CleanOneAsync.
    /// </summary>
    private Task<bool> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        CreateRestorePointOverride?.Invoke(cancellationToken)
        ?? RunElevatedAsync("clean", [], _ => { }, cancellationToken, forceRestorePoint: true);

    private static readonly System.Text.Json.JsonSerializerOptions WorkerJson =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The elevated job kind a single "clean" target's own kind maps to; pure so the mapping is directly
    /// testable. <see cref="CleanupKind.SystemRepair"/>, <see cref="CleanupKind.DiskCleanup"/>,
    /// <see cref="CleanupKind.Defragmentation"/> and <see cref="CleanupKind.RegistryCleanup"/> each get a
    /// lane of their own; every other kind shares the plain "cleanup" lane, which lets the file targets
    /// run TOGETHER. Registry cleanup has its own kind: under the plain "cleanup" kind it would
    /// serialise against the file lane's per-kind semaphore in the elevated worker AND sit in
    /// <see cref="ElevatedWorker"/>'s contending-kinds set, blocking installs behind it, while the
    /// product decision is that registry cleanup runs beside installs, like repair and defrag.
    /// </summary>
    internal static string ElevatedJobKindFor(CleanupKind kind) => kind switch
    {
        CleanupKind.SystemRepair => "cleanup-repair",
        CleanupKind.DiskCleanup => "cleanup-diskcleanup",
        CleanupKind.Defragmentation => "cleanup-defrag",
        CleanupKind.RegistryCleanup or CleanupKind.DriverCleanup or CleanupKind.OldPythonVersions => "cleanup-registry",
        _ => "cleanup",
    };
}
