using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// Measuring what could be freed: the scan run itself, its rescan command, and the elevated helper
// the scan needs to reach targets an ordinary process cannot read.
public partial class CleanupViewModel
{
    [ObservableProperty]
    private bool isScanning;

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        RescanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Asks for administrator rights BEFORE measuring. Without them sfc, the DISM
    /// sequence and Disk Cleanup's unattended profile do nothing at all, and Windows' own temp folders can
    /// be neither measured nor emptied in full, so the dialog would quietly under-report and under-deliver.
    /// Declining is a normal answer, not an error: the run continues unelevated and the affected rows are
    /// marked as skipped rather than the whole run failing. Returns true when the user said no (or the
    /// helper could not be started), so the caller asks for nothing else that needs those rights.
    /// </summary>
    private async Task<bool> EnsureElevationAsync(CancellationToken cancellationToken)
    {
        var needsAdmin = Items.Any(i => i.IsSelected && i.NeedsElevation);
        if (!needsAdmin || ElevationService.IsElevated || _elevated is null)
        {
            // Already an administrator (or nothing needs one): every target runs in-process as before.
            SetElevationHints(show: false);
            return false;
        }

        // If the helper is already running from an earlier install there is nothing to ask about. Otherwise
        // this asks, even if the user said no before: opening this dialog is a deliberate act, and an
        // accidental "no" would otherwise leave the feature half-working for the rest of the session with
        // no way back. Nothing asks on its own; it takes a click to get here.
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
    }

    /// <summary>Which targets this run hands to the elevated helper, none when there is no helper. Reads
    /// the helper's state as it is now: the scan's answer can be stale by the time a run starts.</summary>
    private List<CleanupItemViewModel> ElevatedItems(IEnumerable<CleanupItemViewModel> items) =>
        !ElevationService.IsElevated && _elevated is { IsRunning: true }
            ? items.Where(i => i.Target.NeedsElevation).ToList()
            : [];

    private CleanupItemViewModel? ItemFor(string? id) =>
        id is null ? null : Items.FirstOrDefault(i => i.Target.Id == id);

    private static CleanupEntry ToEntry(CleanupEntryDto dto) => new(
        dto.Path, dto.DisplayName, dto.Bytes, dto.Files, dto.OldestWriteTime, dto.NewestWriteTime, dto.Type,
        dto.OriginalPath);

    /// <summary>
    /// Measures everything again on demand. The figures are a snapshot of a machine that keeps working,
    /// a browser or a build fills a temp folder while the dialog is open, so the user needs a way to ask
    /// for a fresh one without closing and reopening.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => StartScanAsync();

    private bool CanRescan() => Phase is CleanupPhase.Choosing or CleanupPhase.Finished && !IsScanning && !_runClaimed;

    /// <summary>How far one measuring pass reaches. Full is the pass a window runs (and a rescan): every
    /// target, asking for administrator rights first. Prescan is the quiet one at startup: user-scope
    /// targets only, no question, no elevation. Remaining finishes a prescan on open: just the targets it
    /// left out.</summary>
    private enum ScanMode
    {
        Full,
        Prescan,
        Remaining,
    }

    /// <summary>Targets a prescan left out because they need rights the app does not have yet.</summary>
    private List<CleanupItemViewModel> _prescanSkipped = [];

    private Task? _prescanTask;

    /// <summary>Where "now" comes from for the measurement stamp; a test replaces it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>When the startup prescan finished measuring, or null when none did or the user has
    /// rescanned since. Decides whether a reopened dialog may reuse this result.</summary>
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

    /// <summary>
    /// Measures in the background at startup, before any window exists. Waits for the catalogue, then walks
    /// the user-scope targets (plus the admin ones only when the elevated helper is already running) and
    /// never asks for anything. Already measuring or busy: nothing to do.
    /// </summary>
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

    /// <summary>What a freshly opened window starts: the rest of a prescan when one is fresh here, else
    /// the full measuring pass. A prescan still going is waited for first, so the two never overlap.</summary>
    public async Task StartOpenScanAsync()
    {
        if (_prescanTask is { IsCompleted: false } running)
            await running;

        if (IsScanning)
            return;

        await (PrescanFinishedAt is not null ? StartScanTrackedAsync(ScanMode.Remaining) : StartScanAsync());
    }

    private Task StartScanTrackedAsync(ScanMode mode)
    {
        var task = StartScanCoreAsync(mode);
        _scanTask = task;
        return task;
    }

    /// <summary>
    /// Measures every target in the background as soon as the window opens, one row at a time so figures
    /// appear progressively rather than all at the end.
    /// </summary>
    public Task StartScanAsync() => StartScanTrackedAsync(ScanMode.Full);

