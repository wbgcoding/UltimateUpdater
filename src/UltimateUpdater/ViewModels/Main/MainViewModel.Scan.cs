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
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels.Main;

// Scanning: starting, cancelling and finishing a scan run, source toggles and provider-status pills,
// and the batched drain of found items into the table.
public partial class MainViewModel
{
    [ObservableProperty]
    private bool isScanning;

    /// <summary>Built with the view model, not on first use: the cache resolves its file path in its constructor, and a lazily built one would take whatever path is current at the first save, which happens asynchronously after the scan settles.</summary>
    private readonly ScanResultCache _scanResultCache;


    /// <summary>Rows on screen that still hold last session's cached data because nothing this scan reported has replaced them yet (see <see cref="LoadCachedScanIfFirstRunAsync"/>).</summary>
    private readonly HashSet<string> _cacheOnlyKeys = new(StringComparer.Ordinal);

    private bool _cacheLoadAttempted;

    /// <summary>True once a scan finished cleanly or last session's saved result loaded. Until then "everything is up to date" would be a claim nobody checked.</summary>
    private bool _scanResultKnown;

    /// <summary>How many scans have finished this session, so the working-set line is logged after scan 1 and scan 5 only.</summary>
    private int _finishedScanCount;

    /// <summary>When the table's cached rows were captured, or null once no row on screen is cached. Drives the "Result from ..., checking again" banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CachedResultAgeText))]
    [NotifyPropertyChangedFor(nameof(HasCachedResult))]
    private DateTimeOffset? cachedResultSavedAt;

    public bool HasCachedResult => CachedResultSavedAt is not null;

