using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Views;

namespace UltimateUpdater.ViewModels;

public sealed partial class HistoryRowViewModel(
    HistoryEntry entry, bool canDowngrade = false, Action<HistoryEntry>? onDowngrade = null)
    : ObservableObject
{
    public string When => entry.Timestamp.LocalDateTime.ToString("g", CultureInfo.CurrentUICulture);

    /// <summary>Sort key for the When column; the real timestamp, not its formatted text.</summary>
    public DateTimeOffset SortWhen => entry.Timestamp;

    public string Name => entry.Name;

    public override string ToString() => $"{entry.Name}, {When}";

    /// <summary>What a screen reader reads for the entry as a whole: what, how it ended, and when.</summary>
    public string AutomationName => $"{Name}, {Result}, {When}";

    public string SourceId => entry.SourceId;

    /// <summary>The readable source name, for the source filter buttons.</summary>
    public string SourceDisplay => SourceNames.Display(entry.SourceId);

    /// <summary>Right side of the version lane's fixed 3-column grid (mirroring the main table's own version
    /// lane), so the arrow between the two sits at the same x position regardless of how long either version
    /// string is. A missing value is a plain hyphen, never blank.</summary>
    public string FromVersionText => entry.FromVersion ?? "-";

    public string ToVersionText => entry.ToVersion ?? "-";

    /// <summary>False centres <see cref="ToVersionText"/> alone across the whole lane instead of its own
    /// column, same "nothing to point an arrow away from" treatment the main table already gives a row
    /// with no installed version.</summary>
    public bool HasFromVersion => entry.FromVersion is not null;

    /// <summary>A single plain-text string for the failed-row error detail window, which shows the
    /// version change as one line rather than the grid-based lane above: null when the entry carries no
    /// version info at all (unlike the lane's own cells, which always show at least a hyphen).</summary>
    public string? VersionChangeText => entry.FromVersion is null && entry.ToVersion is null
        ? null
        : $"{FromVersionText} → {ToVersionText}";

    /// <summary>The size column's own sort key: the real byte count, so sorting is numeric rather than
    /// alphabetic on the formatted text (which would put "1.2 GB" before "500 MB").</summary>
    public long SortSize => entry.SizeBytes ?? 0;

    /// <summary>Same helper and culture the main table's own size column uses, so German shows a comma;
    /// a missing value (no size reported, or an entry from before this column existed) is a plain hyphen.</summary>
    public string SizeText => entry.SizeBytes is { } bytes ? ByteSize.Format(bytes) : "-";

    /// <summary>A success says what was done: updated, rolled back to an older version, or uninstalled.</summary>
    public string Result => LocalizationService.Instance[
        entry.IsManualHandoff ? "HistoryHandedOff"
        : entry.IsNotApplicable ? "HistoryNotApplicable"
        : entry.IsCancelled ? "RowCancelled"
        : !entry.Success ? "HistoryFailed"
        : entry.IsRollback ? "HistoryRolledBack"
        : entry.Action == "uninstall" ? "HistoryUninstalled"
        : "HistorySucceeded"];

    public string? Error => entry.Error;

    /// <summary>The failing tool's own captured output, when this entry was written with one; null for
    /// every entry from before Detail existed, which falls back to a log excerpt (HistoryWindow.xaml.cs).</summary>
    public string? Detail => entry.Detail;

    public DateTimeOffset Timestamp => entry.Timestamp;

    /// <summary>A handoff is neither: nothing is actually known to have gone wrong, so it must not count
    /// toward "show only failed" or paint in the failure colour. Neither is a package the manager simply
    /// has no fitting version for, nor a row the user stopped.</summary>
    public bool Failed => !entry.Success && !entry.IsManualHandoff && !entry.IsNotApplicable && !entry.IsCancelled;

    /// <summary>A real success paints in the success colour; a handoff, a not-applicable outcome and a
    /// stopped row carry no usable <c>Success</c> and never count.</summary>
    public bool Succeeded => entry.Success && !IsManualHandoff;

    /// <summary>All terminal non-failures share the row's caution styling.</summary>
    public bool IsManualHandoff => entry.IsManualHandoff || entry.IsNotApplicable || entry.IsCancelled;

    /// <summary>A tooltip alone is not enough to read past a log file's first line; a
    /// small button on failed rows opens the full text instead, through the same ErrorDetailWindow a
    /// failed install row in the main table already uses.</summary>
    public string ShowDetailsTooltip => LocalizationService.Instance["HistoryShowDetails"];

    /// <summary>
    /// Same rule the live row's own button applies, computed once by <see cref="HistoryViewModel"/>
    /// when the window opens (whether the source can pin a version, and whether this is the NEWEST
    /// successful install on record for the package, i.e. the one that produced the currently installed
    /// version) and handed in here rather than recomputed per entry.
    /// </summary>
    public bool CanDowngrade => canDowngrade;

    public string DowngradeHeader =>
        canDowngrade ? string.Format(LocalizationService.Instance["RowDowngrade"], entry.FromVersion) : string.Empty;

    [RelayCommand(CanExecute = nameof(CanDowngrade))]
    private void Downgrade() => onDowngrade?.Invoke(entry);

    /// <summary>The result word is read from the resources in code, so a language change has to re-raise it.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(ShowDetailsTooltip));
        OnPropertyChanged(nameof(DowngradeHeader));
        OnPropertyChanged(nameof(SourceDisplay));
    }
}

