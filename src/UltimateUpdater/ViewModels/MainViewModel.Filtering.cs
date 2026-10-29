using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Views;

namespace UltimateUpdater.ViewModels;

// What the table shows: search, filters, sorting, grouping and selection state, and the header summary
// derived from them.
public partial class MainViewModel
{
    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterButtonText))]
    private bool showOnlyCritical;

    /// <summary>Brings the ignored and snoozed rows back into the table, marked with their badge. Off by
    /// default: an ignored update is one the user has already decided about.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterButtonText))]
    private bool showIgnored;

    partial void OnShowIgnoredChanged(bool value)
    {
        _settings.Current.Ui.ShowIgnored = value;
        Persist();
        ApplyFilter();
    }

    /// <summary>Hides the whole "Manual update" category (stores/launchers) and drops its bottom pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterButtonText))]
    private bool hideManual;

    partial void OnHideManualChanged(bool value)
    {
        _settings.Current.Ui.HideManual = value;
        Persist();
        ApplyFilter();
        // rebuild the pills so the "Manual update" one appears/disappears with the toggle, then recount
        FlushProviderStatuses();
        RefreshHeader();
    }

    public RangeObservableCollection<UpdateRowViewModel> Updates { get; } = [];

    /// <summary>
    /// What the table is actually showing: the filtered subset of <see cref="Updates"/>, kept in step by
    /// <see cref="SyncVisible"/>. This is the collection <see cref="UpdatesView"/> is built over; see the
    /// note there for why the filtering is not a filter callback.
    /// </summary>
    private readonly RangeObservableCollection<UpdateRowViewModel> _visible = [];

    /// <summary>Membership of <see cref="_visible"/> in O(1): the diff runs on every keystroke.</summary>
    private readonly HashSet<UpdateRowViewModel> _visibleSet = [];

    /// <summary>
    /// Counts what is actually on screen, not what was found, so a search with no match shows the
    /// "nothing found" card instead of an unexplained empty area. Includes
    /// <see cref="ManualOnlyView"/>: a search matching only a manual-open row must not show the
    /// "nothing found" card above a non-empty compact block.
    /// </summary>
    public bool HasResults => UpdatesView.Cast<object>().Any() || HasManualOnlyResults;

    /// <summary>Whether the compact block has anything to show right now.</summary>
    public bool HasManualOnlyResults => ManualOnlyView.Cast<object>().Any();

    /// <summary>
    /// The rows the user can actually see. EVERYTHING about selecting, counting and installing goes
    /// through this: reading the raw collection instead meant "select all" during a search picked up
    /// every hidden row, and an ignored update that came back critical was silently reselected and
    /// installed without ever appearing on screen.
    /// </summary>
    private IEnumerable<UpdateRowViewModel> VisibleRows => UpdatesView.Cast<UpdateRowViewModel>();

    /// <summary>
    /// Counts what the table shows as updates: not ignored, not folded into another row as a duplicate, and
    /// from a source that is switched on. The headline and the interval balloon both count through this so
    /// they can never promise more than the table lists.
    /// Duplicates are folded by their own key here rather than through <see cref="_mergedAway"/>: that set is
    /// built only from rows passing the view filters (search text, critical-only), so with a filter on, a
    /// package offered by two sources counted twice, in the headline and in the tray notification alike.
    /// </summary>
    private int VisibleUpdateCount(Func<UpdateRowViewModel, bool> counts) =>
        NotIgnored
            .Where(u => IsSourceEnabled(u.Item.SourceId) && counts(u))
            .Select(u => SourceRank.DuplicateKey(u.Item) ?? PackageKey(u.Item))
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>True when at least one source that could have run is switched on and every such source ended in
    /// the error state: nothing was actually checked, which "up to date" would misreport as a clean machine.
    /// Pills for absent tools and cancelled sources checked nothing either way, so they do not count.</summary>
    private bool EveryEnabledSourceFailed
    {
        get
        {
            var enabled = ProviderStatuses
                .Where(p => IsSourceEnabled(p.SourceId)
                    && p.State is not (ProviderScanState.NotInstalled or ProviderScanState.Cancelled))
                .ToList();
            return enabled.Count > 0 && enabled.All(p => p.State == ProviderScanState.Error);
        }
    }

    /// <summary>The one line that tells the user where they stand.</summary>
    public string HeadlineText
    {
        get
        {
            if (IsScanning)
                return Loc["HeadlineScanning"];

            // What the table still lists as an update: rows that can be installed now plus the ones already
            // queued or running (a mid-install row is still an update until it succeeds), so the headline
            // does not shrink while a batch works through the table.
            var total = VisibleUpdateCount(u => u.Item.CanInstall
                && (u.CanInstall || u.InstallState is RowInstallState.Queued or RowInstallState.Running));
            if (total == 0)
            {
                if (!HasScanResult)
                    return Loc["HeadlineNotChecked"];

                return EveryEnabledSourceFailed ? Loc["HeadlineNoSourceChecked"] : Loc["HeadlineUpToDate"];
            }

            // "1 Updates verfügbar" in the app's own headline is exactly the sort of thing that makes
            // a tool feel unfinished, so the singular has its own string in both languages.
            return string.Format(Loc[total == 1 ? "HeadlineUpdatesOne" : "HeadlineUpdates"], total);
        }
    }

    public string SubheadlineText
    {
        get
        {
            if (IsScanning)
            {
                if (ScanProgressTotal == 0)
                    return Loc["SubheadlineScanning"];

                var progress = string.Format(
                    Loc[ScanProgressTotal == 1 ? "SubheadlineScanningProgressOne" : "SubheadlineScanningProgress"],
                    ScanProgressDone, ScanProgressTotal);

                // Which sources are working RIGHT NOW, not merely still pending - so the line moves as
                // each one finishes instead of listing everything not yet done.
                var running = ProviderStatuses
                    .Where(p => p.State == ProviderScanState.Scanning)
                    .Select(p => p.DisplayName)
                    .ToList();

                return running.Count > 0
                    ? $"{progress} · {string.Format(Loc["SubheadlineScanningSources"], string.Join(", ", running))}"
                    : progress;
            }

            // Nothing to say once a scan is done: the headline, the critical badge on the rows and the
            // footer's selection summary already carry the numbers.
            return string.Empty;
        }
    }

    /// <summary>True when a filter (any filter) is hiding everything found; checked against the raw
    /// <see cref="Updates"/>, never <see cref="NotIgnored"/>.</summary>
    public bool IsFilterHidingEverything => Updates.Any() && !HasResults;

    /// <summary>Whether the table has anything to say about the machine: a scan finished or a saved result
    /// loaded this session, or rows are on screen at all (they only ever come from one of the two, and an
    /// ignored or snoozed set of them is a real "nothing left to do").</summary>
    public bool HasScanResult => _scanResultKnown || Updates.Count > 0;

    /// <summary>Before any result exists the empty table is not an answer, so the empty state offers the
    /// scan itself. Hidden while one runs: the toolbar button already shows that.</summary>
    public bool ShowNotCheckedPrompt => !HasScanResult && !IsScanning;

    /// <summary>"nothing was found", "nothing was looked for yet" and "nothing matches what you typed" are
    /// different situations.</summary>
    public string EmptyStateText => IsFilterHidingEverything
        ? Loc["EmptyStateFiltered"]
        : ShowNotCheckedPrompt ? Loc["EmptyStateNotChecked"] : Loc["EmptyState"];

    /// <summary>Called wherever the filter is re-evaluated, since both of these follow the view.</summary>
    private void RefreshFilteredState()
    {
        OnPropertyChanged(nameof(HasManualOnlyResults));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(HasScanResult));
        OnPropertyChanged(nameof(ShowNotCheckedPrompt));
        OnPropertyChanged(nameof(IsFilterHidingEverything));
    }

    /// <param name="resortManualOnly">
    /// False on the scan drain's own tick, which runs five times a second for the whole scan. A Refresh
    /// raises a Reset, and a Reset makes the compact block regenerate its containers; closing an open row
    /// context menu and dropping keyboard focus with them, repeatedly, for as long as the scan lasts. The
    /// drain never needs it: it only ADDS rows, and an add is placed in sort order by the view itself.
    /// Only an existing row's key MOVING (an ignore toggle) needs the re-apply.
    /// </param>
    private void RefreshHeader(bool resortManualOnly = true)
    {
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(SubheadlineText));
        RefreshFilteredState();
        RefreshPillViewIfKeysChanged();
        // IsIgnored is a sort key on this view (ignored rows sink to the bottom) but not a live-sorting
        // property, so a toggle needs an explicit re-apply.
        if (resortManualOnly)
            RefreshViewSafely(ManualOnlyView, nameof(ManualOnlyView));
        OnPropertyChanged(nameof(ScanProgressDone));
        OnPropertyChanged(nameof(ScanProgressTotal));
    }

    /// <summary>
    /// Measured under sustained CPU load: a WPF <see cref="ListCollectionView"/>'s own
    /// <c>RefreshOverride</c>/<c>PrepareLocalArray</c> can throw a bare <see cref="NullReferenceException"/>
    /// here, reproducibly under contention (roughly one run in twenty), never under a normal run and never
    /// from a re-entrant call on this class's side (a depth counter confirmed <see cref="RefreshHeader"/>
    /// is never nested); it reads as a framework-internal timing hazard, not a defect in this method's
    /// call order.
    /// <see cref="RefreshHeader"/> runs from <see cref="FinishScanUiAsync"/>, right before the load-bearing
    /// steps: <see cref="RemoveStaleRows"/>, the scan-result cache save, and clearing
    /// <see cref="IsScanning"/>. An exception escaping here would skip all three AND ScanAsync's own
    /// <c>_scanGate.Release()</c> in the same finally: a stuck row that survives forever plus a permanently
    /// wedged semaphore that hangs every later scan, from one dropped pill-view repaint. A view failing to
    /// repaint on one tick is cosmetic and self-heals on the next; the data-model bookkeeping right after
    /// it must not depend on the repaint having worked.
    /// </summary>
    private bool RefreshViewSafely(ICollectionView view, string name)
    {
        try
        {
            view.Refresh();
            return true;
        }
        catch (Exception ex)
        {
            _logging.Warn($"{name}.Refresh() failed and was skipped for this tick: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Each pill's sort and filter keys as <see cref="PillView"/> last applied them, and the
    /// scanning flag its filter reads.</summary>
    private Dictionary<ProviderStatusViewModel, (int RunningRank, bool IsNoteworthy, string DisplayName)> _pillViewKeys = [];

    private bool _pillViewAppliedWhileScanning;

    /// <summary>
    /// A Refresh raises a Reset, and the pills' ItemsControl answers it by rebuilding every pill button,
    /// closing an open tooltip or context menu and dropping a click in progress; on the scan's flush tick
    /// that was several times a second for the whole scan. The view only needs one when something it sorts
    /// or filters by changed (<see cref="ProviderStatusViewModel.RunningRank"/>, IsNoteworthy, the name,
    /// or <see cref="IsScanning"/>); an added pill is placed by the view itself, but it is added before its
    /// first status is applied, so a pill not seen here yet counts as a change too.
    /// </summary>
    private void RefreshPillViewIfKeysChanged()
    {
        var keys = new Dictionary<ProviderStatusViewModel, (int RunningRank, bool IsNoteworthy, string DisplayName)>(ProviderStatuses.Count);
        var changed = _pillViewAppliedWhileScanning != IsScanning;
        foreach (var pill in ProviderStatuses)
        {
            var key = (pill.RunningRank, pill.IsNoteworthy, pill.DisplayName);
            keys[pill] = key;
            if (!_pillViewKeys.TryGetValue(pill, out var applied) || applied != key)
                changed = true;
        }

        // Recorded only once the view has really applied them: a refresh that failed is retried next time.
        if (changed && !RefreshViewSafely(PillView, nameof(PillView)))
            return;

        _pillViewKeys = keys;
        _pillViewAppliedWhileScanning = IsScanning;
    }

    /// <summary>Grouped + filtered view the table binds to; Updates stays the unfiltered source.</summary>
    public ICollectionView UpdatesView { get; }

    /// <summary>The manual-open rows' compact block: a second, independent <see cref="ListCollectionView"/>
    /// over the same visible collection, not a `GroupStyleSelector`.</summary>
    public ICollectionView ManualOnlyView { get; }

    /// <summary>Which category groups the user collapsed, keyed by <see
    /// cref="UpdateRowViewModel.CategoryKey"/>, never the translated name.</summary>
    private readonly HashSet<string> _collapsedGroups = new();

    public bool IsGroupExpanded(string? groupName) =>
        string.IsNullOrEmpty(groupName) || !_collapsedGroups.Contains(groupName);

    public void SetGroupExpanded(string? groupName, bool expanded)
    {
        if (string.IsNullOrEmpty(groupName))
            return;

        if (expanded)
            _collapsedGroups.Remove(groupName);
        else
            _collapsedGroups.Add(groupName);
    }

    partial void OnShowOnlyCriticalChanged(bool value) => ApplyFilter();

    /// <summary>The "keeps failing" filter chip; collects every row the three-strikes tally
    /// promoted to <see cref="RowInstallState.NeedsManualWork"/> in one place instead of leaving them
    /// scattered through the rest of the table. Session-only, like <see cref="ShowOnlyCritical"/>:
    /// turning it off just goes back to the normal view, the rows themselves
    /// are untouched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterButtonText))]
    private bool showOnlyPersistentFailures;

    partial void OnShowOnlyPersistentFailuresChanged(bool value) => ApplyFilter();

    /// <summary>How many of the filter menu's switches are on right now (the search box is not one).</summary>
    public int ActiveFilterCount =>
        (ShowOnlyCritical ? 1 : 0) + (ShowIgnored ? 1 : 0) + (HideManual ? 1 : 0) + (ShowOnlyPersistentFailures ? 1 : 0);

    /// <summary>The filter button's label: plain while nothing is filtered, with the number of active
    /// filters in brackets otherwise, so a narrowed table is never a surprise.</summary>
    public string FilterButtonText => ActiveFilterCount == 0
        ? Loc["FilterButton"]
        : string.Format(Loc["FilterButtonActive"], ActiveFilterCount);

    /// <summary>
    /// The filter menu's entries in display order. The persistent-failure entry is offered only while there
    /// is something to collect (or while its filter is still on, so it can always be turned off again).
    /// </summary>
    public IReadOnlyList<FilterEntry> BuildFilterEntries()
    {
        var entries = new List<FilterEntry>
        {
            new("critical", () => Loc["FilterCriticalOnly"], () => ShowOnlyCritical, v => ShowOnlyCritical = v),
            new("ignored", () => Loc["FilterShowIgnored"], () => ShowIgnored, v => ShowIgnored = v),
            new("manual", () => Loc["FilterHideManual"], () => HideManual, v => HideManual = v),
        };

        if (HasPersistentFailures || ShowOnlyPersistentFailures)
        {
            entries.Add(new FilterEntry(
                "failures", () => PersistentFailureFilterLabel, () => ShowOnlyPersistentFailures,
                v => ShowOnlyPersistentFailures = v));
        }

        return entries;
    }

    /// <summary>
    /// Rows the table folded into another one because they are the very same update from a second source.
    /// They stay in <see cref="Updates"/>: a scan still knows about them, and they come
    /// back the moment the winning source drops out: they simply do not get a line of their own.
    /// </summary>
    private readonly HashSet<UpdateRowViewModel> _mergedAway = [];

    private bool ComputeVisible(UpdateRowViewModel row) => PassesFilters(row) && !_mergedAway.Contains(row);

    private bool PassesFilters(UpdateRowViewModel row)
    {
        // Attached once per row, the first time any row ever reaches a filter pass, well before
        // it can ever change state (see WatchPersistentFailureState's own doc comment).
        WatchPersistentFailureState(row);

        // ignored rows stay out of the table unless the user asks to see them (they then show marked)
        if (IsIgnored(row.Item) && !ShowIgnored)
            return false;

        // A snoozed row is hidden exactly like an ignored one, by the same switch - it used
        // to have its own separate "Zurückgestellt" chip, which meant it stayed visible whenever that
        // chip was on regardless of the ignore switch, a section of the table nothing else in the UI
        // agreed was there.
        if (IsSnoozed(row.Item) && !ShowIgnored)
            return false;

        // the whole "Manual update" category can be hidden as one
        if (HideManual && row.IsManualOnly)
            return false;

        // a source switched off from its pill drops out of the table at once
        if (!IsSourceEnabled(row.Item.SourceId))
            return false;

        if (ShowOnlyCritical && !row.IsCritical)
            return false;

        // A positive filter like ShowOnlyCritical above, but also drops an ignored row - once
        // ignored, the point of collecting it here (deciding what to do about it) is done, and it would
        // otherwise sit in this one view forever with no way for the user to make it go away.
        if (ShowOnlyPersistentFailures && (row.InstallState != RowInstallState.NeedsManualWork || IsIgnored(row.Item)))
            return false;

        return MatchesQueryCached(row);
    }

    /// <summary>
    /// Rows already wired to keep their own <see cref="UpdateRowViewModel.PersistentFailureAttempts"/>
    /// (and this chip's own count) in step with <see cref="AppSettings.PersistentFailures"/> whenever
    /// InstallState flips - attached lazily here, the first time a row reaches <see cref="PassesFilters"/>,
    /// rather than at construction (MainViewModel.Scan.cs, not owned by this change), since every row
    /// passes through here long before it can ever change state.
    /// </summary>
    private readonly Dictionary<UpdateRowViewModel, PropertyChangedEventHandler> _persistentFailureWatched = [];

    /// <summary>Test seam: whether the row is still wired to the persistent-failure bookkeeping.</summary>
    internal bool IsWatchingPersistentFailuresForTest(UpdateRowViewModel row) => _persistentFailureWatched.ContainsKey(row);

    private void WatchPersistentFailureState(UpdateRowViewModel row)
    {
        if (_persistentFailureWatched.ContainsKey(row))
            return;

        SyncPersistentFailureAttempts(row);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(UpdateRowViewModel.InstallState))
                return;

            SyncPersistentFailureAttempts(row);
            NotifyPersistentFailureCountChanged();
            if (ShowOnlyPersistentFailures)
                ApplyFilter();
        };
        _persistentFailureWatched[row] = handler;
        row.PropertyChanged += handler;
    }

    /// <summary>Lets go of rows that left <see cref="Updates"/> (a rescan replaced them, a sweep dropped them):
    /// otherwise every row ever shown stays reachable through its own subscription for the whole session.</summary>
    private void PruneWatchedPersistentFailureRows()
    {
        if (_persistentFailureWatched.Count == 0)
            return;

        var current = new HashSet<UpdateRowViewModel>(Updates);
        foreach (var (row, handler) in _persistentFailureWatched.ToList())
        {
            if (current.Contains(row))
                continue;

            row.PropertyChanged -= handler;
            _persistentFailureWatched.Remove(row);
        }
    }

    private void SyncPersistentFailureAttempts(UpdateRowViewModel row)
    {
        PersistentFailureRecord? record = null;
        var flagged = row.InstallState == RowInstallState.NeedsManualWork
            && _settings.Current.PersistentFailures.TryGetValue(PackageKey(row.Item), out record);

        row.PersistentTallyIsUnchanged =
            flagged && record!.Signature.StartsWith(UnchangedSignaturePrefix, StringComparison.Ordinal);
        row.PersistentFailureAttempts = flagged ? record!.Count : 0;
    }

    /// <summary>The "keeps failing" filter chip's own count, over the live rows rather than the
    /// settings dictionary directly, so it always matches exactly what turning the chip on would show.</summary>
    // Only rows the filter itself would show: an ignored stuck row (its own hint offers Ignore) kept the
    // chip at "(1)" and turning it on showed an empty table.
    public int PersistentFailureCount => Updates.Count(u => u.InstallState == RowInstallState.NeedsManualWork
        && !IsIgnored(u.Item) && IsSourceEnabled(u.Item.SourceId) && !_mergedAway.Contains(u));

    /// <summary>Hidden entirely while there is nothing to collect, never a chip sitting there empty.</summary>
    public bool HasPersistentFailures => PersistentFailureCount > 0;

    public string PersistentFailureFilterLabel => string.Format(Loc["FilterPersistentFailures"], PersistentFailureCount);

    private void NotifyPersistentFailureCountChanged()
    {
        OnPropertyChanged(nameof(PersistentFailureCount));
        OnPropertyChanged(nameof(HasPersistentFailures));
        OnPropertyChanged(nameof(PersistentFailureFilterLabel));
    }

    /// <summary>
    /// The search match is the expensive part of the filter: it falls back to a fuzzy score per row and per
    /// field, and every filter pass asks for it TWICE per row, in <see cref="RecomputeDuplicates"/> and in
    /// <see cref="SyncVisible"/>. The answer depends on nothing but the row and the query, so it is
    /// computed once and reused for the rest of the pass. Cleared at the start of each pass rather than
    /// kept: a row's name can change underneath it (enrichment, a language switch) while the query stays
    /// the same, and a filter that shows yesterday's answer is worse than a fast one.
    /// </summary>
    private readonly Dictionary<UpdateRowViewModel, bool> _queryMatchCache = [];

    private bool MatchesQueryCached(UpdateRowViewModel row)
    {
        if (_queryMatchCache.TryGetValue(row, out var matches))
            return matches;

        matches = UpdateFilter.MatchesQuery(SearchQuery, row.Name, row.Item.Id, row.SourceDisplay, row.Publisher);
        _queryMatchCache[row] = matches;
        return matches;
    }

    /// <summary>
    /// Neither a running scan nor a running batch blocks this. Selected rows JOIN the batch that is already going, through EnqueueInstall, rather than
    /// starting a second one, which is what the single drain loop in RunInstallLoopAsync exists for.
    /// Computed once per <see cref="RefreshSelectionState"/> pass, not re-walked on every read.
    /// </summary>
    public bool CanInstallSelected => _canInstallSelected;
    private bool _canInstallSelected;

    /// <summary>True when every visible installable row is ticked; drives the "Clear selection" wording.
    /// Computed once per <see cref="RefreshSelectionState"/> pass.</summary>
    private bool AreAllVisibleSelected => _areAllVisibleSelected;
    private bool _areAllVisibleSelected;

    /// <summary>The header checkbox's tooltip and accessible name: "Clear selection" once everything visible
    /// is ticked, "Select all" otherwise.</summary>
    public string SelectAllTooltip => Loc[_areAllVisibleSelected ? "SelectAllClear" : "SelectAllButton"];

    /// <summary>Shows how much is selected. Empty when nothing is: the "nothing selected" words are gone.
    /// The size/count only appear once there is a real selection. Computed once per
    /// <see cref="RefreshSelectionState"/> pass.</summary>
    public string SelectionSummary => _selectionSummary;
    private string _selectionSummary = string.Empty;

    /// <summary>
    /// Set around a bulk selection change (SelectAll, SelectCategory, a source toggle's unselect sweep) so
    /// TrackSelection's own per-row hop into <see cref="RefreshSelectionState"/> is skipped while the loop
    /// runs; the caller runs it once itself when the loop is done. Without this, ticking N rows cost N
    /// full RefreshSelectionState passes, each itself O(N) over the visible rows: an O(N^2) select-all.
    /// </summary>
    private bool _suppressSelectionRefresh;

    /// <summary>Test-only counter, incremented once per real <see cref="RefreshSelectionState"/> run:
    /// proves a bulk selection change costs one pass, not one per row it touches.</summary>
    internal int RefreshSelectionStateRunCountForTest;

    /// <summary>
    /// The single place all six selection-derived properties (<see cref="CanInstallSelected"/>,
    /// <see cref="HasSelection"/>, <see cref="AreAllVisibleSelected"/>, <see cref="AllVisibleSelected"/>,
    /// <see cref="SelectAllTooltip"/>, <see cref="SelectionSummary"/>) are recomputed, walking the visible
    /// rows once instead of each getter re-walking them on every read/binding refresh. Called from every
    /// place a selection or the visible set can change. Internal (not private) so a test that adds rows
    /// directly, bypassing the scan's per-row subscription (<c>DrainPendingItems</c>), can wire the same
    /// invalidation itself.
    /// </summary>
    internal void RefreshSelectionState()
    {
        RefreshSelectionStateRunCountForTest++;
        var visible = VisibleRows.ToList();
        var installable = visible.Where(u => u.CanInstall).ToList();
        var selected = visible.Where(u => u.IsSelected).ToList();
        var selectedInstallableCount = installable.Count(u => u.IsSelected);

        _hasSelection = selected.Count > 0;
        _canInstallSelected = _hasSelection;
        _areAllVisibleSelected = installable.Count > 0 && selectedInstallableCount == installable.Count;
        _allVisibleSelected = installable.Count == 0
            ? false
            : selectedInstallableCount == 0 ? false : selectedInstallableCount == installable.Count ? true : null;
        if (selected.Count == 0)
            _selectionSummary = string.Empty;
        else
        {
            var criticalCount = selected.Count(u => u.IsCritical);
            var baseText = criticalCount > 0
                ? string.Format(Loc["SelectionSummaryWithCritical"], selected.Count, criticalCount)
                : string.Format(Loc["SelectionSummary"], selected.Count);

            // Pre-install preview: the combined download size of the selection, as it becomes known
            // (enrichment fills sizes in after the scan). Shown only once there is a real total.
            var totalBytes = selected.Sum(u => u.DownloadSizeBytes ?? 0L);
            _selectionSummary = totalBytes > 0
                ? baseText + string.Format(Loc["SelectionSummarySizeSuffix"], ByteSize.Format(totalBytes))
                : baseText;
        }

        OnPropertyChanged(nameof(CanInstallSelected));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(AllVisibleSelected));
        OnPropertyChanged(nameof(SelectAllTooltip));
        OnPropertyChanged(nameof(HasSelection));

        SelectionVersion++;
        OnPropertyChanged(nameof(SelectionVersion));
    }

    /// <summary>Whether anything is selected, so the summary text can be hidden when nothing is.
    /// Computed once per <see cref="RefreshSelectionState"/> pass.</summary>
    public bool HasSelection => _hasSelection;
    private bool _hasSelection;

    /// <summary>
    /// Bumped on every selection change. A CollectionViewGroup raises nothing when a row inside it is
    /// ticked, so the category checkboxes bind to this alongside their group to get re-evaluated: that is
    /// what makes "select all" visibly tick the category boxes too.
    /// </summary>
    public int SelectionVersion { get; private set; }

    /// <summary>
    /// Tri-state for the header's select-all checkbox: all visible installable rows ticked (true), none
    /// (false), or some (null → the indeterminate box). Bound one-way; the box's click runs SelectAll,
    /// which toggles. False when there is nothing installable on screen, so the box reads as empty.
    /// Computed once per <see cref="RefreshSelectionState"/> pass.
    /// </summary>
    public bool? AllVisibleSelected => _allVisibleSelected;
    private bool? _allVisibleSelected;

    /// <summary>
    /// Re-filtering re-groups and rebuilds every row container, which is far too much work to do on every
    /// keystroke; typing visibly stuttered. The debounce
    /// lives on the BINDING instead (the search box binds with Delay), so the view model stays synchronous:
    /// setting SearchQuery filters at once, which is also what makes it testable without a message pump.
    /// </summary>
    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    /// <summary>Which column the table is sorted by within each category group, and the direction.</summary>
    [ObservableProperty]
    private string sortColumn = "name";

    [ObservableProperty]
    private bool sortDescending;

    /// <summary>
    /// Clicking a column header sorts by it; clicking the active column again flips the direction. Each
    /// column has a sensible default direction (names A→Z, but dates and sizes largest/newest first).
    /// </summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column))
            return;

        if (column == SortColumn)
            SortDescending = !SortDescending;
        else
        {
            SortColumn = column;
            SortDescending = column is "date" or "size";   // newest / largest first by default
        }

        ApplySort();
        RefreshSortGlyphs();
    }

    private void ApplySort()
    {
        var key = SortColumn switch
        {
            "date" => nameof(UpdateRowViewModel.SortDate),
            "size" => nameof(UpdateRowViewModel.SortSize),
            "version" => nameof(UpdateRowViewModel.SortVersion),
            _ => nameof(UpdateRowViewModel.Name),
        };
        var direction = SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        UpdatesView.SortDescriptions.Clear();
        UpdatesView.SortDescriptions.Add(
            new SortDescription(nameof(UpdateRowViewModel.CategoryOrder), ListSortDirection.Ascending));
        // Ignored rows sink to the bottom of their category (false sorts before true), whatever the
        // chosen column.
        UpdatesView.SortDescriptions.Add(
            new SortDescription(nameof(UpdateRowViewModel.IsIgnored), ListSortDirection.Ascending));
        UpdatesView.SortDescriptions.Add(new SortDescription(key, direction));
    }

    private void RefreshSortGlyphs()
    {
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(DateSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(VersionSortGlyph));
    }

    /// <summary>Only the active column carries a mark (its direction); an inactive one stays empty, the glyph
    /// slot keeps its width so the label never shifts.</summary>
    internal string GlyphFor(string column) =>
        SortColumn != column ? string.Empty : SortDescending ? " ▼" : " ▲";

    public string NameSortTooltip => SortTooltip("ColumnName");
    public string DateSortTooltip => SortTooltip("ColumnDate");
    public string VersionSortTooltip => SortTooltip("ColumnVersion");
    public string SizeSortTooltip => SortTooltip("ColumnSize");

    private string SortTooltip(string columnKey) =>
        string.Format(LocalizationService.Instance["SortByTooltip"], LocalizationService.Instance[columnKey]);

    public string NameSortGlyph => GlyphFor("name");
    public string DateSortGlyph => GlyphFor("date");
    public string SizeSortGlyph => GlyphFor("size");
    public string VersionSortGlyph => GlyphFor("version");

    /// <summary>
    /// The main table's own column list for the shared chooser (<see cref="Views.ColumnChooser"/>),
    /// declared once here. Check/icon/action are structural, not data columns, and are never offered.
    /// Name is a data column but may not be hidden, same as every table's own identity column.
    /// </summary>
    public static IReadOnlyList<ColumnDescriptor> MainTableColumns { get; } =
    [
        new("name", () => LocalizationService.Instance["ColumnName"], CanHide: false),
        new("date", () => LocalizationService.Instance["ColumnDate"], CanHide: true),
        new("version", () => LocalizationService.Instance["ColumnVersion"], CanHide: true),
        new("size", () => LocalizationService.Instance["ColumnSize"], CanHide: true),
        // Hidden by default (see DefaultHiddenMainColumns), unlike the three above; the full publisher
        // name stays in each row's name tooltip.
        new("publisher", () => LocalizationService.Instance["ColumnPublisher"], CanHide: true),
    ];

    /// <summary>Which hideable columns start hidden on a fresh install (everything else starts shown);
    /// also what "Reset columns" restores. The publisher is off by default, its full name lives in the
    /// name tooltip; a settings.json saved before a default-hidden column existed keeps that column hidden
    /// (silence about an id never reads as "shown", only <see cref="TableLayoutState.ShownColumns"/> does).</summary>
    private static readonly HashSet<string> DefaultHiddenMainColumns = new(StringComparer.Ordinal) { "publisher" };

    /// <summary>The default width of each hideable main-table column, restored when it is shown again.</summary>
    private static readonly Dictionary<string, double> MainColumnDefaultWidths = new(StringComparer.Ordinal)
    {
        ["date"] = 104,
        ["version"] = 168,
        ["size"] = 96,
        ["publisher"] = 120,
    };

    /// <summary>Which App.xaml GridLength resource each hideable column's width lives in; shared by the
    /// header row and every row template (MainWindow.xaml), so writing one entry here collapses (or
    /// restores) the column everywhere at once.</summary>
    private static readonly Dictionary<string, string> MainColumnResourceKeys = new(StringComparer.Ordinal)
    {
        ["date"] = "ColWidthDate",
        ["version"] = "ColWidthVersion",
        ["size"] = "ColWidthSize",
        ["publisher"] = "ColWidthPublisher",
    };

    /// <summary>The columns the user hid (or that start hidden); the persisted choice.</summary>
    private readonly HashSet<string> _hiddenMainColumns = new(DefaultHiddenMainColumns, StringComparer.Ordinal);

    /// <summary>Columns the user ticked in the chooser: shown even while no row has a value for them.
    /// Persisted as is (<see cref="TableLayoutState.PinnedColumns"/>).</summary>
    private readonly HashSet<string> _pinnedMainColumns = new(StringComparer.Ordinal);

    /// <summary>Columns hidden only because no row has a value for them (not pinned, not user-hidden).
    /// Derived from the table, never persisted; recomputed once per change of the row set.</summary>
    private readonly HashSet<string> _autoHiddenMainColumns = new(StringComparer.Ordinal);

    /// <summary>The columns that disappear while empty; version always has a value, name and the
    /// structural columns are never data columns.</summary>
    private static readonly string[] AutoHideableMainColumns = ["date", "size", "publisher"];

    /// <summary>Effective state: hidden by the user OR empty and not pinned. This is what the chooser
    /// shows unticked and what collapses the column.</summary>
    public bool IsMainColumnHidden(string columnId) =>
        _hiddenMainColumns.Contains(columnId) || _autoHiddenMainColumns.Contains(columnId);

    /// <summary>Wired to the chooser button in MainWindow.xaml's header. Ticking a column pins it (shown
    /// even when empty); unticking one, pinned, visible or not, unpins it and hides it as the user's choice.</summary>
    public void SetMainColumnHidden(string columnId, bool hidden)
    {
        if (hidden)
        {
            _hiddenMainColumns.Add(columnId);
            _pinnedMainColumns.Remove(columnId);
        }
        else
        {
            _hiddenMainColumns.Remove(columnId);
            if (AutoHideableMainColumns.Contains(columnId))
                _pinnedMainColumns.Add(columnId);
        }

        RecomputeAutoHiddenMainColumns();
        ApplyMainColumnWidths();
        SaveMainTableLayout();
    }

    public void ResetMainColumns()
    {
        _hiddenMainColumns.Clear();
        _hiddenMainColumns.UnionWith(DefaultHiddenMainColumns);
        _pinnedMainColumns.Clear();
        RecomputeAutoHiddenMainColumns();
        ApplyMainColumnWidths();
        SaveMainTableLayout();
    }

    /// <summary>
    /// One pass over the rows the table actually shows (those passing the filters and not folded into another
    /// row), run once per <see cref="ApplyFilter"/>: that is once per change of the row set (a scan's drain
    /// tick raises <c>Updates.CollectionChanged</c> once per batch) and once per filter change such as
    /// "show ignored", never per row. Stops as soon as every still-empty column has been seen with a value.
    /// Applies the widths only when the auto-hidden set actually changed.
    /// </summary>
    private void RecomputeAutoHiddenMainColumns()
    {
        var empty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in AutoHideableMainColumns)
        {
            if (!_pinnedMainColumns.Contains(id))
                empty.Add(id);
        }

        foreach (var row in _visible)
        {
            if (empty.Count == 0)
                break;

            if (row.HasPublishDate)
                empty.Remove("date");
            if (row.HasDownloadSize)
                empty.Remove("size");
            if (row.HasPublisher)
                empty.Remove("publisher");
        }

        if (_autoHiddenMainColumns.SetEquals(empty))
            return;

        _autoHiddenMainColumns.Clear();
        _autoHiddenMainColumns.UnionWith(empty);
        ApplyMainColumnWidths();
    }

    /// <summary>A row gained a date, size or publisher (enrichment lands after the row appeared): when that
    /// column is currently auto-hidden, look again. Costs nothing once the column is visible.</summary>
    private void OnRowColumnDataChanged(string? propertyName)
    {
        var columnId = propertyName switch
        {
            nameof(UpdateRowViewModel.HasPublishDate) => "date",
            nameof(UpdateRowViewModel.HasDownloadSize) => "size",
            nameof(UpdateRowViewModel.HasPublisher) => "publisher",
            _ => null,
        };

        if (columnId is not null && _autoHiddenMainColumns.Contains(columnId))
            RecomputeAutoHiddenMainColumns();
    }

    /// <summary>
    /// Writes the current hidden set into the shared App.xaml GridLength resources: 0 for a hidden
    /// column, its normal width otherwise. DynamicResource on both the header Grid and every row
    /// template picks this up at once, which is what actually collapses the column everywhere; a
    /// StaticResource lookup (the pre-item-77 default) would only ever see the value from first load.
    /// No-op outside a running app (unit tests), where there is no Application to hold the resource.
    /// </summary>
    private void ApplyMainColumnWidths()
    {
        if (Application.Current is not { } app)
            return;

        foreach (var (columnId, resourceKey) in MainColumnResourceKeys)
        {
            var width = IsMainColumnHidden(columnId) ? 0 : MainColumnDefaultWidths[columnId];
            app.Resources[resourceKey] = new GridLength(width);
        }
    }

    /// <summary>
    /// Read once at construction; an unknown id (a removed column, a hand-edited settings.json) is silently
    /// dropped rather than kept around to write back unchanged. Starts from
    /// <see cref="DefaultHiddenMainColumns"/> rather than an empty set, so a saved state that predates a
    /// default-hidden column leaves that column exactly where a fresh install would have it; only
    /// <see cref="TableLayoutState.ShownColumns"/> can override that default, never mere silence about the id.
    /// </summary>
    private void LoadMainTableLayout()
    {
        if (!_settings.Current.Ui.Tables.TryGetValue("main", out var state))
            return;

        _hiddenMainColumns.Clear();
        _hiddenMainColumns.UnionWith(DefaultHiddenMainColumns);

        foreach (var id in state.HiddenColumns)
        {
            if (MainTableColumns.Any(c => c.Id == id && c.CanHide))
                _hiddenMainColumns.Add(id);
        }

        foreach (var id in state.ShownColumns)
        {
            if (MainTableColumns.Any(c => c.Id == id && c.CanHide))
                _hiddenMainColumns.Remove(id);
        }

        _pinnedMainColumns.Clear();
        foreach (var id in state.PinnedColumns)
        {
            if (AutoHideableMainColumns.Contains(id))
                _pinnedMainColumns.Add(id);
        }

        RecomputeAutoHiddenMainColumns();

        if (!string.IsNullOrEmpty(state.SortColumn))
        {
            SortColumn = state.SortColumn;
            SortDescending = state.SortDescending;
        }

        ApplyMainColumnWidths();
        ApplySort();
        RefreshSortGlyphs();
    }

    /// <summary>Back to a fresh install's columns, after a factory reset removed the saved layout.</summary>
    private void ResetMainTableLayout()
    {
        _hiddenMainColumns.Clear();
        _hiddenMainColumns.UnionWith(DefaultHiddenMainColumns);
        _pinnedMainColumns.Clear();
        RecomputeAutoHiddenMainColumns();
        ApplyMainColumnWidths();

        // The sort is part of the layout too; without the saved layout LoadMainTableLayout keeps whatever is
        // set, and the next save wrote the pre-reset sort straight back.
        SortColumn = "name";
        SortDescending = false;
        ApplySort();
        RefreshSortGlyphs();

        LoadMainTableLayout();
    }

    /// <summary>Written back on every change and again when the window closes (MainWindow.xaml.cs,
    /// alongside SaveWindowPlacement).</summary>
    public void SaveMainTableLayout()
    {
        _settings.Current.Ui.Tables["main"] = new TableLayoutState
        {
            HiddenColumns = _hiddenMainColumns.Where(id => !DefaultHiddenMainColumns.Contains(id)).ToList(),
            ShownColumns = DefaultHiddenMainColumns.Where(id => !_hiddenMainColumns.Contains(id)).ToList(),
            PinnedColumns = _pinnedMainColumns.ToList(),
            SortColumn = SortColumn,
            SortDescending = SortDescending,
        };
        Persist();
    }

    /// <summary>Test-only counter, incremented once per real <see cref="ApplyFilter"/> run: proves a bulk
    /// operation over many rows costs one filter pass, not one per row it touches.</summary>
    internal int ApplyFilterRunCountForTest;

    /// <summary>Re-runs the filter and tells the view what is left of the table.</summary>
    private void ApplyFilter()
    {
        ApplyFilterRunCountForTest++;
        _queryMatchCache.Clear();
        RecomputeDuplicates();
        SyncVisible();
        RecomputeAutoHiddenMainColumns();
        RefreshFilteredState();

        // A filter change touches no row's own properties, so the selection-summary properties need an
        // explicit refresh here.
        RefreshSelectionState();
    }

    /// <summary>Folds rows that are the SAME update from a second source into one line; strict key,
    /// filtered rows only, rank breaks ties.</summary>
    private void RecomputeDuplicates()
    {
        _mergedAway.Clear();

        var candidates = new Dictionary<string, List<UpdateRowViewModel>>(StringComparer.Ordinal);

        foreach (var row in Updates)
        {
            // An ignored or snoozed row (visible with "show ignored" on) must not win the fold: it would swallow
            // the installable copy from another source, and it cannot be installed itself.
            if (!PassesFilters(row) || IsIgnored(row.Item) || IsSnoozed(row.Item)
                || SourceRank.DuplicateKey(row.Item) is not { } key)
                continue;

            if (!candidates.TryGetValue(key, out var group))
                candidates[key] = group = [];

            group.Add(row);
        }

        var partners = new Dictionary<UpdateRowViewModel, IReadOnlyList<UpdateRowViewModel>>();
        foreach (var group in candidates.Values)
        {
            if (group.Count < 2)
                continue;

            // Rank first; the source id only breaks a tie, so the choice never depends on scan order.
            var ordered = group
                .OrderBy(r => SourceRank.Of(r.Item))
                .ThenBy(r => r.Item.SourceId, StringComparer.Ordinal)
                .ToList();

            partners[ordered[0]] = ordered.Skip(1).ToList();

            foreach (var loser in ordered.Skip(1))
                _mergedAway.Add(loser);
        }

        // Set once per row, after the groups are known: clearing every row first and then refilling the
        // winners defeated SetAlternateSources' "unchanged, stay silent" check, so every merged row raised its
        // notifications again on each keystroke in the search box.
        foreach (var row in Updates)
            row.SetAlternateSources(partners.GetValueOrDefault(row) ?? []);
    }

    /// <summary>
    /// Brings the visible set in line with the filters, touching ONLY the rows whose visibility actually
    /// changed. Removals go first so a row that moved out never briefly shares the table with its
    /// replacement, and each Add lands at its sorted position because the view owns the ordering.
    /// </summary>
    private void SyncVisible()
    {
        var known = new HashSet<UpdateRowViewModel>(Updates);

        for (var i = _visible.Count - 1; i >= 0; i--)
        {
            var row = _visible[i];
            if (known.Contains(row) && ComputeVisible(row))
                continue;

            _visible.RemoveAt(i);
            _visibleSet.Remove(row);
        }

        foreach (var row in Updates)
        {
            if (!ComputeVisible(row) || !_visibleSet.Add(row))
                continue;

            _visible.Add(row);
        }
    }

    /// <summary>
    /// One tick for a whole category. Selects every installable row in the group, or clears them
    /// all when there is nothing left to select, which is what a second click has to do.
    /// </summary>
    [RelayCommand]
    private void SelectCategory(object? parameter)
    {
        if (parameter is not System.Windows.Data.CollectionViewGroup group)
            return;

        var rows = group.Items.OfType<UpdateRowViewModel>().Where(r => r.CanInstall).ToList();
        if (rows.Count == 0)
            return;

        var selectAll = rows.Any(r => !r.IsSelected);
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var row in rows)
                row.IsSelected = selectAll;
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }

        RefreshSelectionState();
    }

    [RelayCommand]
    private void SelectAll()
    {
        // Tier C rows have nothing to install, and "all" means all of what is on screen; must read
        // from VisibleRows, not Updates, or a search would also tick every hidden row in the table.
        var installable = VisibleRows.Where(u => u.CanInstall).ToList();
        if (installable.Count == 0)
            return;

        var selectAll = installable.Any(u => !u.IsSelected);
        _suppressSelectionRefresh = true;
        try
        {
            foreach (var row in installable)
                row.IsSelected = selectAll;
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }

        RefreshSelectionState();
    }

}
