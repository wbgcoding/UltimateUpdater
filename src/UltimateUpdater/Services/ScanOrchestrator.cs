using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

public enum ProviderScanState
{
    Waiting,
    Scanning,
    Done,
    Error,

    /// <summary>Stopped by its own cancel button: distinct from <see cref="Done"/>, which is
    /// also what a source shows when the WHOLE scan was stopped. Only this one is a deliberate,
    /// single-source cancel; the rest of the scan is unaffected and its result stays valid.</summary>
    Cancelled,

    /// <summary>The source's own tool is not on this machine (<c>IsAvailableAsync</c> returned
    /// false), set BEFORE its scan body ever runs (an absent tool's scan body never runs at all).
    /// Distinct from the silent <see cref="Done"/>, so a pill can be told apart from "ran and found
    /// nothing" and hidden on request.</summary>
    NotInstalled,
}

public sealed class ProviderStatus
{
    public required string SourceId { get; init; }
    public ProviderScanState State { get; set; }
    public int FoundCount { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The raw reason, English and unlocalized: shown only in the pill's hover tooltip, never inline in
    /// the pill itself. <see cref="ErrorMessage"/> stays the translated one-liner everyone sees at a glance.
    /// </summary>
    public string? Detail { get; set; }
}

/// <summary>Runs all active providers in parallel; one provider's failure never kills the overall scan.</summary>
public sealed class ScanOrchestrator
{
    private readonly IReadOnlyList<IUpdateSource> _sources;
    private readonly LoggingService _logging;
    private readonly Func<string, bool>? _isEnabled;
    private readonly Func<bool> _isOnline;
    private readonly Func<int>? _maxParallelSources;

    public event Action<UpdateItem>? ItemFound;

    public event Action<ProviderStatus>? ProviderStatusChanged;

    /// <param name="isEnabled">
    /// Which sources the user still wants scanned. A source switched off is skipped entirely: no
    /// probe, no pill, no results, so switching one off also stops paying for it.
    /// </param>
    /// <param name="isOnline">
    /// Testing seam for <see cref="NetworkAvailability.IsOnline"/>, the real default: a test can inject
    /// a fixed answer instead of depending on whatever the machine running it is actually connected to.
    /// </param>
    /// <param name="maxParallelSources">
    /// How many sources may scan at once, read fresh at the start of every
    /// <see cref="RunAsync"/> call rather than fixed at construction, so a slider moved in the settings
    /// window takes effect on the NEXT scan without needing a restart. Left null (the default, and every
    /// existing caller before this item) means unbounded, exactly the prior behaviour; the real caller
    /// passes <c>() => settings.Current.Scan.MaxParallelSources</c>. Never trusted blindly: clamped to
    /// 1 to 16 here regardless of what it returns.
    /// </param>
    public ScanOrchestrator(
        IReadOnlyList<IUpdateSource> sources, LoggingService logging,
        Func<string, bool>? isEnabled = null, Func<bool>? isOnline = null,
        Func<int>? maxParallelSources = null)
    {
        _sources = sources;
        _logging = logging;
        _isEnabled = isEnabled;
        _isOnline = isOnline ?? NetworkAvailability.IsOnline;
        _maxParallelSources = maxParallelSources;
        SourceIds = sources.Select(s => s.Id).ToList();
    }

    /// <summary>Every source that exists, enabled or not: what the settings window offers.</summary>
    public IReadOnlyList<string> SourceIds { get; }

    /// <summary>
    /// Whether the most recent <see cref="RunAsync"/> found no internet connection at all. Read after
    /// that call finishes; every network source (<see cref="SourceRegistry.NeedsNetwork"/>) was skipped
    /// rather than scanned, so this is what tells the caller to show one banner instead of one pill's
    /// worth of "no connection" per source.
    /// </summary>
    public bool WasOffline { get; private set; }

    /// <summary>
    /// Set AFTER the most recent <see cref="RunAsync"/>: true when at least one source that
    /// <see cref="SourceRegistry.NeedsNetwork"/> actually ran (was not skipped for <see cref="WasOffline"/>
    /// or found not installed) and EVERY such source failed with a connection error specifically - a proxy
    /// refusal or a bare <see cref="HttpRequestException"/> reaching its target, tracked in
    /// <see cref="_connectionFailuresThisRun"/> by <see cref="ScanOneAsync"/>'s own catches. Distinct from
    /// <see cref="WasOffline"/>, which only means the SYSTEM reported no connection at all: a firewall rule
    /// blocking just this app leaves the system "online" while every network source still fails the same
    /// way.
    /// </summary>
    public bool AllOnlineSourcesFailed { get; private set; }

