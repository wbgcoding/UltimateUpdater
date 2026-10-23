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

// Scanning: starting, cancelling and finishing a scan run, source toggles and provider-status pills,
// and the batched drain of found items into the table.
public partial class MainViewModel
{
    [ObservableProperty]
    private bool isScanning;

    /// <summary>
    /// Built with the view model rather than on first use: the cache resolves its file path in its own
    /// constructor, so a lazily built one would take whatever path was current at the first SAVE - and a
    /// save happens asynchronously, after the scan settles. In the test run that meant one test's finished
    /// scan writing into the file the harness had meanwhile redirected to the NEXT test, which then read
    /// rows it never produced. Binding the path to the view model's own lifetime removes that window.
    /// </summary>
    private readonly ScanResultCache _scanResultCache;


    /// <summary>Rows currently on screen that still hold last session's cached data because nothing
    /// this scan reported has replaced them yet (see <see cref="LoadCachedScanIfFirstRunAsync"/>).</summary>
    private readonly HashSet<string> _cacheOnlyKeys = new(StringComparer.Ordinal);

    private bool _cacheLoadAttempted;

    /// <summary>True once a scan finished cleanly or last session's saved result loaded. Until then
    /// "everything is up to date" would be a claim nobody checked.</summary>
    private bool _scanResultKnown;

    /// <summary>How many scans have finished this session, so the working-set line can be written
    /// after scan 1 and scan 5 only, never on every scan (that would flood the log for no benefit).</summary>
    private int _finishedScanCount;

    /// <summary>When the table's cached rows were captured, or null once nothing on screen is
    /// still a cached row. Drives the "Result from ..., checking again" banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CachedResultAgeText))]
    [NotifyPropertyChangedFor(nameof(HasCachedResult))]
    private DateTimeOffset? cachedResultSavedAt;

    public bool HasCachedResult => CachedResultSavedAt is not null;

