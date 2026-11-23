using System.IO;
using UltimateUpdater.Services.Install;

namespace UltimateUpdater.Services.Uninstall;

public enum UninstallOutcome
{
    Removed,
    Failed,
    NeedsRestart,

    /// <summary>The run was stopped before this entry's turn.</summary>
    NotStarted,
}

public enum UninstallEntryState
{
    Waiting,
    Running,
    Done,
}

/// <summary>What running one removal reported: the program's exit code (null when it never ran to an exit) and the
/// tool's own output tail for the details view.</summary>
public sealed record UninstallExecutionResult(int? ExitCode, string? Detail = null, bool Cancelled = false);

/// <summary>Where a removal runs: the elevated worker for machine entries, this process for the rest.</summary>
public interface IUninstallExecutor
{
    Task<UninstallExecutionResult> RunAsync(InstalledProgramEntry entry, UninstallPlan plan, CancellationToken cancellationToken);
}

/// <summary>The end of one entry: what happened and, for a failure, why in the user's words.</summary>
public sealed record UninstallEntryResult(
    InstalledProgramEntry Entry, UninstallOutcome Outcome, string? Reason = null, string? Detail = null, int? ExitCode = null);

/// <summary>One state change of one entry, for the window's row. May arrive from any thread.</summary>
public sealed record UninstallEntryUpdate(InstalledProgramEntry Entry, UninstallEntryState State, UninstallEntryResult? Result);

/// <summary>
/// One run for every ticked entry. Machine-scope entries and user-scope setups run one at a time through one shared
/// gate (Windows Installer holds one machine-wide lock that a per-user MSI takes as well, and most setups collide),
/// each kind in its own order; Store apps run up to three at a time beside them. A removal that shows the vendor's own
/// window is held back until every silent one has finished and then runs alone, one at a time. A stop lets the
/// running removals finish and starts no further entry.
/// After each removal the entry is looked up again: the program being gone is the truth, the exit code only
/// explains a failure. An entry the planner refused fails at once with its reason and never reaches an executor.
/// </summary>
public sealed class UninstallRunner
{
    /// <summary>The most Store apps removed at the same time.</summary>
    public const int MaxParallelStoreApps = 3;

    private readonly Func<InstalledProgramEntry, UninstallPlan> _plan;
    private readonly IUninstallExecutor _machineExecutor;
    private readonly IUninstallExecutor _userExecutor;
    private readonly Func<InstalledProgramEntry, CancellationToken, Task<bool>> _isGone;
    private readonly Func<string, string> _lookup;
    private readonly TimeSpan _lateRemovalWindow;
    private readonly TimeSpan _lateRemovalPoll;
    private volatile bool _stopRequested;

    /// <summary>How long a setup recognised by its files (or shown to a person) is given, after it has ended, to
    /// finish removing the entry: some hand the work to a process the first one does not wait for.</summary>
    public static readonly TimeSpan DefaultLateRemovalWindow = TimeSpan.FromSeconds(60);

    public UninstallRunner(
        Func<InstalledProgramEntry, UninstallPlan> plan,
        IUninstallExecutor machineExecutor,
        IUninstallExecutor userExecutor,
        Func<InstalledProgramEntry, CancellationToken, Task<bool>> isGone,
        Func<string, string> lookup,
        TimeSpan? lateRemovalWindow = null,
        TimeSpan? lateRemovalPoll = null)
    {
        _lateRemovalWindow = lateRemovalWindow ?? DefaultLateRemovalWindow;
        _lateRemovalPoll = lateRemovalPoll ?? TimeSpan.FromSeconds(3);
        _plan = plan;
        _machineExecutor = machineExecutor;
        _userExecutor = userExecutor;
        _isGone = isGone;
        _lookup = lookup;
    }

    public bool StopRequested => _stopRequested;

    /// <summary>No new entry starts; the ones running finish. The entries not started come back as NotStarted.</summary>
    public void Stop() => _stopRequested = true;