    /// <summary>Sources already found present: only the POSITIVE answer is cached, so a missing tool is
    /// still re-probed every scan.</summary>
    private readonly HashSet<string> _knownPresent = [];

    /// <summary>
    /// Which sources have already had a full stack trace written to the log this session. A source stuck
    /// failing the same way writes the same seven-line trace on every scan otherwise; only the FIRST
    /// failure earns the full trace, every later one gets the exception type and message alone.
    /// </summary>
    private readonly HashSet<string> _stackTraceLoggedFor = [];

    /// <summary>
    /// The last state actually reported for each source, kept across scans so a caller that
    /// was not there to see the live event (the settings window, opened long after the last scan finished)
    /// can still ask "was this one not installed". Updated in <see cref="Report"/>, from the SAME status
    /// object every pill already reacts to, so this can never disagree with what is on screen. Concurrent:
    /// several sources report from their own parallel tasks at once.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderScanState> _lastKnownStates = new();

    /// <summary>
    /// Whether the most recent report for this source (across any scan so far this session, not just the
    /// current one) was <see cref="ProviderScanState.NotInstalled"/>. False for a source never scanned yet,
    /// same "unknown counts as available" fail-open rule the rest of this class uses elsewhere.
    /// </summary>
    public bool IsKnownNotInstalled(string sourceId) =>
        _lastKnownStates.TryGetValue(sourceId, out var state) && state == ProviderScanState.NotInstalled;

    /// <summary>
    /// Which scan is the current one. A cancelled scan is ABANDONED rather than waited for (see RunAsync),
    /// so its providers are still running while the next scan is already filling the table, and their late
    /// finds and status events landed in it. That is what put a second copy of a Windows Update row on
    /// screen after a quick rescan, and what let a stale "done" overwrite a source that was scanning again.
    /// Every event is tagged with the scan it belongs to; anything from an older one is dropped.
    /// </summary>
    private int _generation;

    /// <summary>Totals for the run's summary line, added to from the parallel source tasks.</summary>
    private int _foundThisRun;

    private int _failedThisRun;

    /// <summary>Every network-needing source that actually reached its own scan body this run (not
    /// skipped for <see cref="WasOffline"/>, not found absent), and the subset of those that failed with a
    /// connection error specifically. Cleared at the start of every <see cref="RunAsync"/>; feeds
    /// <see cref="AllOnlineSourcesFailed"/> once the run finishes normally. Both a source id can be added
    /// to at most once each, so plain sets suffice.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _networkSourcesRanThisRun = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _connectionFailuresThisRun = new();

    /// <summary>
    /// One permit per source, so the SAME source is never scanned twice at once. Windows Update's COM search
    /// takes no cancellation token and runs for another quarter of a minute regardless, so a rescan started
    /// straight after a stop began a second search on the same instance while the first was still going.
    /// The new scan's provider waits for the old run to end instead: a late start, not a parallel one.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _perSourceLanes = new();

    /// <summary>
    /// One linked token source per source, alive only while that source is actually scanning this
    /// generation. Keyed by source id, not by generation: a stale entry from an earlier run
    /// simply gets overwritten when that source scans again, and <see cref="CancelSource"/> only ever
    /// acts on whatever is in here right now. Cancelling a source nobody is currently running is a no-op.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _perSourceCts = new();

    /// <summary>
    /// Stops exactly ONE source without touching the rest of the scan: the button on its pill.
    /// Unlike the overall Stop button, this is a real, observable cancellation: the source's own token
    /// trips, <see cref="ScanOneAsync"/> reports it as <see cref="ProviderScanState.Cancelled"/>, and every
    /// other source keeps running and keeps contributing to the still-valid overall result.
    /// </summary>
    public bool CancelSource(string sourceId)
    {
        if (!_perSourceCts.TryGetValue(sourceId, out var cts))
            return false;

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // the source finished (and disposed its token) in the moment between the lookup and the cancel
            return false;
        }
    }

