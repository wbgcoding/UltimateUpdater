using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels;

// Installing: building and queueing a batch, the install loop and its handoff to the elevated worker,
// and the batch progress readouts.
public partial class MainViewModel
{
    [ObservableProperty]
    private bool isInstalling;

    /// <summary>True once the batch's own automatic second attempt has started, so the queue panel can show it as a separate section. Reset on the next fresh batch.</summary>
    [ObservableProperty]
    private bool isSecondAttemptActive;

    partial void OnIsInstallingChanged(bool value)
    {
        RefreshSelectionState();
        InstallSelfUpdateCommand.NotifyCanExecuteChanged();

        // Deferred: whatever clears this flag finishes its own step first (clearing _installActivityRunning, handing queued rows to the next batch), and only then is the work really over.
        _dispatcher.InvokeAsync(ApplyWaitingSelfUpdateWhenIdle);

        // The batch loop calls BeginInstallProgress right alongside setting this flag, but nothing enforces that: whatever else flips it must find the queue and running strips already matching InstallBatch.
        SyncQueueRows();
        SyncRunningRows();
    }

    [RelayCommand]
    private async Task InstallSelected()
    {
        // Only what is on screen: a hidden row is one the user never agreed to.
        await EnqueueInstallAsync(VisibleRows.Where(u => u.IsSelected).ToList());
    }

    /// <summary>Rows waiting to be installed. Only ever touched on the UI thread (see RunInstallLoopAsync).</summary>
    private readonly List<UpdateRowViewModel> _installPending = [];

    /// <summary>True while any activity owns the shared install machinery (<see cref="InstallBatch"/>, <see cref="_installPending"/>, <see cref="_installCts"/>, the progress timer): the batch loop, an interactive retry or an uninstall. <see cref="IsInstalling"/> alone is not enough: an Install click on another row during a retry would start a second loop that resets the shared state. Each of the three entry points sets this before touching anything shared and clears it in its own <c>finally</c>. A click that lands meanwhile queues in <see cref="_installPending"/> for <see cref="HandOffPendingInstalls"/>.</summary>
    private bool _installActivityRunning;

    /// <summary>Uninstalls started while another activity owns the shared machinery. Each is its own concurrent <see cref="InstallQueue.RunAsync"/> run and takes no ownership of the token, the progress list or the flags.</summary>
    private int _sideUninstalls;

    /// <summary>The cancellation sources of the running side uninstalls, so Stop reaches them too.</summary>
    private readonly List<CancellationTokenSource> _sideUninstallCts = [];

    /// <summary>True once the owning activity has finished while a side uninstall still runs: the shared state stays up until both have ended.</summary>
    private bool _ownerEnded;

    /// <summary>The owner activity's closing step. With no side uninstall running it releases the shared state and returns true, so the caller hands queued rows to the next batch. Otherwise the last side uninstall releases it (see <see cref="EndSideUninstall"/>).</summary>
    private bool EndInstallActivity()
    {
        if (_sideUninstalls > 0)
        {
            _ownerEnded = true;
            RefreshInstallProgress();
            return false;
        }

        ReleaseInstallActivity();
        return true;
    }

    private void EndSideUninstall(CancellationTokenSource cts)
    {
        _sideUninstallCts.Remove(cts);
        cts.Dispose();
        _sideUninstalls--;

        if (_sideUninstalls == 0 && _ownerEnded)
        {
            ReleaseInstallActivity();
            HandOffPendingInstalls();
        }
        else
        {
            RefreshInstallProgress();
        }
    }

    private void ReleaseInstallActivity()
    {
        _ownerEnded = false;
        IsInstalling = false;
        _installActivityRunning = false;
        _installTimer?.Stop();
        RefreshInstallProgress();
    }

    /// <summary>Turns the picked rows into the rows that really have to be installed. A row the table folded several sources into stands for one real installation per source; all of them are updated, best-ranked first, or the next scan would list the other copy again at the same version.</summary>
    private static IReadOnlyList<UpdateRowViewModel> Expand(IEnumerable<UpdateRowViewModel> rows)
    {
        var expanded = new List<UpdateRowViewModel>();

        foreach (var row in rows)
        {
            if (!expanded.Contains(row))
                expanded.Add(row);

            foreach (var partner in row.MergedPartners.Where(partner => !expanded.Contains(partner)))
                expanded.Add(partner);
        }

        return expanded;
    }

    /// <summary>True only when both versions are known and equal. No version info at all (a launcher row) never counts as current.</summary>
    private static bool IsAlreadyCurrent(UpdateItem item) =>
        item.InstalledVersion is not null
        && string.Equals(item.InstalledVersion, item.AvailableVersion, StringComparison.Ordinal);

