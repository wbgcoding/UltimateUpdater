using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The orchestrator carries the promises the whole scan rests on: one broken source never takes the
/// others down, a failure is visible rather than silently empty, and cancelling actually stops.
/// </summary>
public class ScanOrchestratorTests
{
    [Fact]
    public async Task One_failing_source_does_not_stop_the_others()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("broken", throws: new InvalidOperationException("tool exploded")),
            new StubSource("good", items: [Item("good", "A"), Item("good", "B")]),
        };

        var statuses = new List<ProviderStatus>();
        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.Contains(statuses, s => s.SourceId == "broken" && s.State == ProviderScanState.Error);
        Assert.Contains(statuses, s => s.SourceId == "good" && s.State == ProviderScanState.Done);
    }

    /// <summary>
    /// A source switched off in settings is skipped whole: no probe, no pill, no results. Half-running
    /// it would still pay for the source the user turned off.
    /// </summary>
    [Fact]
    public async Task A_source_switched_off_is_not_scanned_at_all()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("off", items: [Item("off", "A")]),
            new StubSource("on", items: [Item("on", "B")]),
        };

        var statuses = new List<ProviderStatus>();
        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), id => id != "off");
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(found, i => i.SourceId == "off");
        Assert.DoesNotContain(statuses, s => s.SourceId == "off");
        Assert.Contains(found, i => i.SourceId == "on");

        // and the settings window still has to offer it, or it could never be switched back on
        Assert.Contains("off", orchestrator.SourceIds);
    }

    /// <summary>A failure must never look like a source that simply had nothing to report.</summary>
    [Fact]
    public async Task A_failing_source_is_reported_as_an_error_not_as_empty()
    {
        var sources = new IUpdateSource[] { new StubSource("broken", throws: new TimeoutException("scan timed out")) };

        ProviderStatus? last = null;
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ProviderStatusChanged += s => last = s;

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(ProviderScanState.Error, last.State);
        Assert.False(string.IsNullOrWhiteSpace(last.ErrorMessage));

        // the raw exception text is log material; the pill must carry a translated line
        Assert.DoesNotContain("scan timed out", last.ErrorMessage);
    }

    /// <summary>
    /// The raw reason is not thrown away, it rides along as Detail for the pill's hover tooltip
    /// It is just never in the always-visible ErrorMessage.
    /// </summary>
    [Fact]
    public async Task A_failing_source_carries_the_raw_reason_as_detail()
    {
        var sources = new IUpdateSource[] { new StubSource("broken", throws: new TimeoutException("scan timed out")) };

        ProviderStatus? last = null;
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ProviderStatusChanged += s => last = s;

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal("scan timed out", last.Detail);
    }

    /// <summary>
    /// A source stuck failing the same way otherwise wrote the same seven-line stack trace on
    /// every scan; only the FIRST failure this session earns the full trace.
    /// </summary>
    [Fact]
    public async Task A_second_failure_of_the_same_source_does_not_log_a_second_stack_trace()
    {
        var lines = new List<string>();
        var logging = new LoggingService(line => { lock (lines) lines.Add(line); });
        var sources = new IUpdateSource[] { new StubSource("broken", throws: new InvalidOperationException("boom")) };
        var orchestrator = new ScanOrchestrator(sources, logging);

        await orchestrator.RunAsync(CancellationToken.None);
        await orchestrator.RunAsync(CancellationToken.None);

        var fullTraces = lines.Count(l => l.Contains(" E ") && l.Contains("boom"));
        var suppressedNotices = lines.Count(l => l.Contains("stack trace already logged for this source"));

        Assert.Equal(1, fullTraces);
        Assert.Equal(1, suppressedNotices);
    }

    /// <summary>Without a duration, a slow source left no trace in the log of how long it
    /// actually ran (the crash-path slowdown this fixes was measured only by hand).</summary>
    [Fact]
    public async Task The_per_source_log_line_carries_a_duration()
    {
        var lines = new List<string>();
        var logging = new LoggingService(line => { lock (lines) lines.Add(line); });
        var sources = new IUpdateSource[]
        {
            new StubSource("slow", items: [Item("slow", "A")], delay: TimeSpan.FromMilliseconds(30)),
        };
        var orchestrator = new ScanOrchestrator(sources, logging);

        await orchestrator.RunAsync(CancellationToken.None);

        var scanLine = Assert.Single(lines, l => l.Contains(" scan slow "));
        Assert.Matches(@"scan slow 1 \d+ms$", scanLine);
    }

    [Fact]
    public async Task An_unavailable_source_is_finished_without_scanning()
    {
        var sources = new IUpdateSource[] { new StubSource("absent", available: false, items: [Item("absent", "X")]) };

        var found = 0;
        var statuses = new List<ProviderStatus>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ItemFound += _ => found++;
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(0, found);
        // An absent tool is reported as its own state, not the silent Done it used to share
        // with "ran and found nothing", so a pill can be told apart and hidden on request.
        Assert.Contains(statuses, s => s.SourceId == "absent" && s.State == ProviderScanState.NotInstalled);
    }

    [Fact]
    public async Task Cancelling_stops_a_running_scan()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();

        var sources = new IUpdateSource[]
        {
            new StubSource("slow", items: [Item("slow", "A"), Item("slow", "B")], delay: TimeSpan.FromSeconds(30), started: started),
        };

        var found = 0;
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ItemFound += _ => Interlocked.Increment(ref found);

        var run = orchestrator.RunAsync(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, found);
    }

    /// <summary>Stopping one source's pill must not touch any other source, and the overall scan still
    /// finishes normally with everyone else's real results.</summary>
    [Fact]
    public async Task CancelSource_stops_only_that_source_and_marks_it_Cancelled()
    {
        var slowStarted = new TaskCompletionSource();
        var sources = new IUpdateSource[]
        {
            new StubSource("slow", items: [Item("slow", "A")], delay: TimeSpan.FromSeconds(30), started: slowStarted),
            new StubSource("fast", items: [Item("fast", "B")]),
        };

        var statuses = new List<ProviderStatus>();
        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        var run = orchestrator.RunAsync(CancellationToken.None);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(orchestrator.CancelSource("slow"));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var slowFinal = statuses.Last(s => s.SourceId == "slow");
        Assert.Equal(ProviderScanState.Cancelled, slowFinal.State);
        // not an error, the user asked for exactly this, on purpose
        Assert.Null(slowFinal.ErrorMessage);

        Assert.Contains(statuses, s => s.SourceId == "fast" && s.State == ProviderScanState.Done);
        Assert.Contains(found, i => i.SourceId == "fast");
        Assert.DoesNotContain(found, i => i.SourceId == "slow");
    }

    /// <summary>A source whose own pill is pressed while it is between its last cancellation check and its
    /// end finishes without throwing. Its partial result must read as stopped, not as done.</summary>
    [Fact]
    public async Task A_source_stopped_by_its_pill_that_ends_without_throwing_is_Cancelled_not_Done()
    {
        ScanOrchestrator? orchestrator = null;
        var source = new CancelsItselfSource("quiet", () => orchestrator!.CancelSource("quiet"));
        var statuses = new List<ProviderStatus>();
        orchestrator = new ScanOrchestrator([source], new LoggingService());
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await orchestrator.RunAsync(CancellationToken.None);

        var final = statuses.Last(s => s.SourceId == "quiet");
        Assert.Equal(ProviderScanState.Cancelled, final.State);
        Assert.Equal(0, final.FoundCount);
    }

    /// <summary>The scan-side parallelism cap: no more than the configured number of sources may be
    /// actually scanning at once, however many are enabled. Proven with gated sources that block until the
    /// test releases them, so "still waiting" and "actually running" can be told apart without a race.</summary>
    [Fact]
    public async Task No_more_than_the_configured_cap_scans_at_once()
    {
        const int cap = 2;
        const int totalSources = 5;

        var starts = Enumerable.Range(0, totalSources)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var gates = Enumerable.Range(0, totalSources)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var sources = Enumerable.Range(0, totalSources)
            .Select(i => (IUpdateSource)new GatedSource($"s{i}", starts[i], gates[i], Item($"s{i}", "A")))
            .ToArray();

        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), maxParallelSources: () => cap);
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };

        var run = orchestrator.RunAsync(CancellationToken.None);

        // Every source that is going to start at all does so almost immediately (GatedSource signals
        // before it ever awaits its gate); the rest stay blocked on the semaphore, not merely slow.
        await Task.WhenAny(Task.WhenAll(starts.Select(s => s.Task)), Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(cap, starts.Count(s => s.Task.IsCompleted));

        foreach (var gate in gates)
            gate.TrySetResult();

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(totalSources, found.Count);
    }

    /// <summary>The install side of the same pair is unaffected by the new scan cap: passing
    /// nothing for it keeps every existing caller's behaviour exactly as before this gate was added.</summary>
    [Fact]
    public async Task Omitting_the_cap_leaves_scanning_unbounded_as_before()
    {
        var sources = Enumerable.Range(0, 8)
            .Select(i => (IUpdateSource)new StubSource($"s{i}", items: [Item($"s{i}", "A")]))
            .ToArray();

        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };

        await orchestrator.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(8, found.Count);
    }

    /// <summary>A cap of 1 is the extreme case the settings slider actually offers: every source still
    /// finishes and reports, just one at a time rather than in parallel. A real end-to-end run, not just
    /// an assertion against the semaphore's own counter.</summary>
    [Fact]
    public async Task A_scan_with_the_cap_at_one_still_finishes_and_reports_every_source()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("a", items: [Item("a", "1")]),
            new StubSource("b", items: [Item("b", "1")]),
            new StubSource("c", items: [Item("c", "1")]),
        };

        var statuses = new List<ProviderStatus>();
        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), maxParallelSources: () => 1);
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await orchestrator.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, found.Count);
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Contains(statuses, s => s.SourceId == id && s.State == ProviderScanState.Done);
    }

    /// <summary>Mirrors InstallWorker's own "never trust the stored number as-is" test: a cap of 0 must
    /// not reach SemaphoreSlim's constructor unclamped (it throws for a non-positive count).</summary>
    [Fact]
    public async Task A_cap_of_zero_is_clamped_up_to_one_rather_than_crashing()
    {
        var sources = new IUpdateSource[] { new StubSource("a", items: [Item("a", "1")]) };
        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), maxParallelSources: () => 0);

        await orchestrator.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Nothing to cancel, a stale button click, or a source that already finished, is just
    /// ignored rather than throwing.</summary>
    [Fact]
    public void CancelSource_is_a_no_op_when_nothing_is_running_for_that_id()
    {
        var orchestrator = new ScanOrchestrator([new StubSource("idle", items: [])], new LoggingService());

        Assert.False(orchestrator.CancelSource("idle"));
        Assert.False(orchestrator.CancelSource("unknown-source"));
    }

    /// <summary>Stopping the WHOLE scan still reports an in-flight source as Done, not Cancelled, that
    /// distinct state is reserved for a deliberate, single-source cancel.</summary>
    [Fact]
    public async Task Stopping_the_whole_scan_still_reports_an_in_flight_source_as_Done()
    {
        var started = new TaskCompletionSource();
        var source = new StubSource("slow", items: [Item("slow", "A")], delay: TimeSpan.FromSeconds(30), started: started);

        var statuses = new List<ProviderStatus>();
        var orchestrator = new ScanOrchestrator([source], new LoggingService());
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        using var cts = new CancellationTokenSource();
        var run = orchestrator.RunAsync(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cts.CancelAsync();

        // RunAsync itself returns as soon as it observes the cancellation (abandons the straggler),
        // the source's own Done/Cancelled report follows once ITS await actually unwinds, which the
        // orchestrator does not wait for. Same shape as Cancelling_stops_a_running_scan above.
        await Task.Delay(200);

        Assert.DoesNotContain(statuses, s => s.State == ProviderScanState.Cancelled);
    }

    /// <summary>
    /// A source cancelled while it is still waiting for its OWN previous run to free the
    /// per-source lane must report a state, never leave the pill stuck on Waiting forever - the catch
    /// around that wait already explained the case correctly in its comment, the report was simply
    /// missing. Stopping the WHOLE scan (this test) must read as Done, same as everywhere else in this
    /// class; the sibling test right below covers a single-source cancel instead, which must read as
    /// Cancelled.
    /// </summary>
    [Fact]
    public async Task A_source_cancelled_by_the_whole_scan_while_waiting_behind_its_own_run_reports_Done()
    {
        var firstStarted = new TaskCompletionSource();
        var firstGate = new TaskCompletionSource();
        var source = new GatedSource("slow", firstStarted, firstGate, Item("slow", "first"));

        var statuses = new List<ProviderStatus>();
        var waitingCount = 0;
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new ScanOrchestrator([source], new LoggingService());
        orchestrator.ProviderStatusChanged += s =>
        {
            lock (statuses) statuses.Add(s);
            if (s.SourceId == "slow" && s.State == ProviderScanState.Waiting && Interlocked.Increment(ref waitingCount) == 2)
                secondWaiting.TrySetResult();
        };

        // First run occupies the per-source lane and is held open on purpose (the gate, never released
        // until the very end) so the second attempt below is genuinely still waiting behind it, not just
        // racing a real timer.
        var firstRun = orchestrator.RunAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var secondCts = new CancellationTokenSource();
        var secondScan = orchestrator.RescanSourceAsync("slow", secondCts.Token);
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await secondCts.CancelAsync();
        await secondScan.WaitAsync(TimeSpan.FromSeconds(10));

        var lastForSlow = statuses.Last(s => s.SourceId == "slow");
        Assert.Equal(ProviderScanState.Done, lastForSlow.State);

        firstGate.SetResult();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>This fix's other half: a single-source cancel (the pill's own button) while that source
    /// waits behind its own previous run must read as Cancelled, never as Done and never left at Waiting.</summary>
    [Fact]
    public async Task Cancelling_just_that_source_while_it_waits_behind_its_own_run_reports_Cancelled()
    {
        var firstStarted = new TaskCompletionSource();
        var firstGate = new TaskCompletionSource();
        var source = new GatedSource("slow", firstStarted, firstGate, Item("slow", "first"));

        var statuses = new List<ProviderStatus>();
        var waitingCount = 0;
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new ScanOrchestrator([source], new LoggingService());
        orchestrator.ProviderStatusChanged += s =>
        {
            lock (statuses) statuses.Add(s);
            if (s.SourceId == "slow" && s.State == ProviderScanState.Waiting && Interlocked.Increment(ref waitingCount) == 2)
                secondWaiting.TrySetResult();
        };

        var firstRun = orchestrator.RunAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondScan = orchestrator.RescanSourceAsync("slow", CancellationToken.None);
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Registering the second attempt's own cancellation source happens right after the Waiting
        // report above with no await between them, so this is safe without extra synchronization; the
        // short delay is only belt-and-braces.
        await Task.Delay(50);

        Assert.True(orchestrator.CancelSource("slow"));
        await secondScan.WaitAsync(TimeSpan.FromSeconds(10));

        var lastForSlow = statuses.Last(s => s.SourceId == "slow");
        Assert.Equal(ProviderScanState.Cancelled, lastForSlow.State);

        firstGate.SetResult();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Report() already guards its status events by generation: a scan the user abandoned must never
    /// overwrite the NEW scan's pill, but the totals added right next to it, for the summary log line, had
    /// no such guard. A source that was abandoned and only actually finishes once a later scan is already
    /// under way must not add its stale count into that scan's total.
    /// </summary>
    [Fact]
    public async Task An_abandoned_scans_late_finish_does_not_corrupt_the_next_scans_totals()
    {
        var logs = new List<string>();
        var logging = new LoggingService(line => { lock (logs) logs.Add(line); });

        var itemSeen = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var source = new ReusableGatedSource("slow", gate, Item("slow", "A"));
        var orchestrator = new ScanOrchestrator([source], logging);
        orchestrator.ItemFound += _ => itemSeen.TrySetResult();

        using var cts = new CancellationTokenSource();
        var abandoned = orchestrator.RunAsync(cts.Token);

        // Wait for the item to actually be counted (FoundCount++), not just for the source to start,
        // otherwise cancelling could race ahead of the increment this test needs to have already happened.
        await itemSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await abandoned.WaitAsync(TimeSpan.FromSeconds(10));   // returns without waiting for the straggler

        // The next scan's own ScanOneAsync blocks on the same source's per-source lane until the
        // straggler above lets go of it, releasing the gate now lets both happen: the straggler's late,
        // stale finish landing right where the new scan is already under way.
        var current = orchestrator.RunAsync(CancellationToken.None);
        gate.SetResult();
        await current.WaitAsync(TimeSpan.FromSeconds(10));

        var summary = Assert.Single(logs, l => l.Contains(" sources ") && l.Contains(" updates"));
        Assert.Contains("1 updates", summary);
    }

    /// <summary>
    /// Used when a source's own install discovers its row is genuinely gone (WindowsUpdateSource: "no
    /// longer offered"), the source is rescanned on its own, outside of any full run, so the stale row
    /// can disappear without the user rescanning by hand.
    /// </summary>
    [Fact]
    public async Task RescanSourceAsync_scans_only_the_named_source()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("windowsupdate", items: [Item("windowsupdate", "A")]),
            new StubSource("winget", items: [Item("winget", "B")]),
        };

        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };

        await orchestrator.RescanSourceAsync("windowsupdate", CancellationToken.None);

        Assert.Contains(found, i => i.SourceId == "windowsupdate");
        Assert.DoesNotContain(found, i => i.SourceId == "winget");
    }

    [Fact]
    public async Task RescanSourceAsync_is_a_no_op_for_an_unknown_source_id()
    {
        var orchestrator = new ScanOrchestrator([new StubSource("windowsupdate", items: [Item("windowsupdate", "A")])], new LoggingService());

        var found = 0;
        orchestrator.ItemFound += _ => found++;

        await orchestrator.RescanSourceAsync("unknown-source", CancellationToken.None);

        Assert.Equal(0, found);
    }

    /// <summary>
    /// Offline, every network source is skipped outright (never scanned at all, not scanned-and-failed);
    /// a source known to only read the local machine runs exactly as if nothing were wrong. "wsl" is one
    /// of the real, explicit local-only ids in <see cref="SourceRegistry.NeedsNetwork"/>; "winget" is
    /// not, so it stands in for "everything else".
    /// </summary>
    [Fact]
    public async Task Offline_skips_network_sources_but_runs_local_ones_normally()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("winget", items: [Item("winget", "A")]),
            new StubSource("wsl", items: [Item("wsl", "B")]),
        };

        var statuses = new List<ProviderStatus>();
        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), isOnline: () => false);
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.True(orchestrator.WasOffline);
        Assert.DoesNotContain(found, i => i.SourceId == "winget");
        Assert.Contains(found, i => i.SourceId == "wsl");
        var wingetStatus = statuses.Last(s => s.SourceId == "winget");
        Assert.Equal(ProviderScanState.Error, wingetStatus.State);
        Assert.Contains(statuses, s => s.SourceId == "wsl" && s.State == ProviderScanState.Done);
    }

    /// <summary>Every network source failing with a connection error (never reached the host at all, no
    /// proxy involved) is the exact case AllOnlineSourcesFailed exists to catch: the SYSTEM still reports
    /// a connection (unlike WasOffline), but a firewall rule against just this app makes every network
    /// source fail the same way.</summary>
    [Fact]
    public async Task All_network_sources_failing_with_a_connection_error_sets_AllOnlineSourcesFailed()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("net-a", throws: new System.Net.Http.HttpRequestException("connection refused")),
            new StubSource("net-b", throws: new System.Net.Http.HttpRequestException("connection refused")),
        };

        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), isOnline: () => true);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.False(orchestrator.WasOffline);
        Assert.True(orchestrator.AllOnlineSourcesFailed);
    }

    /// <summary>One broken source among several must never look like every online source failing.</summary>
    [Fact]
    public async Task A_single_broken_network_source_does_not_set_AllOnlineSourcesFailed()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("net-a", throws: new System.Net.Http.HttpRequestException("connection refused")),
            new StubSource("net-b", items: [Item("net-b", "A")]),
        };

        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), isOnline: () => true);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.False(orchestrator.AllOnlineSourcesFailed);
    }

    /// <summary>A network source failing for a reason OTHER than a connection error (an ordinary parse or
    /// tool failure) must not count toward AllOnlineSourcesFailed either - only a connection error names
    /// the actual firewall/network problem this property exists to surface.</summary>
    [Fact]
    public async Task A_non_connection_failure_does_not_set_AllOnlineSourcesFailed()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("net-a", throws: new InvalidOperationException("could not parse the tool's output")),
        };

        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), isOnline: () => true);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.False(orchestrator.AllOnlineSourcesFailed);
    }

    /// <summary>The flag describes the run that just ended. A run cancelled before it finished must not
    /// hand the previous run's "every network source failed" to the banner.</summary>
    [Fact]
    public async Task A_cancelled_run_does_not_keep_the_previous_runs_AllOnlineSourcesFailed()
    {
        var secondRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FailsOnceThenWaitsSource("net-a", secondRunStarted, release);
        var orchestrator = new ScanOrchestrator([source], new LoggingService(), isOnline: () => true);

        await orchestrator.RunAsync(CancellationToken.None);
        Assert.True(orchestrator.AllOnlineSourcesFailed);

        using var cts = new CancellationTokenSource();
        var second = orchestrator.RunAsync(cts.Token);
        await secondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await second.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(orchestrator.AllOnlineSourcesFailed);
        release.SetResult();
    }

    /// <summary>Nothing that needed a connection actually ran: AllOnlineSourcesFailed must stay false
    /// rather than vacuously true over an empty set.</summary>
    [Fact]
    public async Task No_network_source_running_leaves_AllOnlineSourcesFailed_false()
    {
        var sources = new IUpdateSource[] { new StubSource("wsl", items: [Item("wsl", "A")]) };

        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), isOnline: () => true);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.False(orchestrator.AllOnlineSourcesFailed);
    }

    /// <summary>Online, nothing about a normal scan changes: every source, network or not, runs exactly
    /// as it always has.</summary>
    [Fact]
    public async Task Online_leaves_every_source_unaffected()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("winget", items: [Item("winget", "A")]),
            new StubSource("wsl", items: [Item("wsl", "B")]),
        };

        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService(), isOnline: () => true);
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.False(orchestrator.WasOffline);
        Assert.Contains(found, i => i.SourceId == "winget");
        Assert.Contains(found, i => i.SourceId == "wsl");
    }

    /// <summary>
    /// A source going silent is still the ordinary error state, just with its own wording naming the idle
    /// span, and never takes another source down with it.
    /// </summary>
    [Fact]
    public async Task A_source_that_goes_idle_reports_too_slow_without_stopping_others()
    {
        var sources = new IUpdateSource[]
        {
            new StubSource("hung", throws: new ProcessIdleTimeoutException(TimeSpan.FromSeconds(45))),
            new StubSource("good", items: [Item("good", "A")]),
        };

        var statuses = new List<ProviderStatus>();
        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator(sources, new LoggingService());
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };
        orchestrator.ProviderStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await orchestrator.RunAsync(CancellationToken.None);

        var hungStatus = statuses.Last(s => s.SourceId == "hung");
        Assert.Equal(ProviderScanState.Error, hungStatus.State);
        Assert.Contains("45", hungStatus.ErrorMessage);
        Assert.Contains(statuses, s => s.SourceId == "good" && s.State == ProviderScanState.Done);
        Assert.Single(found);
    }

    /// <summary>
    /// The exact race: <c>ScanOneAsync</c>'s per-item loop checks <c>IsCurrent(generation)</c> and then
    /// invokes <c>ItemFound</c> as two separate statements, with nothing stopping a NEW run's generation
    /// bump from landing in between. A subscriber that blocks mid-invoke turns that razor-thin window into
    /// one this test can actually observe: while the handler is parked inside the first item's publish, a
    /// second <see cref="ScanOrchestrator.RunAsync"/> is started on another thread. Before the fix, its
    /// generation bump (the very first thing <c>RunAsync</c> does, unguarded) proceeds immediately,
    /// regardless of the in-flight publish. After the fix, that bump shares <c>_generationLock</c> with the
    /// check-and-invoke, so it must wait, genuinely and observably, for the handler to return.
    /// </summary>
    [Fact]
    public async Task A_generation_bump_waits_for_an_in_flight_item_publish_to_finish()
    {
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseHandler = new ManualResetEventSlim(false);

        var source = new StubSource("slow", items: [Item("slow", "A")]);
        var orchestrator = new ScanOrchestrator([source], new LoggingService());

        var invocationCount = 0;
        orchestrator.ItemFound += _ =>
        {
            if (Interlocked.Increment(ref invocationCount) == 1)
            {
                handlerEntered.TrySetResult();
                releaseHandler.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var generationField = typeof(ScanOrchestrator)
            .GetField("_generation", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var firstRun = orchestrator.RunAsync(CancellationToken.None);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var generationBeforeBump = (int)generationField.GetValue(orchestrator)!;

        using var secondCts = new CancellationTokenSource();
        var secondRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = Task.Run(async () =>
        {
            secondRunStarted.TrySetResult();
            await orchestrator.RunAsync(secondCts.Token);
        });
        await secondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Every chance to race ahead: 30 x 10ms is generous next to how fast an unguarded Interlocked
        // bump actually lands, and short enough that a genuinely blocked bump barely slows the test down.
        var racedWhileStillInFlight = false;
        for (var i = 0; i < 30; i++)
        {
            if ((int)generationField.GetValue(orchestrator)! != generationBeforeBump)
            {
                racedWhileStillInFlight = true;
                break;
            }

            await Task.Delay(10);
        }

        Assert.False(racedWhileStillInFlight);

        // Let go cleanly rather than cancelling: the second call is still inside its OWN pre-cancellation
        // WasOffline probe once the lock releases it, and cancelling here would just fault that unrelated
        // await instead of proving anything about the generation bump itself. "slow"'s own per-source lane
        // is still held by the first run at this point, so the second call's own scan of it simply waits
        // its turn, same as any two back-to-back RunAsync calls on the same source, nothing this test
        // needs to race against.
        releaseHandler.Set();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
        await secondRun.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(invocationCount >= 1);
    }

    /// <summary>Yields one item and then blocks until the test releases it, every call, so the same
    /// instance can be scanned again by a later generation once the earlier one lets go of the lane.</summary>
    private sealed class ReusableGatedSource(string id, TaskCompletionSource gate, UpdateItem item) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return item;
            await gate.Task;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static UpdateItem Item(string sourceId, string name) => new()
    {
        Id = name,
        SourceId = sourceId,
        Category = UpdateCategory.Software,
        Name = name,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
    };

    /// <summary>
    /// Cancelling must not mean "wait for the slowest source to notice": not every source CAN notice,
    /// Windows Update scans through a COM call that ignores its token and keeps running regardless.
    /// </summary>
    [Fact]
    public async Task Cancelling_returns_without_waiting_for_a_source_that_ignores_the_token()
    {
        var started = new TaskCompletionSource();
        var deaf = new DeafSource("deaf", started, TimeSpan.FromSeconds(30));

        var orchestrator = new ScanOrchestrator([deaf], new LoggingService());
        using var cts = new CancellationTokenSource();

        var run = orchestrator.RunAsync(cts.Token);
        await started.Task;
        cts.Cancel();

        // Generous compared to what it is proving: the source runs for 30 seconds regardless.
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(run, finished);
    }

    /// <summary>And nothing it produces after the cancellation may still reach the table.</summary>
    [Fact]
    public async Task A_cancelled_source_stops_feeding_the_table()
    {
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var source = new GatedSource("slow", started, gate, Item("slow", "late arrival"));

        var found = new List<UpdateItem>();
        var orchestrator = new ScanOrchestrator([source], new LoggingService());
        orchestrator.ItemFound += i => { lock (found) found.Add(i); };

        using var cts = new CancellationTokenSource();
        var run = orchestrator.RunAsync(cts.Token);

        await started.Task;
        cts.Cancel();
        await run;

        // Only now does the source hand over its result, too late to count.
        gate.SetResult();
        await Task.Delay(50);

        Assert.Empty(found);
    }

    /// <summary>A source whose work cannot be cancelled at all, the Windows Update COM scan in miniature.</summary>
    private sealed class DeafSource(string id, TaskCompletionSource started, TimeSpan runsFor) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            started.TrySetResult();

            // No token: that is the whole point.
            await Task.Delay(runsFor, CancellationToken.None);

            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Hands over one result only when the test lets it, so "too late" can be tested exactly.</summary>
    private sealed class GatedSource(
        string id, TaskCompletionSource started, TaskCompletionSource gate, UpdateItem item) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await gate.Task;
            yield return item;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Fails with a connection error on its first scan; later scans ignore the token and wait for
    /// the test to release them, so the cancelled run is still unfinished when the flag is read.</summary>
    private sealed class FailsOnceThenWaitsSource(
        string id, TaskCompletionSource laterScanStarted, TaskCompletionSource release) : IUpdateSource
    {
        private int _scans;

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _scans) == 1)
                throw new System.Net.Http.HttpRequestException("connection refused");

            laterScanStarted.TrySetResult();
            await release.Task;
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Presses its own pill, then ends normally: the window between a source's last token check and
    /// its end, in miniature.</summary>
    private sealed class CancelsItselfSource(string id, Action cancelSelf) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancelSelf();
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubSource(
        string id,
        IReadOnlyList<UpdateItem>? items = null,
        Exception? throws = null,
        bool available = true,
        TimeSpan delay = default,
        TaskCompletionSource? started = null) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(available);

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            started?.TrySetResult();

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            if (throws is not null)
                throw throws;

            foreach (var item in items ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
