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

/// <summary>
/// One collapsible group of the uninstaller table. Hidden while it holds no row (after a search, or while its source
/// has nothing).
/// </summary>
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

    /// <summary>Whether the section's header shows at all.</summary>
    public bool IsVisible => HasRows;

    /// <summary>The title with the row count.</summary>
    public string Header => $"{Title} ({Rows.Count})";

    [ObservableProperty]
    private bool isExpanded;

    /// <summary>
    /// Brings the shown rows to the given list by removing, moving and inserting only what differs, so the rows that stay
    /// keep their place (and the table its scroll position) while a fresh read is merged in.
    /// </summary>
    internal void Replace(IEnumerable<UninstallRowViewModel> rows)
    {
        var target = rows.ToList();
        var keep = new HashSet<UninstallRowViewModel>(target, ReferenceEqualityComparer.Instance);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
                Rows.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], target[i]))
                continue;

            var at = Rows.IndexOf(target[i]);
            if (at > i)
                Rows.Move(at, i);
            else
                Rows.Insert(i, target[i]);
        }

        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Header));
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Header));
    }
}
