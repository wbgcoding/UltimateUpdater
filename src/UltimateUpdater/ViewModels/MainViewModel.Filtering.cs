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
    private bool showOnlyCritical;

    /// <summary>Brings the ignored and snoozed rows back into the table, marked with their badge. Off by default: an ignored update is one the user already decided about.</summary>
    [ObservableProperty]
    private bool showIgnored;

    partial void OnShowIgnoredChanged(bool value)
    {
        _settings.Current.Ui.ShowIgnored = value;
        Persist();
        ApplyFilter();
    }

    /// <summary>Hides the whole "Manual update" category (stores, launchers). Those sources have no pill, so only the table, the counts and the headline change.</summary>
    [ObservableProperty]
    private bool hideManual;

    /// <summary>The filter chip's polarity: lit means manual rows shown, like the ignored chip. The persisted setting stays <see cref="HideManual"/>; this is its mirror image.</summary>
    public bool ShowManual
    {
        get => !HideManual;
        set => HideManual = !value;
    }

    partial void OnHideManualChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowManual));
        _settings.Current.Ui.HideManual = value;
        Persist();
        ApplyFilter();
        RefreshHeader();
    }

    public RangeObservableCollection<UpdateRowViewModel> Updates { get; } = [];

    /// <summary>What the table shows: the filtered subset of <see cref="Updates"/>, kept in step by <see cref="SyncVisible"/>. <see cref="UpdatesView"/> is built over it (see the note there for why filtering is not a filter callback).</summary>
    private readonly RangeObservableCollection<UpdateRowViewModel> _visible = [];

    /// <summary>Membership of <see cref="_visible"/> in O(1): the diff runs on every keystroke.</summary>
    private readonly HashSet<UpdateRowViewModel> _visibleSet = [];

    /// <summary>Counts what is on screen, not what was found, so a search with no match shows the "nothing found" card. Includes <see cref="ManualOnlyView"/>: a search matching only a manual-open row must not show that card above a non-empty compact block.</summary>
    public bool HasResults => UpdatesView.Cast<object>().Any() || HasManualOnlyResults;

    /// <summary>Whether the compact block has anything to show right now.</summary>
    public bool HasManualOnlyResults => ManualOnlyView.Cast<object>().Any();

    /// <summary>The rows the user can see. Everything about selecting, counting and installing goes through this: the raw collection would let "select all" during a search pick up hidden rows, and an ignored update that turned critical could be reselected and installed unseen.</summary>
    private IEnumerable<UpdateRowViewModel> VisibleRows => UpdatesView.Cast<UpdateRowViewModel>();

    /// <summary>Counts what the table shows as updates: not ignored, not folded into another row, from a source that is switched on. The headline and the interval balloon both count through this so they never promise more than the table lists. Duplicates are folded by their own key rather than <see cref="_mergedAway"/>, which is built only from rows passing the view filters: with a filter on, a package offered by two sources would be counted twice.</summary>
    private int VisibleUpdateCount(Func<UpdateRowViewModel, bool> counts) =>
        NotIgnored
            .Where(u => IsSourceEnabled(u.Item.SourceId) && counts(u))
            .Select(u => SourceRank.DuplicateKey(u.Item) ?? PackageKey(u.Item))
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>True when at least one source that could have run is switched on and every such source ended in error: nothing was checked, and "up to date" would misreport a clean machine. Pills for absent tools and cancelled sources checked nothing either way and do not count.</summary>
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

            // What the table still lists as an update: installable rows plus those queued or running (a mid-install row is an update until it succeeds), so the headline does not shrink while a batch works.
            var total = VisibleUpdateCount(u => u.Item.CanInstall
                && (u.CanInstall || u.InstallState is RowInstallState.Queued or RowInstallState.Running));
            if (total == 0)
            {
                if (!HasScanResult)
                    return Loc["HeadlineNotChecked"];

                return EveryEnabledSourceFailed ? Loc["HeadlineNoSourceChecked"] : Loc["HeadlineUpToDate"];
            }

            // The singular has its own string in both languages.
            return string.Format(Loc[total == 1 ? "HeadlineUpdatesOne" : "HeadlineUpdates"], total);
        }
    }

    /// <summary>"3 critical" beside the headline, over the same rows the headline counts. The leading space adds to the one between the two runs. Empty while scanning or when none is critical.</summary>
    public string HeadlineCriticalText
    {
        get
        {
            if (IsScanning)
                return "";

            var critical = VisibleUpdateCount(u => u.Item.CanInstall && u.IsCritical
                && (u.CanInstall || u.InstallState is RowInstallState.Queued or RowInstallState.Running));
            return critical == 0 ? "" : " " + string.Format(Loc["HeadlineCritical"], critical);
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

                // Which sources are working right now, not merely pending, so the line moves as each finishes.
                var running = ProviderStatuses
                    .Where(p => p.State == ProviderScanState.Scanning)
                    .Select(p => p.DisplayName)
                    .ToList();

                return running.Count > 0
                    ? $"{progress} · {string.Format(Loc["SubheadlineScanningSources"], string.Join(", ", running))}"
                    : progress;
            }

            // Nothing to say once a scan is done: the headline, the critical badge and the footer's selection summary carry the numbers.
            return string.Empty;
        }
    }

    /// <summary>True when a filter is hiding everything found; checked against the raw <see cref="Updates"/>, never <see cref="NotIgnored"/>.</summary>
    public bool IsFilterHidingEverything => Updates.Any() && !HasResults;

    /// <summary>Whether the table has anything to say about the machine: a scan finished or a saved result loaded this session, or rows are on screen (an ignored or snoozed set of them is a real "nothing left to do").</summary>
    public bool HasScanResult => _scanResultKnown || Updates.Count > 0;

    /// <summary>Before any result exists the empty table is not an answer, so the empty state offers the scan itself. Hidden while one runs: the toolbar button shows that.</summary>
    public bool ShowNotCheckedPrompt => !HasScanResult && !IsScanning;

    /// <summary>"Nothing found", "not looked for yet" and "nothing matches what you typed" are different situations.</summary>
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

    /// <summary>Test-only counter, incremented once per <see cref="RefreshHeader"/> run.</summary>
    internal int RefreshHeaderRunCountForTest;

    /// <param name="resortManualOnly">False on the scan drain's tick, which runs five times a second. A Refresh raises a Reset and the compact block regenerates its containers, closing an open context menu and dropping keyboard focus. The drain only adds rows and the view places an add in sort order itself; only an existing row's key moving (an ignore toggle) needs the re-apply.</param>
    private void RefreshHeader(bool resortManualOnly = true)
    {
        RefreshHeaderRunCountForTest++;
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(HeadlineCriticalText));
        OnPropertyChanged(nameof(SubheadlineText));
        RefreshFilteredState();
        RefreshPillOpenCounts();
        RefreshPillViewIfKeysChanged();
        // IsIgnored is a sort key (ignored rows sink to the bottom) but not a live-sorting property, so a toggle needs an explicit re-apply.
        if (resortManualOnly)
            RefreshViewSafely(ManualOnlyView, nameof(ManualOnlyView));
        OnPropertyChanged(nameof(ScanProgressDone));
        OnPropertyChanged(nameof(ScanProgressTotal));
    }

    /// <summary>Refreshes a view and swallows the failure. Under CPU contention a WPF <see cref="ListCollectionView"/> can throw a bare <see cref="NullReferenceException"/> from its own refresh code, a framework timing hazard rather than a bug in the call order. <see cref="RefreshHeader"/> runs from <see cref="FinishScanUiAsync"/> right before <see cref="RemoveStaleRows"/>, the cache save and clearing <see cref="IsScanning"/>: an escaping exception would skip those and ScanAsync's <c>_scanGate.Release()</c>, wedging every later scan. A missed repaint is cosmetic and heals on the next tick.</summary>
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

    /// <summary>Each pill's sort and filter keys as <see cref="PillView"/> last applied them, and the scanning flag its filter reads.</summary>
    private Dictionary<ProviderStatusViewModel, (int RunningRank, bool IsNoteworthy, string DisplayName)> _pillViewKeys = [];

    private bool _pillViewAppliedWhileScanning;

    /// <summary>A Refresh raises a Reset and the pills' ItemsControl rebuilds every pill button, closing an open tooltip or menu and dropping a click in progress, which on the flush tick would happen several times a second. The view needs one only when a key it sorts or filters by changed (<see cref="ProviderStatusViewModel.RunningRank"/>, IsNoteworthy, the name, <see cref="IsScanning"/>). A pill is added before its first status is applied, so one not seen here yet counts as a change.</summary>
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

        // Recorded only once the view really applied them: a failed refresh is retried next time.
        if (changed && !RefreshViewSafely(PillView, nameof(PillView)))
            return;

        _pillViewKeys = keys;
        _pillViewAppliedWhileScanning = IsScanning;
    }

    /// <summary>Grouped and filtered view the table binds to; Updates stays the unfiltered source.</summary>
    public ICollectionView UpdatesView { get; }

    /// <summary>The manual-open rows' compact block: a second, independent <see cref="ListCollectionView"/> over the same visible collection, not a GroupStyleSelector.</summary>
    public ICollectionView ManualOnlyView { get; }

    /// <summary>Which category groups the user collapsed, keyed by <see cref="UpdateRowViewModel.CategoryKey"/>, never the translated name.</summary>
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

    /// <summary>The "keeps failing" filter chip: collects every row the three-strikes tally promoted to <see cref="RowInstallState.NeedsManualWork"/>. Session-only, like <see cref="ShowOnlyCritical"/>; turning it off returns to the normal view and leaves the rows untouched.</summary>
    [ObservableProperty]
    private bool showOnlyPersistentFailures;

    partial void OnShowOnlyPersistentFailuresChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPersistentFailureFilter));
        ApplyFilter();
    }

    /// <summary>Rows the table folded into another because they are the same update from a second source. They stay in <see cref="Updates"/>, since a scan still knows them and they return when the winning source drops out; they just get no line of their own.</summary>
    private readonly HashSet<UpdateRowViewModel> _mergedAway = [];

    private bool ComputeVisible(UpdateRowViewModel row) => PassesFilters(row) && !_mergedAway.Contains(row);

    private bool PassesFilters(UpdateRowViewModel row)
    {
        // Attached once per row, the first time it reaches a filter pass, long before it can change state.
        WatchPersistentFailureState(row);

        // Ignored rows stay out of the table unless the user asks to see them; they then show marked.
        if (IsIgnored(row.Item) && !ShowIgnored)
            return false;

        // A snoozed row is hidden exactly like an ignored one, by the same switch.
        if (IsSnoozed(row.Item) && !ShowIgnored)
            return false;

        // The whole "Manual update" category can be hidden as one.
        if (HideManual && row.IsManualOnly)
            return false;

        // A source switched off from its pill drops out of the table at once.
        if (!IsSourceEnabled(row.Item.SourceId))
            return false;

        if (ShowOnlyCritical && !row.IsCritical)
            return false;

        // A positive filter like ShowOnlyCritical above, but it also drops an ignored row: once ignored, the point of collecting it (deciding what to do) is done, and it would otherwise sit in this view with no way to make it go.
        if (ShowOnlyPersistentFailures && (row.InstallState != RowInstallState.NeedsManualWork || IsIgnored(row.Item)))
            return false;

        return MatchesQueryCached(row);
    }

    /// <summary>Rows wired to keep their <see cref="UpdateRowViewModel.PersistentFailureAttempts"/> and this chip's count in step with <see cref="AppSettings.PersistentFailures"/> whenever InstallState flips. Attached lazily the first time a row reaches <see cref="PassesFilters"/>, since every row passes there long before it can change state.</summary>
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
            if (ShowOnlyPersistentFailures
                || (LeavesFold(row) && (_mergedAway.Contains(row) || row.HasAlternateSources)))
                ApplyFilter();
        };
        _persistentFailureWatched[row] = handler;
        row.PropertyChanged += handler;
    }

    /// <summary>Lets go of rows that left <see cref="Updates"/> (replaced by a rescan, dropped by a sweep); otherwise every row ever shown stays reachable through its subscription for the whole session.</summary>
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

    /// <summary>The "keeps failing" chip's count, over the live rows rather than the settings dictionary, so it matches what turning the chip on would show. Only rows the filter itself would show: an ignored stuck row, a snoozed row or one of the hidden manual category is held back by PassesFilters the same way.</summary>
    public int PersistentFailureCount => Updates.Count(u => u.InstallState == RowInstallState.NeedsManualWork
        && !IsIgnored(u.Item) && (ShowIgnored || !IsSnoozed(u.Item)) && !(HideManual && u.IsManualOnly)
        && IsSourceEnabled(u.Item.SourceId) && !_mergedAway.Contains(u));

    /// <summary>Hidden entirely while there is nothing to collect, never an empty chip.</summary>
    public bool HasPersistentFailures => PersistentFailureCount > 0;

    /// <summary>Offered only while there is something to collect or its filter is on, so it can always be turned off.</summary>
    public bool ShowPersistentFailureFilter => HasPersistentFailures || ShowOnlyPersistentFailures;

    public string PersistentFailureFilterLabel => string.Format(Loc["FilterPersistentFailures"], PersistentFailureCount);

    /// <summary>The chip's visible text, short enough for the default window width; the long <see cref="PersistentFailureFilterLabel"/> stays the tooltip and accessible name.</summary>
    public string PersistentFailureChipLabel => string.Format(Loc["FilterChipFailures"], PersistentFailureCount);

    private void NotifyPersistentFailureCountChanged()
    {
        OnPropertyChanged(nameof(PersistentFailureCount));
        OnPropertyChanged(nameof(HasPersistentFailures));
        OnPropertyChanged(nameof(ShowPersistentFailureFilter));
        OnPropertyChanged(nameof(PersistentFailureFilterLabel));
        OnPropertyChanged(nameof(PersistentFailureChipLabel));
    }

    /// <summary>The search match is the expensive part of the filter (a fuzzy score per row and field) and every pass asks for it twice per row, in <see cref="RecomputeDuplicates"/> and <see cref="SyncVisible"/>. It depends only on the row and the query, so it is computed once per pass. Cleared at the start of each pass because a row's name can change under it (enrichment, language switch) while the query stays.</summary>
    private readonly Dictionary<UpdateRowViewModel, bool> _queryMatchCache = [];

    private bool MatchesQueryCached(UpdateRowViewModel row)
    {
        if (_queryMatchCache.TryGetValue(row, out var matches))
            return matches;

        matches = UpdateFilter.MatchesQuery(SearchQuery, row.Name, row.Item.Id, row.SourceDisplay, row.Publisher);
        _queryMatchCache[row] = matches;
        return matches;
    }

    /// <summary>Neither a running scan nor a running batch blocks this: selected rows join the running batch through EnqueueInstall instead of starting a second one (the single drain loop in RunInstallLoopAsync). Computed once per <see cref="RefreshSelectionState"/> pass.</summary>
    public bool CanInstallSelected => _canInstallSelected;
    private bool _canInstallSelected;

    /// <summary>True when every visible installable row is ticked; drives the "Clear selection" wording. Computed once per <see cref="RefreshSelectionState"/> pass.</summary>
    private bool AreAllVisibleSelected => _areAllVisibleSelected;
    private bool _areAllVisibleSelected;

    /// <summary>The header checkbox's tooltip and accessible name: "Clear selection" once everything visible is ticked, "Select all" otherwise.</summary>
    public string SelectAllTooltip => Loc[_areAllVisibleSelected ? "SelectAllClear" : "SelectAllButton"];

    /// <summary>How much is selected; empty when nothing is. Computed once per <see cref="RefreshSelectionState"/> pass.</summary>
    public string SelectionSummary => _selectionSummary;
    private string _selectionSummary = string.Empty;

    /// <summary>Set around a bulk selection change (SelectAll, SelectCategory, a source toggle's unselect sweep) so TrackSelection's per-row hop into <see cref="RefreshSelectionState"/> is skipped; the caller runs it once afterwards. Otherwise ticking N rows costs N passes, each O(N).</summary>
    private bool _suppressSelectionRefresh;

    /// <summary>Test-only counter, incremented once per real <see cref="RefreshSelectionState"/> run: proves a bulk selection change costs one pass.</summary>
    internal int RefreshSelectionStateRunCountForTest;

    /// <summary>The single place the six selection-derived properties (<see cref="CanInstallSelected"/>, <see cref="HasSelection"/>, <see cref="AreAllVisibleSelected"/>, <see cref="AllVisibleSelected"/>, <see cref="SelectAllTooltip"/>, <see cref="SelectionSummary"/>) are recomputed, walking the visible rows once instead of each getter on every binding refresh. Called wherever a selection or the visible set can change. Internal so a test that adds rows directly, bypassing the scan's per-row subscription, can wire the same invalidation.</summary>
    internal void RefreshSelectionState()
    {
        RefreshSelectionStateRunCountForTest++;
        var visible = VisibleRows.ToList();
        var installable = visible.Where(u => u.CanInstall).ToList();
        var selected = visible.Where(u => u.IsSelected).ToList();
        var selectedInstallable = installable.Where(u => u.IsSelected).ToList();
        var selectedInstallableCount = selectedInstallable.Count;

        _hasSelection = selected.Count > 0;
        // A ticked row that cannot install (a failed uninstall, a row mid-run) must not keep the button on: it would queue nothing, or reinstall what the user just removed.
        _canInstallSelected = selectedInstallableCount > 0;
        _areAllVisibleSelected = installable.Count > 0 && selectedInstallableCount == installable.Count;
        _allVisibleSelected = installable.Count == 0
            ? false
            : selectedInstallableCount == 0 ? false : selectedInstallableCount == installable.Count ? true : null;
        if (selectedInstallableCount == 0)
            _selectionSummary = string.Empty;
        else
        {
            var criticalCount = selectedInstallable.Count(u => u.IsCritical);
            var baseText = criticalCount > 0
                ? string.Format(Loc["SelectionSummaryWithCritical"], selectedInstallableCount, criticalCount)
                : string.Format(Loc["SelectionSummary"], selectedInstallableCount);

            // Pre-install preview: the combined download size of the selection as it becomes known (enrichment fills sizes in after the scan); shown only once there is a real total.
            var totalBytes = selectedInstallable.Sum(u => u.DownloadSizeBytes ?? 0L);
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

    /// <summary>Whether anything is selected, so the summary text can be hidden when nothing is. Computed once per <see cref="RefreshSelectionState"/> pass.</summary>
    public bool HasSelection => _hasSelection;
    private bool _hasSelection;

    /// <summary>Bumped on every selection change. A CollectionViewGroup raises nothing when a row inside it is ticked, so the category checkboxes bind to this next to their group to be re-evaluated.</summary>
    public int SelectionVersion { get; private set; }

    /// <summary>Tri-state for the header's select-all checkbox: all visible installable rows ticked (true), none (false), some (null, the indeterminate box). Bound one-way; the click runs SelectAll, which toggles. False when nothing installable is on screen. Computed once per <see cref="RefreshSelectionState"/> pass.</summary>
    public bool? AllVisibleSelected => _allVisibleSelected;
    private bool? _allVisibleSelected;

    /// <summary>Re-filtering re-groups and rebuilds every row container, far too much for every keystroke. The debounce lives on the binding (the search box binds with Delay), so the view model stays synchronous: setting SearchQuery filters at once, which keeps it testable without a message pump.</summary>
    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    /// <summary>Which column the table is sorted by within each category group, and the direction.</summary>
    [ObservableProperty]
    private string sortColumn = "name";

    [ObservableProperty]
    private bool sortDescending;

    /// <summary>Clicking a column header sorts by it; clicking the active column flips the direction. Each column has a default direction: names A to Z, dates and sizes largest or newest first.</summary>
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
        // Ignored rows sink to the bottom of their category (false sorts before true), whatever the column.
        UpdatesView.SortDescriptions.Add(
            new SortDescription(nameof(UpdateRowViewModel.IsIgnored), ListSortDirection.Ascending));
        var hasValue = SortColumn switch
        {
            "date" => nameof(UpdateRowViewModel.HasPublishDate),
            "size" => nameof(UpdateRowViewModel.HasDownloadSize),
            "version" => nameof(UpdateRowViewModel.HasVersions),
            _ => null,
        };
        if (hasValue is not null)
            UpdatesView.SortDescriptions.Add(new SortDescription(hasValue, ListSortDirection.Descending));
        UpdatesView.SortDescriptions.Add(new SortDescription(key, direction));
    }

    private void RefreshSortGlyphs()
    {
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(DateSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(VersionSortGlyph));
    }

    /// <summary>Only the active column carries a mark; an inactive one stays empty, and the glyph slot keeps its width so the label never shifts.</summary>
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

    /// <summary>The main table's column list for the shared chooser (<see cref="Views.ColumnChooser"/>). Check, icon and action are structural and never offered; Name may not be hidden.</summary>
    public static IReadOnlyList<ColumnDescriptor> MainTableColumns { get; } =
    [
        new("name", () => LocalizationService.Instance["ColumnName"], CanHide: false),
        new("date", () => LocalizationService.Instance["ColumnDate"], CanHide: true),
        new("version", () => LocalizationService.Instance["ColumnVersion"], CanHide: true),
        new("size", () => LocalizationService.Instance["ColumnSize"], CanHide: true),
        new("publisher", () => LocalizationService.Instance["ColumnPublisher"], CanHide: true),
    ];

    /// <summary>Which hideable columns start hidden on a fresh install; also what "Reset columns" restores. Empty: every column shows by default. Only <see cref="TableLayoutState.ShownColumns"/> counts as "shown", never silence about an id.</summary>
    private static readonly HashSet<string> DefaultHiddenMainColumns = new(StringComparer.Ordinal);

    /// <summary>The default width of each hideable main-table column, restored when it is shown again.</summary>
    private static readonly Dictionary<string, double> MainColumnDefaultWidths = new(StringComparer.Ordinal)
    {
        ["date"] = 104,
        ["version"] = 168,
        ["size"] = 96,
        ["publisher"] = 120,
    };

    /// <summary>Which App.xaml GridLength resource each hideable column's width lives in; the header row and every row template (MainWindow.xaml) share it, so one entry collapses or restores the column everywhere.</summary>
    private static readonly Dictionary<string, string> MainColumnResourceKeys = new(StringComparer.Ordinal)
    {
        ["date"] = "ColWidthDate",
        ["version"] = "ColWidthVersion",
        ["size"] = "ColWidthSize",
        ["publisher"] = "ColWidthPublisher",
    };

    /// <summary>The columns the user hid (or that start hidden); the persisted choice.</summary>
    private readonly HashSet<string> _hiddenMainColumns = new(DefaultHiddenMainColumns, StringComparer.Ordinal);

    /// <summary>Columns the user ticked in the chooser: shown even while no row has a value. Persisted as is (<see cref="TableLayoutState.PinnedColumns"/>).</summary>
    private readonly HashSet<string> _pinnedMainColumns = new(StringComparer.Ordinal);

    /// <summary>Columns hidden only because no row has a value for them. Derived from the table, never persisted; recomputed once per change of the row set.</summary>
    private readonly HashSet<string> _autoHiddenMainColumns = new(StringComparer.Ordinal);

    /// <summary>The columns that disappear while empty; version always has a value, name and the structural columns are never data columns.</summary>
    private static readonly string[] AutoHideableMainColumns = ["date", "size", "publisher"];

    /// <summary>Effective state: hidden by the user, or empty and not pinned. The chooser shows it unticked and it collapses the column.</summary>
    public bool IsMainColumnHidden(string columnId) =>
        _hiddenMainColumns.Contains(columnId) || _autoHiddenMainColumns.Contains(columnId);

    /// <summary>Wired to the chooser button in MainWindow.xaml. Ticking a column pins it (shown even when empty); unticking one, pinned or not, unpins it and hides it as the user's choice.</summary>
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

    /// <summary>One pass over the rows the table shows (passing the filters, not folded into another), run once per <see cref="ApplyFilter"/>, never per row. Stops once every still-empty column has been seen with a value. Applies the widths only when the auto-hidden set changed.</summary>
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

    /// <summary>A row gained a date, size or publisher (enrichment lands after the row appeared): when that column is auto-hidden, look again. Free once the column is visible.</summary>
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

    /// <summary>Writes the hidden set into the shared App.xaml GridLength resources: 0 for a hidden column, its normal width otherwise. DynamicResource on the header Grid and every row template picks it up at once; a StaticResource would only see the value from first load. No-op outside a running app (unit tests).</summary>
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

    /// <summary>Read once at construction; an unknown id (a removed column, a hand-edited settings.json) is dropped instead of written back. Starts from <see cref="DefaultHiddenMainColumns"/> so a saved state that predates a default-hidden column leaves it where a fresh install has it; only <see cref="TableLayoutState.ShownColumns"/> can override that.</summary>
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

        // The sort is part of the layout too; otherwise LoadMainTableLayout keeps what is set and the next save writes the old sort back.
        SortColumn = "name";
        SortDescending = false;
        ApplySort();
        RefreshSortGlyphs();

        LoadMainTableLayout();
    }

    /// <summary>Written on every change and again when the window closes (MainWindow.xaml.cs, with SaveWindowPlacement).</summary>
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

    /// <summary>Test-only counter, incremented once per real <see cref="ApplyFilter"/> run: proves a bulk operation costs one filter pass.</summary>
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

        // Snoozing, un-snoozing, "show ignored" and the manual toggle all end here, and each moves the chip's count.
        NotifyPersistentFailureCountChanged();

        // A filter change touches no row's own properties, so the selection-summary properties need an explicit refresh.
        RefreshSelectionState();
    }

    /// <summary>Folds rows that are the same update from a second source into one line: strict key, filtered rows only, rank breaks ties.</summary>
    private static bool LeavesFold(UpdateRowViewModel row) => row.InstallState is RowInstallState.Failed
        or RowInstallState.Stuck or RowInstallState.HandedOff or RowInstallState.NeedsManualWork
        or RowInstallState.Cancelled;

    private void RecomputeDuplicates()
    {
        _mergedAway.Clear();

        var candidates = new Dictionary<string, List<UpdateRowViewModel>>(StringComparer.Ordinal);

        foreach (var row in Updates)
        {
            // An ignored or snoozed row (visible with "show ignored" on) must not win the fold: it would swallow the installable copy from another source and cannot be installed itself. A row that failed, got stuck, was handed off or stopped shows as its own line, or its outcome would stay invisible until the next scan.
            if (!PassesFilters(row) || IsIgnored(row.Item) || IsSnoozed(row.Item) || LeavesFold(row)
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

        // Set once per row after the groups are known: clearing every row first and refilling the winners defeats SetAlternateSources' "unchanged, stay silent" check and every merged row would notify on each keystroke.
        foreach (var row in Updates)
            row.SetAlternateSources(partners.GetValueOrDefault(row) ?? []);
    }

    /// <summary>Takes a row out of the visible set so the next <see cref="SyncVisible"/> adds it at its sorted place. <c>IsIgnored</c> is a sort key but the view does not sort live, and the diff otherwise leaves a still-visible row where it is.</summary>
    private void ReseatInVisible(UpdateRowViewModel row)
    {
        if (_visibleSet.Remove(row))
            _visible.Remove(row);
    }

    /// <summary>Brings the visible set in line with the filters, touching only rows whose visibility changed. Removals go first so a departing row never briefly shares the table with its replacement; each Add lands at its sorted position because the view owns the ordering.</summary>
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

    /// <summary>One tick for a whole category: selects every installable row in the group, or clears them all when none is left to select (the second click).</summary>
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
        // Tier C rows have nothing to install, and "all" means all that is on screen: read from VisibleRows, not Updates, or a search would also tick every hidden row.
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