/// <summary>How far back the history list reaches; one of the filter buttons above the table.</summary>
public enum HistoryTimeRange
{
    All,
    Today,
    Week,
    Month,
}

/// <summary>One source's filter button; several can be active at once.</summary>
public sealed partial class HistorySourceFilter : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    private bool isActive;

    public HistorySourceFilter(string sourceId, Action onChanged)
    {
        SourceId = sourceId;
        _onChanged = onChanged;
    }

    public string SourceId { get; }

    public string Display => SourceNames.Display(SourceId);

    public override string ToString() => Display;

    /// <summary>Some source names are translated, so a language switch has to re-raise the chip text.</summary>
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(Display));

    partial void OnIsActiveChanged(bool value) => _onChanged();
}

public partial class HistoryViewModel : ObservableObject
{
    /// <summary>
    /// Optional (trailing, added later) rather than required: several existing tests construct this
    /// view model directly with only a HistoryService and positional filter/downgrade arguments, and
    /// changing those call sites is out of this item's own owned-files scope. Null simply means the
    /// column chooser state is never persisted (nothing to load, nothing to save), everything else works
    /// the same.
    /// </summary>
    private readonly SettingsService? _settings;

    public LocalizationService Loc => LocalizationService.Instance;

    public ObservableCollection<HistoryRowViewModel> Entries { get; }

    /// <summary>Search, failed-only, per-source and time-range filters over the history.</summary>
    public ICollectionView EntriesView { get; }

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private bool showOnlyFailed;

    /// <summary>
    /// One toggle button per source that actually appears in the history; so the row of buttons stays
    /// short and never offers a filter that would match nothing. None active means
    /// "all sources".
    /// </summary>
    public IReadOnlyList<HistorySourceFilter> SourceFilters { get; }

    public bool HasSourceFilters => SourceFilters.Count > 0;

    [ObservableProperty]
    private HistoryTimeRange timeRange = HistoryTimeRange.All;

    /// <summary>Which time-range button reads as pressed.</summary>
    public bool IsRangeAll => TimeRange == HistoryTimeRange.All;
    public bool IsRangeToday => TimeRange == HistoryTimeRange.Today;
    public bool IsRangeWeek => TimeRange == HistoryTimeRange.Week;
    public bool IsRangeMonth => TimeRange == HistoryTimeRange.Month;

    [RelayCommand]
    private void SetTimeRange(string? range) =>
        TimeRange = range switch
        {
            "today" => HistoryTimeRange.Today,
            "week" => HistoryTimeRange.Week,
            "month" => HistoryTimeRange.Month,
            _ => HistoryTimeRange.All,
        };

    /// <summary>There are entries at all (the empty-state vs. the table).</summary>
    public bool HasEntries => Entries.Count > 0;

    public bool IsEmpty => !HasEntries;

    /// <summary>Nothing matches the current search/filter, though the history is not itself empty.</summary>
    public bool NoMatches => HasEntries && !EntriesView.Cast<object>().Any();

