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

    internal LoggingService Logging => _logging;

    public ObservableCollection<UninstallSectionViewModel> Sections { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmptyText), nameof(ShowsLoadingText))]
    private bool isLoading;

    /// <summary>When the rows on show were read, while they come from the saved listing and the real read is still running; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewNote), nameof(HasPreviewNote), nameof(ShowsLoadingText))]
    private DateTimeOffset? previewSavedAt;

    /// <summary>"As of ..., updating": the header note over rows taken from the saved listing.</summary>
    public string PreviewNote => PreviewSavedAt is { } at
        ? string.Format(Loc["UninstallerPreviewNote"], at.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture))
        : string.Empty;

    public bool HasPreviewNote => PreviewSavedAt is not null;

    /// <summary>The plain "Loading" line: while a read runs and no saved rows are on show instead.</summary>
    public bool ShowsLoadingText => IsLoading && !HasPreviewNote;

    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>The column the table is sorted by: name, publisher, version, size, installed or risk.</summary>
    [ObservableProperty]
    private string sortColumn = "name";

    [ObservableProperty]
    private bool sortDescending;

    /// <summary>Every row that exists, searched or not.</summary>
    public IReadOnlyList<UninstallRowViewModel> AllRows => _all;

    /// <summary>The rows the search lets through, across all sections.</summary>
    public int VisibleCount => Sections.Sum(section => section.Rows.Count);

    public bool ShowsEmptyText => !IsLoading && VisibleCount == 0;

    /// <summary>True when the last list read threw: the empty table then says so instead of claiming the machine has nothing installed (the toolbar's reload button and F5 try again).</summary>
    public bool LoadFailed => _loadFailed;

    private bool _loadFailed;

    public string EmptyText => Loc[_all.Count > 0 ? "UninstallerNoMatch" : _loadFailed ? "UninstallerLoadFailed" : "UninstallerEmpty"];

    /// <summary>The short explanation under the empty line: what would fill the table, or how to get the search results back; none after a failed read (that line already says to reload).</summary>
    public string EmptyHint => _all.Count > 0 ? Loc["UninstallerNoMatchHint"] : _loadFailed ? string.Empty : Loc["UninstallerEmptyHint"];

    public bool HasEmptyHint => EmptyHint.Length > 0;

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

            _loadFailed = false;
            SetListings(listings);
            await SaveCacheAsync(listings);
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer load or the window went away
        }
        catch (Exception ex)
        {
            _logging.Warn($"Uninstaller list failed: {ex.Message}");
            _loadFailed = true;
            SetListings([]);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    /// <summary>
    /// Fills the table when the window opens: the saved listing at once (greyed, not tickable) with a note, then the
    /// cheap recheck that drops what is certainly gone and lets the rest be ticked, then the full read merged into the
    /// same rows. Without a saved listing it is a plain <see cref="LoadAsync"/>.
    /// </summary>
    public async Task OpenAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            if (_all.Count == 0 && await _backend.LoadCacheAsync(cts.Token) is { Listings.Count: > 0 } cached)
            {
                if (cts.IsCancellationRequested)
                    return;

                ShowPreview(cached);
                var kept = await _backend.RecheckAsync(cached.Listings, cts.Token);
                if (cts.IsCancellationRequested)
                    return;

                ConfirmPreview(kept);
            }

            var listings = await _backend.ListForOpenAsync(cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _loadFailed = false;
            if (_all.Count == 0 && PreviewSavedAt is null)
                SetListings(listings);
            else
                MergeListings(listings);

            PreviewSavedAt = null;
            await SaveCacheAsync(listings);
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer load or the window went away
        }
        catch (Exception ex)
        {
            _logging.Warn($"Uninstaller list failed: {ex.Message}");

            // Saved rows that passed the recheck stay usable; with nothing on show the table says the read failed.
            if (_all.Count == 0)
            {
                _loadFailed = true;
                SetListings([]);
            }

            PreviewSavedAt = null;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                PreviewSavedAt = null;
            }
        }
    }

    /// <summary>Shows the saved rows, greyed until the recheck confirms them.</summary>
    private void ShowPreview(UninstallCachedListing cached)
    {
        _all = cached.Listings.Select(listing => new UninstallRowViewModel(listing, OnRowSelectionChanged).AsUnverified()).ToList();
        PreviewSavedAt = cached.SavedAt;
        _logging.Info($"uninstaller preview: {_all.Count} rows from cache");
        Refresh();
        UpdateSelectionState();
        ResolveIcons();
    }

    /// <summary>The recheck's verdict: rows it no longer lists vanish, the others become selectable.</summary>
    private void ConfirmPreview(IReadOnlyList<UninstallListing> kept)
    {
        var keptKeys = kept.Select(listing => listing.Entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _all = _all.Where(row => keptKeys.Contains(row.Entry.Key)).ToList();
        foreach (var row in _all)
            row.MarkVerified();

        Refresh();
        UpdateSelectionState();
    }

    /// <summary>
    /// Brings the table to a fresh full read without replacing it: a row whose key is still there keeps its object
    /// (tick, place in the list) and takes the new data, a new key adds a row, a key that vanished removes its row.
    /// Skipped while a run or a leftover step is working, whose rows (including the removed ones the summary counts)
    /// must stay as they are.
    /// </summary>
    internal void MergeListings(IReadOnlyList<UninstallListing> listings)
    {
        if (IsBusy)
        {
            _logging.Info("uninstaller: fresh read not merged, a run is in progress");
            return;
        }

        var existing = new Dictionary<string, UninstallRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _all)
            existing.TryAdd(row.Entry.Key, row);

        var used = new HashSet<UninstallRowViewModel>(ReferenceEqualityComparer.Instance);
        var merged = new List<UninstallRowViewModel>(listings.Count);
        foreach (var listing in listings)
        {
            if (existing.TryGetValue(listing.Entry.Key, out var row) && used.Add(row))
            {
                row.Update(listing);
                row.MarkVerified();
                merged.Add(row);
            }
            else
            {
                merged.Add(new UninstallRowViewModel(listing, OnRowSelectionChanged));
            }
        }

        _all = merged;
        Refresh();
        UpdateSelectionState();
        ResolveIcons();
    }

    /// <summary>Saves the entries as the listing of the last full read; a failure is the backend's to log, never ours to show.</summary>
    private async Task SaveCacheAsync(IEnumerable<UninstallListing> listings)
    {
        try
        {
            await _backend.SaveCacheAsync([.. listings.Select(listing => listing.Entry)]);
        }
        catch (Exception ex)
        {
            _logging.Warn($"Uninstaller: saving the listing failed: {ex.Message}");
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
        ResolveIcons();
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
        MarkSharedNames(visible);

        foreach (var section in Sections)
            section.Replace(Sort(visible.Where(row => SectionOf(row) == section.Kind)));

        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ShowsEmptyText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(HasEmptyHint));
    }

    /// <summary>Tells every row whether another visible row has its name, so look-alike entries always show where they come from.</summary>
    private void MarkSharedNames(List<UninstallRowViewModel> visible)
    {
        var shared = visible.GroupBy(row => row.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();

        foreach (var row in _all)
            row.SetSharesNameWithVisibleRow(shared.Contains(row));
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
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(HasEmptyHint));
        OnPropertyChanged(nameof(PreviewNote));
        RefreshLeftoverText();
    }

    /// <summary>Cancels a list read in flight; the window closed for good.</summary>
    public void Discard()
    {
        _loadCts?.Cancel();
    }
}
