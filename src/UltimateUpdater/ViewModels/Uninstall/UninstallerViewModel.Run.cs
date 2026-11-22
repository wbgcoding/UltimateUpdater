using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>The run: confirm, remove every ticked entry through the runner, show each row's state, stop on request.</summary>
public sealed partial class UninstallerViewModel
{
    private UninstallRunner? _runner;
    private CancellationTokenSource? _runCts;
    private SynchronizationContext? _uiContext;

    /// <summary>Asks the user before a run: headline, body, the sentence to tick (null: none). Replaced by tests; the default is the themed confirm dialog.</summary>
    public Func<string, string, string?, bool> Confirm { get; set; } = (headline, body, acknowledge) =>
        Dialogs.Confirm(
            headline, body, LocalizationService.Instance["UninstallButton"], destructive: true, acknowledgeText: acknowledge);

    /// <summary>Asks whether to remove anyway when no restore point could be created. Replaced by tests; the default is the themed confirm dialog.</summary>
    public Func<bool> ConfirmWithoutRestorePoint { get; set; } = () =>
        Dialogs.Confirm(
            LocalizationService.Instance["UninstallerRestorePointFailed"], string.Empty,
            LocalizationService.Instance["UninstallerRestorePointFailedButton"], destructive: true);

    /// <summary>What the restore point step says: "Creating a restore point..." while it runs, "Using the restore point from ..." when one within 24 hours covers the run, else empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestorePointText))]
    private string restorePointText = string.Empty;

    public bool HasRestorePointText => RestorePointText.Length > 0;

    [ObservableProperty]
    private bool isRunning;

    partial void OnIsRunningChanged(bool value) => OnBusyChanged();

    partial void OnIsScanningLeftoversChanged(bool value) => OnBusyChanged();

    partial void OnIsRemovingLeftoversChanged(bool value) => OnBusyChanged();

