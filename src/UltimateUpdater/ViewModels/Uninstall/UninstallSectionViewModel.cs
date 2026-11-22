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

/// <summary>One collapsible group of the uninstaller table. Hidden while it holds no row (after a search, or while its source has nothing).</summary>
public sealed partial class UninstallSectionViewModel : ObservableObject
{
    public UninstallSectionViewModel(UninstallSection kind, string titleKey, bool expanded)
    {
        Kind = kind;
        TitleKey = titleKey;
        isExpanded = expanded;
    }

    public UninstallSection Kind { get; }

    public string TitleKey { get; }

    public string Title => LocalizationService.Instance[TitleKey];

    /// <summary>The rows shown now: filtered and sorted by the owner.</summary>
    public ObservableCollection<UninstallRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public string Header => $"{Title} ({Rows.Count})";

    [ObservableProperty]
    private bool isExpanded;

    internal void Replace(IEnumerable<UninstallRowViewModel> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Header));
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Header));
    }
}
