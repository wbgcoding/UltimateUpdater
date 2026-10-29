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

    /// <summary>True once the batch's own automatic second attempt has actually started, so the
    /// queue panel can show it as its own visibly separate section. Reset on the next fresh batch.</summary>
    [ObservableProperty]
    private bool isSecondAttemptActive;

    partial void OnIsInstallingChanged(bool value)
    {
        RefreshSelectionState();
        InstallSelfUpdateCommand.NotifyCanExecuteChanged();

        // Deferred, not inline: whatever clears this flag finishes its own step first (clearing
        // _installActivityRunning, handing rows queued meanwhile to the next batch), and only then is the
        // work known to be over.
        _dispatcher.InvokeAsync(ApplyWaitingSelfUpdateWhenIdle);

        // The batch loop always calls BeginInstallProgress right alongside setting this flag, but that is
        // caller discipline, not a guarantee: whatever else flips this flag must find the queue and running
        // strips already matching InstallBatch, not stale from before the flip.
        SyncQueueRows();
        SyncRunningRows();
    }

    [RelayCommand]
    private async Task InstallSelected()
    {
        // only what is on screen: a hidden row is one the user never agreed to
        await EnqueueInstallAsync(VisibleRows.Where(u => u.IsSelected).ToList());
    }

    /// <summary>Rows waiting to be installed. Only ever touched on the UI thread (see RunInstallLoopAsync).</summary>
    private readonly List<UpdateRowViewModel> _installPending = [];

    /// <summary>
    /// True while ANY activity owns the shared install machinery (<see cref="InstallBatch"/>,
    /// <see cref="_installPending"/>, <see cref="_installCts"/>, the progress timer): the batch loop
    /// (<see cref="RunInstallLoopAsync"/>), an interactive retry, or an uninstall. Setting only
    /// <see cref="IsInstalling"/> would not do: a per-row Install click on a DIFFERENT row while a retry or
    /// uninstall runs would find the loop "not running" and start a second, concurrent
    /// <see cref="RunInstallLoopAsync"/> that resets the same shared state under the first activity. Every
    /// one of the three entry points sets this before it touches anything shared and clears it in its own
    /// <c>finally</c>; a click that lands while it is true is never rejected, it queues in
    /// <see cref="_installPending"/> (see <see cref="EnqueueInstallAsync"/>) for
    /// <see cref="HandOffPendingInstalls"/> to pick up once the activity holding the flag is done.
    /// </summary>
    private bool _installActivityRunning;

    /// <summary>
    /// Uninstalls started while another activity already owns the shared machinery. Each is its own
    /// concurrent <see cref="InstallQueue.RunAsync"/> run (see <see cref="UninstallRowAsync"/>) and
    /// takes no ownership: not the token, not the progress list, not the flags.
    /// </summary>
    private int _sideUninstalls;

    /// <summary>The cancellation sources of the running <see cref="_sideUninstalls"/>, so Stop reaches them too.</summary>
    private readonly List<CancellationTokenSource> _sideUninstallCts = [];

    /// <summary>True once the activity that owns the shared state has finished while a side uninstall
    /// still runs: the state stays up until the last of both ends.</summary>
    private bool _ownerEnded;

    /// <summary>
    /// The owner activity's closing step. With no side uninstall running it releases the shared state
    /// and returns true (the caller then hands the rows queued meanwhile to the next batch). Otherwise
    /// the state stays up and the last side uninstall releases it, see <see cref="EndSideUninstall"/>.
    /// </summary>
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

    /// <summary>
    /// Turns the rows the user picked into the rows that actually have to be installed: a row the table
    /// folded several sources into stands for a REAL installation per source, so every one of them must be
    /// updated, best-ranked first; otherwise updating a folded row (e.g. graphify via both uv and pip)
    /// would update only the winning source's copy, and the next scan would list the other one again at
    /// the very same version. One row on screen, one click, every copy current.
    /// </summary>
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

    /// <summary>
    /// True only when the row's own data says there is genuinely nothing to install, both
    /// versions known and equal. Null-vs-null (no version info at all, e.g. Steam's launcher row) must
    /// never read as "already current".
    /// </summary>
    private static bool IsAlreadyCurrent(UpdateItem item) =>
        item.InstalledVersion is not null
        && string.Equals(item.InstalledVersion, item.AvailableVersion, StringComparison.Ordinal);

    /// <summary>
    /// Adds rows to the install queue. If nothing is running it starts the queue; if a batch is already
    /// running the rows start at once beside it (only a lane already busy makes them wait); the per-row Install button feeds
    /// the queue of running updates. Rows already done or in flight are skipped.
    /// </summary>
    private async Task EnqueueInstallAsync(IReadOnlyList<UpdateRowViewModel> rows)
    {
        // Scanning and installing run side by side without colliding: a scan RECONCILES the table rather
        // than clearing it, DrainPendingItems only reuses rows that are Idle, and RemoveStaleRows
        // leaves queued and running rows alone.
        if (RefusedWhileSelfUpdateApplies("Install", tellUser: true))
            return;

        var candidates = Expand(rows)
            .Where(row => row.CanInstall
                          && row.InstallState is RowInstallState.Idle or RowInstallState.Failed or RowInstallState.Cancelled
                          && !_installPending.Contains(row))
            .ToList();
        if (candidates.Count == 0)
            return;

        // The pre-flight dialog can pump messages while it is up; re-entrant clicks during
        // that wait could otherwise queue the same row twice. Every row is re-checked against the exact
        // conditions `candidates` was built from above, not just re-filtered by identity, since a row's
        // state can also have moved on for other reasons while the dialog was up (e.g. finishing a batch
        // that was already running).
        var skippedBlocked = new List<UpdateRowViewModel>();

        // The pre-flight window counts as work for the self update: without it a cleanup ending while the
        // window is up would let the update start applying, and end the app under the user's open dialog.
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

        var resolved = resolvedCandidates
            .Where(row => row.CanInstall
                          && row.InstallState is RowInstallState.Idle or RowInstallState.Failed or RowInstallState.Cancelled
                          && !_installPending.Contains(row))
            .ToList();

        // The version-based part of the guarantee, on top of the scan-reconciliation fix that
        // stops a mid-install row from ever being replaced by an installable-looking clone in the first
        // place (Scan.cs's DrainPendingItems). If a row's own data already says the installed and
        // available versions match, running the installer again would be a no-op the user never asked
        // for; refused here rather than run, one status line, same pattern as the auto-skipped-blocker
        // message below.
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

        // Only THIS batch's own rows are sorted among themselves, critical first; OrderByDescending
        // is a stable sort, so two rows tied on IsCritical keep the order the user (or the table) already
        // gave them. Rows queued later still simply append behind whatever is already in _installPending
        // (untouched below), and a batch already running is never re-sorted, only extended.
        toAdd = toAdd.OrderByDescending(row => row.IsCritical).ToList();

        // A fresh run starts a clean progress list; joining rows extend the one already showing.
        if (!_installActivityRunning)
        {
            InstallBatch.Clear();
            IsSecondAttemptActive = false;
        }

        // Rows skipped for a running program stay visible in this run's strip (table row and history
        // already say why); added only now, because the clear above belongs to the run they were skipped for.
        foreach (var row in skippedBlocked.Where(row => !_mergedAway.Contains(row) && !InstallBatch.Contains(row)))
            InstallBatch.Add(row);

        if (toAdd.Count == 0)
        {
            ApplyWaitingSelfUpdateWhenIdle();
            return;
        }

        // One selection refresh for the whole batch, not two per row (BeginInstall moves CanInstall, the
        // untick moves IsSelected, each re-walking every visible row): quadratic for a few hundred rows.
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var row in toAdd)
            {
                row.BeginInstall();
                row.IsSelected = false;
                _installPending.Add(row);

                // A package the table folded into ONE row stays one row here too. Both sources are installed
                // (they are two real installations), but the one the user cannot see does not get a line of
                // its own in the strip.
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

        _ = Background.Run(RunInstallLoopAsync(), _logging, "install loop");
    }

    /// <summary>
    /// Test-only seam (same shape as <see cref="RetryInteractiveOverride"/>): replaces the pre-flight
    /// window itself, so a test can simulate Start/Cancel and set each blocker line's <c>Choice</c> without
    /// a real WPF window. Null in production, where <see cref="ShowInstallPreflight"/> opens the real
    /// <see cref="Views.InstallPreflightWindow"/>.
    /// </summary>
    internal Func<InstallPreflightViewModel, bool>? InstallPreflightOverride { get; set; }

    /// <summary>Test seam for "close and continue": replaces the real close/force-kill step, so a test can
    /// simulate a declined force close without touching a live process. Null in production.</summary>
    internal Func<IReadOnlyList<string>, Task<bool>>? CloseBlockingProcessesOverride { get; set; }

    /// <summary>Test seam for the metered-connection check: default <see cref="MeteredConnection.IsMetered"/>,
    /// the real probe reads whatever connection genuinely happens to be active on the host it runs on.</summary>
    internal Func<bool>? IsMeteredOverride { get; set; }

    /// <summary>Test seam for the battery check: default reads the real
    /// <c>SystemInformation.PowerStatus</c>, which reflects whatever power state genuinely happens to be
    /// true on the host it runs on.</summary>
    internal Func<(System.Windows.Forms.PowerLineStatus Line, float BatteryLifePercent)>? PowerStatusOverride { get; set; }

    /// <summary>
    /// A program an update would overwrite may still be running (winget's Visual Studio exit code 8006);
    /// the disk may be too small, the connection metered, or the battery low. Runs app-side, unelevated,
    /// before any row is queued.
    /// Unattended (build, scheduled scan, tray run; <see cref="App.IsUnattended"/>): none of this is
    /// checked: a blocked row is skipped automatically with its reason recorded rather than silently
    /// dropped, and there is nobody to warn about disk space or battery.
    /// Interactive with nothing to say (no lines): the candidates are returned unchanged, no window, no
    /// extra click for the normal case. Interactive with something to say: the pre-flight window collects
    /// every warning and lists every blocked group, then asks once for all of them; the start button picked
    /// returns exactly the accepted rows (unblocked candidates, plus every blocked group when it was "close
    /// and continue" or "try anyway"), Cancel returns none at all, not even the rows nothing was wrong with.
    /// </summary>
    private async Task<List<UpdateRowViewModel>> ResolveInstallCandidatesAsync(
        IReadOnlyList<UpdateRowViewModel> candidates, List<UpdateRowViewModel> skipped)
    {
        var accepted = new List<UpdateRowViewModel>(candidates.Count);

        // First pass: detect only, never ask yet. Two rows waiting on the SAME running program (two Visual
        // Studio components both held up by devenv) are one real decision, not two, so the question is
        // asked once per group below, not once per row here. One process snapshot for the whole batch,
        // taken off the UI thread: a full system enumeration per row would both repeat the same work and
        // block the window while it runs.
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

        // Warnings read before the blocked-group questions.
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

        // The restore point is only ever made for an elevated batch with the setting on (InstallQueue), so
        // the dialog mentions it only then.
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
                    // Rows are only queued once the blocking program is really gone; a declined force-kill
                    // or a process that survived leaves them out, exactly like "Blockierte überspringen".
                    if (await (CloseBlockingProcessesOverride ?? (names => CloseBlockingProcessesAsync(names)))(line.BlockedProcessNames!))
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

    /// <summary>
    /// Does the machine have room for the WHOLE candidate list, blocked rows included, before any of it is
    /// queued? An installer needs the download plus the unpacked copy, so
    /// <see cref="Services.Install.DiskSpaceCheck"/> doubles the known sizes; the smaller of the system
    /// drive and the temp drive's free space is what it is checked against, since most installers land
    /// their download in %TEMP% before unpacking to Program Files. Null when there is nothing to warn
    /// about, including when every row's size is unknown: <see cref="Services.Install.DiskSpaceVerdict.IsShort"/>
    /// is never true on a guess.
    /// </summary>
    private InstallPreflightLineViewModel? BuildDiskSpaceLine(IReadOnlyList<UpdateRowViewModel> candidates)
    {
        // The row's own figure, enrichment included: the source's scan often reports no size at all.
        var sizes = candidates.Select(row => row.DownloadSizeBytes).ToList();
        var freeSystem = SafeFreeBytes(Path.GetPathRoot(Environment.SystemDirectory));
        var freeTemp = SafeFreeBytes(Path.GetPathRoot(Path.GetTempPath()));
        var verdict = Services.Install.DiskSpaceCheck.Evaluate(sizes, freeSystem, freeTemp);

        if (!verdict.IsShort)
            return null;

        // Unknown sizes are named separately, never folded into the required-bytes figure: that number
        // only ever reflects what is actually known.
        var text = string.Format(Loc["InstallSpaceShort"], FormatBytes(verdict.FreeBytes), FormatBytes(verdict.RequiredBytes));
        if (verdict.UnknownCount > 0)
            text += " " + string.Format(Loc["InstallSpaceUnknown"], verdict.UnknownCount);

        return new InstallPreflightLineViewModel(
            text, isCritical: true, actionText: Loc["InstallSpaceOpenCleanup"], action: ShowCleanup);
    }

    /// <summary>A drive that cannot be read (a removed root, a permissions quirk) must never manufacture
    /// a false "short" warning; treated as effectively unlimited, same fail-open contract as every other
    /// environment probe in this app.</summary>
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

    /// <summary>
    /// Metered plus at least one known size greater than zero warns with the total size; starting
    /// stays available regardless, this only informs. A batch with no known sizes at all (every row a
    /// launcher hand-off, say) has nothing worth quoting a total for, so it stays silent even while
    /// metered.
    /// </summary>
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

    /// <summary>No setting, no blocking, just a line when the battery is genuinely low and
    /// nothing is plugged in. <see cref="Services.Install.PowerPrecondition.ShouldWarn"/> never guesses:
    /// an unknown line status or an unreported percent means no warning.</summary>
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

    /// <summary>
    /// Opens the real pre-flight window (owned by whichever window is active, same rule
    /// <see cref="Dialogs.ShowCore"/> uses) and returns whether Start was pressed. Test seam first;
    /// failing that, <see cref="Dialogs.SuppressInteractiveDialogs"/> (headless test runs) returns the
    /// safe/no-op Cancel rather than opening a real window that nothing would ever click.
    /// </summary>
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

    /// <summary>
    /// "Programm schließen und fortfahren": ask nicely first (<c>CloseMainWindow</c>, up to 10 s, polled;
    /// never a blocking <c>Thread.Sleep</c> on a UI-reachable path), and only on a SECOND, separate
    /// confirmation (never silently), <c>Kill()</c> whatever is still standing. Returns true only when no
    /// blocking process is left, so a declined force-kill or a refused Kill never lets the install start
    /// against a program that is still open.
    /// </summary>
    internal static async Task<bool> CloseBlockingProcessesAsync(
        IReadOnlyList<string> processNames,
        Func<string, IReadOnlyList<System.Diagnostics.Process>>? processLookup = null,
        TimeSpan? gracePeriod = null)
    {
        var stillRunning = new List<System.Diagnostics.Process>();
        try
        {
            return await CloseBlockingProcessesCoreAsync(
                processNames, stillRunning, processLookup ?? SafeProcessesByName, gracePeriod ?? TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var process in stillRunning)
                process.Dispose();
        }
    }

    /// <summary>True when the process's image lies under the Windows folder. A path that cannot be read
    /// counts as false, so such a process is handled exactly as before.</summary>
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

    internal static bool IsUnderWindowsFolder(string? path, string? windowsFolder) =>
        !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(windowsFolder)
        && path.StartsWith(windowsFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> CloseBlockingProcessesCoreAsync(
        IReadOnlyList<string> processNames,
        List<System.Diagnostics.Process> stillRunning,
        Func<string, IReadOnlyList<System.Diagnostics.Process>> processLookup,
        TimeSpan gracePeriod)
    {
        var exempt = new List<System.Diagnostics.Process>();
        foreach (var name in processNames)
        {
            // Re-checked HERE, not only where the names were detected: this is the step that actually
            // reaches a live process, and a name resolves to whatever is running under it right now.
            // Killing this app itself would abandon the very install it is clearing the way for.
            if (Services.Install.InstallBlockers.IsNeverClosed(name))
                continue;

            foreach (var process in processLookup(name))
            {
                // Every handle is disposed by the caller's finally, the skipped ones included.
                stillRunning.Add(process);

                if (process.Id == Environment.ProcessId)
                    continue;

                // A short, common name ("ssh", "bash") also belongs to Windows' own OpenSSH and WSL: those run
                // from the Windows folder, which no update writes to, so they are never in the way and never closed.
                if (RunsFromWindowsFolder(process))
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
                    // Already gone, or no main window to ask nicely; the wait/force-kill pass below still
                    // checks HasExited itself, so this is not fatal to the flow.
                }
            }
        }

        // This app's own process was only collected for disposal, never asked to close.
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
                // Best effort; a process that exited or refuses to be killed between the check and here
                // must not abort the rest of the batch.
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
            // Every session's processes come back; only this user's session is one the install can be
            // blocked by and one this app may close, so the rest are released at once.
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
            // Cannot ask (access denied, protected process): it may well still be running, and the caller
            // is deciding whether the program that blocks an install is gone. Fail closed, so the row stays
            // blocked with its usual text rather than installing over a live program.
            return false;
        }
    }

    /// <summary>
    /// Unattended path: no dialog, ever. The row is not silently dropped; it is marked failed with the
    /// reason, recorded in history exactly like any other install outcome, so it does not vanish with no
    /// trace.
    /// </summary>
    private void SkipBlockedRowUnattended(UpdateRowViewModel row, string blockingProgramName)
    {
        var message = string.Format(Loc["InstallBlockerAutoSkipped"], blockingProgramName);

        row.BeginInstall();
        row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, message, false, message));

        _history.Append(HistoryEntry.ForItem(row.Item, InstallAction.Install, success: false, error: message));
    }

    /// <summary>
    /// Interactive path: the user picked "skip blocked", or "close and continue" did not get the program
    /// closed (a declined force close, a survivor). The rows are not silently dropped: each shows
    /// Cancelled with the reason and gets one cancelled history entry, so the table and the history say
    /// why nothing happened. Not a failure: the user (or the program still being open) decided, nothing
    /// went wrong.
    /// </summary>
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

    /// <summary>Completed when rows join the queue while <see cref="RunInstallLoopAsync"/> runs, so they start
    /// right away. Null while no loop runs.</summary>
    private TaskCompletionSource? _installWake;

    private async Task RunInstallLoopAsync()
    {
        // The batch that owned the shared state has ended and only a side uninstall keeps it up: this loop
        // takes ownership back instead of leaving the new rows waiting for that uninstall to finish.
        var takingOver = _installActivityRunning && _ownerEnded && _installWake is null;
        if (_installActivityRunning && !takingOver)
        {
            // A running loop starts the new rows at once, beside the run already going.
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

        // Pairs with BatchFinished in the finally below, so a run's total duration is on record and a
        // "it hung for a minute" report is provable.
        _logging.Event(ActivityLog.Install, ActivityLog.BatchStarted(
            _installPending.Count, _installPending.Any(r => InstallQueue.NeedsElevation(r.SourceId))));

        try
        {
            // Rows queued while a run is going start their own run straight away instead of waiting for the
            // first one to finish: the queue's lanes are shared across runs, so a package manager still never
            // runs two of its own installs at once, while every other lane gets going immediately.
            var running = new List<Task>();
            while (!_installCts.IsCancellationRequested)
            {
                // Set up before the pending rows are taken, so a row queued from here on wakes the wait below.
                _installWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var snapshot = _installPending.ToList();
                _installPending.Clear();

                if (snapshot.Count > 0)
                {
                    // ONE call per turn, not one per elevation half: InstallQueue.RunAsync already splits by
                    // elevation and starts both halves together internally, and its automatic retry round runs
                    // once, after both halves of that same turn. Admin rows of one turn go out in ONE elevated batch.
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
                        // One turn failing must not end the others still running beside it.
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
            // The top of the install loop: whatever failed, the finally below still has to close out every
            // pending/held row rather than leave them stuck reading "queued" forever.
            _logging.Error("Install batch failed", ex);
        }
        finally
        {
            _installWake = null;

            // Rows that were still waiting when Stop was pressed never ran, and clearing the list silently
            // left them showing "queued" for ever. They are closed out here; the only
            // place that knows they never reached the queue. Admin-scope rows held back for the elevated
            // batch are the same story: a cancellation while the user-scope phase was still running never
            // reaches the loop iteration that would have dispatched or cleared them.
            // After a Stop, CancelInstall already settled every row waiting at that moment; what is pending
            // now was queued during the unwind and is handed to a fresh batch below instead.
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

            // Surface any "installed, reboot pending" results once the batch is done.
            var rebootingRows = InstallBatch.Where(r => r.NeedsReboot).ToList();
            if (rebootingRows.Count > 0)
            {
                ShowRebootNotice(rebootingRows.Count);

                ShowRebootDialogIfNeeded(rebootingRows.Select(r => r.Name).ToList());
            }

            // Held open by a running uninstall: that one hands the pending rows on when it ends.
            if (stopped && released)
                HandOffPendingInstalls();
        }
    }

    [ObservableProperty]
    private bool rebootRequiredNotice;

    private int _rebootPendingCount;

    /// <summary>Built on every read from the stored count, so a language switch only has to re-raise it.</summary>
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

    /// <summary>The packages that asked for a restart in the batch that just finished. Read by
    /// <see cref="ShowRebootDialogIfNeeded"/> and exposed for whatever else wants the list (a future
    /// notification detail, a test).</summary>
    internal IReadOnlyList<string> RebootRequestedBy { get; private set; } = [];

    /// <summary>Test seam: replaces the real two-button dialog, returning whether "restart now" was
    /// chosen. Null in production, where <see cref="ShowRebootDialog"/> opens the real
    /// <see cref="Views.MessageDialogWindow"/> through <see cref="Dialogs.Confirm"/>.</summary>
    internal Func<IReadOnlyList<string>, bool>? RebootDialogOverride { get; set; }

    /// <summary>Test seam: default reads the real <c>Application.Current.MainWindow.IsVisible</c>, which
    /// reflects whether the window genuinely happens to be hidden to tray on the host it runs on.
    /// Constructing and showing a real <see cref="Window"/> against the shared test-process
    /// <see cref="Application"/> proved unsafe (it corrupted later tests' own windows), so this is a plain
    /// probe rather than a live window, the same shape as <see cref="IsMeteredOverride"/>.</summary>
    internal Func<bool>? MainWindowVisibleOverride { get; set; }

    /// <summary>
    /// Once per batch (called only from <see cref="RunInstallLoopAsync"/>'s <c>finally</c>, never
    /// mid-batch), and never onto a window nobody can see: hidden to tray gets the banner plus one balloon
    /// instead, no reminder option and never an automatic restart, so a hidden run is never left to
    /// restart itself unattended. Internal, not private: like <see cref="ScanScheduler.TickAsync"/>, a test
    /// calls it directly instead of driving a whole batch through a mock source that cannot report
    /// <c>RebootRequired</c>.
    /// </summary>
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

    /// <summary>Full path resolution like every other external call here (ProcessRunner.Resolve); fire
    /// and forget, the restart itself is imminent either way.</summary>
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

    /// <summary>Stops the batch. Rows still waiting are settled right here, as cancelled: the loop only
    /// notices the token at its next turn, and rows the user queues while the batch is still unwinding (an
    /// installer being killed, the elevated worker shutting down) belong to a NEW batch, not to this Stop.</summary>
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

    /// <summary>
    /// Called from every solo activity's (interactive retry, uninstall) <c>finally</c> block, right after
    /// it clears <see cref="_installActivityRunning"/>: a per-row Install click that landed on a
    /// different row while the activity was running never started a second loop (see
    /// <see cref="_installActivityRunning"/>'s doc); its row just sits in <see cref="_installPending"/>
    /// instead. This is what actually installs it, now that the shared machinery is free again.
    /// </summary>
    private void HandOffPendingInstalls()
    {
        if (_installPending.Count > 0)
            _ = Background.Run(RunInstallLoopAsync(), _logging, "install loop");
    }

    /// <summary>The batch that is running, in the order it will be installed.</summary>
    public ObservableCollection<UpdateRowViewModel> InstallBatch { get; } = [];

    private DispatcherTimer? _installTimer;
    private DateTimeOffset _installStartedAt;

    [ObservableProperty]
    private string installElapsedText = "0:00";

    // HandedOff belongs here as much as the other two: it is a terminal state, so leaving it out had the
    // strip read "0 of 1" next to a bar already sitting at 100 % after a handoff to Steam. NeedsManualWork
    // is terminal too (a row there is never Idle again); a three-strikes row mid-batch was left out of
    // the count for ever, so the strip could get permanently stuck at "2 of 3" or similar. Cancelled
    // (a row the user stopped) is terminal for the same reason.
    public int InstallDoneCount => InstallBatch.Count(r => IsTerminalInstallState(r.InstallState));

    private static bool IsTerminalInstallState(RowInstallState state) =>
        state is RowInstallState.Succeeded or RowInstallState.Failed or RowInstallState.HandedOff
            or RowInstallState.Stuck or RowInstallState.NeedsManualWork or RowInstallState.Cancelled;

    public int InstallTotalCount => InstallBatch.Count;

    // A finished row is a finished share of the work whatever its outcome: a failed or handed-off row left
    // at its last reported percent kept the bar from ever reaching 100 % at the end of a batch. A row that
    // is installing has its whole download behind it and counts as that, but never as finished: only a
    // terminal state takes a share to 100, so the figure cannot read complete while a row still works.
    public double InstallOverallPercent =>
        InstallBatch.Count == 0
            ? 0
            : InstallBatch.Average(r => IsTerminalInstallState(r.InstallState)
                ? 100
                : r.Phase == InstallPhase.Installing ? 99 : Math.Min(99, r.InstallPercent));

    // No figure while nothing in the batch has a real one: the strip shows the moving bar alone.
    public string InstallOverallPercentText =>
        IsInstalling && !InstallHasRealProgress ? string.Empty : PercentFormat.Format(InstallOverallPercent);

    /// <summary>False while no running row has a real download figure (all installing, or nothing
    /// measurable reported yet); the install band shows a wandering shine instead of a bar stuck at 0.</summary>
    public bool InstallHasRealProgress => InstallBatch.Any(r =>
        (r.HasRealProgress && r.Phase != InstallPhase.Installing) || r.InstallState == RowInstallState.Succeeded);

    public string InstallCountText =>
        string.Format(Loc["InstallProgressCount"], InstallDoneCount, InstallTotalCount);

    /// <summary>
    /// Batch-wide throughput for the install strip: total size, how much has come down, and how fast right
    /// now; three separate figures (total top-left, downloaded below it, speed to its right), each shown
    /// only when it has a real number, so a batch that reports no bytes (a launcher hand-off) shows nothing
    /// rather than a zero. Not gated on batch size: a single queued item reports exactly like a batch of
    /// ten. Deliberately excludes the free space on the system drive, which has nothing to do with the
    /// batch.
    /// </summary>
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

    /// <summary>
    /// The queue panel's own row order; separate from <see cref="InstallBatch"/> (whose order reflects
    /// when rows were added, not the user's drag-and-drop) so a reorder never has to reason about where
    /// running or finished rows sit. Rebuilt incrementally: rows that left the Queued state drop out, new
    /// ones append at the end, and everything else keeps whatever order the user last set.
    /// </summary>
    public ObservableCollection<UpdateRowViewModel> QueueRows { get; } = [];

    [ObservableProperty]
    private bool isQueuePanelExpanded;

    public bool HasQueuedRows => QueueRows.Count > 0;

    public string QueuePanelHeaderText => string.Format(Loc["InstallQueueHeader"], QueueRows.Count);

    /// <summary>Sits right-aligned beside the header text; the queue's own combined download size, empty
    /// (not "0 B") when nothing in it reports one.</summary>
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

    /// <summary>Same incremental rebuild <see cref="SyncQueueRows"/> already does for the queue panel,
    /// applied to the running-rows strip: rows that stopped running drop out, newly running ones append,
    /// everything else is left untouched instead of the whole list being thrown away and rebuilt.</summary>
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

    /// <summary>
    /// Pushes the queue panel's current order down into whatever is actually holding those rows back;
    /// the pending list for rows not yet handed to the queue, and the queue itself for rows
    /// already inside a running <see cref="InstallQueue.RunAsync"/> call. Either is a no-op for rows it
    /// does not recognise, so calling both unconditionally is safe.
    /// </summary>
    private void PushQueueOrder()
    {
        var order = QueueRows.ToList();
        if (order.Count == 0)
            return;

        // Rows the panel does not list (the folded-away twins of a shown row) sort behind the listed ones;
        // OrderBy is stable, so they keep their relative order.
        var position = new Dictionary<UpdateRowViewModel, int>();
        for (var i = 0; i < order.Count; i++)
            position[order[i]] = i;

        var reordered = _installPending.OrderBy(r => position.GetValueOrDefault(r, int.MaxValue)).ToList();
        _installPending.Clear();
        _installPending.AddRange(reordered);

        _installQueue.Reorder(order.Select(r => (r.Item.SourceId, r.Item.Id)).ToList());
    }

    /// <summary>Called by the queue panel's drag-and-drop drop handler with the row's old and new index.</summary>
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

        // The row goes only once something has really let go of it: either the pending list, which still
        // holds the rows the loop has not handed over yet, or the queue itself. A queue that
        // says no has already handed the item to the elevated worker; taking the row away then would tell
        // the user it was cancelled while it carries on installing.
        if (!_installPending.Remove(row) && !_installQueue.Remove(row.Item.SourceId, row.Item.Id))
            return;

        QueueRows.Remove(row);
        InstallBatch.Remove(row);

        row.MarkNotInstalled(Loc["InstallQueueItemRemoved"]);

        // The folded-away copies were queued together with this row (see Expand) and have no queue line
        // of their own to remove them by, so they leave with it, under the same "really let go" rule.
        foreach (var partner in row.MergedPartners.Where(p => p.InstallState == RowInstallState.Queued && !QueueRows.Contains(p)))
        {
            if (!_installPending.Remove(partner) && !_installQueue.Remove(partner.Item.SourceId, partner.Item.Id))
                continue;

            InstallBatch.Remove(partner);
            partner.MarkNotInstalled(Loc["InstallQueueItemRemoved"]);
        }

        RefreshInstallProgress();
    }

    /// <summary>What the window puts at the top: the package actually being worked on.</summary>
    public UpdateRowViewModel? CurrentInstallRow =>
        InstallBatch.FirstOrDefault(r => r.InstallState == RowInstallState.Running)
        ?? InstallBatch.FirstOrDefault(r => r.InstallState == RowInstallState.Queued);

    public bool HasCurrentInstall => CurrentInstallRow is not null;

    /// <summary>Every row currently installing, in batch order: the strip's own per-row list
    /// once more than one row is running at the same time (which can happen now). Rebuilt incrementally
    /// by <see cref="SyncRunningRows"/> (same pattern as <see cref="QueueRows"/>), not replaced wholesale
    /// every tick: a brand new list every 250ms gave the bound ItemsControl a full reset instead of the
    /// handful of Add/Remove notifications a running batch actually produces.</summary>
    public ObservableCollection<UpdateRowViewModel> RunningRows { get; } = [];

    public int RunningCount => RunningRows.Count;

    /// <summary>The strip's own switch: its single-row look while at most one row runs (unchanged
    /// from before multiple rows could run at once), the compact multi-row list once a second one starts alongside it.</summary>
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

    /// <summary>Starts the elapsed/throughput ticker for whatever is in <see cref="InstallBatch"/>.</summary>
    private void BeginInstallProgress()
    {
        _installStartedAt = DateTimeOffset.UtcNow;

        _installTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            // 250ms: the aggregate batch figures (MB/s, elapsed) only move on this tick, and a slower
            // interval reads as visibly static next to how often a real per-row report can arrive.
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _installTimer.Tick -= OnInstallTimerTick;
        _installTimer.Tick += OnInstallTimerTick;
        _installTimer.Start();

        RefreshInstallProgress();
    }

    private void OnInstallTimerTick(object? sender, EventArgs e)
    {
        // Ends a finished download's pause and moves each running row's elapsed time before the figures
        // are recomputed.
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

    /// <summary>UI cue: fires once the batch's own retry round actually starts.</summary>
    private void OnSecondAttemptStarted() => _dispatcher.Invoke(() => IsSecondAttemptActive = true);

    private void OnInstallStatusChanged(InstallItemStatus status)
    {
        var key = $"{status.SourceId}:{status.ItemId}";
        var rounded = Math.Floor(status.Percent);

        // Only a plain, mid-install progress tick may be dropped; a terminal result (Success/Error set,
        // or a manual handoff) always crosses immediately, so a chatty tool's redraw storm can never
        // delay the one report the user is actually waiting for.
        var isTerminal = status.IsTerminal || status.Error is not null;

        lock (_lastDispatchedInstallStatus)
        {
            if (!isTerminal
                && _lastDispatchedInstallStatus.TryGetValue(key, out var last)
                && last.Percent == rounded && last.Status == status.Status)
            {
                return;
            }

            if (isTerminal)
                _lastDispatchedInstallStatus.Remove(key);
            else
                _lastDispatchedInstallStatus[key] = (rounded, status.Status);
        }

        _dispatcher.Invoke(() =>
        {
            // Source included: ids are unique per source only, and chocolatey/scoop/pip/npm share
            // package names, so matching on the id alone would report two installs onto one row.
            // Children too: Steam's parent row carries id "steam", and every actual game
            // update rides as a CHILD with its own id; searching only the top level would leave a
            // Steam child's install/uninstall progress or manual-handoff result never reaching its row.
            var row = FindRow(status.SourceId, status.ItemId);
            if (row is null)
                return;

            row.ApplyInstallStatus(status);
            if (isTerminal)
                TrackPersistentFailure(row);

            // WindowsUpdateSource.Install throws this exact text only once the update's own GUID is
            // genuinely gone from a fresh search, never for a mere revision bump (WindowsUpdateMatching
            // already handles that case without failing). The row is stale either way it arrived here (an
            // in-process install or the elevated worker's own "result" message both funnel through this
            // same status), so the source is rescanned on its own rather than making the user do it by hand.
            if (status.Success == false
                && string.Equals(status.SourceId, "windowsupdate", StringComparison.Ordinal)
                && status.Detail?.Contains("is no longer offered", StringComparison.Ordinal) == true)
            {
                _ = Background.Run(RescanSingleSourceAsync(status.SourceId), _logging, "rescan single source");
            }
        });
    }
}