    /// <summary>
    /// How long a new scan waits for the previous run of the same source, so a merely slow provider is
    /// never reported as failed for being slow. Has to clear the SLOWEST source, not the typical one:
    /// winget's own scan timeout is five minutes and it runs twice per scan (its own list, then the Store),
    /// so a shorter limit would give up on a source that is still perfectly healthy.
    /// </summary>
    private static readonly TimeSpan SourceLaneTimeout = TimeSpan.FromMinutes(12);

    /// <summary>The state a source ends in when a token tripped. The overall scan being stopped reads as done
    /// (the whole table is abandoned anyway); only this source's own cancel reads as cancelled.</summary>
    private static ProviderScanState CancelState(CancellationToken runToken) =>
        runToken.IsCancellationRequested ? ProviderScanState.Done : ProviderScanState.Cancelled;

    private bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

    /// <summary>
    /// Guards the generation bump in <see cref="RunAsync"/> against the check-then-invoke in the
    /// ItemFound path below: without a shared lock, an abandoned provider could pass its own
    /// <see cref="IsCurrent"/> check the instant before a new run bumps <see cref="_generation"/>, then
    /// publish a stale item into the table the new scan is filling (the exact bug that put a duplicate
    /// row on screen after a quick rescan). Held only for the bump itself and for one item's worth of
    /// check-and-invoke, never for a whole source's scan.
    /// </summary>
    private readonly object _generationLock = new();

    private async Task<bool> IsAvailableAsync(IUpdateSource source, CancellationToken cancellationToken)
    {
        lock (_knownPresent)
        {
            if (_knownPresent.Contains(source.Id))
                return true;
        }

        if (!await source.IsAvailableAsync(cancellationToken))
            return false;

        lock (_knownPresent)
            _knownPresent.Add(source.Id);

        return true;
    }

    /// <summary>
    /// Every provider runs on a background thread. Without Task.Run each provider would execute
    /// synchronously on the calling (UI) thread until its first real await, and availability
    /// probing alone hits the registry and the file system for every source, which visibly froze
    /// the window at the start of a scan.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int generation;
        lock (_generationLock)
            generation = ++_generation;
        var started = System.Diagnostics.Stopwatch.StartNew();

        // Counted across the sources, which run in parallel, and read once at the end for the summary line.
        Interlocked.Exchange(ref _foundThisRun, 0);
        Interlocked.Exchange(ref _failedThisRun, 0);
        _networkSourcesRanThisRun.Clear();
        _connectionFailuresThisRun.Clear();

        // Only a run that finishes assigns this, so a cancelled one would otherwise keep the previous
        // run's verdict and show a "no connection" banner that no longer describes anything.
        AllOnlineSourcesFailed = false;

        // Checked once per scan, off the calling thread (a COM activation, however quick, has no place
        // blocking a caller that may be the UI thread): every network source fails the same obvious way
        // instead of each one separately timing out against a connection that is already known to be
        // dead.
        WasOffline = !await Task.Run(_isOnline, cancellationToken);

        var enabled = _sources.Where(source => _isEnabled?.Invoke(source.Id) ?? true).ToList();

        // A fresh gate per run, never disposed: an abandoned scan's stragglers (see below) may still be
        // waiting on or releasing this after RunAsync itself has returned, and disposing out from under
        // them would turn a harmless late finish into an ObjectDisposedException. Same reasoning as
        // _perSourceLanes never being disposed either.
        var maxParallel = _maxParallelSources is null ? (int?)null : Math.Clamp(_maxParallelSources(), 1, 16);
        var gate = maxParallel is int cap ? new SemaphoreSlim(cap, cap) : null;

        var running = Task.WhenAll(enabled
            .Select(source => Task.Run(() => ScanOneAsync(source, generation, cancellationToken, WasOffline, gate), cancellationToken)));