    /// <summary>The scan's own body, pulled out so <see cref="StartScanAsync"/> can capture the running
    /// task into <see cref="_scanTask"/> before returning it, letting a later run wait for THIS scan to
    /// actually finish rather than only cancelling it (see <see cref="CleanupViewModel.AwaitScanUnwindAsync"/>).</summary>
    private async Task StartScanCoreAsync(ScanMode mode)
    {
        // A rescan the user asked for replaces the prescan's figures for good: the stamp goes with them.
        if (mode == ScanMode.Full)
        {
            SetPrescanFinishedAt(null);
            _prescanSkipped = [];
        }

        // What this pass measures: everything, or only what the prescan left out.
        var scope = mode == ScanMode.Remaining ? _prescanSkipped.ToList() : Items.ToList();
        if (mode == ScanMode.Remaining)
            _prescanSkipped = [];

        // A fresh measurement after a finished run is a new choice: back to choosing, without the old
        // run's capsules and figures on the rows.
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

        // Cancelled, not disposed: the previous scan is still unwinding and still holds tokens from it,
        // and disposing a source out from under a live token throws where the token is used.
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        // A newer scan replaces _scanCts; from then on this one is stale and must not write a number or reset
        // any state the newer scan owns (its cancelled loop can still be mid-await and deliver a late result).
        bool IsCurrentScan() => ReferenceEquals(_scanCts, cts);

        // Every row is being measured again: shown as such (not with the old figure as if it were current),
        // and its "nothing found" states are re-raised when its own result arrives.
        foreach (var item in scope)
            item.IsScanning = true;

        IsScanning = true;
        try
        {
            // StartScanAsync is always started fire-and-forget (the window's Loaded handler,
            // RescanCommand), so an exception escaping EnsureElevationAsync or RunElevatedAsync would only
            // reach the process-wide unobserved-task handler, leaving the dialog's "…" spinners running and
            // the rescan button disabled. A failure here degrades to "scan the unelevated targets only",
            // the same fail-closed shape EnsureElevationAsync documents for a declined prompt.
            var elevated = new List<CleanupItemViewModel>();
            try
            {
                // Asked before anything is measured: without admin the elevated targets report a floor
                // rather than a total, and three of them do nothing whatsoever.
                // The prescan asks nothing: it takes what the app can read as it is. Finishing a prescan
                // asks only when something it left out really needs the rights.
                if (mode == ScanMode.Full || (mode == ScanMode.Remaining && scope.Any(i => i.Target.NeedsElevation)))
                    await EnsureElevationAsync(cts.Token);

                elevated = ElevatedItems(scope);
                if (elevated.Count > 0)
                {
                    await RunElevatedAsync(
                        "scan",
                        elevated,
                        message =>
                        {
                            if (message.Type != "scan" || !IsCurrentScan() || ItemFor(message.Id) is not { } row)
                                return;

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
                        allowStart: mode != ScanMode.Prescan);

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
                if (IsCurrentScan())
                {
                    foreach (var item in elevated)
                        item.IsScanning = false;
                }
            }

            // These targets run with bounded parallelism instead of one after another, since a serial
            // walk would make the dialog's total scan time the SUM of every target's own scan (mostly
            // independent directory/registry walks, each waiting on disk or the registry rather than the
            // CPU). Bounded to min(4, core count): unbounded would have every temp/cache folder and the
            // registry scan fight over the same disk and the same few registry hives at once.
            // A prescan leaves out the targets that need rights it does not have; they are measured on open.
            var skipped = mode == ScanMode.Prescan && !ElevationService.IsElevated
                ? scope.Where(item => item.Target.NeedsElevation && !elevated.Contains(item)).ToList()
                : [];
            if (mode == ScanMode.Prescan && IsCurrentScan())
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
                        var result = await _service.ScanAsync(item.Target, cts.Token, item.RecycleBinMinAgeDays);
                        measured = true;
                        if (!IsCurrentScan())
                            return;

                        // The same mapping the elevated helper sends, so the driver and Python rows get their
                        // candidates when the app itself is elevated or the helper was declined.
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
                        // The whole scan was cancelled; nothing more to do for this one target either.
                    }
                    catch (Exception ex)
                    {
                        // A target that cannot be measured is shown as empty; it must not break the dialog. Its
                        // previous figures and lists are cleared too: they are what the next clean would act on.
                        _logging.Warn($"Cleanup scan failed for '{item.Target.Id}': {ex.Message}");
                        if (IsCurrentScan())
                        {
                            // A measurement that came back keeps its figures: only the lists failed to apply.
                            if (!measured)
                            {
                                item.Bytes = 0;
                                item.Files = 0;
                            }

                            // The failure may have come from one of these very setters (the row's own list
                            // refusing the update): thrown again from here it would escape the scan and leave
                            // every other row spinning.
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
                // Cancelled while queued behind the throttle (never having started its own scan yet); the
                // targets that did start already handled their own cancellation above.
            }
        }
        finally
        {
            // A superseded scan leaves the flag and the rows to the scan that replaced it.
            if (IsCurrentScan())
            {
                IsScanning = false;

                // A pass that returned early (cancelled, or the helper dropping out) leaves every target it
                // never reached showing the measuring "…" for ever.
                foreach (var item in Items)
                    item.IsScanning = false;

                RefreshTotals();

                if (mode == ScanMode.Prescan && !cts.IsCancellationRequested)
                    SetPrescanFinishedAt(Clock());

                // Measures the component store on its own once administrator rights are already there, so
                // the card needs no click. Never raises a prompt: the condition is exactly the one under
                // which EnsureElevationForComponentStoreAsync asks nothing. A result already present is not
                // measured again (the button still re-runs it).
                if (mode != ScanMode.Prescan
                    && !cts.IsCancellationRequested
                    && ComponentStoreResult is null
                    && !IsAnalyzingComponentStore
                    && (ElevationService.IsElevated || _elevated?.IsRunning == true))
                {
                    AnalyzeComponentStoreCommand.Execute(null);
                }
            }
        }
    }
}