    /// <summary>
    /// Reads and parses the history file on the CALLING thread. Production code uses
    /// <see cref="CreateAsync"/>; this one is for callers that already are off the UI thread, and for tests.
    /// </summary>
    internal HistoryViewModel(
        HistoryService history,
        IReadOnlySet<string>? pinningCapableSourceIds = null,
        Action<HistoryEntry>? onDowngrade = null,
        SettingsService? settings = null)
        : this(history.Read(), pinningCapableSourceIds, onDowngrade, settings)
    {
    }

    /// <summary>Reads the history file on a pool thread and builds the view model on the caller's thread
    /// afterwards, so opening the window never parses up to 5 MB of JSON on the dispatcher.</summary>
    public static async Task<HistoryViewModel> CreateAsync(
        HistoryService history,
        IReadOnlySet<string>? pinningCapableSourceIds = null,
        Action<HistoryEntry>? onDowngrade = null,
        SettingsService? settings = null)
    {
        var read = await Task.Run(() => history.Read());
        return new HistoryViewModel(read, pinningCapableSourceIds, onDowngrade, settings);
    }

    private HistoryViewModel(
        IReadOnlyList<HistoryEntry> read,
        IReadOnlySet<string>? pinningCapableSourceIds,
        Action<HistoryEntry>? onDowngrade,
        SettingsService? settings)
    {
        _settings = settings;

        // The newest successful "install" entry per package is the one that produced the
        // CURRENTLY installed version, so it is the only one a rollback can be offered from; Read() is
        // already newest first, so the first match per key here is that entry.
        var newestSuccessfulInstallKeys = new HashSet<string>(StringComparer.Ordinal);
        var canDowngradeFlags = new bool[read.Count];
        for (var i = 0; i < read.Count; i++)
        {
            var entry = read[i];
            if (entry.Action != "install" || !entry.Success || entry.IsManualHandoff
                || entry.IsNotApplicable || entry.IsCancelled || string.IsNullOrEmpty(entry.FromVersion))
            {
                continue;
            }

            var key = $"{entry.SourceId}:{entry.ItemId}";
            if (!newestSuccessfulInstallKeys.Add(key))
                continue; // an older entry for the same package, already superseded

            canDowngradeFlags[i] = pinningCapableSourceIds?.Contains(entry.SourceId) == true;
        }

        Entries = new ObservableCollection<HistoryRowViewModel>(
            read.Select((e, i) => new HistoryRowViewModel(e, canDowngradeFlags[i], onDowngrade)));

        SourceFilters = Entries
            .Select(row => row.SourceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => SourceNames.Display(id), StringComparer.CurrentCultureIgnoreCase)
            .Select(id => new HistorySourceFilter(id, RefreshView))
            .ToList();
        _activeSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // the new buttons start unpressed

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = Matches;

        // Newest first by default, which is what a history is read for.
        ApplySort();

        // The remembered column chooser state and sort, after the default above.
        LoadTableLayout();
    }

    /// <summary>
    /// The history table's own column list for the shared chooser (<see cref="Views.ColumnChooser"/>).
    /// The details button is structural (no header of its own) and is never offered; Name may not be hidden.
    /// </summary>
    public static IReadOnlyList<ColumnDescriptor> HistoryTableColumns { get; } =
    [
        new("name", () => LocalizationService.Instance["ColumnName"], CanHide: false),
        new("when", () => LocalizationService.Instance["HistoryColumnWhen"], CanHide: true),
        new("source", () => LocalizationService.Instance["ColumnSource"], CanHide: true),
        new("version", () => LocalizationService.Instance["ColumnVersion"], CanHide: true),
        new("size", () => LocalizationService.Instance["HistoryColumnSize"], CanHide: true),
        new("result", () => LocalizationService.Instance["HistoryColumnResult"], CanHide: true),
    ];

    private static readonly Dictionary<string, double> HistoryColumnDefaultWidths = new(StringComparer.Ordinal)
    {
        ["when"] = 130,
        ["source"] = 110,
        ["version"] = 170,
        ["size"] = 90,
        ["result"] = 110,
    };