    public string CachedResultAgeText => CachedResultSavedAt is { } savedAt
        ? string.Format(Loc["ScanCachedAge"], savedAt.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture))
        : string.Empty;

    /// <summary>True right after a scan that found no internet connection at all: network sources were skipped (see <see cref="ScanOrchestrator.WasOffline"/>), local sources ran normally.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineBannerText))]
    [NotifyPropertyChangedFor(nameof(HasConnectionBanner))]
    private bool wasOffline;

    /// <summary>True right after a scan where the system reported a connection but every network source failed with a connection error, e.g. a firewall rule against this app. Mirrored from <see cref="ScanOrchestrator.AllOnlineSourcesFailed"/> in <see cref="FinishScanUiAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineBannerText))]
    [NotifyPropertyChangedFor(nameof(HasConnectionBanner))]
    private bool allOnlineSourcesFailed;

    /// <summary>"No connection at all" wins when both apply: it covers the offline case too, while a failing source on a reported connection is the more specific finding.</summary>
    public string OfflineBannerText => WasOffline
        ? Loc["ScanOfflineBanner"]
        : AllOnlineSourcesFailed
            ? Loc["ScanNoConnectionBanner"]
            : string.Empty;

    public bool HasConnectionBanner => !string.IsNullOrEmpty(OfflineBannerText);

    /// <summary>Fills the table from the previous session's saved scan before this session's first scan does any work, so the window is not empty for the length of a real scan. Runs once per app lifetime; later scans reconcile normally and only overwrite the cache in <see cref="FinishScanUiAsync"/>.</summary>
    private async Task LoadCachedScanIfFirstRunAsync()
    {
        if (_cacheLoadAttempted)
            return;
        _cacheLoadAttempted = true;

        // File I/O, so off the UI thread; the result is read back on the UI thread where its rows are built.
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
            // The same states a live row gets on creation. Without them a package flagged NeedsManualWork would come back Idle and installable after a restart, switching the three-strikes protection off.
            ApplySnoozeState(row);
            ApplyPersistentFailureState(row);
            TrackSelection(row);
            _rowsByKey[key] = row;
            _cacheOnlyKeys.Add(key);
            newRows.Add(row);
        }

        // One notification for the whole cache load: Updates.CollectionChanged runs a full ApplyFilter each time.
        Updates.AddRange(newRows);

        // The live scan keeps an unchanged cached row on its fast path and never resolves its icon, so this is the only place a restored row gets one.
        ResolveIconsAsync(newRows);

        RefreshHeader(resortManualOnly: false);

        // Only a row from the cache actually reaching the screen counts; a save with zero new rows proves nothing.
        if (newRows.Count > 0)
        {
            _logging.Event(
                ActivityLog.App,
                ActivityLog.FirstCachedRow(System.Diagnostics.Process.GetCurrentProcess().StartTime, DateTime.Now));
        }
    }

    /// <summary>A row survives a scan that never got to re-check it: one whose source failed outright, or was stopped from its pill and delivered only part of its list. Only a source that ran to the end without reporting the package means it is gone.</summary>
    private bool IsRowFromIncompleteSource(string key) =>
        _rowsByKey.TryGetValue(key, out var row)
        && _liveStatuses.TryGetValue(row.Item.SourceId, out var status)
        && status.State is ProviderScanState.Cancelled or ProviderScanState.Error;

    public ObservableCollection<ProviderStatusViewModel> ProviderStatuses { get; } = [];

    /// <summary>The pills at the bottom, grouped like the table: Software, Store, System and drivers, Developer tools, Games, then Steam and the launcher rows. Only sources worth a pill appear (found something, failed, still scanning or switched off).</summary>
    public ICollectionView PillView { get; }

    /// <summary>How many sources have finished, so a long scan shows movement. Every terminal state counts, absent and stopped sources too, or the bar stalls below its total.</summary>
    public int ScanProgressTotal => ProviderStatuses.Count;

    public int ScanProgressDone => ProviderStatuses.Count(p => p.State is ProviderScanState.Done
        or ProviderScanState.Error or ProviderScanState.NotInstalled or ProviderScanState.Cancelled);

    private bool IsSourceEnabled(string sourceId) => _settings.Current.Sources.GetValueOrDefault(sourceId, true);

    /// <summary>Clicking a source pill toggles it. Off: its rows leave the table at once and the next scan skips it; the pill stays, dimmed. It writes the same setting the settings window shows.</summary>
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

    /// <summary>The cancel button on a running source's pill: stops only that source and leaves the rest of the scan valid. A no-op for a source that already finished; the orchestrator is the authority on that.</summary>
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

        // A hidden row must not stay ticked and be installed anyway.
        if (!enabled)
        {
            // The caller (ToggleSource) runs RefreshSelectionState once itself, so unselecting many rows costs one pass.
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

    /// <summary>Interval-driven scan: the same work, but it notifies instead of demanding attention. Returns false when it stepped aside without scanning, so the scheduler keeps it due.</summary>
    public async Task<bool> RunScheduledScanAsync()
    {
        // ScanAsync ignores a request while a scan runs, which would count as scanned to the scheduler; stepping aside keeps it due. The same while an install runs.
        if (IsScanning || IsInstalling)
            return false;

        var generationBefore = _scanGeneration;
        await ScanAsync();

        // Only a scan that ran to its clean end speaks for the machine: a cancelled or superseded one leaves stale or partial rows.
        if (_scanCompletedCleanly && _scanGeneration == generationBefore + 1)
        {
            // Only what can really be installed, like the headline: Tier C rows (Store, launchers) exist whenever the client is present and would make the balloon announce updates every interval.
            var found = VisibleUpdateCount(u => u.CanInstall);
            var critical = VisibleUpdateCount(u => u.IsCritical && u.CanInstall);
            _notifications.NotifyUpdatesFound(found, critical);
        }

        // Same cadence as the update scan, so a machine left running still learns about a new app version.
        await CheckForSelfUpdatePeriodicAsync(CancellationToken.None);
        return true;
    }

    // Scanning during an install is allowed (a scan reconciles, and RemoveStaleRows skips Queued and Running), so the command has no CanExecute. AllowConcurrentExecutions because an async command disables itself while its Task runs, and this one returns only after every provider unwound: the Scan button would stay grey after a slow stop. The button is disabled by its binding while scanning, but F5 and the tray entry run the command regardless, so a request during IsScanning is ignored: only Stop ends a scan. Any remaining overlap is serialised by the scan gate.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            _logging.Info("Scan request ignored: a scan is already running.");
            return;
        }

        // Taken before the cache await: a request made during a slow first cache read bumps the generation, and this one steps aside on resume instead of scanning twice.
        var myGeneration = ++_scanGeneration;

        // The window says "searching" from the click on: after Stop the old scan can take a while to unwind, and a button that looks dead gets pressed again.
        _queuedScanGeneration = myGeneration;
        RefreshIsScanning();
        var holdsGate = false;
        try
        {
            // The first scan of a session must not leave the table empty when the previous session knows what was there.
            await LoadCachedScanIfFirstRunAsync();
            if (myGeneration != _scanGeneration)
                return;

            // The rollback index is built on a pool thread and must exist before the first row drains: the drain only reads it, and nothing would add a missing offer later. Instant after the first scan.
            await _history.EnsureRollbackIndexAsync();
            if (myGeneration != _scanGeneration)
                return;

            // Two scans must never overlap: the older run's cleanup stops the flush timer and reports finished, so a scan started next to it would keep working while the window claimed to be done. Cancel it and wait until it is gone before touching shared state.
            _scanCts?.Cancel();
            await _scanGate.WaitAsync();
            holdsGate = true;

            // Programs installed or moved since the last scan must be found by "open folder" and the running-program check.
            _installLocations.Invalidate();
        }
        finally
        {
            if (_queuedScanGeneration == myGeneration)
                _queuedScanGeneration = 0;

            // Once the gate is won, the scan sets the state itself; refreshing here would flicker.
            if (!holdsGate)
                RefreshIsScanning();
        }

        // A newer click bumped the generation while this one queued for the gate and owns the next scan. Running a second one is the double-click double scan; step aside.
        if (myGeneration != _scanGeneration)
        {
            _scanGate.Release();
            RefreshIsScanning();
            return;
        }

        // Everything after the gate lives in the try: a throw before the orchestrator call would keep the permit for good and every later scan would wait on it.
        try
        {
            // The previous scan has left, so nothing uses its token any more. The enrichment source below is not disposed: its background tasks may still read it.
            var previousScanCts = _scanCts;
            var cts = new CancellationTokenSource();
            _scanCts = cts;
            previousScanCts?.Dispose();
            _scanCompletedCleanly = false;

            // Stop the previous scan's enrichment before its rows are enriched out from under it, and start this scan's own; each source's enrichment fires as that source drains.
            _enrichCts?.Cancel();
            _enrichCts = new CancellationTokenSource();
            _enrichedSourcesThisScan.Clear();

            // The table is reconciled, not cleared, so it never blanks. Only the pills are rebuilt, since their state really resets to scanning. Found keys accumulate in _scanSeenKeys; whatever is unseen at the end is removed.
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

            // A cancelled run returns normally (the orchestrator abandons slow sources), so the token says whether every source finished.
            _scanCompletedCleanly = !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            // Cancelling is normal; what was found stays visible.
            _scanCompletedCleanly = false;
        }
        finally
        {
            // FinishScanUi must not keep the gate held: a throw inside it (a ScanCompleted subscriber, an icon or history read) would skip Release() below and hang every later scan. No-op when Cancel already ended the UI side.
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

    /// <summary>Takes the window out of the scanning state. Runs at most once per scan, from whichever comes first: the orchestrator finishing or the user cancelling.</summary>
    private async Task FinishScanUiAsync(bool completedCleanly)
    {
        if (!_scanUiActive)
            return;

        _scanUiActive = false;
        _flushTimer.Stop();
        Flush();
        WasOffline = _scanOrchestrator.WasOffline;
        AllOnlineSourcesFailed = _scanOrchestrator.AllOnlineSourcesFailed;

        // A clean scan removes rows nothing found; a cancelled one keeps the partial table, since its seen-set is incomplete.
        if (completedCleanly)
        {
            _scanResultKnown = true;
            RemoveStaleRows();

            // File I/O, so off the UI thread; the snapshot is taken here first.
            var itemsToSave = Updates.Select(row => row.Item).ToList();
            await Task.Run(() => _scanResultCache.Save(itemsToSave));

            // A key whose row left the table another way (installed and dropped, removed by a single-source rescan) is no longer cache-only; left in, it would keep the banner up.
            _cacheOnlyKeys.IntersectWith(_rowsByKey.Keys);
            if (_cacheOnlyKeys.Count == 0)
                CachedResultSavedAt = null;
        }

        RefreshIsScanning();

        // Only scan 1 (cold) and scan 5 (warm), taken as the scan settles so the figure reflects this run.
        _finishedScanCount++;
        if (_finishedScanCount is 1 or 5)
        {
            _logging.Event(
                ActivityLog.App,
                ActivityLog.WorkingSetAfterScan(_finishedScanCount, System.Diagnostics.Process.GetCurrentProcess().WorkingSet64));
        }

        // Only a finished scan restarts the interval: a stopped scan checked nothing.
        if (completedCleanly)
        {
            ScanCompleted?.Invoke();
            StartCleanupPrescanIfWanted();
        }

        // Fire and forget after the scan UI settled: it may show a dialog and do network I/O, which the scan should not wait on. A no-op once winget is present or this session already asked (WingetBootstrapService.NeedsOffer). A mock session (--mock-sources) must never ask: NeedsOffer probes the real winget.exe, and "yes" would install real software in a session meant to touch nothing (same rule as InstallQueue's elevation routing). The offer is modal and needs someone at the screen; with the gate closed the question is not used up and the next scan asks.
        if (completedCleanly && !_installQueue.IsMockSession && _canShowWingetOffer()
            && _wingetBootstrap is { NeedsOffer: true } bootstrap)
            _ = Background.Run(OfferWingetBootstrapAsync(bootstrap), _logging, "winget bootstrap offer");
    }

    private async Task OfferWingetBootstrapAsync(WingetBootstrapService bootstrap)
    {
        // The winget pill shows the download as its state text; reports queue on the dispatcher in order, so the clearing call lands after the last percentage.
        void ShowDownload(int? percent) => _dispatcher.InvokeAsync(() =>
        {
            if (!_providerStatusById.TryGetValue("winget", out var pill))
                return;

            pill.SetupPercent = percent;
            RefreshHeader(resortManualOnly: false);
        });

        WingetBootstrapOutcome outcome;
        try
        {
            outcome = await bootstrap.OfferAsync(new CallbackProgress<int>(percent => ShowDownload(percent)), CancellationToken.None);
        }
        finally
        {
            ShowDownload(null);
        }

        switch (outcome.Status)
        {
            case WingetBootstrapStatus.Installed:
                Dialogs.Success(Loc["WingetBootstrapDone"]);
                break;
            case WingetBootstrapStatus.Failed:
                var reason = Loc[outcome.ReasonKey ?? "WingetBootstrapReasonDownload"];
                Dialogs.Error(string.Format(Loc["WingetBootstrapFailed"], reason), details: outcome.Detail);
                break;
        }

        // Only worth a rescan if winget really arrived; NeedsOffer is false either way, so ProcessRunner.Exists tells the outcomes apart.
        if (bootstrap.IsWingetPresent)
            await RescanSingleSourceAsync("winget");
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    /// <summary>True between a scan starting and the window leaving the scanning state.</summary>
    private bool _scanUiActive;

    /// <summary>Stop means stop: the window leaves the scanning state at once instead of waiting for the slowest provider.</summary>
    [RelayCommand]
    private async Task CancelScan()
    {
        // A request still waiting for the gate is stopped too, so it does not start a scan the user just stopped.
        _scanGeneration++;
        _scanCts?.Cancel();

        // A single-source rescan holds the scanning state too, so Stop must reach it. Swapped for a fresh source first so a later rescan runs normally; the old one is not disposed, since a starting rescan may still read its token.
        Interlocked.Exchange(ref _rescanCts, new CancellationTokenSource()).Cancel();

        await FinishScanUiAsync(completedCleanly: false);
        RefreshIsScanning();
    }

    /// <summary>Cancelled by <see cref="CancelScan"/> to stop every running single-source rescan. Read and swapped from any thread.</summary>
    private CancellationTokenSource _rescanCts = new();

    /// <summary>Single-source rescans running now (UI thread only). Each holds the scanning state on until it ends, whatever a full scan does meanwhile.</summary>
    private int _singleSourceRescanLeases;

    /// <summary>The window is scanning while a full scan owns the UI, a scan request waits for its turn, or a single-source rescan holds a lease; none may clear a state another still needs.</summary>
    private void RefreshIsScanning() => IsScanning = _scanUiActive || ScanRequestQueued || _singleSourceRescanLeases > 0;

    /// <summary>Generation of the newest scan request that has not yet won the scan gate (0 = none). Stop and a newer click both advance <see cref="_scanGeneration"/>, which retires the request.</summary>
    private int _queuedScanGeneration;

    private bool ScanRequestQueued => _queuedScanGeneration != 0 && _queuedScanGeneration == _scanGeneration;

    partial void OnIsScanningChanged(bool value)
    {
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>Searches top-level rows and their children; see <see cref="OnInstallStatusChanged"/>.</summary>
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

    /// <summary>Providers report finds from their own threads. A blocking dispatcher call plus a full header recount per find would stall the window for most of a second on a large scan, so finds are queued here and drained in batches by a timer.</summary>
    private void OnItemFound(UpdateItem item) => _pendingItems.Enqueue(item);

    /// <summary>Past this many rows the drain slows down: every tick re-groups and re-counts the whole table, which grows with the rows already shown, not with the few that arrived.</summary>
    private const int SlowFlushRowCount = 100;

    private static readonly TimeSpan FastFlushInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SlowFlushInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>One timer-driven pass over everything the providers reported since the last tick.</summary>
    internal void Flush()
    {
        // Which sources had finished before the drain: a source reports its last item before Done, so all their items are drained below. One finishing during the drain waits for the next tick; enriching it now would miss items that arrived meanwhile, and enrichment runs once per source per scan.
        var doneBeforeDrain = _liveStatuses.Values
            .Where(status => status.State == ProviderScanState.Done)
            .Select(status => status.SourceId)
            .ToHashSet(StringComparer.Ordinal);

        var added = DrainPendingItems();

        if (added.Count > 0)
        {
            // Before FlushProviderStatuses: a source reporting Done on the same tick as its last items triggers EnrichSource, which reads Updates and fires once per source per scan. One notification for the whole batch; the table binds to the visible set, which gains the new rows one at a time so each eases in on its own.
            Updates.AddRange(added);
        }

        // Assigning Interval restarts the timer, so it is written only when the band changed.
        var interval = Updates.Count >= SlowFlushRowCount ? SlowFlushInterval : FastFlushInterval;
        if (_flushTimer.Interval != interval)
            _flushTimer.Interval = interval;

        FlushProviderStatuses(doneBeforeDrain);

        if (added.Count > 0)
        {
            RefreshSelectionState();
            ResolveIconsAsync(added);
        }

        // One recount per tick instead of one per item.
        RefreshHeader(resortManualOnly: false);
    }

    private static readonly IReadOnlyDictionary<string, HistoryEntry> NoRollbackIndex =
        new Dictionary<string, HistoryEntry>();

    private List<UpdateRowViewModel> DrainPendingItems()
    {
        var added = new List<UpdateRowViewModel>();
        // A row replaced within this drain leaves Updates in the single RemoveRange below.
        var replaced = new List<UpdateRowViewModel>();
        // Reads the HistoryService's index as it stands, never a file: this runs once per flush tick on the dispatcher. No index yet means no rollback offers on these rows.
        var lastSuccessfulInstalls = _history.RollbackIndex ?? NoRollbackIndex;

        while (_pendingItems.TryDequeue(out var item))
        {
            var key = PackageKey(item);
            _scanSeenKeys.Add(key);

            // The same update still on screen from the last scan keeps its row so the table never blanks; it is replaced only when the version changed or it carries a stale install result.
            if (_rowsByKey.TryGetValue(key, out var existing))
            {
                // A live result of any shape means this row no longer serves cached data.
                _cacheOnlyKeys.Remove(key);

                // A row in an install batch must never be replaced here although the source still reports its pre-install data. Replacing it orphans the running row (status reports match by source and id, not identity) and leaves an Idle look-alike that could be queued for a second, concurrent install.
                if (existing.InstallState is RowInstallState.Queued or RowInstallState.Running)
                    continue;

                // A reinstall's row stays the same across its removal and install and holds the install-only retry afterwards: a listing from before the removal must not swap it for a plain update row.
                if (HeldByReinstall(existing))
                    continue;

                if (existing.InstallState == RowInstallState.Idle
                    && string.Equals(existing.AvailableVersion, item.AvailableVersion, StringComparison.Ordinal)
                    && string.Equals(existing.InstalledVersion, item.InstalledVersion, StringComparison.Ordinal)
                    && ChildrenUnchanged(existing, item))
                {
                    // A snooze can expire between scans with nothing else changed, so this fast path must re-check it; so must a rollback that became available.
                    ApplySnoozeState(existing);
                    ApplyPersistentFailureState(existing);
                    ApplyDowngradeState(existing, lastSuccessfulInstalls);
                    continue;
                }

                // Also out of this batch, not only the table: a row created earlier in the same drain is not in Updates yet.
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
            // Also sets IsSnoozed, which clears IsSelected again for a snoozed critical row.
            ApplySnoozeState(row);
            // A package flagged NeedsManualWork by an earlier session must read as flagged from the moment it appears.
            ApplyPersistentFailureState(row);
            // A package rollback-eligible from an earlier session's install must read as such from the moment it appears.
            ApplyDowngradeState(row, lastSuccessfulInstalls);
            TrackSelection(row);
            _rowsByKey[key] = row;
            added.Add(row);
        }

        Updates.RemoveRange(replaced);

        return added;
    }

    /// <summary>Wires a new row into selection-state recomputation. Every path that creates an <see cref="UpdateRowViewModel"/> must call this before the row reaches the UI: an untracked row can be ticked on screen, but <see cref="RefreshSelectionState"/> never reruns for it and the toolbar's install button stays disabled.</summary>
    private void TrackSelection(UpdateRowViewModel row)
    {
        row.PropertyChanged += (_, args) =>
        {
            OnRowColumnDataChanged(args.PropertyName);

            // A bulk change raises these once itself when done (see _suppressSelectionRefresh).
            if (_suppressSelectionRefresh)
                return;

            // The headline counts pending installs, so a finishing or failing row moves it.
            if (args.PropertyName == nameof(UpdateRowViewModel.InstallState))
            {
                OnPropertyChanged(nameof(HeadlineText));
                OnPropertyChanged(nameof(HeadlineCriticalText));
                RefreshPillOpenCounts();
            }

            if (args.PropertyName is nameof(UpdateRowViewModel.IsSelected)
                or nameof(UpdateRowViewModel.CanInstall)
                or nameof(UpdateRowViewModel.DownloadSizeBytes))
            {
                RefreshSelectionState();
            }
        };
    }

    /// <summary>The caller's version comparison is meaningless for a row whose parent has no version: Steam's single row has both versions null, so every scan would find nothing changed and freeze its children at the first scan's list. True only when both lists have the same length and every child's id and versions match in order (the source reports children in a stable order).</summary>
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

    /// <summary>Finished rows leave when the next scan starts. Succeeded only: a failure keeps its Retry and Details.</summary>
    private void DropFinishedRows()
    {
        var finished = _rowsByKey
            .Where(pair => pair.Value.InstallState == RowInstallState.Succeeded && !HeldByReinstall(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        var toRemove = new List<UpdateRowViewModel>(finished.Count);
        foreach (var key in finished)
        {
            if (_rowsByKey.TryGetValue(key, out var row))
                toRemove.Add(row);

            _rowsByKey.Remove(key);
        }

        // One notification for the whole sweep: Updates.CollectionChanged runs a full ApplyFilter each time.
        Updates.RemoveRange(toRemove);
    }

    /// <summary>Drops rows a completed scan no longer found, except a queued or running row, which is not the scan's to remove.</summary>
    private void RemoveStaleRows()
    {
        var stale = _rowsByKey.Keys
            .Where(k => !_scanSeenKeys.Contains(k))
            .Where(k => _rowsByKey[k].InstallState is not (RowInstallState.Queued or RowInstallState.Running))
            .Where(k => !HeldByReinstall(_rowsByKey[k]))
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

        // One notification for the whole sweep: Updates.CollectionChanged runs a full ApplyFilter each time.
        Updates.RemoveRange(toRemove);
    }

    /// <summary>Copies the providers' current state into their pills. The ProviderStatus objects are mutated in place by the scanning threads, so reading them here yields the latest counts without any provider marshalling to the UI thread.</summary>
    /// <param name="doneBeforeDrain">The sources whose items have all reached the table (see <see cref="Flush"/>); only these are enriched. Null (a pill rebuild outside a flush) enriches none.</param>
    private void FlushProviderStatuses(IReadOnlySet<string>? doneBeforeDrain = null)
    {
        // One shared instant for every pill's "running a while" check, on the flush tick that already runs during a scan.
        var now = DateTimeOffset.UtcNow;

        foreach (var status in _liveStatuses.Values)
        {
            // Ahead of the Tier C skip below: every source's rows enrich once it is done, pill or not.
            if (doneBeforeDrain?.Contains(status.SourceId) == true && _enrichedSourcesThisScan.Add(status.SourceId))
                EnrichSource(status.SourceId);

            var (category, tier) = SourceGroupingFor(status.SourceId);

            // Tier C sources (stores, launchers, Steam, vendor driver pages) get no pill: the bottom row is for sources that install something, the manual rows have their own category.
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

        RefreshPillOpenCounts();
    }

    /// <summary>Sets each pill's chip number to the rows of its source that still offer an update: one pass over the rows, cheap enough for the flush tick.</summary>
    private void RefreshPillOpenCounts()
    {
        var open = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Updates)
        {
            if (row.InstallState == RowInstallState.Succeeded)
                continue;

            // Ignored and snoozed rows are hidden by the filter unless "show ignored" is on, so the chip leaves them out like the table and the failure chip.
            if (!ShowIgnored && (IsIgnored(row.Item) || IsSnoozed(row.Item)))
                continue;

            open[row.Item.SourceId] = open.GetValueOrDefault(row.Item.SourceId) + 1;
        }

        foreach (var pill in ProviderStatuses)
            pill.OpenCount = open.GetValueOrDefault(pill.SourceId);
    }

    private void RemovePill(string key)
    {
        if (!_providerStatusById.TryGetValue(key, out var vm))
            return;

        ProviderStatuses.Remove(vm);
        _providerStatusById.Remove(key);
    }

    /// <summary>The category and tier a pill groups by, read from the source so it cannot drift.</summary>
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

        // Children (Steam's games) show inside their parent row and need icons of their own.
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
                    // One row's icon resolution failing (locked file, odd registry entry) must not stop the rest of the batch.
                    _logging.Warn($"Icon resolution failed for '{row.Item.SourceId}:{row.Item.Id}': {ex.Message}");
                }
            }
        });
    }

    /// <summary>WindowsUpdateSource.Install throws the "no longer offered" message only when the update's GUID is absent from a fresh search; a revision bump between scan and install never gets here (WindowsUpdateMatching.PickInstallTarget handles it). The row is then stale, so this rescans that one source in the background, without disturbing a running full scan or other sources' rows.</summary>
    internal async Task RescanSingleSourceAsync(string sourceId)
    {
        var beforeKeys = _rowsByKey
            .Where(pair => pair.Value.Item.SourceId == sourceId
                && pair.Value.InstallState is not (RowInstallState.Queued or RowInstallState.Running))
            .Select(pair => pair.Key)
            .ToList();

        // _scanSeenKeys accumulates across full scans and resets only when one starts, so it may hold this source's old keys; using it would never find a row stale. This rescan tracks what it finds itself.
        var seenThisRescan = new HashSet<string>(StringComparer.Ordinal);
        void OnFound(UpdateItem item)
        {
            if (string.Equals(item.SourceId, sourceId, StringComparison.Ordinal))
                lock (seenThisRescan)
                    seenThisRescan.Add(PackageKey(item));
        }

        // Must start on the UI thread: the key snapshot above reads the row index before the first await. Continuations may run on a pool thread, so every touch of view-model state below goes through the dispatcher. The pill's spinner and the progress bar read like a full scan meanwhile; the rescan holds a lease on the scanning state instead of restoring it, so a full scan starting or ending meanwhile is never overridden.
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

            // A transient failure or the user's cancel must not read as "not offered any more". OnStatus kept _liveStatuses current, so its value is what this rescan ended on. Only a clean Done or NotInstalled means the source ran its course; anything else (Error, Cancelled, no status) leaves the source's rows as they were, as a full scan's RemoveStaleRows does for cache-only rows.
            var rescanSucceeded = _liveStatuses.TryGetValue(sourceId, out var finalStatus)
                && finalStatus.State is ProviderScanState.Done or ProviderScanState.NotInstalled;

            if (rescanSucceeded)
            {
                // Anything from before that this rescan did not find is gone: dropped like RemoveStaleRows does, but only for this source.
                var gone = new List<UpdateRowViewModel>();
                foreach (var key in beforeKeys)
                {
                    if (seenThisRescan.Contains(key))
                        continue;

                    if (_rowsByKey.TryGetValue(key, out var row)
                        && row.InstallState is not (RowInstallState.Queued or RowInstallState.Running)
                        && !HeldByReinstall(row))
                    {
                        gone.Add(row);
                        _rowsByKey.Remove(key);
                    }
                }

                // One Reset for the whole sweep, not a Remove and filter pass per row.
                Updates.RemoveRange(gone);
            }

            RefreshHeader(resortManualOnly: false);
        });
    }

    /// <summary>Providers report from their own threads. A dispatcher hop per status change (each recounting every header figure) would bury the UI thread under callbacks on a large scan, so nothing is marshalled here and the flush timer picks the state up at a fixed rate.</summary>
    private void OnProviderStatusChanged(ProviderStatus status) => _liveStatuses[status.SourceId] = status;
}