    /// <summary>Adds rows to the install queue. Nothing running: starts the queue. A batch running: the rows start at once beside it, waiting only for a busy lane. Rows already done or in flight are skipped.</summary>
    private async Task EnqueueInstallAsync(IReadOnlyList<UpdateRowViewModel> rows)
    {
        // Scanning and installing do not collide: a scan reconciles the table rather than clearing it, DrainPendingItems reuses only Idle rows, and RemoveStaleRows leaves queued and running rows alone.
        if (RefusedWhileSelfUpdateApplies("Install", tellUser: true))
            return;

        var candidates = Expand(rows)
            .Where(row => row.CanInstall
                          && row.InstallState is RowInstallState.Idle or RowInstallState.Failed or RowInstallState.Cancelled
                          && !_installPending.Contains(row)
                          && !_reinstallingKeys.Contains(PackageKey(row.Item)))
            .ToList();
        if (candidates.Count == 0)
            return;

        // The pre-flight dialog pumps messages, so a re-entrant click could queue the same row twice. Every row is re-checked against the conditions the candidates were built from, since its state may also have moved on meanwhile.
        var skippedBlocked = new List<UpdateRowViewModel>();

        // The pre-flight window counts as work for the self update: otherwise a cleanup ending while it is up would let the update start applying under the open dialog.
        List<UpdateRowViewModel> resolvedCandidates;
        _installPreflightsOpen++;
        try
        {
            resolvedCandidates = await ResolveInstallCandidatesAsync(candidates, skippedBlocked);
        }
        finally
        {
            _installPreflightsOpen--;
        }

        // The update may have started applying while the pre-flight window was up.
        if (RefusedWhileSelfUpdateApplies("Install", tellUser: true))
            return;

        var resolved = ResolveLiveRows(resolvedCandidates)
            .Where(row => row.CanInstall
                          && row.InstallState is RowInstallState.Idle or RowInstallState.Failed or RowInstallState.Cancelled
                          && !_installPending.Contains(row)
                          && !_reinstallingKeys.Contains(PackageKey(row.Item)))
            .ToList();

        // When the row's own data says installed and available versions match, running the installer again is a no-op nobody asked for: refused with one status line, like the auto-skipped-blocker message below.
        var toAdd = new List<UpdateRowViewModel>(resolved.Count);
        foreach (var row in resolved)
        {
            if (IsAlreadyCurrent(row.Item))
            {
                row.MarkNotInstalled(string.Format(Loc["InstallAlreadyCurrent"], row.Item.InstalledVersion));
                continue;
            }

            toAdd.Add(row);
        }

        if (toAdd.Count == 0 && skippedBlocked.Count == 0)
        {
            // Nothing was queued; work that ended during the window may have been holding a waiting update back.
            ApplyWaitingSelfUpdateWhenIdle();
            return;
        }

        // Only this batch's rows are sorted, critical first. The sort is stable, so rows tied on IsCritical keep their order; later rows append behind _installPending and a running batch is only extended.
        toAdd = toAdd.OrderByDescending(row => row.IsCritical).ToList();

        // A fresh run starts a clean progress list; joining rows extend the one showing.
        if (!_installActivityRunning)
        {
            InstallBatch.Clear();
            IsSecondAttemptActive = false;
        }

        // Rows skipped for a running program stay visible in this run's strip; added only now because the clear above belongs to the run they were skipped for.
        foreach (var row in skippedBlocked.Where(row => !_mergedAway.Contains(row) && !InstallBatch.Contains(row)))
            InstallBatch.Add(row);

        if (toAdd.Count == 0)
        {
            ApplyWaitingSelfUpdateWhenIdle();
            return;
        }

        // One selection refresh for the whole batch: BeginInstall and the untick each re-walk every visible row, which is quadratic for a few hundred rows.
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var row in toAdd)
            {
                row.BeginInstall();
                row.IsSelected = false;
                _installPending.Add(row);

                // A package folded into one row stays one row here: both sources are installed, but the hidden one gets no line in the strip.
                if (!_mergedAway.Contains(row))
                    InstallBatch.Add(row);
            }
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }

        RefreshSelectionState();
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(HeadlineCriticalText));

        _ = Background.Run(RunInstallLoopAsync(), _logging, "install loop");
    }

    /// <summary>A scan that finished while the pre-flight window was up may have replaced or removed a candidate's row. A replaced row offering the same version is swapped for the live one, any other stale row is dropped, or the entry would be orphaned while the live row stays installable. An unknown row counts as live only while it is still in the table, as a row or sub-row.</summary>
    private List<UpdateRowViewModel> ResolveLiveRows(IReadOnlyList<UpdateRowViewModel> rows)
    {
        var live = new List<UpdateRowViewModel>(rows.Count);
        foreach (var row in rows)
        {
            UpdateRowViewModel? target;
            if (!_rowsByKey.TryGetValue(PackageKey(row.Item), out var current))
                target = Updates.Contains(row) || Updates.Any(parent => parent.Children.Contains(row)) ? row : null;
            else if (ReferenceEquals(current, row))
                target = row;
            else if (string.Equals(current.AvailableVersion, row.AvailableVersion, StringComparison.Ordinal))
                target = current;
            else
                target = null;

            if (target is not null && !live.Contains(target))
                live.Add(target);
        }

        return live;
    }

    /// <summary>Test seam: replaces the pre-flight window so a test can simulate Start or Cancel and set each blocker line's <c>Choice</c> without a WPF window. Null in production, where <see cref="ShowInstallPreflight"/> opens <see cref="Views.InstallPreflightWindow"/>.</summary>
    internal Func<InstallPreflightViewModel, bool>? InstallPreflightOverride { get; set; }

    /// <summary>Test seam for "close and continue": replaces the close and force-kill step. Null in production.</summary>
    internal Func<IReadOnlyList<string>, Task<bool>>? CloseBlockingProcessesOverride { get; set; }

    /// <summary>Test seam for the metered-connection check; the real probe reads the host's active connection.</summary>
    internal Func<bool>? IsMeteredOverride { get; set; }

    /// <summary>Test seam for the battery check; the real one reads <c>SystemInformation.PowerStatus</c>.</summary>
    internal Func<(System.Windows.Forms.PowerLineStatus Line, float BatteryLifePercent)>? PowerStatusOverride { get; set; }

    /// <summary>Checks what could spoil the batch before any row is queued, app-side and unelevated: a program the update would overwrite may be running (winget's Visual Studio exit code 8006), the disk may be too small, the connection metered or the battery low. Unattended runs (<see cref="App.IsUnattended"/>) skip these checks: a blocked row is skipped with its reason recorded. Interactive with nothing to report: the candidates come back unchanged, no window. Otherwise one pre-flight window lists every warning and blocked group; Start returns the accepted rows, Cancel returns none.</summary>
    private async Task<List<UpdateRowViewModel>> ResolveInstallCandidatesAsync(
        IReadOnlyList<UpdateRowViewModel> candidates, List<UpdateRowViewModel> skipped)
    {
        var accepted = new List<UpdateRowViewModel>(candidates.Count);

        // First pass: detect only. Two rows waiting on the same running program (two Visual Studio components held up by devenv) are one decision, asked once per group below. One process snapshot per batch, off the UI thread: a full enumeration per row would repeat the work and block the window.
        var detections = await Task.Run(() =>
        {
            var runningProcessNames = _installBlockers.CaptureBatchSnapshot();
            return candidates
                .Select(row => (Row: row, Blocker: _installBlockers.Detect(row.Item, runningProcessNames)))
                .ToList();
        });

        var blocked = new List<(UpdateRowViewModel Row, Services.Install.BlockedInstall Blocker)>();

        foreach (var (row, blocker) in detections)
        {
            if (blocker is null)
            {
                accepted.Add(row);
                continue;
            }

            // Never asked about and never closed: it is the next run's job, not a failure.
            if (blocker.SkipOnly)
            {
                SkipRowInUse(row, blocker);
                skipped.Add(row);
                continue;
            }

            if (App.IsUnattended)
            {
                SkipBlockedRowUnattended(row, blocker.DisplayProgramName);
                skipped.Add(row);
                continue;
            }

            blocked.Add((row, blocker));
        }

        if (App.IsUnattended)
            return accepted;

        var blockerLines = InstallPreflightLineViewModel.FromBlockedGroups(blocked);

        // Warnings come before the blocked-group questions.
        var lines = new List<InstallPreflightLineViewModel>();
        if (BuildDiskSpaceLine(candidates) is { } diskSpaceLine)
            lines.Add(diskSpaceLine);
        if (BuildMeteredLine(candidates) is { } meteredLine)
            lines.Add(meteredLine);
        if (BuildBatteryLine() is { } batteryLine)
            lines.Add(batteryLine);
        lines.AddRange(blockerLines);

        if (lines.Count == 0)
            return accepted;

        // The restore point is made only for an elevated batch with the setting on (InstallQueue), so the dialog mentions it only then.
        var restorePointRelevant = _settings.Current.Install.CreateRestorePoint
            && candidates.Any(row => InstallQueue.NeedsElevation(row.SourceId));
        var preflight = new InstallPreflightViewModel(lines, _logging, restorePointRelevant);
        if (!ShowInstallPreflight(preflight))
            return []; // Cancel: nothing queued, not even the rows nothing was wrong with.

        // One answer for every blocked group: the window asks once, not once per line.
        foreach (var line in blockerLines)
        {
            switch (preflight.BlockerChoice)
            {
                case ThreeWayChoice.Primary: // "Programm schließen und fortfahren"
                    // Rows are queued only once the blocking program is really gone; a declined force-kill or a survivor leaves them out, like "skip blocked".
                    if (await (CloseBlockingProcessesOverride
                                 ?? (names => CloseBlockingProcessesAsync(names, onlyInsideFolder: line.BlockedOnlyInsideFolder)))(line.BlockedProcessNames!))
                        accepted.AddRange(line.BlockedRows!);
                    else
                    {
                        SkipBlockedRowsStillOpen(line);
                        skipped.AddRange(line.BlockedRows!);
                    }

                    break;
                case ThreeWayChoice.Tertiary: // "Trotzdem versuchen"
                    accepted.AddRange(line.BlockedRows!);
                    break;
                case ThreeWayChoice.Secondary: // "Blockierte überspringen"
                case null: // never answered; the cautious reading is the same as Skip
                default:
                    SkipBlockedRowsStillOpen(line);
                    skipped.AddRange(line.BlockedRows!);
                    break;
            }
        }

        return accepted;
    }

    /// <summary>Whether the machine has room for the whole candidate list, blocked rows included, before any of it is queued. An installer needs the download plus the unpacked copy, so <see cref="Services.Install.DiskSpaceCheck"/> doubles the known sizes and compares them with the smaller of the system drive's and the temp drive's free space. Null when there is nothing to warn about, including when no size is known: <see cref="Services.Install.DiskSpaceVerdict.IsShort"/> is never true on a guess.</summary>
    private InstallPreflightLineViewModel? BuildDiskSpaceLine(IReadOnlyList<UpdateRowViewModel> candidates)
    {
        // The row's own figure, enrichment included: the source's scan often reports no size.
        var sizes = candidates.Select(row => row.DownloadSizeBytes).ToList();
        var freeSystem = SafeFreeBytes(Path.GetPathRoot(Environment.SystemDirectory));
        var freeTemp = SafeFreeBytes(Path.GetPathRoot(Path.GetTempPath()));
        var verdict = Services.Install.DiskSpaceCheck.Evaluate(sizes, freeSystem, freeTemp);

        if (!verdict.IsShort)
            return null;

        // Unknown sizes are named separately and never folded into the required-bytes figure.
        var text = string.Format(Loc["InstallSpaceShort"], FormatBytes(verdict.FreeBytes), FormatBytes(verdict.RequiredBytes));
        if (verdict.UnknownCount > 0)
            text += " " + string.Format(Loc[verdict.UnknownCount == 1 ? "InstallSpaceUnknownOne" : "InstallSpaceUnknown"], verdict.UnknownCount);

        return new InstallPreflightLineViewModel(
            text, isCritical: true, actionText: Loc["InstallSpaceOpenCleanup"], action: ShowCleanup);
    }

    /// <summary>An unreadable drive (removed root, permissions) must not produce a false "short" warning: treated as unlimited, like every other environment probe here.</summary>
    private static long SafeFreeBytes(string? root)
    {
        if (root is null)
            return long.MaxValue;

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>Metered with at least one known size above zero warns with the total size; starting stays possible. With no known size there is nothing to quote, so it stays silent.</summary>
    private InstallPreflightLineViewModel? BuildMeteredLine(IReadOnlyList<UpdateRowViewModel> candidates)
    {
        var isMetered = IsMeteredOverride ?? MeteredConnection.IsMetered;
        if (!isMetered())
            return null;

        var knownTotal = candidates.Sum(row => row.DownloadSizeBytes ?? 0L);
        if (knownTotal <= 0)
            return null;

        return new InstallPreflightLineViewModel(string.Format(Loc["InstallMeteredWarn"], FormatBytes(knownTotal)));
    }

    /// <summary>A line only when the battery is low and nothing is plugged in; an unknown line status or percentage never warns (<see cref="Services.Install.PowerPrecondition.ShouldWarn"/>).</summary>
    private InstallPreflightLineViewModel? BuildBatteryLine()
    {
        var (line, percent) = PowerStatusOverride is not null
            ? PowerStatusOverride()
            : (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus,
               System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent);

        if (!Services.Install.PowerPrecondition.ShouldWarn(line, percent))
            return null;

        var wholePercent = (int)Math.Round(percent * 100);
        return new InstallPreflightLineViewModel(string.Format(Loc["InstallBatteryWarn"], wholePercent));
    }

    /// <summary>Opens the pre-flight window, owned by the active window like <see cref="Dialogs.ShowCore"/>, and returns whether Start was pressed. The test seam wins; with <see cref="Dialogs.SuppressInteractiveDialogs"/> (headless tests) it returns Cancel instead of a window nobody would click.</summary>
    private bool ShowInstallPreflight(InstallPreflightViewModel viewModel)
    {
        if (InstallPreflightOverride is not null)
            return InstallPreflightOverride(viewModel);

        if (Dialogs.SuppressInteractiveDialogs)
            return false;

        var window = new Views.InstallPreflightWindow(viewModel);

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;

        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, window))
            window.Owner = owner;

        Views.DialogKeyboard.ShowModal(window);
        return viewModel.Started;
    }

    /// <summary>"Close the program and continue": asks politely first (<c>CloseMainWindow</c>, up to 10 s, polled, never a blocking sleep on a UI-reachable path) and only after a second, separate confirmation kills what is left. True only when no blocking process remains, so a declined kill never lets the install start against an open program.</summary>
    internal static async Task<bool> CloseBlockingProcessesAsync(
        IReadOnlyList<string> processNames,
        Func<string, IReadOnlyList<System.Diagnostics.Process>>? processLookup = null,
        TimeSpan? gracePeriod = null,
        string? onlyInsideFolder = null)
    {
        var stillRunning = new List<System.Diagnostics.Process>();
        try
        {
            return await CloseBlockingProcessesCoreAsync(
                processNames, stillRunning, processLookup ?? SafeProcessesByName, gracePeriod ?? TimeSpan.FromSeconds(10),
                onlyInsideFolder);
        }
        finally
        {
            foreach (var process in stillRunning)
                process.Dispose();
        }
    }

    /// <summary>True when the process image lies under the Windows folder; an unreadable path counts as false.</summary>
    internal static bool RunsFromWindowsFolder(System.Diagnostics.Process process)
    {
        try
        {
            return IsUnderWindowsFolder(Services.ProcessImage.TryGetPath(process.Id), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when the process image lies inside <paramref name="folder"/>; an unreadable path is false.</summary>
    internal static bool RunsFromFolder(System.Diagnostics.Process process, string folder)
    {
        try
        {
            return Services.ProcessImage.TryGetPath(process.Id) is { Length: > 0 } path
                   && Services.Install.InstallBlockers.IsInside(path, folder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsUnderWindowsFolder(string? path, string? windowsFolder) =>
        !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(windowsFolder)
        && path.StartsWith(windowsFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> CloseBlockingProcessesCoreAsync(
        IReadOnlyList<string> processNames,
        List<System.Diagnostics.Process> stillRunning,
        Func<string, IReadOnlyList<System.Diagnostics.Process>> processLookup,
        TimeSpan gracePeriod,
        string? onlyInsideFolder)
    {
        var exempt = new List<System.Diagnostics.Process>();
        foreach (var name in processNames)
        {
            // Checked again here, not only where the names were detected: this step reaches a live process, and a name resolves to whatever runs under it now. Killing this app would abandon the install it is clearing the way for.
            if (Services.Install.InstallBlockers.IsNeverClosed(name))
                continue;

            foreach (var process in processLookup(name))
            {
                // The caller's finally disposes every handle, skipped ones included.
                stillRunning.Add(process);

                if (process.Id == Environment.ProcessId)
                    continue;

                // A short name ("ssh", "bash") also belongs to Windows' own OpenSSH and WSL. Those run from the Windows folder, which no update writes to, so they are never closed.
                if (RunsFromWindowsFolder(process))
                {
                    exempt.Add(process);
                    continue;
                }

                // A Store app is only its own package's processes; a same-named program elsewhere, or one whose image cannot be read, is left alone.
                if (onlyInsideFolder is not null && !RunsFromFolder(process, onlyInsideFolder))
                {
                    exempt.Add(process);
                    continue;
                }

                try
                {
                    if (!process.HasExited)
                        process.CloseMainWindow();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or no main window to ask; the wait and force-kill pass below checks HasExited itself.
                }
            }
        }

        // This app's own process is collected for disposal only, never asked to close.
        var closing = stillRunning.Where(p => !IsThisProcess(p) && !exempt.Contains(p)).ToList();
        if (closing.Count == 0)
            return true;

        var deadline = DateTime.UtcNow + gracePeriod;
        while (DateTime.UtcNow < deadline && closing.Any(p => !SafeHasExited(p)))
            await Task.Delay(100);

        var survivors = closing.Where(p => !SafeHasExited(p)).ToList();
        if (survivors.Count == 0)
            return true;

        var forced = Dialogs.Confirm(
            LocalizationService.Instance["InstallBlockerForceKillHeadline"],
            LocalizationService.Instance["InstallBlockerForceKillBody"],
            destructive: true);
        if (!forced)
            return false;

        foreach (var process in survivors)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Best effort: a process that exited or refuses to die must not abort the rest of the batch.
            }
        }

        var killDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < killDeadline && survivors.Any(p => !SafeHasExited(p)))
            await Task.Delay(100);

        return survivors.All(SafeHasExited);
    }

    private static bool IsThisProcess(System.Diagnostics.Process process)
    {
        try
        {
            return process.Id == Environment.ProcessId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int CurrentSessionId()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.SessionId;
    }

    private static int? SessionIdOf(System.Diagnostics.Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Unreadable: kept in the list, because a program that cannot be inspected must still block.
            return null;
        }
    }

    private static IReadOnlyList<System.Diagnostics.Process> SafeProcessesByName(string name)
    {
        try
        {
            // Every session's processes come back; only this user's session can block an install or be closed by this app, so the rest are released at once.
            var sessionId = CurrentSessionId();
            var mine = new List<System.Diagnostics.Process>();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                if (SessionIdOf(process) is not { } id || id == sessionId)
                    mine.Add(process);
                else
                    process.Dispose();
            }

            return mine;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static bool SafeHasExited(System.Diagnostics.Process process) => SafeHasExited(() => process.HasExited);

    internal static bool SafeHasExited(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Cannot ask (access denied, protected process): it may still run, so fail closed and keep the row blocked rather than install over a live program.
            return false;
        }
    }

    /// <summary>Unattended path, no dialog: the row is marked failed with the reason and recorded in history like any install outcome, never silently dropped.</summary>
    private void SkipBlockedRowUnattended(UpdateRowViewModel row, string blockingProgramName)
    {
        var message = string.Format(Loc["InstallBlockerAutoSkipped"], blockingProgramName);

        row.BeginInstall();
        row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, message, false, message));

        _history.Append(HistoryEntry.ForItem(row.Item, InstallAction.Install, success: false, error: message));
    }

    /// <summary>A program that must not be closed (Git's own shells) uses the files the update would replace. The row ends Cancelled with the reason, not as a failure, so it never counts towards the repeated-failure flag and returns on the next scan.</summary>
    private void SkipRowInUse(UpdateRowViewModel row, Services.Install.BlockedInstall blocker)
    {
        var message = string.Format(Loc["InstallBlockerInUse"], blocker.DescribeProcesses());

        row.BeginInstall();
        row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, message, null, null)
        {
            IsCancelled = true,
        });

        _history.Append(HistoryEntry.ForItem(row.Item, InstallAction.Install, success: false, error: message, isCancelled: true));
    }

    /// <summary>Interactive path: the user chose "skip blocked", or "close and continue" did not get the program closed. Each row shows Cancelled with the reason and gets one cancelled history entry. Not a failure.</summary>
    private void SkipBlockedRowsStillOpen(InstallPreflightLineViewModel line)
    {
        var message = string.Format(Loc["InstallBlockerSkippedStillOpen"], line.BlockedProgramName);

        foreach (var row in line.BlockedRows!)
        {
            row.BeginInstall();
            row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, message, null, null)
            {
                IsCancelled = true,
            });

            _history.Append(HistoryEntry.ForItem(row.Item, InstallAction.Install, success: false, error: message, isCancelled: true));
        }
    }

    /// <summary>Completed when rows join the queue while <see cref="RunInstallLoopAsync"/> runs, so they start right away. Null while no loop runs.</summary>
    private TaskCompletionSource? _installWake;

    private async Task RunInstallLoopAsync()
    {
        // The batch that owned the shared state has ended and only a side uninstall keeps it up: this loop takes ownership back instead of leaving the new rows waiting.
        var takingOver = _installActivityRunning && _ownerEnded && _installWake is null;
        if (_installActivityRunning && !takingOver)
        {
            // A running loop starts the new rows at once, beside the run going.
            _installWake?.TrySetResult();
            RefreshInstallProgress();
            return;
        }

        _ownerEnded = false;
        _installActivityRunning = true;
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        IsInstalling = true;
        RebootRequiredNotice = false;
        BeginInstallProgress();

        // Pairs with BatchFinished in the finally below, so a run's total duration is on record.
        _logging.Event(ActivityLog.Install, ActivityLog.BatchStarted(
            _installPending.Count, _installPending.Any(r => InstallQueue.NeedsElevation(r.SourceId))));

        try
        {
            // Rows queued during a run start their own run at once: the queue's lanes are shared across runs, so a package manager never runs two of its installs at a time while every other lane starts immediately.
            var running = new List<Task>();
            while (!_installCts.IsCancellationRequested)
            {
                // Set up before the pending rows are taken, so a row queued from here on wakes the wait below.
                _installWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var snapshot = _installPending.ToList();
                _installPending.Clear();

                if (snapshot.Count > 0)
                {
                    // One call per turn: InstallQueue.RunAsync splits by elevation itself, starts both halves together and runs its automatic retry once after both. Admin rows of one turn go out in one elevated batch.
                    var adminBatch = snapshot.Where(r => InstallQueue.NeedsElevation(r.SourceId));
                    var userScope = snapshot.Where(r => !InstallQueue.NeedsElevation(r.SourceId));
                    var items = userScope.Select(r => r.Item).Concat(adminBatch.Select(r => r.Item)).ToList();
                    running.Add(_installQueue.RunAsync(items, _installCts.Token));
                }

                if (running.Count == 0)
                    break;

                await Task.WhenAny(running.Append(_installWake.Task));
                foreach (var done in running.Where(t => t.IsCompleted).ToList())
                {
                    running.Remove(done);
                    try
                    {
                        await done;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        // One turn failing must not end the others running beside it.
                        _logging.Error("Install batch failed", ex);
                    }
                }
            }

            // Stopped: the runs still going unwind on the same token; their rows close out before this one ends.
            foreach (var task in running)
            {
                try
                {
                    await task;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logging.Error("Install batch failed", ex);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Whatever failed, the finally below still has to close out every pending or held row instead of leaving them reading "queued".
            _logging.Error("Install batch failed", ex);
        }
        finally
        {
            _installWake = null;

            // Rows still waiting at Stop never ran and would stay "queued", so they are closed out here, the only place that knows they never reached the queue. The same holds for admin rows held back for the elevated batch. After a Stop, CancelInstall already settled every row waiting then; what is pending now was queued during the unwind and goes to a fresh batch below.
            var stopped = _installCts.IsCancellationRequested;
            if (!stopped)
            {
                foreach (var row in _installPending)
                    row.MarkNotInstalled(Loc["InstallCancelled"]);
                _installPending.Clear();
            }

            _logging.Event(ActivityLog.Install, ActivityLog.BatchFinished(
                InstallBatch.Count, DateTimeOffset.UtcNow - _installStartedAt));

            var released = EndInstallActivity();

            // Surface "installed, reboot pending" results once the batch is done.
            ReportRebootsFor(InstallBatch);

            // Held open by a running uninstall: that one hands the pending rows on when it ends.
            if (stopped && released)
                HandOffPendingInstalls();
        }
    }

    /// <summary>Rows whose restart request was already announced. A takeover run keeps the previous batch's rows in <see cref="InstallBatch"/> until its side uninstall ends, so without this the next batch's end would announce the same package twice.</summary>
    private readonly HashSet<UpdateRowViewModel> _rebootReported = [];

    /// <summary>Raises the restart banner for every row still waiting for a restart, and the dialog (or a balloon when the window is hidden) once for rows not announced before. Called when a batch or a solo activity (downgrade, uninstall, reinstall, retry) ends.</summary>
    private void ReportRebootsFor(IEnumerable<UpdateRowViewModel> rows)
    {
        // A row that started over no longer asks for a restart; if it asks again it is news again.
        _rebootReported.RemoveWhere(r => !r.NeedsReboot);

        var rebooting = rows.Where(r => r.NeedsReboot).ToList();
        if (rebooting.Count == 0)
            return;

        var fresh = rebooting.Where(_rebootReported.Add).ToList();
        ShowRebootNotice(_rebootReported.Count);

        if (fresh.Count > 0)
            ShowRebootDialogIfNeeded(fresh.Select(r => r.Name).ToList());
    }

    [ObservableProperty]
    private bool rebootRequiredNotice;

    private int _rebootPendingCount;

    /// <summary>Built on every read from the stored count, so a language switch only re-raises it.</summary>
    public string RebootInfoText => _rebootPendingCount == 0
        ? string.Empty
        : string.Format(Loc[_rebootPendingCount == 1 ? "RebootRequiredInfoOne" : "RebootRequiredInfo"], _rebootPendingCount);

    internal void ShowRebootNotice(int pendingCount)
    {
        _rebootPendingCount = pendingCount;
        OnPropertyChanged(nameof(RebootInfoText));
        RebootRequiredNotice = true;
    }

    [RelayCommand]
    private void DismissRebootNotice() => RebootRequiredNotice = false;

    /// <summary>The packages that asked for a restart in the batch that just finished; read by <see cref="ShowRebootDialogIfNeeded"/>.</summary>
    internal IReadOnlyList<string> RebootRequestedBy { get; private set; } = [];

    /// <summary>Test seam: replaces the two-button dialog and returns whether "restart now" was chosen. Null in production, where <see cref="ShowRebootDialog"/> goes through <see cref="Dialogs.Confirm"/>.</summary>
    internal Func<IReadOnlyList<string>, bool>? RebootDialogOverride { get; set; }

    /// <summary>Test seam for whether the main window is visible. A real <see cref="Window"/> against the shared test <see cref="Application"/> corrupts later tests' windows, so this is a plain probe.</summary>
    internal Func<bool>? MainWindowVisibleOverride { get; set; }

    /// <summary>Once per batch or solo activity, called only from <see cref="ReportRebootsFor"/>, never mid-batch, and never onto a window nobody can see: hidden to tray gets the banner plus one balloon, with no reminder option and no automatic restart. Internal so a test can call it without driving a batch through a mock source that cannot report <c>RebootRequired</c>.</summary>
    internal void ShowRebootDialogIfNeeded(IReadOnlyList<string> names)
    {
        RebootRequestedBy = names;

        var mainWindowVisible = MainWindowVisibleOverride ?? (() => Application.Current?.MainWindow?.IsVisible == true);
        if (!mainWindowVisible())
        {
            _notifications.NotifyRebootNeeded(names.Count);
            return;
        }

        var restartNow = RebootDialogOverride?.Invoke(names) ?? ShowRebootDialog(names);
        if (restartNow)
            _ = Background.Run(RestartNowAsync(), _logging, "restart now");
    }

    private bool ShowRebootDialog(IReadOnlyList<string> names)
    {
        var body = Loc["RebootDialogBody"] + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, names);
        return Dialogs.Confirm(Loc["RebootDialogTitle"], body, Loc["RebootDialogNow"], Loc["RebootDialogLater"]);
    }

    /// <summary>Resolves the full path like every other external call (ProcessRunner.Resolve); fire and forget, the restart is imminent either way.</summary>
    private async Task RestartNowAsync()
    {
        try
        {
            await ProcessRunner.RunAsync("shutdown", ["/r", "/t", "0"], TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logging.Error("Failed to start a restart.", ex);
        }
    }

    /// <summary>Stops the batch. Rows still waiting are settled here as cancelled: the loop only sees the token at its next turn, and rows queued while the batch unwinds (an installer being killed, the elevated worker shutting down) belong to a new batch.</summary>
    [RelayCommand]
    private void CancelInstall()
    {
        _installCts?.Cancel();
        foreach (var sideCts in _sideUninstallCts.ToList())
            sideCts.Cancel();
        foreach (var row in _installPending)
            row.MarkNotInstalled(Loc["InstallCancelled"]);
        _installPending.Clear();
        RefreshInstallProgress();
    }

    /// <summary>Called from the <c>finally</c> of every solo activity (interactive retry, uninstall) right after it clears <see cref="_installActivityRunning"/>. An Install click on another row meanwhile only sat in <see cref="_installPending"/>; this installs it now that the machinery is free.</summary>
    private void HandOffPendingInstalls()
    {
        if (_installPending.Count > 0)
            _ = Background.Run(RunInstallLoopAsync(), _logging, "install loop");
    }

    /// <summary>The batch that is running, in install order.</summary>
    public ObservableCollection<UpdateRowViewModel> InstallBatch { get; } = [];

    private DispatcherTimer? _installTimer;
    private DateTimeOffset _installStartedAt;

    [ObservableProperty]
    private string installElapsedText = "0:00";

    // HandedOff is terminal, like NeedsManualWork (never Idle again) and Cancelled; leaving any of them out would pin the strip at "2 of 3" or "0 of 1" beside a full bar.
    public int InstallDoneCount => InstallBatch.Count(r => IsTerminalInstallState(r.InstallState));

    private static bool IsTerminalInstallState(RowInstallState state) =>
        state is RowInstallState.Succeeded or RowInstallState.Failed or RowInstallState.HandedOff
            or RowInstallState.Stuck or RowInstallState.NeedsManualWork or RowInstallState.Cancelled;

    public int InstallTotalCount => InstallBatch.Count;

    // A finished row is a finished share whatever its outcome. An installing row counts as its whole download but never as finished: only a terminal state takes a share to 100, so the figure cannot read complete while a row still works.
    public double InstallOverallPercent =>
        InstallBatch.Count == 0
            ? 0
            : InstallBatch.Average(r => IsTerminalInstallState(r.InstallState)
                ? 100
                : r.Phase == InstallPhase.Installing ? 99 : Math.Min(99, r.InstallPercent));

    // No figure while nothing in the batch has a real one: the strip shows the moving bar alone.
    public string InstallOverallPercentText =>
        IsInstalling && !InstallHasRealProgress ? string.Empty : PercentFormat.Format(InstallOverallPercent);

    /// <summary>False while no running row has a real download figure; the install band then shows a wandering shine instead of a bar stuck at 0.</summary>
    public bool InstallHasRealProgress => InstallBatch.Any(r =>
        (r.HasRealProgress && r.Phase != InstallPhase.Installing) || r.InstallState == RowInstallState.Succeeded);

    public string InstallCountText =>
        string.Format(Loc["InstallProgressCount"], InstallDoneCount, InstallTotalCount);

    /// <summary>Batch-wide figures for the install strip: total size, downloaded so far and current speed, each shown only when it has a real number, so a batch that reports no bytes (a launcher hand-off) shows nothing rather than a zero. Not gated on batch size. The system drive's free space is deliberately not part of it.</summary>
    public string InstallTotalSizeText
    {
        get
        {
            var totalDownload = InstallBatch.Sum(r => r.DownloadSizeBytes ?? 0L);
            return totalDownload > 0 ? string.Format(Loc["InstallProgressTotalSize"], FormatBytes(totalDownload)) : string.Empty;
        }
    }

    public bool HasInstallTotalSize => InstallTotalSizeText.Length > 0;

    public string InstallDownloadedText
    {
        get
        {
            var downloaded = InstallBatch.Sum(r => r.BytesReceived ?? 0L);
            return downloaded > 0 ? string.Format(Loc["InstallProgressDownloaded"], FormatBytes(downloaded)) : string.Empty;
        }
    }

    public bool HasInstallDownloaded => InstallDownloadedText.Length > 0;

    public string InstallSpeedText
    {
        get
        {
            var speed = InstallBatch.Where(r => r.InstallState == RowInstallState.Running).Sum(r => r.BytesPerSecond);
            return speed > 0 ? $"{FormatBytes((long)speed)}/s" : string.Empty;
        }
    }

    public bool HasInstallSpeed => InstallSpeedText.Length > 0;

    private static string FormatBytes(long bytes) => ByteSize.Format(bytes);

    /// <summary>The queue panel's own row order, separate from <see cref="InstallBatch"/> (which follows the order rows were added, not the user's drag and drop). Rebuilt incrementally: rows that left Queued drop out, new ones append, the rest keep the user's order.</summary>
    public ObservableCollection<UpdateRowViewModel> QueueRows { get; } = [];

    [ObservableProperty]
    private bool isQueuePanelExpanded;

    public bool HasQueuedRows => QueueRows.Count > 0;

    public string QueuePanelHeaderText => string.Format(Loc["InstallQueueHeader"], QueueRows.Count);

    /// <summary>The queue's combined download size beside the header text; empty, not "0 B", when nothing reports one.</summary>
    public string QueueTotalSizeText
    {
        get
        {
            var total = QueueRows.Sum(r => r.DownloadSizeBytes ?? 0L);
            return total > 0 ? FormatBytes(total) : string.Empty;
        }
    }

    public bool HasQueueTotalSize => QueueTotalSizeText.Length > 0;

    private void SyncQueueRows()
    {
        var queued = InstallBatch.Where(r => r.InstallState == RowInstallState.Queued).ToList();
        var queuedSet = new HashSet<UpdateRowViewModel>(queued);

        for (var i = QueueRows.Count - 1; i >= 0; i--)
            if (!queuedSet.Contains(QueueRows[i]))
                QueueRows.RemoveAt(i);

        foreach (var row in queued)
            if (!QueueRows.Contains(row))
                QueueRows.Add(row);

        OnPropertyChanged(nameof(HasQueuedRows));
        OnPropertyChanged(nameof(QueuePanelHeaderText));
        OnPropertyChanged(nameof(QueueTotalSizeText));
        OnPropertyChanged(nameof(HasQueueTotalSize));
    }

    /// <summary>The same incremental rebuild as <see cref="SyncQueueRows"/>, for the running-rows strip.</summary>
    private void SyncRunningRows()
    {
        var running = InstallBatch.Where(r => r.InstallState == RowInstallState.Running).ToList();
        var runningSet = new HashSet<UpdateRowViewModel>(running);

        for (var i = RunningRows.Count - 1; i >= 0; i--)
            if (!runningSet.Contains(RunningRows[i]))
                RunningRows.RemoveAt(i);

        foreach (var row in running)
            if (!RunningRows.Contains(row))
                RunningRows.Add(row);

        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(HasMultipleRunning));
        OnPropertyChanged(nameof(ShowSingleInstallView));
        OnPropertyChanged(nameof(InstallRunningCountText));
    }

    /// <summary>Pushes the queue panel's order into whatever holds the rows back: the pending list for rows not yet handed over, the queue itself for rows inside a running <see cref="InstallQueue.RunAsync"/>. Each ignores rows it does not know, so calling both is safe.</summary>
    private void PushQueueOrder()
    {
        var order = QueueRows.ToList();
        if (order.Count == 0)
            return;

        // Rows the panel does not list (the folded-away twins of a shown row) sort behind the listed ones; OrderBy is stable.
        var position = new Dictionary<UpdateRowViewModel, int>();
        for (var i = 0; i < order.Count; i++)
            position[order[i]] = i;

        var reordered = _installPending.OrderBy(r => position.GetValueOrDefault(r, int.MaxValue)).ToList();
        _installPending.Clear();
        _installPending.AddRange(reordered);

        _installQueue.Reorder(order.Select(r => (r.Item.SourceId, r.Item.Id)).ToList());
    }

    /// <summary>Called by the queue panel's drop handler with the row's old and new index.</summary>
    public void MoveQueueRow(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= QueueRows.Count || newIndex >= QueueRows.Count)
            return;

        QueueRows.Move(oldIndex, newIndex);
        PushQueueOrder();
    }

    [RelayCommand]
    private void RemoveFromQueue(UpdateRowViewModel? row)
    {
        if (row is null || row.InstallState != RowInstallState.Queued)
            return;

        // The row goes only once something has really let go of it: the pending list, or the queue. A queue that refuses has already handed the item to the elevated worker; removing the row then would say cancelled while it keeps installing.
        if (!_installPending.Remove(row) && !_installQueue.Remove(row.Item.SourceId, row.Item.Id))
            return;

        QueueRows.Remove(row);
        InstallBatch.Remove(row);

        row.MarkNotInstalled(Loc["InstallQueueItemRemoved"]);

        // The folded-away copies were queued with this row (see Expand) and have no queue line of their own, so they leave with it under the same rule.
        foreach (var partner in row.MergedPartners.Where(p => p.InstallState == RowInstallState.Queued && !QueueRows.Contains(p)))
        {
            if (!_installPending.Remove(partner) && !_installQueue.Remove(partner.Item.SourceId, partner.Item.Id))
                continue;

            InstallBatch.Remove(partner);
            partner.MarkNotInstalled(Loc["InstallQueueItemRemoved"]);
        }

        RefreshInstallProgress();
    }

    /// <summary>What the window shows at the top: the package being worked on.</summary>
    public UpdateRowViewModel? CurrentInstallRow =>
        InstallBatch.FirstOrDefault(r => r.InstallState == RowInstallState.Running)
        ?? InstallBatch.FirstOrDefault(r => r.InstallState == RowInstallState.Queued);

    public bool HasCurrentInstall => CurrentInstallRow is not null;

    /// <summary>Every row currently installing, in batch order: the strip's per-row list once more than one runs at a time. Rebuilt incrementally by <see cref="SyncRunningRows"/>; a new list every 250 ms would give the bound ItemsControl a full reset instead of a few Add/Remove notifications.</summary>
    public ObservableCollection<UpdateRowViewModel> RunningRows { get; } = [];

    public int RunningCount => RunningRows.Count;

    /// <summary>The strip's switch: single-row look while at most one row runs, the compact list once a second starts.</summary>
    public bool HasMultipleRunning => RunningCount >= 2;

    public bool ShowSingleInstallView => !HasMultipleRunning;

    public string InstallRunningCountText => string.Format(Loc["InstallRunningCount"], RunningCount);

    private void StartInstallProgress(IReadOnlyList<UpdateRowViewModel> batch)
    {
        InstallBatch.Clear();
        foreach (var row in batch)
            InstallBatch.Add(row);

        BeginInstallProgress();
    }

    /// <summary>Starts the elapsed and throughput ticker for <see cref="InstallBatch"/>.</summary>
    private void BeginInstallProgress()
    {
        _installStartedAt = DateTimeOffset.UtcNow;

        _installTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            // 250 ms: the batch figures (MB/s, elapsed) only move on this tick, and a slower one looks static next to how often per-row reports arrive.
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _installTimer.Tick -= OnInstallTimerTick;
        _installTimer.Tick += OnInstallTimerTick;
        _installTimer.Start();

        RefreshInstallProgress();
    }

    private void OnInstallTimerTick(object? sender, EventArgs e)
    {
        // Ends a finished download's pause and advances each running row's elapsed time before the figures are recomputed.
        foreach (var row in InstallBatch)
            row.Tick();

        RefreshInstallProgress();
    }

    private void RefreshInstallProgress()
    {
        var elapsed = DateTimeOffset.UtcNow - _installStartedAt;
        InstallElapsedText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";

        OnPropertyChanged(nameof(InstallDoneCount));
        OnPropertyChanged(nameof(InstallTotalCount));
        OnPropertyChanged(nameof(InstallOverallPercent));
        OnPropertyChanged(nameof(InstallOverallPercentText));
        OnPropertyChanged(nameof(InstallHasRealProgress));
        OnPropertyChanged(nameof(InstallCountText));
        OnPropertyChanged(nameof(InstallTotalSizeText));
        OnPropertyChanged(nameof(HasInstallTotalSize));
        OnPropertyChanged(nameof(InstallDownloadedText));
        OnPropertyChanged(nameof(HasInstallDownloaded));
        OnPropertyChanged(nameof(InstallSpeedText));
        OnPropertyChanged(nameof(HasInstallSpeed));
        OnPropertyChanged(nameof(CurrentInstallRow));
        OnPropertyChanged(nameof(HasCurrentInstall));

        SyncQueueRows();
        SyncRunningRows();
    }

    /// <summary>UI cue: fires once the batch's own retry round starts.</summary>
    private void OnSecondAttemptStarted() => _dispatcher.Invoke(() => IsSecondAttemptActive = true);

    /// <summary>How often a row's plain progress reaches the window: at most four times a second however often the tool prints. A synchronous hop per report ran above input priority and froze the other windows while several installs were chatting.</summary>
    internal static readonly TimeSpan InstallProgressFlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Newest not yet applied plain progress per row.</summary>
    private readonly Dictionary<string, InstallItemStatus> _pendingInstallProgress = new(StringComparer.OrdinalIgnoreCase);

    private bool _installProgressFlushScheduled;

    internal void OnInstallStatusChanged(InstallItemStatus status)
    {
        var key = $"{status.SourceId}:{status.ItemId}";
        var rounded = Math.Floor(status.Percent);

        // Only a plain mid-install tick may be dropped or delayed. A terminal result (Success or Error set, a manual handoff) and the flags a later tick must not overwrite cross at once, so a chatty tool never delays the report the user waits for.
        var isImmediate = status.IsTerminal || status.Error is not null || status.IsAwaitingRetry
            || status.RebootRequired || status.VersionUnchanged;
        var isTerminal = status.IsTerminal || status.Error is not null;

        var scheduleFlush = false;
        lock (_lastDispatchedInstallStatus)
        {
            if (!isImmediate
                && _lastDispatchedInstallStatus.TryGetValue(key, out var last)
                && last.Percent == rounded && last.Status == status.Status)
            {
                return;
            }

            if (isTerminal)
                _lastDispatchedInstallStatus.Remove(key);
            else
                _lastDispatchedInstallStatus[key] = (rounded, status.Status);

            if (isImmediate)
            {
                // Whatever waited for this row is older than this report.
                _pendingInstallProgress.Remove(key);
            }
            else
            {
                _pendingInstallProgress[key] = status;
                scheduleFlush = !_installProgressFlushScheduled;
                _installProgressFlushScheduled = true;
            }
        }

        if (!isImmediate)
        {
            if (scheduleFlush)
            {
                _ = Task.Delay(InstallProgressFlushInterval).ContinueWith(
                    _ => _dispatcher.BeginInvoke(FlushPendingInstallProgress, DispatcherPriority.Background),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            return;
        }

        _dispatcher.Invoke(() => ApplyInstallStatus(status, isTerminal));
    }

    /// <summary>Applies the newest plain progress of every row that reported since the last flush.</summary>
    private void FlushPendingInstallProgress()
    {
        InstallItemStatus[] batch;
        lock (_lastDispatchedInstallStatus)
        {
            batch = [.. _pendingInstallProgress.Values];
            _pendingInstallProgress.Clear();
            _installProgressFlushScheduled = false;
        }

        foreach (var status in batch)
            ApplyInstallStatus(status, isTerminal: false);
    }

    private void ApplyInstallStatus(InstallItemStatus status, bool isTerminal)
    {
        // Source included: ids are unique per source only, and chocolatey, scoop, pip and npm share package names. Children too: Steam's parent row has id "steam" and every game update is a child with its own id.
        var row = FindRow(status.SourceId, status.ItemId);
        if (row is null)
            return;

        row.ApplyInstallStatus(status);
        if (isTerminal)
            TrackPersistentFailure(row);

        // WindowsUpdateSource.Install throws this exact text only once the update's GUID is gone from a fresh search, never for a mere revision bump. The row is stale either way (in-process or from the elevated worker), so the source is rescanned instead of leaving that to the user.
        if (status.Success == false
            && string.Equals(status.SourceId, "windowsupdate", StringComparison.Ordinal)
            && status.Detail?.Contains("is no longer offered", StringComparison.Ordinal) == true)
        {
            _ = Background.Run(RescanSingleSourceAsync(status.SourceId), _logging, "rescan single source");
        }
    }
}