    /// <summary>Same DynamicResource-write trick as the main table's own column widths
    /// (<see cref="MainViewModel.MainColumnResourceKeys"/>): one write here collapses (or restores) the
    /// column in both the header Grid and the row template at once.</summary>
    private static readonly Dictionary<string, string> HistoryColumnResourceKeys = new(StringComparer.Ordinal)
    {
        ["when"] = "HistoryColWidthWhen",
        ["source"] = "HistoryColWidthSource",
        ["version"] = "HistoryColWidthVersion",
        ["size"] = "HistoryColWidthSize",
        ["result"] = "HistoryColWidthResult",
    };

    private readonly HashSet<string> _hiddenColumns = new(StringComparer.Ordinal);

    public bool IsColumnHidden(string columnId) => _hiddenColumns.Contains(columnId);

    public void SetColumnHidden(string columnId, bool hidden)
    {
        if (hidden)
            _hiddenColumns.Add(columnId);
        else
            _hiddenColumns.Remove(columnId);

        ApplyColumnWidths();
        SaveTableLayout();
    }

    public void ResetColumns()
    {
        _hiddenColumns.Clear();
        ApplyColumnWidths();
        SaveTableLayout();
    }

    private void ApplyColumnWidths()
    {
        if (System.Windows.Application.Current is not { } app)
            return;

        foreach (var (columnId, resourceKey) in HistoryColumnResourceKeys)
        {
            var width = _hiddenColumns.Contains(columnId) ? 0 : HistoryColumnDefaultWidths[columnId];
            app.Resources[resourceKey] = new System.Windows.GridLength(width);
        }
    }

    private void LoadTableLayout()
    {
        if (_settings is null || !_settings.Current.Ui.Tables.TryGetValue("history", out var state))
            return;

        _hiddenColumns.Clear();
        foreach (var id in state.HiddenColumns)
        {
            if (HistoryTableColumns.Any(c => c.Id == id && c.CanHide))
                _hiddenColumns.Add(id);
        }

        if (!string.IsNullOrEmpty(state.SortColumn))
        {
            SortColumn = state.SortColumn;
            SortDescending = state.SortDescending;
        }

        ApplyColumnWidths();
        ApplySort();
        RefreshSortGlyphs();
    }

    /// <summary>Written on every change and again when the window closes (HistoryWindow.xaml.cs); a no-op
    /// without a SettingsService (see the field's own doc comment).</summary>
    public void SaveTableLayout()
    {
        if (_settings is null)
            return;

        _settings.Current.Ui.Tables["history"] = new TableLayoutState
        {
            HiddenColumns = _hiddenColumns.ToList(),
            SortColumn = SortColumn,
            SortDescending = SortDescending,
        };
        _settings.SaveDeferred();
    }

    private bool Matches(object candidate)
    {
        if (candidate is not HistoryRowViewModel row)
            return false;

        if (ShowOnlyFailed && !row.Failed)
            return false;

        if (Cutoff() is { } cutoff && row.SortWhen < cutoff)
            return false;

        // No source button pressed means every source; otherwise only the pressed ones.
        if (_activeSourceIds.Count > 0 && !_activeSourceIds.Contains(row.SourceId))
            return false;

        return UpdateFilter.MatchesQuery(SearchQuery, row.Name, row.SourceId);
    }

    private DateTimeOffset? Cutoff() => TimeRange switch
    {
        HistoryTimeRange.Today => DateTimeOffset.Now.Date,
        HistoryTimeRange.Week => DateTimeOffset.Now.AddDays(-7),
        HistoryTimeRange.Month => DateTimeOffset.Now.AddDays(-30),
        _ => null,
    };

    // Sorting: click a column header to sort by it, click the active one again to flip direction.

    [ObservableProperty]
    private string sortColumn = "when";

