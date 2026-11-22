using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Main;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>
/// Backs the uninstaller window: the table of everything installed with a search box, sortable columns and a
/// checkbox per row, the selection rules (<see cref="UninstallerViewModel"/>.Selection) and the run
/// (<c>.Run</c>). Like the cleanup window's, it outlives its window: closing the window mid-run keeps the run going
/// and reopening picks it back up.
/// </summary>
public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly IUninstallerBackend _backend;
    private readonly LoggingService _logging;
    private List<UninstallRowViewModel> _all = [];
    private CancellationTokenSource? _loadCts;

    public UninstallerViewModel(IUninstallerBackend backend, LoggingService logging)
    {
        _backend = backend;
        _logging = logging;
        Sections =
        [
            new(UninstallSection.Programs, "UninstallerSectionPrograms", expanded: true),
            new(UninstallSection.Packages, "UninstallerSectionPackages", expanded: false),
            new(UninstallSection.Drivers, "UninstallerSectionDrivers", expanded: false),
            new(UninstallSection.Updates, "UninstallerSectionUpdates", expanded: false),
        ];
    }

    public LocalizationService Loc => LocalizationService.Instance;

    public ObservableCollection<UninstallSectionViewModel> Sections { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmptyText))]
    private bool isLoading;

    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>The column the table is sorted by: name, publisher, version, size, installed, risk or method.</summary>
    [ObservableProperty]
    private string sortColumn = "name";

    [ObservableProperty]
    private bool sortDescending;

    /// <summary>Every row that exists, searched or not.</summary>
    public IReadOnlyList<UninstallRowViewModel> AllRows => _all;

    /// <summary>The rows the search lets through, across all sections.</summary>
    public int VisibleCount => Sections.Sum(section => section.Rows.Count);

    public bool ShowsEmptyText => !IsLoading && VisibleCount == 0;

    public string EmptyText => Loc[_all.Count == 0 ? "UninstallerEmpty" : "UninstallerNoMatch"];

    partial void OnSearchTextChanged(string value) => Refresh();

    /// <summary>Reads the machine's installed entries on a background thread and fills the table. A second call replaces the first.</summary>
    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var listings = await _backend.ListAsync(cts.Token);
            if (cts.IsCancellationRequested)
                return;

            SetListings(listings);
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer load or the window went away
        }
        catch (Exception ex)
        {
            _logging.Warn($"Uninstaller list failed: {ex.Message}");
            SetListings([]);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    [RelayCommand]
    private Task Reload() => IsBusy ? Task.CompletedTask : LoadAsync();

    /// <summary>Replaces the table's content. Ticks do not survive a reload: the rows are new.</summary>
    internal void SetListings(IReadOnlyList<UninstallListing> listings)
    {
        _all = listings.Select(listing => new UninstallRowViewModel(listing, OnRowSelectionChanged)).ToList();
        Refresh();
        UpdateSelectionState();
    }

    internal static UninstallSection SectionOf(UninstallRowViewModel row) => row.Entry.Kind switch
    {
        UninstallEntryKind.Package => UninstallSection.Packages,
        UninstallEntryKind.Driver => UninstallSection.Drivers,
        UninstallEntryKind.WindowsUpdate => UninstallSection.Updates,
        _ => UninstallSection.Programs,
    };

    /// <summary>Applies the search and the sort to every section.</summary>
    internal void Refresh()
    {
        var query = SearchText.Trim();
        var visible = _all.Where(row => Matches(row, query)).ToList();

        foreach (var section in Sections)
            section.Replace(Sort(visible.Where(row => SectionOf(row) == section.Kind)));

        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ShowsEmptyText));
        OnPropertyChanged(nameof(EmptyText));
    }

    private static bool Matches(UninstallRowViewModel row, string query) =>
        UpdateFilter.MatchesQuery(query, row.Name, row.Entry.WingetId ?? string.Empty, null, row.Publisher);

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
            SortDescending = column is "size" or "installed" or "risk";   // biggest, newest, riskiest first; text A to Z
        }

        Refresh();
    }

    /// <summary>
    /// The rows in the chosen order. A missing value (no size, no date, no publisher) sorts last in both
    /// directions, so a click never buries the rows that have a value under a block of blanks. Ties fall back to the name.
    /// </summary>
    internal IEnumerable<UninstallRowViewModel> Sort(IEnumerable<UninstallRowViewModel> rows)
    {
        var list = rows.ToList();
        var present = list.Where(row => HasValue(row, SortColumn)).ToList();
        var missing = list.Where(row => !HasValue(row, SortColumn)).OrderBy(row => row.SortName, StringComparer.CurrentCultureIgnoreCase);

        IOrderedEnumerable<UninstallRowViewModel> ordered = SortDescending
            ? present.OrderByDescending(row => Key(row, SortColumn), Comparer<IComparable>.Create(Compare))
            : present.OrderBy(row => Key(row, SortColumn), Comparer<IComparable>.Create(Compare));

        return ordered.ThenBy(row => row.SortName, StringComparer.CurrentCultureIgnoreCase).Concat(missing);
    }

    private static int Compare(IComparable left, IComparable right) =>
        left is string a && right is string b
            ? string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase)
            : left.CompareTo(right);

    private static bool HasValue(UninstallRowViewModel row, string column) => column switch
    {
        "publisher" => !string.IsNullOrWhiteSpace(row.Publisher),
        "version" => !string.IsNullOrWhiteSpace(row.Version),
        "size" => row.SizeBytes is > 0,
        "installed" => row.InstallDate is not null,
        _ => true,
    };

    private static IComparable Key(UninstallRowViewModel row, string column) => column switch
    {
        "publisher" => row.Publisher,
        "version" => VersionSortKey.For(row.Version),
        "size" => row.SizeBytes ?? 0L,
        "installed" => row.InstallDate ?? DateOnly.MinValue,
        "risk" => (int)row.RiskLevel,
        "method" => row.MethodText,
        _ => row.SortName,
    };

    /// <summary>Re-reads every text after a language change.</summary>
    public void RefreshText()
    {
        foreach (var row in _all)
            row.RefreshText();

        foreach (var section in Sections)
            section.RefreshText();

        OnPropertyChanged(nameof(EmptyText));
        RefreshLeftoverText();
    }

    /// <summary>Cancels a list read in flight; the window closed for good.</summary>
    public void Discard() => _loadCts?.Cancel();
}
