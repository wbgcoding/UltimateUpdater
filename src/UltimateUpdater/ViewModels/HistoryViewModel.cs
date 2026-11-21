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
    HistoryEntry entry, bool canDowngrade = false, Action<HistoryEntry>? onDowngrade = null,
    int unchangedCount = 0)
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

    /// <summary>Right side of the version lane's fixed 3-column grid, mirroring the main table, so the arrow sits at the same x whatever the version lengths. A missing value is a hyphen, never blank.</summary>
    public string FromVersionText => entry.FromVersion ?? "-";

    public string ToVersionText => entry.ToVersion ?? "-";

    /// <summary>False centres <see cref="ToVersionText"/> across the whole lane, as the main table does for a row with no installed version.</summary>
    public bool HasFromVersion => entry.FromVersion is not null;

    /// <summary>One plain-text line for the failed-row error detail window; null when the entry carries no version info at all.</summary>
    public string? VersionChangeText => entry.FromVersion is null && entry.ToVersion is null
        ? null
        : $"{FromVersionText} → {ToVersionText}";

    /// <summary>The size column's sort key: the real byte count, so sorting is numeric rather than on the formatted text.</summary>
    public long SortSize => entry.SizeBytes ?? 0;

    public bool HasSize => entry.SizeBytes is not null;

    public string SortVersion => VersionSortKey.For(entry.ToVersion);

    public bool HasToVersion => !string.IsNullOrWhiteSpace(entry.ToVersion);

    /// <summary>Same helper and culture as the main table's size column; a missing value is a hyphen.</summary>
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

    /// <summary>The Result cell's hover text: the failure text, or for an install that left the version unchanged, why and how often this package already did the same.</summary>
    public string? ResultTooltip => entry is { Action: "install", Success: true, VersionUnchanged: true }
        ? unchangedCount >= 2
            ? LocalizationService.Instance["InstallVersionUnchangedDetail"] + "\n"
                + string.Format(LocalizationService.Instance["InstallVersionUnchangedRepeat"], unchangedCount)
            : LocalizationService.Instance["InstallVersionUnchangedDetail"]
        : entry.Error;

    /// <summary>The failing tool's captured output when the entry was written with one; null otherwise, which falls back to a log excerpt (HistoryWindow.xaml.cs).</summary>
    public string? Detail => entry.Detail;

    public DateTimeOffset Timestamp => entry.Timestamp;

    /// <summary>A handoff, a package the manager has no fitting version for, and a row the user stopped are not failures: they do not count toward "show only failed" or paint in the failure colour.</summary>
    public bool Failed => !entry.Success && !entry.IsManualHandoff && !entry.IsNotApplicable && !entry.IsCancelled;

    /// <summary>A real success paints in the success colour; a handoff, a not-applicable outcome and a stopped row never count.</summary>
    public bool Succeeded => entry.Success && !IsManualHandoff;

    /// <summary>All terminal non-failures share the row's caution styling.</summary>
    public bool IsManualHandoff => entry.IsManualHandoff || entry.IsNotApplicable || entry.IsCancelled;

    /// <summary>A tooltip cannot show past a log file's first line, so failed rows get a button that opens the full text in the same ErrorDetailWindow the main table uses.</summary>
    public string ShowDetailsTooltip => LocalizationService.Instance["HistoryShowDetails"];

    /// <summary>The same rule as the live row's button, computed once by <see cref="HistoryViewModel"/> when the window opens (the source can pin a version, and this is the newest successful install on record for the package) and handed in.</summary>
    public bool CanDowngrade => canDowngrade;

    public string DowngradeHeader =>
        canDowngrade ? string.Format(LocalizationService.Instance["RowDowngrade"], entry.FromVersion) : string.Empty;

    [RelayCommand(CanExecute = nameof(CanDowngrade))]
    private void Downgrade() => onDowngrade?.Invoke(entry);

    /// <summary>The result word is read from the resources in code, so a language change has to re-raise it.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(When));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(ResultTooltip));
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
    /// <summary>Optional: null means the column chooser state is never persisted, everything else works the same.</summary>
    private readonly SettingsService? _settings;

    public LocalizationService Loc => LocalizationService.Instance;

    public ObservableCollection<HistoryRowViewModel> Entries { get; }

    /// <summary>Search, failed-only, per-source and time-range filters over the history.</summary>
    public ICollectionView EntriesView { get; }

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private bool showOnlyFailed;

    /// <summary>One toggle button per source that appears in the history, so the row stays short and never offers a filter matching nothing. None active means all sources.</summary>
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

    /// <summary>Reads and parses the history file on the calling thread. Production code uses <see cref="CreateAsync"/>; this is for callers already off the UI thread, and tests.</summary>
    internal HistoryViewModel(
        HistoryService history,
        IReadOnlySet<string>? pinningCapableSourceIds = null,
        Action<HistoryEntry>? onDowngrade = null,
        SettingsService? settings = null)
        : this(history.Read(), pinningCapableSourceIds, onDowngrade, settings)
    {
    }

    /// <summary>Reads the history file on a pool thread and builds the view model on the caller's thread, so opening the window never parses up to 5 MB of JSON on the dispatcher.</summary>
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

        // The newest successful install per package produced the currently installed version, so it is the only one a rollback is offered from; Read() is newest first, so the first match per key is that entry.
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

        // For each "installed, version unchanged" entry: its place in the run of identical results for the same package and versions. Read() is newest first, so walk it from the oldest end.
        var unchangedCounts = new int[read.Count];
        var unchangedSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = read.Count - 1; i >= 0; i--)
        {
            var e = read[i];
            if (e is not { Action: "install", Success: true, VersionUnchanged: true })
                continue;

            var key = $"{e.SourceId}:{e.ItemId}:{e.FromVersion}->{e.ToVersion}";
            unchangedSeen[key] = unchangedCounts[i] = unchangedSeen.GetValueOrDefault(key) + 1;
        }

        Entries = new ObservableCollection<HistoryRowViewModel>(
            read.Select((e, i) => new HistoryRowViewModel(e, canDowngradeFlags[i], onDowngrade, unchangedCounts[i])));

        SourceFilters = Entries
            .Select(row => row.SourceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => SourceNames.Display(id), StringComparer.CurrentCultureIgnoreCase)
            .Select(id => new HistorySourceFilter(id, RefreshView))
            .ToList();
        _activeSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // the new buttons start unpressed

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = Matches;

        // Newest first by default.
        ApplySort();

        // The remembered column chooser state and sort, after the default above.
        LoadTableLayout();
    }

    /// <summary>The history table's column list for the shared chooser (<see cref="Views.ColumnChooser"/>). The details button is structural and never offered; Name may not be hidden.</summary>
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

    /// <summary>The same DynamicResource write as the main table's column widths (<see cref="MainViewModel.MainColumnResourceKeys"/>): one write collapses or restores a column in both the header and the row template.</summary>
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
        {
            // The widths live in app-wide resources that outlive this view model: with no saved state a column hidden earlier would stay at width 0.
            ApplyColumnWidths();
            return;
        }

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

    /// <summary>Written on every change and when the window closes; a no-op without a SettingsService.</summary>
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

    // Sorting: a header click sorts by that column, a second click flips the direction.

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
            "version" => nameof(HistoryRowViewModel.SortVersion),
            "size" => nameof(HistoryRowViewModel.SortSize),
            "result" => nameof(HistoryRowViewModel.Result),
            _ => nameof(HistoryRowViewModel.SortWhen),
        };
        var hasValue = SortColumn switch
        {
            "version" => nameof(HistoryRowViewModel.HasToVersion),
            "size" => nameof(HistoryRowViewModel.HasSize),
            _ => null,
        };

        EntriesView.SortDescriptions.Clear();
        if (hasValue is not null)
            EntriesView.SortDescriptions.Add(new SortDescription(hasValue, ListSortDirection.Descending));
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

    /// <summary>The language changed while this window was open. The rows read their result word in code, so nothing else re-reads it.</summary>
    public void RefreshLocalizedText()
    {
        foreach (var entry in Entries)
            entry.RefreshLocalizedText();

        foreach (var filter in SourceFilters)
            filter.RefreshLocalizedText();

        // Source and result sort by their translated text, so the order may differ in the new language.
        ApplySort();
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

    /// <summary>The pressed source buttons, gathered once per refresh: the filter runs for every row of a history that can be megabytes long, on every keystroke.</summary>
    private HashSet<string> _activeSourceIds = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshView()
    {
        _activeSourceIds = SourceFilters.Where(f => f.IsActive).Select(f => f.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EntriesView.Refresh();
        OnPropertyChanged(nameof(NoMatches));
    }

    // Copies the filtered and sorted rows as tab-separated text, the shape a spreadsheet paste expects.

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
            CopiedMessage = string.Format(Loc[rows.Count == 1 ? "HistoryCopiedOne" : "HistoryCopied"], rows.Count);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard can be held open by another process; a failed copy is not worth surfacing.
        }
    }

    /// <summary>Pure and testable on its own: a header line followed by one line per row in the order given; an empty list yields only the header. Cells come from the same formatted properties the table shows, so a copy matches the window in shape and culture.</summary>
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