    /// <summary>The removals or the leftover work started or ended: every command that waits for both is asked again.</summary>
    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanRemoveLeftovers));
        UninstallSelectedCommand.NotifyCanExecuteChanged();
        RemoveLeftoversCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True from the moment Stop was pressed until the run ends: the running removals finish, no further one starts.</summary>
    [ObservableProperty]
    private bool isStopping;

    /// <summary>The task of the current run, so a test (or a closing app) can await it.</summary>
    public Task? RunTask { get; private set; }

    /// <summary>The text of the confirmation: the item count, and with a Risky row among them each one with its reason.</summary>
    internal (string Headline, string Body, string? Acknowledge) ConfirmationTexts(IReadOnlyList<UninstallRowViewModel> rows)
    {
        var headline = string.Format(Loc["UninstallerConfirm"], rows.Count);
        var risky = rows.Where(row => row.IsRisky).ToList();
        if (risky.Count == 0)
            return (headline, string.Empty, null);

        var body = string.Join(
            Environment.NewLine, risky.Select(row => $"{row.Name}: {row.ReasonText}"));
        return (headline, body, Loc["UninstallerAcknowledge"]);
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private Task UninstallSelected()
    {
        if (IsBusy)
            return Task.CompletedTask;

        var rows = SelectedRows;
        if (rows.Count == 0)
            return Task.CompletedTask;

        var (headline, body, acknowledge) = ConfirmationTexts(rows);
        if (!Confirm(headline, body, acknowledge))
            return Task.CompletedTask;

        return RunTask = RunAsync(rows);
    }

    private async Task RunAsync(IReadOnlyList<UninstallRowViewModel> rows)
    {
        _uiContext = SynchronizationContext.Current;
        IsRunning = true;
        IsStopping = false;
        ClearLeftovers();
        foreach (var row in _all)
        {
            row.Reason = null;
            row.State = UninstallRowState.Idle;
        }

        foreach (var row in rows)
            row.State = UninstallRowState.Waiting;

        var byEntry = rows.ToDictionary(row => row.Entry);
        var runner = _runner = _backend.CreateRunner(entry => byEntry[entry].Listing.Plan);
        using var cts = _runCts = new CancellationTokenSource();
        if (!await PrepareRestorePointAsync(rows, cts.Token))
        {
            // Nothing was removed: the rows go back to what they were and no summary appears.
            foreach (var row in rows)
                row.State = UninstallRowState.Idle;

            _runner = null;
            _runCts = null;
            IsStopping = false;
            IsRunning = false;
            return;
        }

        try
        {
            var results = await Task.Run(
                () => runner.RunAsync(rows.Select(row => row.Entry).ToList(), update => Post(() => ApplyUpdate(byEntry, update)), cts.Token));

            Post(() =>
            {
                foreach (var result in results)
                {
                    ApplyResult(byEntry[result.Entry], result);
                    if (result.Outcome is UninstallOutcome.Removed or UninstallOutcome.NeedsRestart)
                        _backend.RecordRemoved(result.Entry);
                }
            });
            await FlushAsync();
        }
        catch (Exception ex)
        {
            _logging.Warn($"Uninstall run failed: {ex.Message}");
            foreach (var row in rows.Where(row => row.State is UninstallRowState.Waiting or UninstallRowState.Running))
            {
                row.Reason = ex.Message;
                row.State = UninstallRowState.Failed;
            }
        }
        finally
        {
            SetTicked(_all.Where(row => row.IsSelected), false);
            _runner = null;
            _runCts = null;
            IsStopping = false;
            IsRunning = false;
            RunFinished = true;
        }

        await ScanLeftoversAsync([.. rows.Where(row => row.State == UninstallRowState.Removed)]);
    }

    /// <summary>
    /// The restore point before the removals, when the setting and the ticked rows call for one. False when no restore
    /// point could be made and the person chose not to go on. A reused one (within 24 hours) is only said.
    /// </summary>
    private async Task<bool> PrepareRestorePointAsync(IReadOnlyList<UninstallRowViewModel> rows, CancellationToken cancellationToken)
    {
        RestorePointText = string.Empty;
        var wanted = UninstallRestorePointPolicy.Wanted(
            _backend.BackupMode,
            anyElevated: rows.Any(row => row.Listing.Plan is { IsRunnable: true, Elevated: true }),
            anyRisky: rows.Any(row => row.IsRisky));
        if (!wanted)
            return true;

        RestorePointText = Loc["InstallRestorePointStep"];
        UninstallRestorePointResult result;
        try
        {
            result = await _backend.CreateRestorePointAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RestorePointText = string.Empty;
            return false;
        }
        catch (Exception ex)
        {
            _logging.Warn($"Restore point before uninstalling failed: {ex.Message}");
            result = new UninstallRestorePointResult(UninstallRestorePointOutcome.Failed);
        }

        RestorePointText = result is { Outcome: UninstallRestorePointOutcome.Reused, At: { } at }
            ? string.Format(Loc["UninstallerRestorePointReused"], at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture))
            : string.Empty;

        return result.Outcome != UninstallRestorePointOutcome.Failed || ConfirmWithoutRestorePoint();
    }

    private void Post(Action action)
    {
        if (_uiContext is null)
            action();
        else
            _uiContext.Post(_ => action(), null);
    }

    /// <summary>Lets every update posted before this call run first.</summary>
    private Task FlushAsync()
    {
        if (_uiContext is null)
            return Task.CompletedTask;

        var done = new TaskCompletionSource();
        _uiContext.Post(_ => done.TrySetResult(), null);
        return done.Task;
    }

    private static void ApplyUpdate(Dictionary<InstalledProgramEntry, UninstallRowViewModel> byEntry, UninstallEntryUpdate update)
    {
        if (!byEntry.TryGetValue(update.Entry, out var row))
            return;

        if (update.State == UninstallEntryState.Done && update.Result is { } result)
            ApplyResult(row, result);
        else
            row.State = update.State == UninstallEntryState.Running ? UninstallRowState.Running : UninstallRowState.Waiting;
    }

    private static void ApplyResult(UninstallRowViewModel row, UninstallEntryResult result)
    {
        row.Reason = result.Outcome == UninstallOutcome.Failed ? result.Reason : null;
        row.State = result.Outcome switch
        {
            UninstallOutcome.Removed => UninstallRowState.Removed,
            UninstallOutcome.NeedsRestart => UninstallRowState.NeedsRestart,
            UninstallOutcome.NotStarted => UninstallRowState.NotStarted,
            _ => UninstallRowState.Failed,
        };
    }

    /// <summary>Stops the run: the removals already going finish, no further one starts.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (_runner is null || IsStopping)
            return;

        IsStopping = true;
        _runner.Stop();
    }
}
