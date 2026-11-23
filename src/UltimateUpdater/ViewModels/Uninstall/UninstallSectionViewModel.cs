using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>The table's groups, in display order.</summary>
public enum UninstallSection
{
    /// <summary>Classic programs and Store apps.</summary>
    Programs,

    /// <summary>Packages of the dev package managers.</summary>
    Packages,
    Drivers,
    Updates,
}

/// <summary>Where a section that is read on demand stands.</summary>
public enum UninstallSectionLoad
{
    NotLoaded,
    Loading,
    Loaded,

    /// <summary>The read worked and found nothing to list.</summary>
    Empty,

    /// <summary>The read did not work (declined elevation, DISM failed); opening the section again tries once more.</summary>
    Failed,
}

/// <summary>
/// One collapsible group of the uninstaller table. Hidden while it holds no row (after a search, or while its source
/// has nothing), except a section read on demand: it stays until it has been read, so the person can open it to
/// start the read.
/// </summary>
public sealed partial class UninstallSectionViewModel : ObservableObject
{
    private UninstallSectionLoad _load;

    public UninstallSectionViewModel(UninstallSection kind, string titleKey, bool expanded, bool lazy = false)
    {
        Kind = kind;
        Lazy = lazy;
        TitleKey = titleKey;
        isExpanded = expanded;
    }

    public UninstallSection Kind { get; }

    public string TitleKey { get; }

    /// <summary>Read only when first opened (it needs the elevated helper), not with the rest of the list.</summary>
    public bool Lazy { get; }

    public string Title => LocalizationService.Instance[TitleKey];

    /// <summary>The rows shown now: filtered and sorted by the owner.</summary>
    public ObservableCollection<UninstallRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>Whether the section's header shows at all.</summary>
    public bool IsVisible => HasRows || (Lazy && _load != UninstallSectionLoad.Loaded);

    /// <summary>The title, with the row count once the rows are known.</summary>
    public string Header => Lazy && !HasRows && _load is UninstallSectionLoad.NotLoaded or UninstallSectionLoad.Loading or UninstallSectionLoad.Failed
        ? Title
        : $"{Title} ({Rows.Count})";

    public UninstallSectionLoad Load
    {
        get => _load;
        internal set
        {
            if (_load == value)
                return;

            _load = value;
            OnPropertyChanged();
            RaiseState();
        }
    }

    /// <summary>The line under the header while there is nothing to list: reading, could not read, or none found.</summary>
    public string StatusText => !Lazy ? string.Empty : _load switch
    {
        UninstallSectionLoad.Loading => LocalizationService.Instance["UninstallerUpdatesLoading"],
        UninstallSectionLoad.Failed => LocalizationService.Instance["UninstallerUpdatesFailed"],
        UninstallSectionLoad.Empty => LocalizationService.Instance["UninstallerUpdatesNone"],
        _ => string.Empty,
    };

    public bool HasStatus => !HasRows && StatusText.Length > 0;

    [ObservableProperty]
    private bool isExpanded;

    internal void Replace(IEnumerable<UninstallRowViewModel> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);

        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(StatusText));
    }
}