    [ObservableProperty]
    private bool sortDescending = true;

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column))
            return;

        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column == "when";      // newest first, everything else A→Z
        }

        ApplySort();
        RefreshSortGlyphs();
    }

    private void ApplySort()
    {
        var key = SortColumn switch
        {
            "name" => nameof(HistoryRowViewModel.Name),
            "source" => nameof(HistoryRowViewModel.SourceDisplay),
            "version" => nameof(HistoryRowViewModel.ToVersionText),
            "size" => nameof(HistoryRowViewModel.SortSize),
            "result" => nameof(HistoryRowViewModel.Result),
            _ => nameof(HistoryRowViewModel.SortWhen),
        };

        EntriesView.SortDescriptions.Clear();
        EntriesView.SortDescriptions.Add(new SortDescription(
            key, SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    private string GlyphFor(string column) =>
        SortColumn != column ? string.Empty : SortDescending ? " ▼" : " ▲";

    public string WhenSortGlyph => GlyphFor("when");
    public string NameSortGlyph => GlyphFor("name");
    public string SourceSortGlyph => GlyphFor("source");
    public string VersionSortGlyph => GlyphFor("version");
    public string SizeSortGlyph => GlyphFor("size");
    public string ResultSortGlyph => GlyphFor("result");

    /// <summary>
    /// The language changed while this window was open. The rows read their result word from the resources
    /// in code, so nothing would re-read it; the list stayed in the old language until it was reopened.
    /// </summary>
    public void RefreshLocalizedText()
    {
        foreach (var entry in Entries)
            entry.RefreshLocalizedText();

        foreach (var filter in SourceFilters)
            filter.RefreshLocalizedText();
    }

    private void RefreshSortGlyphs()
    {
        OnPropertyChanged(nameof(WhenSortGlyph));
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(SourceSortGlyph));
        OnPropertyChanged(nameof(VersionSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(ResultSortGlyph));
    }

    partial void OnSearchQueryChanged(string value) => RefreshView();

    partial void OnShowOnlyFailedChanged(bool value) => RefreshView();

    partial void OnTimeRangeChanged(HistoryTimeRange value)
    {
        OnPropertyChanged(nameof(IsRangeAll));
        OnPropertyChanged(nameof(IsRangeToday));
        OnPropertyChanged(nameof(IsRangeWeek));
        OnPropertyChanged(nameof(IsRangeMonth));
        RefreshView();
    }

    /// <summary>The pressed source buttons, gathered once per refresh instead of once per row: the filter runs
    /// for every row of a history that can be megabytes long, on every keystroke.</summary>
    private HashSet<string> _activeSourceIds = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshView()
    {
        _activeSourceIds = SourceFilters.Where(f => f.IsActive).Select(f => f.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EntriesView.Refresh();
        OnPropertyChanged(nameof(NoMatches));
    }

    // Copy the currently filtered/sorted rows as tab-separated text, the same shape a paste
    // into a spreadsheet expects. Plain text only, no file dialog and no file format.

    [ObservableProperty]
    private string copiedMessage = string.Empty;

    public bool HasCopiedMessage => !string.IsNullOrEmpty(CopiedMessage);

    partial void OnCopiedMessageChanged(string value) => OnPropertyChanged(nameof(HasCopiedMessage));

    [RelayCommand]
    private void CopyList()
    {
        var rows = EntriesView.Cast<HistoryRowViewModel>().ToList();
        try
        {
            System.Windows.Clipboard.SetText(BuildClipboardText(rows));
            CopiedMessage = string.Format(Loc["HistoryCopied"], rows.Count);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard can be held open by another process; a failed copy is not worth surfacing
            // (same reasoning as ErrorDetailViewModel.Copy)
        }
    }

    /// <summary>
    /// Pure and testable on its own: a header line (name, from, to, source, date, result) followed by
    /// one line per row, in whatever order <paramref name="rows"/> is already filtered/sorted into.
    /// An empty <paramref name="rows"/> yields only the header, never an empty string. Each row reads
    /// its cells from the SAME formatted properties the table itself shows, so a copy can never show a
    /// number or date in a different shape (or a different culture) than the window the user just looked at.
    /// </summary>
    internal static string BuildClipboardText(IEnumerable<HistoryRowViewModel> rows)
    {
        var loc = LocalizationService.Instance;
        var lines = new List<string>
        {
            string.Join('\t',
                loc["ColumnName"], loc["HistoryCopyColumnFrom"], loc["HistoryCopyColumnTo"],
                loc["ColumnSource"], loc["HistoryColumnWhen"], loc["HistoryColumnResult"]),
        };

        lines.AddRange(rows.Select(row => string.Join('\t',
            row.Name, row.FromVersionText, row.ToVersionText, row.SourceDisplay, row.When, row.Result)));

        return string.Join("\r\n", lines);
    }
}