    public string CachedResultAgeText => CachedResultSavedAt is { } savedAt
        ? string.Format(Loc["ScanCachedAge"], savedAt.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture))
        : string.Empty;

    /// <summary>True right after a scan that found no internet connection at all: every network
    /// source was skipped rather than scanned (see <see cref="ScanOrchestrator.WasOffline"/>), and
    /// local sources ran normally.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineBannerText))]
    [NotifyPropertyChangedFor(nameof(HasConnectionBanner))]
    private bool wasOffline;

    /// <summary>True right after a scan where the SYSTEM reported a connection, but every network source
    /// still failed with a connection error - a firewall rule against just this app, mirrored straight
    /// from <see cref="ScanOrchestrator.AllOnlineSourcesFailed"/> the same place <see cref="WasOffline"/>
    /// already is (<see cref="FinishScanUiAsync"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineBannerText))]
    [NotifyPropertyChangedFor(nameof(HasConnectionBanner))]
    private bool allOnlineSourcesFailed;

    /// <summary>"No connection at all" wins when both apply, since it explains the offline case too and a
    /// system that reports a connection while every source still fails is the more specific, more
    /// actionable finding.</summary>
    public string OfflineBannerText => WasOffline
        ? Loc["ScanOfflineBanner"]
        : AllOnlineSourcesFailed
            ? Loc["ScanNoConnectionBanner"]
            : string.Empty;

    public bool HasConnectionBanner => !string.IsNullOrEmpty(OfflineBannerText);

    /// <summary>
    /// Fills the table from the previous session's saved scan before this session's own first scan does
    /// any work, so the window never sits empty for however long a real scan takes. Runs exactly once
    /// per app lifetime; every scan after the first reconciles for real, the normal way, and never
    /// touches this cache except to overwrite it in <see cref="FinishScanUiAsync"/>.
    /// </summary>
    private async Task LoadCachedScanIfFirstRunAsync()
    {
        if (_cacheLoadAttempted)
            return;
        _cacheLoadAttempted = true;

        // File I/O; off the UI thread so a slow disk never stalls the window at launch. The result is
        // only read back on the UI thread below, where the rows it describes are actually built.
        var result = await Task.Run(() => _scanResultCache.TryLoad());
        if (result is null)
            return;

        CachedResultSavedAt = result.Value.SavedAt;
        _scanResultKnown = true;

        var newRows = new List<UpdateRowViewModel>();
        foreach (var item in result.Value.Items)
        {
            var key = PackageKey(item);
            if (_rowsByKey.ContainsKey(key))
                continue;

            var row = new UpdateRowViewModel(item, _logging, Ignore, this, ShowErrorDetail, ShowChangelogAsync)
            {
                IsIgnored = IsIgnored(item),
                IsIgnoredAllVersions = _settings.Current.Excludes.Contains(key),
            };
            // The same states a live row gets on creation: without them a package flagged NeedsManualWork
            // came back Idle and installable after every restart (the live scan then kept that cached row on
            // its unchanged fast path), switching the three-strikes protection off.
            ApplySnoozeState(row);
            ApplyPersistentFailureState(row);
            TrackSelection(row);
            _rowsByKey[key] = row;
            _cacheOnlyKeys.Add(key);
            newRows.Add(row);
        }

        // One notification for the whole cache load, not one per row: Updates.CollectionChanged runs a
        // full ApplyFilter every time it fires.
        Updates.AddRange(newRows);

        RefreshHeader(resortManualOnly: false);

        // The first row from last session's cache actually reaching the screen, not just the
        // cache file existing; a save with zero new rows (everything already on screen) proves nothing.
        if (newRows.Count > 0)
        {
            _logging.Event(
                ActivityLog.App,
                ActivityLog.FirstCachedRow(System.Diagnostics.Process.GetCurrentProcess().StartTime, DateTime.Now));
        }
    }

    /// <summary>A row survives a scan that never got to re-check it: any row whose source failed outright
    /// (a cached row and one an earlier live scan found alike, as a single-source rescan already treats
    /// them), or whose source was stopped from its pill and so delivered only part of its list. Only a
    /// source that genuinely ran to the end and did not report the package again means it is actually
    /// gone.</summary>
    private bool IsRowFromIncompleteSource(string key) =>
        _rowsByKey.TryGetValue(key, out var row)
        && _liveStatuses.TryGetValue(row.Item.SourceId, out var status)
        && status.State is ProviderScanState.Cancelled or ProviderScanState.Error;

    public ObservableCollection<ProviderStatusViewModel> ProviderStatuses { get; } = [];

    /// <summary>
    /// The pills at the bottom, grouped by category exactly like the table: Software, Store, System &amp;
    /// drivers, Developer tools, Games, then Steam and the "open it yourself" launcher rows. Only sources
    /// worth a pill appear (found something, failed, still scanning, or switched off); the quiet rest get
    /// none. Refreshed from RefreshHeader as their state changes.
    /// </summary>
    public ICollectionView PillView { get; }

    /// <summary>How many sources have finished, so a long scan shows movement rather than nothing.
    /// Every terminal state counts (absent and stopped sources too), or the bar stalls below its total.</summary>
    public int ScanProgressTotal => ProviderStatuses.Count;

    public int ScanProgressDone => ProviderStatuses.Count(p => p.State is ProviderScanState.Done
        or ProviderScanState.Error or ProviderScanState.NotInstalled or ProviderScanState.Cancelled);

    private bool IsSourceEnabled(string sourceId) => _settings.Current.Sources.GetValueOrDefault(sourceId, true);

    /// <summary>
    /// Clicking a source pill toggles it. Off: its rows leave the table immediately and the next scan
    /// skips it; the pill stays, dimmed, so a second click brings it back. The setting is the same one
    /// the settings window shows, so the two never disagree.
    /// </summary>
    [RelayCommand]
    private void ToggleSource(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return;

        var nowEnabled = !IsSourceEnabled(sourceId);
        ApplySourceEnabled(sourceId, nowEnabled);

        if (_providerStatusById.TryGetValue(sourceId, out var pill))
            pill.IsEnabled = nowEnabled;

        Persist();
        ApplyFilter();
        RefreshHeader();
        RefreshSelectionState();
    }

    /// <summary>
    /// The cancel button on a running source's pill. It stops only that one source: the rest of
    /// the scan is untouched and its result stays valid. A no-op for a source that already finished,
    /// as the orchestrator itself is the one source of truth for "is this still running", so nothing here
    /// needs to duplicate that check.
    /// </summary>
    [RelayCommand]
    private void CancelSourceScan(string? sourceId)
    {
        if (!string.IsNullOrWhiteSpace(sourceId))
            _scanOrchestrator.CancelSource(sourceId);
    }

    /// <summary>Writes one source's on/off state and unticks anything it had selected.</summary>
    private void ApplySourceEnabled(string sourceId, bool enabled)
    {
        _settings.Current.Sources[sourceId] = enabled;

        // a hidden row must not stay ticked and be installed anyway
        if (!enabled)
        {
            // Caller (ToggleSource) runs RefreshSelectionState once itself right after this returns;
            // suppressed here so unselecting many rows of the same source costs one pass, not one per row.
            _suppressSelectionRefresh = true;
            try
            {
                foreach (var row in Updates.Where(r => r.Item.SourceId == sourceId))
                    row.IsSelected = false;
            }
            finally
            {
                _suppressSelectionRefresh = false;
            }
        }
    }

    /// <summary>Raised after every completed scan so the scheduler can reset its interval.</summary>
    public event Action? ScanCompleted;

    /// <summary>Interval-driven scan: identical work, but it notifies instead of demanding attention.
    /// Returns false when it stepped aside without scanning, so the scheduler keeps it due.</summary>
    public async Task<bool> RunScheduledScanAsync()
    {
        // The first thing ScanAsync does is cancel whatever is running, so without this a due
        // interval scan would kill the scan the user is sitting in front of.
        if (IsScanning || IsInstalling)
            return false;

        var generationBefore = _scanGeneration;
        await ScanAsync();

        // Only a scan that ran to its clean end speaks for the machine: a cancelled or superseded one leaves
        // stale or partial rows behind, and announcing those would report updates nobody just verified.
        if (_scanCompletedCleanly && _scanGeneration == generationBefore + 1)
        {
            // Only what can actually be installed, exactly like the headline: the Tier C rows (Store, game
            // launchers) exist whenever the client is merely present, so counting them made the balloon
            // announce updates at every interval, forever, on a machine that was fully up to date.
            var found = VisibleUpdateCount(u => u.CanInstall);
            var critical = VisibleUpdateCount(u => u.IsCritical && u.CanInstall);
            _notifications.NotifyUpdatesFound(found, critical);
        }

        // same cadence as the update scan: a machine that is left running still learns about a new
        // version of the app itself, not only on the next launch.
        await CheckForSelfUpdatePeriodicAsync(CancellationToken.None);
        return true;
    }

    // Scanning during an install is allowed (a scan reconciles rather than clearing, and
    // RemoveStaleRows skips Queued/Running), so the command has no CanExecute at all.
    // AllowConcurrentExecutions, because an async command disables itself for as long as its Task runs;
    // and this one only returns once every provider has unwound. After a stop that took pip or the winget
    // CLI a while to notice, the Scan button would otherwise stay greyed out long after the window said
    // it had finished. The button is disabled by its own binding while a scan shows as running, but F5
    // and the tray entry execute the command regardless, so the request itself is ignored while
    // IsScanning: only Stop ends a running scan, a second request never restarts it. Whatever overlap is
    // left (a request made while the window shows idle but the old run still unwinds) is serialised by
    // the scan gate inside.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            _logging.Info("Scan request ignored: a scan is already running.");
            return;
        }

        // Taken before the cache await below: a second click during a slow first cache read then owns
        // the scan, and this one steps aside instead of cancelling and restarting it on resume.
        var myGeneration = ++_scanGeneration;

        // The window says "searching" from the click on, not only once the gate is won: after Stop the old
        // scan can take a while to unwind, and a button that looks dead meanwhile gets pressed again.
        _queuedScanGeneration = myGeneration;
        RefreshIsScanning();
        var holdsGate = false;
        try
        {
            // Runs once, before anything else below: the very first scan of a session must not leave the
            // table empty while it works, when the previous session already knows what was there.
            await LoadCachedScanIfFirstRunAsync();
            if (myGeneration != _scanGeneration)
                return;

            // The rollback index is built on a pool thread and must exist before the first row is drained:
            // the drain only reads it (a missing one would leave every row without its rollback offer, and
            // nothing would add it later). Instant on every scan after the first.
            await _history.EnsureRollbackIndexAsync();
            if (myGeneration != _scanGeneration)
                return;

            // Two scans must never overlap. The older run's cleanup stops the flush timer and reports
            // "finished", so a scan started next to it would keep working while the window claimed to be
            // done and its results never reached the table. Ask the running scan to stop, then wait for
            // it to actually be gone before touching any shared state.
            _scanCts?.Cancel();
            await _scanGate.WaitAsync();
            holdsGate = true;

            // Programs installed or moved since the last scan must be found by "open folder" and by the
            // running-program check before an install.
            _installLocations.Invalidate();
        }
        finally
        {
            if (_queuedScanGeneration == myGeneration)
                _queuedScanGeneration = 0;

            // Once the gate is won, the scan below sets the state itself; refreshing here would flicker it.
            if (!holdsGate)
                RefreshIsScanning();
        }

        // A newer click already bumped the generation while this one queued for the gate; that request
        // owns the next scan (or has already run it). Running a second one back to back is the double-
        // scan-on-a-double-click bug; step aside instead.
        if (myGeneration != _scanGeneration)
        {
            _scanGate.Release();
            RefreshIsScanning();
            return;
        }

        // Everything after the gate lives in the try: a throw between here and the orchestrator call
        // would otherwise keep the permit for good, and every later scan would wait on a semaphore
        // nobody releases.
        try
        {
            // The previous scan has left (the gate above is held), so nothing else uses its token any more.
            // The enrichment source below is NOT disposed: its background tasks may still be reading it.
            var previousScanCts = _scanCts;
            var cts = new CancellationTokenSource();
            _scanCts = cts;
            previousScanCts?.Dispose();
            _scanCompletedCleanly = false;

            // Stop the previous scan's background enrichment before its rows are enriched out from under
            // it, and start this scan's own; per-source enrichment (below) fires against this token the
            // moment each source's own scan drains, not at the end of the whole scan.
            _enrichCts?.Cancel();
            _enrichCts = new CancellationTokenSource();
            _enrichedSourcesThisScan.Clear();

            // The table is reconciled, not cleared: rows already on screen stay put through the rescan so
            // it never blanks and flashes. Only the pills are rebuilt (small, at the
            // bottom, and their state genuinely resets to "scanning"). This scan's found keys accumulate
            // in _scanSeenKeys; anything not seen by the end is removed as gone.
            _scanSeenKeys.Clear();
            DropFinishedRows();
            ProviderStatuses.Clear();
            _providerStatusById.Clear();
            _liveStatuses.Clear();
            while (_pendingItems.TryDequeue(out _))
            {
            }

            _scanUiActive = true;
            RefreshIsScanning();
            _flushTimer.Start();

            await _scanOrchestrator.RunAsync(cts.Token);

            // A cancelled run returns normally instead of throwing (the orchestrator abandons its slow
            // sources), so the token is what says whether every source really finished.
            _scanCompletedCleanly = !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            // cancelling is a normal outcome, and whatever was already found stays visible
            _scanCompletedCleanly = false;
        }
        finally
        {
            // FinishScanUi itself must never be allowed to keep the gate held: a throw anywhere inside it
            // (a ScanCompleted subscriber, an icon or history read, ...) would propagate straight past
            // Release() below and hang every scan after this one. No-op when Cancel already ended the UI
            // side of this scan.
            try
            {
                await FinishScanUiAsync(_scanCompletedCleanly);
            }
            finally
            {
                _scanGate.Release();
            }
        }
    }

    /// <summary>
    /// Takes the window out of the scanning state. Runs at most once per scan, from whichever comes first:
    /// the orchestrator finishing, or the user cancelling.
    /// </summary>
    private async Task FinishScanUiAsync(bool completedCleanly)
    {
        if (!_scanUiActive)
            return;

        _scanUiActive = false;
        _flushTimer.Stop();
        Flush();
        WasOffline = _scanOrchestrator.WasOffline;
        AllOnlineSourcesFailed = _scanOrchestrator.AllOnlineSourcesFailed;

        // A clean scan removes the rows nothing found this time; a cancelled one keeps the partial
        // table untouched (its seen-set is incomplete, so it must not delete anything).
        if (completedCleanly)
        {
            _scanResultKnown = true;
            RemoveStaleRows();

            // The save is file I/O; off the UI thread so a slow disk never stalls the window right as
            // a scan settles. The snapshot of items is taken here, on the UI thread, before the hop.
            var itemsToSave = Updates.Select(row => row.Item).ToList();
            await Task.Run(() => _scanResultCache.Save(itemsToSave));

            // A key whose row left the table some other way (installed and dropped as finished, removed by
            // a single-source rescan) is no longer "only from the cache"; left in, it kept the banner up for
            // the rest of the session.
            _cacheOnlyKeys.IntersectWith(_rowsByKey.Keys);
            if (_cacheOnlyKeys.Count == 0)
                CachedResultSavedAt = null;
        }

        RefreshIsScanning();

        // Only scan 1 (cold) and scan 5 (warmed up) of a session, taken right when the scan
        // itself settles so the number reflects this run, not whatever the window does a moment later.
        _finishedScanCount++;
        if (_finishedScanCount is 1 or 5)
        {
            _logging.Event(
                ActivityLog.App,
                ActivityLog.WorkingSetAfterScan(_finishedScanCount, System.Diagnostics.Process.GetCurrentProcess().WorkingSet64));
        }

        // Only a finished scan restarts the interval: a scan the user stopped checked nothing, and postponed a
        // due scheduled scan by a whole interval.
        if (completedCleanly)
            ScanCompleted?.Invoke();

        // Fire-and-forget, after the scan UI has settled: this may show a dialog and do network I/O, neither
        // of which the scan should wait on. A no-op once winget is present or once this session already
        // asked (WingetBootstrapService.NeedsOffer).
        // A mock session (--mock-sources) must never even ASK: NeedsOffer probes the real winget.exe on the
        // real machine, which has nothing to do with the mock table, and answering "yes, install it" would
        // install real software for a session meant to touch nothing real (the same session-level rule as
        // InstallQueue's elevation routing).
        // The offer is a modal question: it needs someone at the screen and a window to put it in front of.
        // When the gate is closed the question is not used up, the next scan that meets it asks.
        if (completedCleanly && !_installQueue.IsMockSession && _canShowWingetOffer()
            && _wingetBootstrap is { NeedsOffer: true } bootstrap)
            _ = Background.Run(OfferWingetBootstrapAsync(bootstrap), _logging, "winget bootstrap offer");
    }

    private async Task OfferWingetBootstrapAsync(Services.WingetBootstrapService bootstrap)
    {
        await bootstrap.OfferAsync(CancellationToken.None);

        // Only worth a rescan if the offer actually put winget on the machine; NeedsOffer is now false
        // either way (declined or installed), so ProcessRunner.Exists is the only way left to tell them apart.
        if (bootstrap.IsWingetPresent)
            await RescanSingleSourceAsync("winget");
    }

    /// <summary>True between a scan starting and the window leaving the scanning state.</summary>
    private bool _scanUiActive;

    /// <summary>Stop means stop; the window leaves the scanning state at once rather than waiting for
    /// the slowest provider.</summary>
    [RelayCommand]
    private async Task CancelScan()
    {
        // A request still waiting for the gate is stopped too: it steps aside instead of starting a scan the
        // user just asked to stop.
        _scanGeneration++;
        _scanCts?.Cancel();

        // A single-source rescan holds the scanning state too, so Stop must reach it as well. Swapped for a
        // fresh source first, so a rescan started after this Stop runs normally; the old one is not
        // disposed, since a rescan starting right now may still be reading its token.
        Interlocked.Exchange(ref _rescanCts, new CancellationTokenSource()).Cancel();

        await FinishScanUiAsync(completedCleanly: false);
        RefreshIsScanning();
    }

    /// <summary>Cancelled by <see cref="CancelScan"/> to stop every single-source rescan running at that
    /// moment. Read and swapped from any thread.</summary>
    private CancellationTokenSource _rescanCts = new();

    /// <summary>Single-source rescans currently running (UI thread only). Each holds the scanning state on
    /// until it ends, independent of whether a full scan starts or finishes in the meantime.</summary>
    private int _singleSourceRescanLeases;

    /// <summary>The window is scanning while a full scan owns the UI, a scan request is waiting for its turn, or any
    /// single-source rescan holds a lease; none of them may clear a state another still needs.</summary>
    private void RefreshIsScanning() => IsScanning = _scanUiActive || ScanRequestQueued || _singleSourceRescanLeases > 0;

    /// <summary>Generation of the newest scan request that has not yet won the scan gate (0 = none). Stop and
    /// a newer click both move <see cref="_scanGeneration"/> on, which retires the request at once.</summary>
    private int _queuedScanGeneration;

    private bool ScanRequestQueued => _queuedScanGeneration != 0 && _queuedScanGeneration == _scanGeneration;

    partial void OnIsScanningChanged(bool value)
    {
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>Searches top-level rows AND their children; see <see cref="OnInstallStatusChanged"/>.</summary>
    internal UpdateRowViewModel? FindRow(string sourceId, string itemId)
    {
        foreach (var row in Updates)
        {
            if (row.Item.Id == itemId && row.Item.SourceId == sourceId)
                return row;

            foreach (var child in row.Children)
            {
                if (child.Item.Id == itemId && child.Item.SourceId == sourceId)
                    return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Providers report finds from their own threads. Pushing each one straight into the UI with a
    /// blocking dispatcher call plus a full recount of every header figure would stall the window for
    /// most of a second on a large scan, so finds are queued here instead and drained
    /// in batches by a timer, keeping the UI responsive no matter how many results arrive.
    /// </summary>
    private void OnItemFound(UpdateItem item) => _pendingItems.Enqueue(item);

    /// <summary>
    /// Past this many rows the drain slows down. Every tick re-groups and re-counts the whole table, and
    /// that work grows with the rows already on screen, not with the handful that just arrived; five ticks
    /// a second stay cheap on a short table and start costing more than the finds are worth on a long one.
    /// </summary>
    private const int SlowFlushRowCount = 100;

    private static readonly TimeSpan FastFlushInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SlowFlushInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>One timer-driven pass over everything the providers reported since the last tick.</summary>
    internal void Flush()
    {
        // Which sources had finished BEFORE the drain: a source reports its last item before it reports
        // Done, so every item of these is drained below. One that finishes while the drain runs is left for
        // the next tick; read after the drain, it would be enriched without the items that arrived meanwhile,
        // and enrichment runs only once per source per scan.
        var doneBeforeDrain = _liveStatuses.Values
            .Where(status => status.State == ProviderScanState.Done)
            .Select(status => status.SourceId)
            .ToHashSet(StringComparer.Ordinal);

        var added = DrainPendingItems();

        if (added.Count > 0)
        {
            // Before FlushProviderStatuses, not after: a source reporting Done on the same tick as its
            // last items triggers EnrichSource, which reads Updates and fires only once per source per
            // scan; anything still missing from Updates then is never enriched at all.
            // One notification for the whole batch; Updates is not what the table binds to, so this
            // costs nothing; the visible set then gains the new rows one at a time, which is what gives
            // each of them its own ease-in instead of re-animating the whole table.
            Updates.AddRange(added);
        }

        // Assigning Interval restarts the timer, so it is only written when the band really changed.
        var interval = Updates.Count >= SlowFlushRowCount ? SlowFlushInterval : FastFlushInterval;
        if (_flushTimer.Interval != interval)
            _flushTimer.Interval = interval;

        FlushProviderStatuses(doneBeforeDrain);

        if (added.Count > 0)
        {
            RefreshSelectionState();
            ResolveIconsAsync(added);
        }

        // one recount per tick instead of one per item
        RefreshHeader(resortManualOnly: false);
    }

    private static readonly IReadOnlyDictionary<string, HistoryEntry> NoRollbackIndex =
        new Dictionary<string, HistoryEntry>();

    private List<UpdateRowViewModel> DrainPendingItems()
    {
        var added = new List<UpdateRowViewModel>();
        // A row replaced within this same drain (version genuinely changed) leaves Updates once, in the
        // one RemoveRange below, rather than through one Updates.Remove per replaced row.
        var replaced = new List<UpdateRowViewModel>();
        // Reads the HistoryService's index as it stands (built off the UI thread before a scan starts, then
        // kept current by Append and cleanup), never a file read: this runs once per flush tick on the
        // dispatcher. No index yet simply means no rollback offers on these rows.
        var lastSuccessfulInstalls = _history.RollbackIndex ?? NoRollbackIndex;

        while (_pendingItems.TryDequeue(out var item))
        {
            var key = PackageKey(item);
            _scanSeenKeys.Add(key);

            // Same update still on screen from the last scan: keep its row so the table never blanks.
            // Only replace it when the version genuinely changed or it carries a stale install result.
            if (_rowsByKey.TryGetValue(key, out var existing))
            {
                // A live result of any shape, unchanged or not, means this row is no longer serving
                // last session's cached data.
                _cacheOnlyKeys.Remove(key);

                // A row that is part of an install batch must never be discarded and replaced here, even
                // though the source still reports its pre-install data (the install has not finished). Replacing
                // it orphans the running row (status reports are matched by source+id against Updates, never
                // by object identity) and leaves a fresh, Idle look-alike that reads as installable, letting
                // the same package be queued for a SECOND, concurrent install while the first still runs.
                if (existing.InstallState is RowInstallState.Queued or RowInstallState.Running)
                    continue;

                if (existing.InstallState == RowInstallState.Idle
                    && string.Equals(existing.AvailableVersion, item.AvailableVersion, StringComparison.Ordinal)
                    && string.Equals(existing.InstalledVersion, item.InstalledVersion, StringComparison.Ordinal)
                    && ChildrenUnchanged(existing, item))
                {
                    // A snooze can expire between scans even when nothing else about the row changed;
                    // the common case, so this fast path must not skip re-checking it. A rollback becoming
                    // available (a fresh successful install just landed in history) is the same story.
                    ApplySnoozeState(existing);
                    ApplyPersistentFailureState(existing);
                    ApplyDowngradeState(existing, lastSuccessfulInstalls);
                    continue;
                }

                // Also out of THIS batch, not only out of the table; a row created earlier in the same
                // drain is not in Updates yet.
                replaced.Add(existing);
                added.Remove(existing);
                _rowsByKey.Remove(key);
            }

            var ignored = IsIgnored(item);
            var row = new UpdateRowViewModel(item, _logging, Ignore, this, ShowErrorDetail, ShowChangelogAsync)
            {
                IsIgnored = ignored,
                IsIgnoredAllVersions = _settings.Current.Excludes.Contains(PackageKey(item)),
                IsSelected = item.CanInstall && item.IsCritical
                             && _settings.Current.Install.PreselectCritical && !ignored,
            };
            // Also sets the row's IsSnoozed, which clears IsSelected above back off again for a snoozed
            // critical row; the same "not now" that made it hide from the default view.
            ApplySnoozeState(row);
            // Same reasoning for a package flagged NeedsManualWork by an earlier session: the row must
            // read as flagged from the moment it appears, not only after the user tries it and fails a
            // fourth time.
            ApplyPersistentFailureState(row);
            // Same reasoning again, a package rollback-eligible from an earlier session's install
            // must read as such the moment its row appears.
            ApplyDowngradeState(row, lastSuccessfulInstalls);
            TrackSelection(row);
            _rowsByKey[key] = row;
            added.Add(row);
        }

        Updates.RemoveRange(replaced);

        return added;
    }

    /// <summary>Wires a freshly created row into selection-state recomputation. Every path that creates
    /// an <see cref="UpdateRowViewModel"/> must call this before the row reaches the UI: a row that is
    /// not tracked can still be ticked on screen, but <see cref="RefreshSelectionState"/> never reruns for
    /// it, so CanInstallSelected/SelectionSummary/HasSelection/AllVisibleSelected/SelectAllTooltip all
    /// stay stuck at their pre-row values and the toolbar's install button stays disabled although the row
    /// reads as selected.</summary>
    private void TrackSelection(UpdateRowViewModel row)
    {
        row.PropertyChanged += (_, args) =>
        {
            OnRowColumnDataChanged(args.PropertyName);

            // A bulk change raises these once itself when it is done (see _suppressSelectionRefresh).
            if (_suppressSelectionRefresh)
                return;

            // The headline counts pending installs, so a row finishing (or failing) moves it.
            if (args.PropertyName == nameof(UpdateRowViewModel.InstallState))
                OnPropertyChanged(nameof(HeadlineText));

            if (args.PropertyName is nameof(UpdateRowViewModel.IsSelected)
                or nameof(UpdateRowViewModel.CanInstall)
                or nameof(UpdateRowViewModel.DownloadSizeBytes))
            {
                RefreshSelectionState();
            }
        };
    }

    /// <summary>
    /// The caller's own version comparison alone is meaningless for a row whose parent carries no version;
    /// Steam's single "steam" row has both <c>InstalledVersion</c> and <c>AvailableVersion</c> null, so that
    /// comparison would be null-equal-null every scan, decide nothing changed, and freeze Steam's children
    /// list at whatever the first scan found. True only when both lists are the same length and every
    /// child's id/versions still match, in order (the source reports its children in a stable order).
    /// </summary>
    private static bool ChildrenUnchanged(UpdateRowViewModel existing, UpdateItem item)
    {
        if (existing.Children.Count != item.Children.Count)
            return false;

        for (var i = 0; i < item.Children.Count; i++)
        {
            var existingChild = existing.Children[i];
            var newChild = item.Children[i];

            if (!string.Equals(existingChild.Item.Id, newChild.Id, StringComparison.Ordinal)
                || !string.Equals(existingChild.AvailableVersion, newChild.AvailableVersion, StringComparison.Ordinal)
                || !string.Equals(existingChild.InstalledVersion, newChild.InstalledVersion, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Finished rows leave when the next scan starts; Succeeded only, a failure keeps its
    /// Retry/Details.</summary>
    private void DropFinishedRows()
    {
        var finished = _rowsByKey
            .Where(pair => pair.Value.InstallState == RowInstallState.Succeeded)
            .Select(pair => pair.Key)
            .ToList();

        var toRemove = new List<UpdateRowViewModel>(finished.Count);
        foreach (var key in finished)
        {
            if (_rowsByKey.TryGetValue(key, out var row))
                toRemove.Add(row);

            _rowsByKey.Remove(key);
        }

        // One notification for the whole sweep, not one per row: Updates.CollectionChanged runs a full
        // ApplyFilter every time it fires.
        Updates.RemoveRange(toRemove);
    }

    /// <summary>Drops rows no longer found by a completed scan; except a row queued or running, which
    /// is not the scan's to remove.</summary>
    private void RemoveStaleRows()
    {
        var stale = _rowsByKey.Keys
            .Where(k => !_scanSeenKeys.Contains(k))
            .Where(k => _rowsByKey[k].InstallState is not (RowInstallState.Queued or RowInstallState.Running))
            .Where(k => !IsRowFromIncompleteSource(k))
            .ToList();

        var toRemove = new List<UpdateRowViewModel>(stale.Count);
        foreach (var key in stale)
        {
            if (_rowsByKey.TryGetValue(key, out var row))
                toRemove.Add(row);

            _rowsByKey.Remove(key);
            _cacheOnlyKeys.Remove(key);
        }

        // One notification for the whole sweep, not one per row: Updates.CollectionChanged runs a full
        // ApplyFilter every time it fires.
        Updates.RemoveRange(toRemove);
    }

    /// <summary>
    /// Copies the providers' current state into their pills. The ProviderStatus objects are owned by
    /// the scanning threads and mutated in place, so reading them here always yields the latest
    /// counts without any provider ever having to marshal to the UI thread.
    /// </summary>
    /// <param name="doneBeforeDrain">The sources whose items have all reached the table, see
    /// <see cref="Flush"/>; only these are enriched. Null (a pill rebuild outside a flush) enriches none,
    /// the next flush does.</param>
    private void FlushProviderStatuses(IReadOnlySet<string>? doneBeforeDrain = null)
    {
        // One shared instant for every pill's "has this been running a while" check; cheap,
        // and reuses the flush tick that already runs throughout a scan instead of a second timer.
        var now = DateTimeOffset.UtcNow;

        foreach (var status in _liveStatuses.Values)
        {
            // Ahead of the Tier-C pill skip below: every source's rows should enrich once it is done,
            // pill or not.
            if (doneBeforeDrain?.Contains(status.SourceId) == true && _enrichedSourcesThisScan.Add(status.SourceId))
                EnrichSource(status.SourceId);

            var (category, tier) = SourceGroupingFor(status.SourceId);

            // The "you open it yourself" sources (Tier C: stores, launchers, Steam, vendor driver pages)
            // get NO pill at all; not one each and not a combined one. The bottom row is
            // for the sources that actually install something; the manual rows have their own category.
            if (tier == SourceTier.C)
            {
                RemovePill(status.SourceId);
                continue;
            }

            if (!_providerStatusById.TryGetValue(status.SourceId, out var vm))
            {
                vm = new ProviderStatusViewModel(status.SourceId, category, tier)
                {
                    IsEnabled = IsSourceEnabled(status.SourceId),
                };
                _providerStatusById[status.SourceId] = vm;
                ProviderStatuses.Add(vm);
            }

            vm.Apply(status);
            vm.RefreshSlowState(now);
        }
    }

    private void RemovePill(string key)
    {
        if (!_providerStatusById.TryGetValue(key, out var vm))
            return;

        ProviderStatuses.Remove(vm);
        _providerStatusById.Remove(key);
    }

    /// <summary>The category and tier a pill groups by, read from the source itself so it never drifts.</summary>
    private (UpdateCategory Category, SourceTier Tier) SourceGroupingFor(string sourceId)
    {
        var source = _sources.FirstOrDefault(s => s.Id == sourceId);
        return source is null ? (UpdateCategory.Software, SourceTier.A) : (source.Category, source.Tier);
    }

    /// <summary>Icons come from the registry and the file system, so they are resolved off the UI thread.</summary>
    private void ResolveIconsAsync(IReadOnlyList<UpdateRowViewModel> rows)
    {
        if (rows.Count == 0)
            return;

        // Children (Steam's games) are shown inside their parent row, so they need icons of their own.
        var all = rows.Concat(rows.SelectMany(row => row.Children)).ToList();

        _ = Task.Run(() =>
        {
            foreach (var row in all)
            {
                try
                {
                    var icon = _iconCache.GetIcon(row.Item.SourceId, row.Item.Id, row.Item.Name, row.Item.OpenTarget);
                    if (icon is null)
                        continue;

                    icon.Freeze();
                    _dispatcher.InvokeAsync(() => row.Icon = icon, DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    // One row's icon resolution failing (a locked file, an odd registry entry) must not
                    // silently stop every remaining row in this batch from getting an icon.
                    _logging.Warn($"Icon resolution failed for '{row.Item.SourceId}:{row.Item.Id}': {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// WindowsUpdateSource.Install throws exactly the "no longer offered" message only when the update's
    /// own GUID is genuinely absent from a fresh search; an ordinary revision bump between scan and
    /// install never reaches this (WindowsUpdateMatching.PickInstallTarget handles that on its own).
    /// Genuinely gone means the row is stale, so this rescans that ONE source in the background,
    /// without disturbing whatever full scan may or may not be running and without touching any other
    /// source's rows the way a full <see cref="ScanAsync"/> would.
    /// </summary>
    internal async Task RescanSingleSourceAsync(string sourceId)
    {
        var beforeKeys = _rowsByKey
            .Where(pair => pair.Value.Item.SourceId == sourceId
                && pair.Value.InstallState is not (RowInstallState.Queued or RowInstallState.Running))
            .Select(pair => pair.Key)
            .ToList();

        // _scanSeenKeys accumulates across every full scan and is only reset when one starts, so it can
        // still hold this source's OLD keys from before this call. Using it here would never see a row
        // as stale, so what THIS rescan actually finds is tracked separately, for exactly this one source.
        var seenThisRescan = new HashSet<string>(StringComparer.Ordinal);
        void OnFound(UpdateItem item)
        {
            if (string.Equals(item.SourceId, sourceId, StringComparison.Ordinal))
                lock (seenThisRescan)
                    seenThisRescan.Add(PackageKey(item));
        }

        // Neither a scheduled offer nor the "no longer offered" repair above runs on the UI thread, and
        // this whole method must work from either; every touch of view-model state below goes through the
        // dispatcher rather than assuming one.
        // Below, so the pill's own spinner and the top progress bar read exactly like a full scan for as
        // long as this one source is being checked; this rescan holds a lease on the scanning state rather
        // than reading and restoring it, so a full scan starting or ending meanwhile is never overridden.
        void OnStatus(ProviderStatus status)
        {
            if (!string.Equals(status.SourceId, sourceId, StringComparison.Ordinal))
                return;

            _dispatcher.InvokeAsync(() =>
            {
                if (_providerStatusById.TryGetValue(sourceId, out var pill))
                    pill.Apply(status);

                RefreshHeader(resortManualOnly: false);
            });
        }

        _scanOrchestrator.ItemFound += OnFound;
        _scanOrchestrator.ProviderStatusChanged += OnStatus;

        _dispatcher.Invoke(() =>
        {
            _singleSourceRescanLeases++;
            RefreshIsScanning();
        });

        try
        {
            await _scanOrchestrator.RescanSourceAsync(sourceId, Volatile.Read(ref _rescanCts).Token);
        }
        catch (Exception ex)
        {
            _logging.Warn($"Automatic rescan of '{sourceId}' after a stale install result did not complete: {ex.Message}");
            return;
        }
        finally
        {
            _scanOrchestrator.ItemFound -= OnFound;
            _scanOrchestrator.ProviderStatusChanged -= OnStatus;

            _dispatcher.Invoke(() =>
            {
                _singleSourceRescanLeases--;
                RefreshIsScanning();
            });
        }

        _dispatcher.Invoke(() =>
        {
            Flush();

            // A transient failure or the user's own cancel must never read as "not offered any more":
            // OnStatus above kept _liveStatuses current for this source through every state change, so its
            // value here is whatever this rescan actually ended on. Only a clean Done or NotInstalled means
            // the source genuinely ran its course; anything else (Error, Cancelled, or no status at all,
            // which should not happen but must not be read as success either) leaves every existing row
            // for this source exactly as it was, same as a full scan's own RemoveStaleRows already does for
            // its cache-only rows.
            var rescanSucceeded = _liveStatuses.TryGetValue(sourceId, out var finalStatus)
                && finalStatus.State is ProviderScanState.Done or ProviderScanState.NotInstalled;

            if (rescanSucceeded)
            {
                // Anything from before that this rescan did not rediscover is genuinely gone; drop it
                // exactly like RemoveStaleRows does for a full scan, but scoped to this one source's own
                // rows only.
                foreach (var key in beforeKeys)
                {
                    if (seenThisRescan.Contains(key))
                        continue;

                    if (_rowsByKey.TryGetValue(key, out var row)
                        && row.InstallState is not (RowInstallState.Queued or RowInstallState.Running))
                    {
                        Updates.Remove(row);
                        _rowsByKey.Remove(key);
                    }
                }
            }

            RefreshHeader(resortManualOnly: false);
        });
    }

    /// <summary>
    /// Providers report from their own threads. Hopping onto the dispatcher for every single status
    /// change (including once per found item, each recounting every header figure) would bury the UI
    /// thread under thousands of queued callbacks on a scan with a few hundred results. Nothing is
    /// marshalled here; the flush timer picks the state up at a fixed rate instead.
    /// </summary>
    private void OnProviderStatusChanged(ProviderStatus status) => _liveStatuses[status.SourceId] = status;
}
