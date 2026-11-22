using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Cleanup;

// Measuring what could be freed: the scan run itself, its rescan command, and the elevated helper
// the scan needs to reach targets an ordinary process cannot read.
public partial class CleanupViewModel
{
    [ObservableProperty]
    private bool isScanning;

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        NotifyRescanCanExecuteChanged();
    }

    /// <summary>Asks for administrator rights before measuring: without them sfc, the DISM sequence and Disk Cleanup do nothing and Windows' temp folders cannot be measured or emptied in full. Declining is a normal answer: the run continues unelevated and the affected rows are marked skipped. Returns true when the user said no (or the helper could not be started).</summary>
    private async Task<bool> EnsureElevationAsync(
        CancellationToken cancellationToken, IReadOnlyCollection<CleanupItemViewModel>? scope = null)
    {
        var needsAdmin = (scope ?? Items).Any(i => i.IsSelected && i.NeedsElevation);
        if (!needsAdmin || ElevationService.IsElevated || _elevated is null)
        {
            SetElevationHints(show: false);
            return false;
        }

        // If the helper already runs there is nothing to ask. Otherwise this asks even after an earlier "no": opening this dialog is a deliberate click, and an accidental "no" would leave the feature half-working for the session.
        var force = false;
        if (!_elevated.IsRunning)
        {
            var accepted = Dialogs.Confirm(
                Loc["CleanupAdminHeadline"],
                Loc["CleanupAdminBody"],
                confirmText: Loc["CleanupAdminConfirm"],
                cancelText: Loc["CleanupAdminDecline"]);

            if (!accepted)
            {
                SetElevationHints(show: true);
                return true;
            }

            force = true;
        }

        var started = await _elevated.EnsureStartedAsync(cancellationToken, force);
        if (!started)
            _logging.Info("Cleanup: continuing without administrator rights.");

        SetElevationHints(show: !started);
        return !started;
    }

    private void SetElevationHints(bool show)
    {
        foreach (var item in Items)
            item.ShowsElevationHint = show && item.NeedsElevation;

        foreach (var group in Groups)
            group.RefreshAdminDependentTotals();

        RefreshTotals();
    }

    /// <summary>Which targets this run hands to the elevated helper, none when there is no helper. Reads the helper's state now, since the scan's answer can be stale.</summary>
    private List<CleanupItemViewModel> ElevatedItems(IEnumerable<CleanupItemViewModel> items) =>
        !ElevationService.IsElevated && _elevated is { IsRunning: true }
            ? items.Where(i => i.Target.NeedsElevation).ToList()
            : [];

    private CleanupItemViewModel? ItemFor(string? id) =>
        id is null ? null : Items.FirstOrDefault(i => i.Target.Id == id);

    private static CleanupEntry ToEntry(CleanupEntryDto dto) => new(
        dto.Path, dto.DisplayName, dto.Bytes, dto.Files, dto.OldestWriteTime, dto.NewestWriteTime, dto.Type,
        dto.OriginalPath);

    /// <summary>Measures everything again on demand: the figures are a snapshot of a machine that keeps working.</summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => StartScanAsync();

    private bool CanRescan() => Phase is CleanupPhase.Choosing or CleanupPhase.Finished && !IsScanning && !_runClaimed;

    /// <summary>The right-click "Rescan" of one sub-category: measures just that row through the same pass as the full rescan, leaving every other row as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanItemAsync(CleanupItemViewModel? item) =>
        item is not null && Items.Contains(item) ? StartSubsetScanAsync([item]) : Task.CompletedTask;

    /// <summary>The right-click "Rescan" of a whole category: its rows only, otherwise as <see cref="RescanItemAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanGroupAsync(CleanupGroupViewModel? group) =>
        group is not null && Groups.Contains(group) ? StartSubsetScanAsync(group.Items) : Task.CompletedTask;

    /// <summary>After a finished run every row goes back to choosing, so a partial pass would leave just-cleaned rows with old sizes and ticks. There the rescan covers every row.</summary>
    private Task StartSubsetScanAsync(IReadOnlyList<CleanupItemViewModel> subset)
    {
        if (subset.Count == 0 || !CanRescan())
            return Task.CompletedTask;

        return Phase == CleanupPhase.Finished ? StartScanAsync() : StartScanTrackedAsync(ScanMode.Subset, subset);
    }

    private void NotifyRescanCanExecuteChanged()
    {
        RescanCommand.NotifyCanExecuteChanged();
        RescanItemCommand.NotifyCanExecuteChanged();
        RescanGroupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>How far one measuring pass reaches. Full: every target, asking for rights first. Prescan: the quiet startup pass, user-scope targets only. Remaining: finishes a prescan on open. Late: Remaining done quietly after the elevated helper started, at low priority. Subset: a user-requested rescan of chosen rows, otherwise Full.</summary>
    private enum ScanMode
    {
        Full,
        Prescan,
        Remaining,
        Late,
        Subset,
    }

    /// <summary>Targets a prescan left out because they need rights the app does not have yet.</summary>
    private List<CleanupItemViewModel> _prescanSkipped = [];

    private Task? _prescanTask;

    /// <summary>Where "now" comes from for the measurement stamp; a test replaces it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>When the startup prescan finished, or null when none did or the user rescanned since. Decides whether a reopened dialog may reuse the result.</summary>
    public DateTime? PrescanFinishedAt { get; private set; }

    /// <summary>True while the startup prescan is still measuring.</summary>
    internal bool IsPrescanRunning => _prescanTask is { IsCompleted: false };

    internal Task WaitForPrescanAsync() => _prescanTask ?? Task.CompletedTask;

    /// <summary>The quiet "As of 14:05" line for a dialog opened on a prescanned result; empty after a rescan.</summary>
    public string MeasuredAtText =>
        PrescanFinishedAt is { } at
            ? string.Format(Loc["CleanupMeasuredAt"], at.ToString("t", System.Globalization.CultureInfo.CurrentUICulture))
            : string.Empty;

    public bool HasMeasuredAt => PrescanFinishedAt is not null;

    private void SetPrescanFinishedAt(DateTime? value)
    {
        PrescanFinishedAt = value;
        OnPropertyChanged(nameof(PrescanFinishedAt));
        OnPropertyChanged(nameof(MeasuredAtText));
        OnPropertyChanged(nameof(HasMeasuredAt));
    }

    /// <summary>Measures in the background at startup, before any window exists: the user-scope targets (plus admin ones only when the helper already runs), never asking for anything.</summary>
    internal Task PrescanAsync()
    {
        if (IsBusy || _prescanTask is { IsCompleted: false })
            return Task.CompletedTask;

        var task = PrescanCoreAsync();
        _prescanTask = task;
        return task;
    }

    private async Task PrescanCoreAsync()
    {
        try
        {
            await CatalogReadyTask;
            if (IsBusy)
                return;

            await StartScanTrackedAsync(ScanMode.Prescan);
        }
        catch (Exception ex)
        {
            _logging.Warn($"Cleanup prescan failed: {ex.Message}");
        }
    }

    /// <summary>What a freshly opened window starts: the rest of a fresh prescan, else the full pass. A prescan still going is waited for first.</summary>
    public async Task StartOpenScanAsync()
    {
        if (_prescanTask is { IsCompleted: false } running)
            await running;

        if (IsScanning)
            return;

        await (PrescanFinishedAt is not null ? StartScanTrackedAsync(ScanMode.Remaining) : StartScanAsync());
    }

    /// <summary>Measures, quietly, the admin targets a prescan left out once the elevated helper runs. Only for an idle prescanned view model with no window open.</summary>
    private void StartLateMeasuring()
    {
        if (_windowAttached || IsCleaning || _runClaimed || IsScanning || _prescanSkipped.Count == 0
            || PrescanFinishedAt is null || Phase != CleanupPhase.Choosing || !HasElevationAvailable())
        {
            return;
        }

        _ = StartScanTrackedAsync(ScanMode.Late);
    }

    private Task StartScanTrackedAsync(ScanMode mode, IReadOnlyList<CleanupItemViewModel>? subset = null)
    {
        var task = StartScanCoreAsync(mode, subset);
        _scanTask = task;
        return task;
    }

    /// <summary>Measures every target in the background when the window opens, one row at a time so figures appear progressively.</summary>
    public Task StartScanAsync() => StartScanTrackedAsync(ScanMode.Full);

    /// <summary>The scan's body, so <see cref="StartScanAsync"/> can capture the task into <see cref="_scanTask"/> and a later run can wait for it to finish (see <see cref="CleanupViewModel.AwaitScanUnwindAsync"/>).</summary>
    private async Task StartScanCoreAsync(ScanMode mode, IReadOnlyList<CleanupItemViewModel>? subset = null)
    {
        // A rescan the user asked for replaces the prescan's figures; the stamp goes with them.
        if (mode == ScanMode.Full)
        {
            SetPrescanFinishedAt(null);
            _prescanSkipped = [];
        }

        // What this pass measures: everything, only what the prescan left out, or only the chosen rows.
        var scope = mode switch
        {
            ScanMode.Remaining or ScanMode.Late => _prescanSkipped.ToList(),
            ScanMode.Subset => (subset ?? []).ToList(),
            _ => Items.ToList(),
        };
        if (mode is ScanMode.Remaining or ScanMode.Late)
            _prescanSkipped = [];

        if (mode == ScanMode.Subset)
            _prescanSkipped = _prescanSkipped.Except(scope).ToList();

        // The two quiet passes (startup prescan, late catch-up) never ask and never start the helper.
        var quietMode = mode is ScanMode.Prescan or ScanMode.Late;

        // A fresh measurement after a finished run is a new choice: back to choosing, without the old run's capsules and figures.
        if (Phase == CleanupPhase.Finished)
        {
            foreach (var item in Items)
            {
                item.RunState = CleanupTargetState.Idle;
                item.IsWaiting = false;
                item.IsDone = false;
                item.Percent = 0;
                item.FreedBytes = 0;
                item.SkippedBytes = 0;
            }

            Phase = CleanupPhase.Choosing;
        }

        // Cancelled, not disposed: the previous scan still holds tokens from it, and disposing a source under a live token throws.
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        // A scan that measures the bin starts from the age as it stands now; only a later change needs its own rescan.
        if (scope.Any(item => item.IsRecycleBin))
            _recycleBinRescanPending = false;

        // A newer scan replaces _scanCts; this one is then stale and must not write a number or reset state the newer scan owns.
        bool IsCurrentScan() => ReferenceEquals(_scanCts, cts);

        // Every row is being measured again: shown as such, not with the old figure as if current.
        foreach (var item in scope)
            item.IsScanning = true;

        IsScanning = true;
        try
        {
            // StartScanAsync runs fire-and-forget, so an exception escaping here would only reach the unobserved-task handler and leave spinners running. A failure degrades to scanning the unelevated targets only.
            var elevated = new List<CleanupItemViewModel>();

            // Admin rows a quiet pass handed to the helper but never got a figure for must not be stamped as measured.
            var helperMissed = new List<CleanupItemViewModel>();

            // An open scan has no later pass to catch these up, and their previous figures are not a measurement of now: cleared with the admin capsule saying why.
            void MarkUnmeasured(IEnumerable<CleanupItemViewModel> rows)
            {
                if (!IsCurrentScan())
                    return;

                foreach (var row in rows)
                {
                    row.Bytes = 0;
                    row.Files = 0;
                    row.SetEntries(null);
                    row.SetRegistryFindings(null);
                    row.SetLeftoverCandidates(null);
                    row.SetDriverCandidates(null, null);
                    row.SetOldPythonVersionCandidates(null);
                    row.ShowsElevationHint = true;
                    row.IsScanning = false;
                }

                RefreshTotals();
            }

            try
            {
                // Asked before anything is measured: without admin the elevated targets report a floor rather than a total. The prescan asks nothing; finishing it asks only when a left-out target really needs the rights.
                if (mode == ScanMode.Full || (mode == ScanMode.Remaining && scope.Any(i => i.Target.NeedsElevation)))
                    await EnsureElevationAsync(cts.Token);
                else if (mode == ScanMode.Subset && scope.Any(i => i.IsSelected && i.NeedsElevation))
                    await EnsureElevationAsync(cts.Token, scope);

                elevated = ElevatedItems(scope);

                // As soon as admin rights exist, not after the whole walk: the analysis is the longest single measurement and needs nothing from the scan.
                if (!cts.IsCancellationRequested)
                    StartQuietComponentStoreAnalysis();

                if (elevated.Count > 0)
                {
                    var measuredByHelper = new HashSet<CleanupItemViewModel>();
                    var reached = await RunElevatedAsync(
                        "scan",
                        elevated,
                        message =>
                        {
                            if (message.Type != "scan" || !IsCurrentScan() || ItemFor(message.Id) is not { } row)
                                return;

                            measuredByHelper.Add(row);
                            row.Bytes = message.Bytes;
                            row.Files = message.Files;
                            row.SetLeftoverCandidates(message.LeftoverCandidates);
                            row.SetDriverCandidates(message.DriverDeviceCandidates, message.DriverPackageCandidates);
                            row.SetOldPythonVersionCandidates(message.PythonVersionCandidates);
                            row.SetRegistryFindings(message.RegistryFindings?
                                .Select(f => new RegistryFinding(f.Category, f.KeyPath, f.ValueName, f.DisplayName, f.BrokenTarget))
                                .ToList());
                            row.SetEntries(message.Entries?.Select(ToEntry), message.OmittedEntryCount, message.OmittedEntryBytes);
                            ApplyPermanentExclusions(row);
                            row.IsScanning = false;
                            RefreshTotals();
                        },
                        cts.Token,
                        allowStart: !quietMode,
                        backgroundPriority: quietMode && !_windowAttached);

                    var unmeasured = elevated.Where(item => !measuredByHelper.Contains(item)).ToList();
                    if (quietMode && !reached)
                        helperMissed.AddRange(unmeasured);
                    else if (!quietMode)
                        MarkUnmeasured(unmeasured);

                    // A helper that died mid-scan must not leave rows spinning for ever.
                    if (IsCurrentScan())
                    {
                        foreach (var item in elevated)
                            item.IsScanning = false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logging.Warn($"Cleanup elevation scan failed: {ex.Message}");
                if (quietMode)
                    helperMissed.AddRange(elevated);
                else
                    MarkUnmeasured(elevated.Where(item => item.IsScanning));

                if (IsCurrentScan())
                {
                    foreach (var item in elevated)
                        item.IsScanning = false;
                }
            }

            // Bounded parallelism, min(4, core count): a serial walk would take the sum of every target's scan, while unbounded would make every folder and registry scan fight over the same disk and hives. A prescan leaves out targets needing rights it lacks; they are measured on open.
            var skipped = quietMode && !ElevationService.IsElevated
                ? scope.Where(item => item.Target.NeedsElevation && !elevated.Contains(item)).ToList()
                : [];
            skipped.AddRange(helperMissed.Except(skipped));
            if (quietMode && IsCurrentScan())
                _prescanSkipped = skipped;

            var unelevated = scope.Where(item => !elevated.Contains(item) && !skipped.Contains(item)).ToList();
            using var throttle = new SemaphoreSlim(Math.Max(1, Math.Min(4, Environment.ProcessorCount)));

            async Task ScanOneAsync(CleanupItemViewModel item)
            {
                if (cts.IsCancellationRequested)
                    return;

                await throttle.WaitAsync(cts.Token);
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var measured = false;
                    try
                    {
                        // Background thread mode only while no window is open; chosen as each target starts, so a window attaching mid-prescan gets normal priority for the rest.
                        var result = await _service.ScanAsync(
                            item.Target, cts.Token, item.RecycleBinMinAgeDays, quietMode && !_windowAttached);
                        measured = true;
                        if (!IsCurrentScan())
                            return;

                        // The same mapping the elevated helper sends, so the driver and Python rows get their candidates when the app itself is elevated or the helper was declined.
                        var message = CleanupWorker.ScanMessage(item.Target.Id, result);
                        item.Bytes = result.Bytes;
                        item.Files = result.Files;
                        item.SetLeftoverCandidates(message.LeftoverCandidates);
                        item.SetDriverCandidates(message.DriverDeviceCandidates, message.DriverPackageCandidates);
                        item.SetOldPythonVersionCandidates(message.PythonVersionCandidates);
                        item.SetRegistryFindings(result.RegistryFindings);
                        item.SetEntries(
                            result.Entries, result.OmittedEntryCount, result.OmittedEntryBytes, result.RecycleBinDrives);
                        ApplyPermanentExclusions(item);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        // A target that cannot be measured is shown as empty and must not break the dialog; its previous figures are cleared, since the next clean would act on them.
                        _logging.Warn($"Cleanup scan failed for '{item.Target.Id}': {ex.Message}");
                        if (IsCurrentScan())
                        {
                            // A measurement that came back keeps its figures: only the lists failed to apply.
                            if (!measured)
                            {
                                item.Bytes = 0;
                                item.Files = 0;
                            }

                            // The failure may have come from one of these setters; thrown again it would escape the scan and leave every other row spinning.
                            try
                            {
                                item.SetEntries(null);
                                item.SetRegistryFindings(null);
                                item.SetLeftoverCandidates(null);
                                item.SetDriverCandidates(null, null);
                                item.SetOldPythonVersionCandidates(null);
                            }
                            catch (Exception resetError)
                            {
                                _logging.Warn($"Cleanup scan could not reset '{item.Target.Id}': {resetError.Message}");
                            }
                        }
                    }
                    finally
                    {
                        if (IsCurrentScan())
                        {
                            item.IsScanning = false;
                            RefreshTotals();
                        }

                        _logging.Info($"cleanup scan {item.Target.Id} {stopwatch.ElapsedMilliseconds}");
                    }
                }
                finally
                {
                    throttle.Release();
                }
            }

            try
            {
                await Task.WhenAll(unelevated.Select(ScanOneAsync));
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            // A superseded scan leaves the flag and the rows to the scan that replaced it.
            if (IsCurrentScan())
            {
                IsScanning = false;

                // A pass that returned early (cancelled, or the helper dropping out) would leave unreached targets showing the measuring "..." forever.
                foreach (var item in Items)
                    item.IsScanning = false;

                RefreshTotals();

                if (quietMode && !cts.IsCancellationRequested)
                    SetPrescanFinishedAt(Clock());

                // The helper may have started during the prescan: the admin targets it left out are measured now.
                if (mode == ScanMode.Prescan && !cts.IsCancellationRequested)
                    StartLateMeasuring();

                // An age change the scan swallowed.
                if (!cts.IsCancellationRequested)
                    RunPendingRecycleBinRescan();
            }
        }
    }
}