    /// <param name="hardToken">Ends the running removals too (the app shuts down); a normal stop uses <see cref="Stop"/>.</param>
    public async Task<IReadOnlyList<UninstallEntryResult>> RunAsync(
        IReadOnlyList<InstalledProgramEntry> entries, Action<UninstallEntryUpdate>? onUpdate, CancellationToken hardToken)
    {
        var results = new UninstallEntryResult?[entries.Count];
        var plans = new UninstallPlan[entries.Count];
        var machine = new List<int>();
        var userSetups = new List<int>();
        var storeApps = new List<int>();
        var interactive = new List<int>();

        void Report(int index, UninstallEntryState state)
            => onUpdate?.Invoke(new UninstallEntryUpdate(entries[index], state, results[index]));

        void Finish(int index, UninstallEntryResult result)
        {
            results[index] = result;
            Report(index, UninstallEntryState.Done);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            Report(i, UninstallEntryState.Waiting);
            plans[i] = _plan(entries[i]);
            if (!plans[i].IsRunnable)
            {
                Finish(i, new UninstallEntryResult(entries[i], UninstallOutcome.Failed, RefusalReason(plans[i].Refusal)));
                continue;
            }

            if (plans[i].Interactive)
                interactive.Add(i);
            else if (plans[i].Method == UninstallMethod.StoreApp)
                storeApps.Add(i);
            else if (plans[i].Elevated)
                machine.Add(i);
            else
                userSetups.Add(i);
        }

        async Task RunOneAsync(int index)
        {
            if (_stopRequested || hardToken.IsCancellationRequested)
            {
                Finish(index, new UninstallEntryResult(entries[index], UninstallOutcome.NotStarted, _lookup("UninstallNotStarted")));
                return;
            }

            Report(index, UninstallEntryState.Running);
            Finish(index, await RemoveAsync(entries[index], plans[index], hardToken));
        }

        // One setup at a time across the machine lane and the user lane.
        using var setupGate = new SemaphoreSlim(1);
        async Task RunSetupsInOrderAsync(IEnumerable<int> indices)
        {
            foreach (var index in indices)
            {
                await setupGate.WaitAsync();
                try
                {
                    await RunOneAsync(index);
                }
                finally
                {
                    setupGate.Release();
                }
            }
        }

        using var storeGate = new SemaphoreSlim(MaxParallelStoreApps);
        async Task RunStoreAppAsync(int index)
        {
            await storeGate.WaitAsync();
            try
            {
                await RunOneAsync(index);
            }
            finally
            {
                storeGate.Release();
            }
        }

        // The silent lanes run side by side, the vendor windows only afterwards and alone.
        await Task.WhenAll(
            RunSetupsInOrderAsync(machine),
            RunSetupsInOrderAsync(userSetups),
            Task.WhenAll(storeApps.Select(RunStoreAppAsync)));
        foreach (var index in interactive)
            await RunOneAsync(index);

        return results!;
    }

    private async Task<UninstallEntryResult> RemoveAsync(InstalledProgramEntry entry, UninstallPlan plan, CancellationToken hardToken)
    {
        UninstallExecutionResult run;
        try
        {
            run = await (plan.Elevated ? _machineExecutor : _userExecutor).RunAsync(entry, plan, hardToken);
        }
        catch (OperationCanceledException)
        {
            return new UninstallEntryResult(entry, UninstallOutcome.Failed, _lookup("UninstallCancelled"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return new UninstallEntryResult(entry, UninstallOutcome.Failed, _lookup("UninstallCouldNotStart"), ex.Message);
        }

        if (run.Cancelled)
            return new UninstallEntryResult(entry, UninstallOutcome.Failed, _lookup("UninstallCancelled"), run.Detail, run.ExitCode);

        if (run.ExitCode is not { } exitCode)
            return new UninstallEntryResult(entry, UninstallOutcome.Failed, _lookup("UninstallCouldNotStart"), run.Detail);

        bool gone;
        try
        {
            gone = await _isGone(entry, hardToken);
        }
        catch (OperationCanceledException)
        {
            gone = false;
        }

        if (!gone && plan.Method is UninstallMethod.SilentSwitch or UninstallMethod.Interactive && exitCode is not (3010 or 1641))
            gone = await WaitForLateRemovalAsync(entry, hardToken);

        if (exitCode is 3010 or 1641)
            return new UninstallEntryResult(entry, UninstallOutcome.NeedsRestart, _lookup("UninstallNeedsRestart"), run.Detail, exitCode);

        // A setup that deletes itself often exits with a code of its own; the entry being gone settles it.
        if (gone)
            return new UninstallEntryResult(entry, UninstallOutcome.Removed, null, run.Detail, exitCode);

        return new UninstallEntryResult(entry, UninstallOutcome.Failed, FailureReason(plan, exitCode, run.Detail), run.Detail, exitCode);
    }

    /// <summary>Asks again until the entry is gone or the window is over. A list that cannot be read, or a
    /// cancel, ends the wait with "not gone".</summary>
    private async Task<bool> WaitForLateRemovalAsync(InstalledProgramEntry entry, CancellationToken hardToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (watch.Elapsed < _lateRemovalWindow)
            {
                await Task.Delay(_lateRemovalPoll, hardToken);
                if (await _isGone(entry, hardToken))
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }

    /// <summary>The reason for a removal that ran but left the entry installed: the program's exit code in the
    /// words of the table below, winget's own output through the install explainer, else the bare code.</summary>
    internal string FailureReason(UninstallPlan plan, int exitCode, string? detail)
    {
        if (exitCode == 0)
            return _lookup("UninstallStillPresent");

        // 1602 Windows Installer and 1223 Win32: the person closed or declined the setup.
        if (exitCode is 1602 or 1223)
            return _lookup("UninstallCancelled");

        // 1618: another installation holds the machine-wide installer lock.
        if (exitCode == 1618)
            return _lookup("UninstallBusy");

        // 1625: a Software Restriction or AppLocker policy forbids the removal.
        if (exitCode == 1625)
            return _lookup("UninstallBlockedByPolicy");

        // pnputil refuses a package a device still uses (no /force); the code alone says nothing, the cause is named.
        if (plan.Method == UninstallMethod.Driver)
            return string.Format(_lookup("UninstallDriverKept"), exitCode);

        if (plan.Method == UninstallMethod.Winget
            && InstallErrorExplainer.Explain("winget", detail)?.Format(_lookup) is { Length: > 0 } explained)
            return explained;

        return string.Format(_lookup("UninstallErrorCode"), exitCode);
    }

    private string RefusalReason(UninstallRefusal refusal) =>
        _lookup(refusal == UninstallRefusal.UntrustedLocation ? "UninstallRefusedUntrusted" : "UninstallNotRemovable");
}