        // A cancelled run RETURNS rather than waiting for the slowest source (Windows Update's COM call
        // takes no token).
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult()))
        {
            await Task.WhenAny(running, cancelled.Task);
        }

        if (running.IsCompleted)
        {
            await running;      // surfaces anything that faulted in a normal run

            // One line for the whole run: how much was asked, how much came back, how long it took. That
            // is what answers "was this machine actually checked, and when" months later.
            _logging.Event(
                ActivityLog.Scan,
                ActivityLog.ScanFinished(enabled.Count, _foundThisRun, _failedThisRun, started.Elapsed));

            AllOnlineSourcesFailed = !_networkSourcesRanThisRun.IsEmpty
                && _networkSourcesRanThisRun.Keys.All(id => _connectionFailuresThisRun.ContainsKey(id));

            return;
        }

        // Abandoned, but never unobserved: an unobserved faulted Task is a process-wide event.
        _ = running.ContinueWith(
            task => _logging.Info($"A cancelled scan finished in the background: {task.Exception?.Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Raises a status event only while this scan is still the current one. A provider abandoned by a
    /// cancelled scan keeps running and keeps reaching its own end, and its "done" then landed on top of
    /// the NEW scan's state for that source, count and all.
    /// </summary>
    internal void Report(ProviderStatus status, int generation)
    {
        // The check and the state write are ONE step under the same lock the generation bump takes: with
        // the check outside, a straggler could pass it, a new run could bump and report its own state for
        // this source, and the straggler's older state would then land on top of it.
        lock (_generationLock)
        {
            if (!IsCurrent(generation))
                return;

            ReportSeamForTest?.Invoke("locked");
            _lastKnownStates[status.SourceId] = status.State;
        }

        ReportSeamForTest?.Invoke("released");

        // The event itself is raised outside the lock (a subscriber must never run under it), so the
        // generation is checked once more right before: a run that started in the gap supersedes this one.
        if (!IsCurrent(generation))
            return;

        try
        {
            ProviderStatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            // A subscriber's own defect must not end the scan. The very first report happens outside
            // ScanOneAsync's try block and the last ones happen inside its catch, so an exception
            // thrown here would fault Task.WhenAll and take every other source down with it.
            _logging.Error($"A status subscriber threw while reporting '{status.SourceId}'", ex);
        }
    }

    /// <summary>Test-only: called with "locked" inside <see cref="Report"/>'s critical section (after the
    /// generation check, before the state write) and with "released" right after it, so a test can land a
    /// generation bump at exactly those two points.</summary>
    internal Action<string>? ReportSeamForTest { get; set; }

    internal int GenerationForTest => Volatile.Read(ref _generation);

    internal ProviderScanState? LastKnownStateForTest(string sourceId) =>
        _lastKnownStates.TryGetValue(sourceId, out var state) ? state : null;

    /// <summary>
    /// Scans exactly one source, outside of any full run, used when an install just discovered that a
    /// row it was handed is genuinely gone (WindowsUpdateSource: "no longer offered") and the stale row
    /// needs to disappear without waiting for the user's next manual rescan. Reports under whatever
    /// generation is CURRENT right now rather than starting a new one: bumping <see cref="_generation"/>
    /// here would make every OTHER source's still-running reports in a real scan look stale and get
    /// silently dropped by <see cref="Report"/>, which is a far worse bug than the one this fixes.
    /// </summary>
    public Task RescanSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        var source = _sources.FirstOrDefault(s => s.Id == sourceId);
        if (source is null)
            return Task.CompletedTask;

        var generation = Volatile.Read(ref _generation);
        return Task.Run(() => ScanOneAsync(source, generation, cancellationToken), cancellationToken);
    }

    private async Task ScanOneAsync(
        IUpdateSource source, int generation, CancellationToken cancellationToken, bool offline = false,
        SemaphoreSlim? globalGate = null)
    {
        var status = new ProviderStatus { SourceId = source.Id, State = ProviderScanState.Waiting };
        Report(status, generation);

        // Skipped before even touching its lane or availability probe: there is no point waiting on, or
        // eventually timing out, a source that can only ever fail with no connection at all. Local
        // sources are unaffected.
        if (offline && SourceRegistry.NeedsNetwork(source.Id))
        {
            status.State = ProviderScanState.Error;
            status.ErrorMessage = LocalizationService.Instance["ScanOfflineSource"];
            status.Detail = "No internet connection.";
            Report(status, generation);
            return;
        }

        // Linked, not the raw token: cancelling THIS source must trip only its own token, never the
        // overall one every other source is still awaiting. Registered under the source id so
        // CancelSource can find it; removed again in the finally below regardless of how this ends.
        using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _perSourceCts[source.Id] = sourceCts;
        var token = sourceCts.Token;

        // Everything this source runs for the scan, its probe and its lookups included, starts its tool
        // processes below normal priority so the scan never makes the user's own programs sluggish.
        using var scanScope = ScanScope.Enter();

        try
        {
            // Never two runs of one source at once: the previous scan's run of it may still be going. Bounded,
            // and generously: a source that is genuinely still busy after this has failed, and saying so is
            // better than a scan that never returns because one provider will not end.
            var lane = _perSourceLanes.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
            try
            {
                if (!await lane.WaitAsync(SourceLaneTimeout, token))
                {
                    _logging.Warn($"Scan of '{source.Id}' skipped: its previous run has still not finished.");
                    status.State = ProviderScanState.Error;
                    status.ErrorMessage = LocalizationService.Instance["ScanFailed"];
                    status.Detail = "The previous scan of this source had not finished yet.";
                    Report(status, generation);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // This scan was stopped while the previous run of this source was still finishing. The state
                // has to be reported here, or the pill stays on Waiting for ever. Same distinction as the
                // cancellation catch further down: the OVERALL token decides this is Done (the whole scan
                // stopped), this source's own token alone decides it is Cancelled.
                status.State = CancelState(cancellationToken);
                Report(status, generation);
                return;
            }

            // The overall "how many sources at once" cap, on top of the per-source lane just above (that
            // one only ever stops the SAME source overlapping itself). Waited for while already holding
            // the lane, so a source parked here still counts as "using its own slot" and cannot be
            // scanned again from a second concurrent call.
            if (globalGate is not null)
            {
                try
                {
                    await globalGate.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    lane.Release();
                    status.State = CancelState(cancellationToken);
                    Report(status, generation);
                    return;
                }
            }

            try
            {
                if (!await IsAvailableAsync(source, token))
                {
                    status.State = ProviderScanState.NotInstalled;
                    Report(status, generation);
                    return;
                }

                status.State = ProviderScanState.Scanning;
                Report(status, generation);

                // Marked the moment a network source genuinely starts scanning (never for a skipped-offline
                // or not-installed one): the denominator AllOnlineSourcesFailed needs. Same generation
                // guard as the found/failed counters below, so an abandoned scan's straggler can never add
                // itself into a NEWER run's set.
                if (IsCurrent(generation) && SourceRegistry.NeedsNetwork(source.Id))
                    _networkSourcesRanThisRun[source.Id] = 0;

                // Started here, not at the very top of the try: waiting on the lane/global gate is queue
                // time, not scan time, and would otherwise inflate the duration of a source that itself
                // ran quickly but had to wait its turn.
                var scanStopwatch = System.Diagnostics.Stopwatch.StartNew();

                // FoundCount is mutated in place and read by the UI on its own schedule, so a source with
                // hundreds of results does not raise hundreds of status events.
                await foreach (var item in source.ScanAsync(token))
                {
                    if (token.IsCancellationRequested)
                        break;

                    // A cancelled scan is abandoned by RunAsync, so this provider may still be running while
                    // the NEXT scan is already filling the table. Nothing it finds from here on may reach it.
                    // The check and the publish must happen as ONE step under _generationLock: a plain
                    // "check IsCurrent, then invoke" left a window between the two where a new run's
                    // generation bump (also under this lock) could land, and the stale item still reached
                    // the new scan's table anyway.
                    bool stillCurrent;
                    lock (_generationLock)
                    {
                        stillCurrent = IsCurrent(generation);
                        if (stillCurrent)
                        {
                            status.FoundCount++;
                            ItemFound?.Invoke(item);
                        }
                    }

                    if (!stillCurrent)
                        break;
                }

                // The pill's cancel can trip between the source's last check and its end, so the loop above
                // ends quietly with a partial result. That must not read as done, count as found, or
                // stand in for a complete scan of this source.
                if (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    status.State = ProviderScanState.Cancelled;
                    Report(status, generation);
                    return;
                }

                status.State = ProviderScanState.Done;
                Report(status, generation);

                // What each source actually produced, and how long it took: without the count a source
                // that quietly returns nothing is indistinguishable in the log from one that was never
                // asked, and without the duration a slow source leaves no trace of how long it actually ran.
                // Same generation guard as Report() above: an abandoned source reaching here after a newer
                // scan has already reset the counters must not add to the NEW scan's total.
                if (IsCurrent(generation))
                    Interlocked.Add(ref _foundThisRun, status.FoundCount);
                _logging.Event(ActivityLog.Scan, $"{source.Id} {status.FoundCount} {scanStopwatch.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException)
            {
                // A source-only cancel (its own token tripped, but the overall run did not) is reported
                // distinctly: the user asked for exactly this one to stop, which is not the same thing as
                // the whole scan being stopped, and must not read like either a failure or a plain "done".
                status.State = CancelState(cancellationToken);
                Report(status, generation);
            }
            catch (Exception ex) when (token.IsCancellationRequested)
            {
                // Stopping kills the tool, which exits non-zero and would otherwise read as a failed scan:
                // a cancellation reports no source errors.
                _logging.Info($"Scan of '{source.Id}' ended with the cancellation: {ex.Message}");
                status.State = CancelState(cancellationToken);
                Report(status, generation);
            }
            catch (ProxyConnectionFailedException ex)
            {
                // Still the ordinary error state, just with its own wording naming the proxy instead of
                // whatever bare socket error the failed direct retry itself produced.
                if (IsCurrent(generation))
                {
                    Interlocked.Increment(ref _failedThisRun);
                    _connectionFailuresThisRun[source.Id] = 0;
                }
                _logging.Info($"Scan of '{source.Id}' failed: proxy '{ex.ProxyAddress}' refused the connection and the direct retry also failed.");
                status.State = ProviderScanState.Error;
                status.ErrorMessage = string.Format(LocalizationService.Instance["NetProxyRefused"], ex.ProxyAddress);
                status.Detail = ex.Message;
                Report(status, generation);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                // A connection error that never involved a proxy at all (no proxy configured, or the direct
                // path itself never reached the host). It gets the same error state and user-facing text as
                // the generic catch below, and is tracked separately only so AllOnlineSourcesFailed can tell
                // a connection problem apart from an unrelated parse/tool failure.
                if (IsCurrent(generation))
                {
                    Interlocked.Increment(ref _failedThisRun);
                    _connectionFailuresThisRun[source.Id] = 0;
                }
                _logging.Info($"Scan of '{source.Id}' failed to connect: {ex.Message}");
                status.State = ProviderScanState.Error;
                status.ErrorMessage = LocalizationService.Instance["ScanFailed"];
                status.Detail = ex.Message;
                Report(status, generation);
            }
            catch (ProcessIdleTimeoutException ex)
            {
                // Judged by silence, not total runtime: this is still the ordinary error state, just with
                // its own wording naming the idle span instead of the generic scan-failed text.
                if (IsCurrent(generation))
                    Interlocked.Increment(ref _failedThisRun);
                _logging.Info($"Scan of '{source.Id}' gave up after {ex.IdleTimeout.TotalSeconds:0}s of silence.");
                status.State = ProviderScanState.Error;
                status.ErrorMessage = string.Format(
                    LocalizationService.Instance["ScanSourceTooSlow"], (int)ex.IdleTimeout.TotalSeconds);
                status.Detail = ex.Message;
                Report(status, generation);
            }
            catch (Exception ex)
            {
                // Intentionally broad: an unexpected fault in one provider (COM, parsing, I/O) must never
                // kill the overall scan. The exception text is raw tool output. English, and often
                // containing the user's own paths: so it goes to the log and the pill gets a translated
                // line instead (SECURITY.md).
                if (IsCurrent(generation))
                    Interlocked.Increment(ref _failedThisRun);

                // A source stuck failing the same way every scan otherwise writes the same seven-line
                // stack trace over and over; only the first failure this session earns the full trace.
                bool firstFailure;
                lock (_stackTraceLoggedFor)
                    firstFailure = _stackTraceLoggedFor.Add(source.Id);

                if (firstFailure)
                    _logging.Error($"Scan failed for source '{source.Id}'", ex);
                else
                    _logging.Warn($"Scan failed for source '{source.Id}': {ex.GetType().Name}: {ex.Message} (stack trace already logged for this source)");

                status.State = ProviderScanState.Error;
                status.ErrorMessage = LocalizationService.Instance["ScanFailed"];
                status.Detail = ex.Message;
                Report(status, generation);
            }
            finally
            {
                globalGate?.Release();
                lane.Release();
            }
        }
        finally
        {
            // Only if it is still OUR token: a newer scan of the same source has already published its
            // own by the time a stale one gets here, and removing that would leave IT uncancellable.
            _perSourceCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(source.Id, sourceCts));
        }
    }
}
