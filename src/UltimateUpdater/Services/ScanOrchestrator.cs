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

    /// <summary>A localized caveat about a scan that still ended Done (rows the source had to leave out),
    /// shown under the pill's tooltip text. Not an error.</summary>
    public string? Note { get; set; }
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
    /// Which sources the user still wants scanned. A source switched off is skipped entirely: no probe, no
    /// pill, no results.
    /// </param>
    /// <param name="isOnline">
    /// Testing seam for <see cref="NetworkAvailability.IsOnline"/>, so a test can inject a fixed answer.
    /// </param>
    /// <param name="maxParallelSources">
    /// How many sources may scan at once, read at the start of every <see cref="RunAsync"/> so a slider moved
    /// in the settings window takes effect on the next scan. Null means unbounded; the real caller passes
    /// <c>() => settings.Current.Scan.MaxParallelSources</c>. Clamped to 1 to 16 here regardless of what it
    /// returns.
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
    /// Whether the most recent <see cref="RunAsync"/> found no internet connection at all. Every network
    /// source (<see cref="SourceRegistry.NeedsNetwork"/>) was skipped rather than scanned, so this tells the
    /// caller to show one banner instead of one "no connection" pill per source.
    /// </summary>
    public bool WasOffline { get; private set; }

    /// <summary>
    /// Set after the most recent <see cref="RunAsync"/>: true when at least one network source actually ran
    /// (not skipped for <see cref="WasOffline"/>, not found absent) and every such source failed with a
    /// connection error specifically (a proxy refusal or a bare <see cref="HttpRequestException"/>), tracked
    /// in <see cref="_connectionFailuresThisRun"/> by <see cref="ScanOneAsync"/>. Distinct from
    /// <see cref="WasOffline"/>, which means the system reported no connection: a firewall rule blocking just
    /// this app leaves the system "online" while every network source still fails.
    /// </summary>
    public bool AllOnlineSourcesFailed { get; private set; }

    /// <summary>Sources already found present: only the POSITIVE answer is cached, so a missing tool is
    /// still re-probed every scan.</summary>
    private readonly HashSet<string> _knownPresent = [];

    /// <summary>
    /// Which sources have already had a full stack trace written to the log this session. Only the first
    /// failure earns the full trace; later ones get the exception type and message alone.
    /// </summary>
    private readonly HashSet<string> _stackTraceLoggedFor = [];

    /// <summary>
    /// The last state reported for each source, kept across scans so a caller that did not see the live
    /// event (the settings window, opened long after the last scan) can still ask "was this one not
    /// installed". Updated in <see cref="Report"/> from the same status object every pill reacts to.
    /// Concurrent: sources report from their own parallel tasks.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderScanState> _lastKnownStates = new();

    /// <summary>
    /// Whether the most recent report for this source (across any scan this session) was
    /// <see cref="ProviderScanState.NotInstalled"/>. False for a source never scanned: unknown counts as
    /// available.
    /// </summary>
    public bool IsKnownNotInstalled(string sourceId) =>
        _lastKnownStates.TryGetValue(sourceId, out var state) && state == ProviderScanState.NotInstalled;

    /// <summary>
    /// Which scan is the current one. A cancelled scan is abandoned rather than waited for (see RunAsync), so
    /// its providers keep running while the next scan fills the table; their late finds and status events
    /// would show a duplicate row or let a stale "done" overwrite a source that is scanning again. Every
    /// event is tagged with its scan, and anything from an older one is dropped.
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
    /// One permit per source, so the same source is never scanned twice at once. Windows Update's COM search
    /// takes no cancellation token and keeps running after a stop, so a quick rescan would start a second
    /// search on the same instance. The new scan's provider waits for the old run to end instead.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _perSourceLanes = new();

    /// <summary>
    /// One linked token source per source, alive only while that source is scanning. Keyed by source id, not
    /// generation: a stale entry is overwritten when the source scans again, and <see cref="CancelSource"/>
    /// acts only on what is in here now. Cancelling a source nobody is running is a no-op.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _perSourceCts = new();

    /// <summary>
    /// Stops exactly one source without touching the rest of the scan: the button on its pill. Unlike the
    /// overall Stop, the source's own token trips, <see cref="ScanOneAsync"/> reports
    /// <see cref="ProviderScanState.Cancelled"/>, and every other source keeps contributing to the still-valid
    /// result.
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
    /// How long a new scan waits for the previous run of the same source, so a merely slow provider is not
    /// reported as failed. It has to clear the slowest source: winget's scan timeout is five minutes and it
    /// runs twice per scan (its own list, then the Store).
    /// </summary>
    private static readonly TimeSpan SourceLaneTimeout = TimeSpan.FromMinutes(12);

    /// <summary>The state a source ends in when a token tripped. The overall scan being stopped reads as done
    /// (the whole table is abandoned anyway); only this source's own cancel reads as cancelled.</summary>
    private static ProviderScanState CancelState(CancellationToken runToken) =>
        runToken.IsCancellationRequested ? ProviderScanState.Done : ProviderScanState.Cancelled;

    private bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

    /// <summary>
    /// Guards the generation bump in <see cref="RunAsync"/> against the check-then-invoke in the ItemFound
    /// path: without a shared lock an abandoned provider could pass its <see cref="IsCurrent"/> check just
    /// before a new run bumps <see cref="_generation"/>, then publish a stale item into the new scan's table.
    /// Held only for the bump and for one item's check-and-invoke, never for a whole source's scan.
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
    /// Every provider runs on a background thread. Without Task.Run each would execute synchronously on the
    /// calling (UI) thread until its first real await, and availability probing alone hits the registry and
    /// file system for every source.
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

        // Only a run that finishes assigns this, so a cancelled one does not keep the previous verdict.
        AllOnlineSourcesFailed = false;

        // Checked once per scan, off the calling thread (a COM activation has no place blocking a caller that
        // may be the UI thread), so every network source fails the same obvious way instead of each timing out
        // against a connection already known to be dead.
        WasOffline = !await Task.Run(_isOnline, cancellationToken);

        var enabled = _sources.Where(source => _isEnabled?.Invoke(source.Id) ?? true).ToList();

        // A fresh gate per run, never disposed: an abandoned scan's stragglers may still be waiting on or
        // releasing it after RunAsync returned, and disposing it would turn a harmless late finish into an
        // ObjectDisposedException. Same for _perSourceLanes.
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

            // One line for the whole run: how much was asked, how much came back, how long it took.
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
    /// Raises a status event only while this scan is still the current one: a provider abandoned by a
    /// cancelled scan keeps running, and its "done" must not land on the new scan's state for that source.
    /// </summary>
    internal void Report(ProviderStatus status, int generation)
    {
        // The check and the state write are one step under the lock the generation bump takes: with the check
        // outside, a straggler could pass it, a new run bump and report its own state, and the straggler's older
        // state would land on top.
        lock (_generationLock)
        {
            if (!IsCurrent(generation))
                return;

            ReportSeamForTest?.Invoke("locked");
            _lastKnownStates[status.SourceId] = status.State;
        }

        ReportSeamForTest?.Invoke("released");

        // Raised outside the lock (a subscriber must never run under it), so the generation is checked once
        // more right before: a run that started in the gap supersedes this one.
        if (!IsCurrent(generation))
            return;

        try
        {
            ProviderStatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            // A subscriber's defect must not end the scan. The first report happens outside ScanOneAsync's try and
            // the last inside its catch, so an exception here would fault Task.WhenAll and take every source down.
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
    /// Scans exactly one source outside a full run, for when an install found that a row it was handed is
    /// gone (WindowsUpdateSource: "no longer offered") and the stale row should disappear without waiting for
    /// the next manual rescan. Reports under the current generation instead of starting a new one: bumping
    /// <see cref="_generation"/> would make every other source's running reports look stale and get dropped
    /// by <see cref="Report"/>.
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

        // Skipped before touching its lane or availability probe: no point waiting on a source that can only
        // fail with no connection. Local sources are unaffected.
        if (offline && SourceRegistry.NeedsNetwork(source.Id))
        {
            status.State = ProviderScanState.Error;
            status.ErrorMessage = LocalizationService.Instance["ScanOfflineSource"];
            Report(status, generation);
            return;
        }

        // Linked, not the raw token: cancelling this source must trip only its own token. Registered under the
        // source id so CancelSource can find it; removed in the finally below.
        using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _perSourceCts[source.Id] = sourceCts;
        var token = sourceCts.Token;

        // Everything this source runs for the scan, its probe and its lookups included, starts its tool
        // processes below normal priority so the scan never makes the user's own programs sluggish.
        using var scanScope = ScanScope.Enter();

        try
        {
            // Never two runs of one source at once: the previous scan's run may still be going. Bounded, and
            // generously: a source still busy after this has failed, which beats a scan that never returns.
            var lane = _perSourceLanes.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
            try
            {
                if (!await lane.WaitAsync(SourceLaneTimeout, token))
                {
                    _logging.Warn($"Scan of '{source.Id}' skipped: its previous run has still not finished.");
                    status.State = ProviderScanState.Error;
                    status.ErrorMessage = LocalizationService.Instance["ScanSourceStillBusy"];
                    Report(status, generation);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // This scan was stopped while the previous run of this source was still finishing. The state must be
                // reported here or the pill stays on Waiting. Same distinction as the cancellation catch below: the
                // overall token means Done, this source's own token means Cancelled.
                status.State = CancelState(cancellationToken);
                Report(status, generation);
                return;
            }

            // The overall "how many sources at once" cap, on top of the per-source lane (which only stops the same
            // source overlapping itself). Waited for while holding the lane, so a source parked here cannot be
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

                // Marked when a network source genuinely starts scanning (never for a skipped or not-installed one):
                // the denominator AllOnlineSourcesFailed needs. Generation-guarded like the counters below.
                if (IsCurrent(generation) && SourceRegistry.NeedsNetwork(source.Id))
                    _networkSourcesRanThisRun[source.Id] = 0;

                // Started here, not at the top of the try: time spent waiting on the lane or global gate is queue time,
                // not scan time.
                var scanStopwatch = System.Diagnostics.Stopwatch.StartNew();

                await foreach (var item in source.ScanAsync(token))
                {
                    if (token.IsCancellationRequested)
                        break;

                    // A cancelled scan is abandoned by RunAsync, so this provider may still be running while the next
                    // scan fills the table. The check and the publish are one step under _generationLock, so a
                    // generation bump cannot land between them.
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

                // The pill's cancel can trip between the source's last check and its end, leaving a partial result.
                // That must not read as done or count as found.
                if (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    status.State = ProviderScanState.Cancelled;
                    Report(status, generation);
                    return;
                }

                if (source is IScanNoteSource noteSource)
                    status.Note = noteSource.ScanNote;

                status.State = ProviderScanState.Done;
                Report(status, generation);

                // Logged so a source that quietly returns nothing is distinguishable from one never asked. Generation
                // guarded like Report(): an abandoned source must not add to a newer scan's total.
                if (IsCurrent(generation))
                    Interlocked.Add(ref _foundThisRun, status.FoundCount);
                _logging.Event(ActivityLog.Scan, $"{source.Id} {status.FoundCount} {scanStopwatch.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException)
            {
                // A source-only cancel (its own token, not the overall run) is reported distinctly from a failure and
                // from a plain "done".
                status.State = CancelState(cancellationToken);
                Report(status, generation);
            }
            catch (Exception ex) when (token.IsCancellationRequested)
            {
                // Stopping kills the tool, which exits non-zero; a cancellation reports no source errors.
                _logging.Info($"Scan of '{source.Id}' ended with the cancellation: {ex.Message}");
                status.State = CancelState(cancellationToken);
                Report(status, generation);
            }
            catch (ProxyConnectionFailedException ex)
            {
                // The ordinary error state, worded to name the proxy instead of the bare socket error of the direct
                // retry.
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
                // A connection error with no proxy involved. Same error state and text as the generic catch below,
                // tracked separately so AllOnlineSourcesFailed can tell it from a parse or tool failure.
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
            catch (RegistryRateLimitedException ex)
            {
                // The ordinary error state, but the pill says when the limit lifts.
                if (IsCurrent(generation))
                    Interlocked.Increment(ref _failedThisRun);
                _logging.Info($"Scan of '{source.Id}' hit the rate limit until {ex.ResetAt:u}.");
                status.State = ProviderScanState.Error;
                status.ErrorMessage = string.Format(
                    LocalizationService.Instance["ScanRateLimited"], ex.ResetAt.ToLocalTime().ToString("HH:mm"));
                status.Detail = ex.Message;
                Report(status, generation);
            }
            catch (ProcessIdleTimeoutException ex)
            {
                // Judged by silence, not total runtime; the ordinary error state with wording naming the idle span.
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
                // Intentionally broad: a fault in one provider (COM, parsing, I/O) must never kill the overall scan.
                // The exception text is raw English tool output that often contains the user's paths, so it goes to the
                // log and the pill gets a translated line instead.
                if (IsCurrent(generation))
                    Interlocked.Increment(ref _failedThisRun);

                // Only the first failure of a source this session earns the full stack trace.
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
            // Only if it is still our token: a newer scan of the same source has published its own, and removing
            // that would leave it uncancellable.
            _perSourceCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(source.Id, sourceCts));
        }
    }
}
