using CommunityToolkit.Mvvm.Input;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>
/// The selection rules. A row's own checkbox may tick any selectable row, a Risky one included. Every bulk way to tick
/// (select all, a Shift range, Space on several rows, select all under a search) skips Risky rows, so a Risky row is
/// only ever ticked by a deliberate click on it. A row with Selectable false (this app, an entry with no way to remove
/// it) cannot be ticked at all.
/// </summary>
public sealed partial class UninstallerViewModel
{
    private bool _bulkChange;

    /// <summary>The ticked rows, in table order, across every section.</summary>
    public IReadOnlyList<UninstallRowViewModel> SelectedRows =>
        _all.Where(row => row.IsSelected && row.Selectable).ToList();

    public int SelectedCount => _all.Count(row => row.IsSelected && row.Selectable);

    public bool HasSelection => SelectedCount > 0;

    /// <summary>Whether the uninstall button works: something is ticked and no run is going.</summary>
    public bool CanUninstall => HasSelection && !IsBusy;

    public string SelectionText => string.Format(Loc["UninstallerSelectedCount"], SelectedCount);

    private void OnRowSelectionChanged(UninstallRowViewModel row)
    {
        if (!_bulkChange)
            UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanUninstall));
        UninstallSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Applies one bulk change and notifies once.</summary>
    private void SetTicked(IEnumerable<UninstallRowViewModel> rows, bool ticked)
    {
        _bulkChange = true;
        try
        {
            foreach (var row in rows)
                row.IsSelected = ticked;
        }
        finally
        {
            _bulkChange = false;
        }

        UpdateSelectionState();
    }

    /// <summary>The rows a bulk tick may take: selectable and not Risky.</summary>
    private static List<UninstallRowViewModel> Bulk(IEnumerable<UninstallRowViewModel> rows) =>
        rows.Where(row => row.Selectable && !row.IsRisky).ToList();

    /// <summary>Ticks every shown, non-Risky, selectable row; with all of those already ticked it unticks them instead.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        if (IsRunning)
            return;

        var eligible = Bulk(Sections.SelectMany(section => section.Rows));
        SetTicked(eligible, !(eligible.Count > 0 && eligible.All(row => row.IsSelected)));
    }

    /// <summary>Unticks everything, Risky rows too.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        if (!IsRunning)
            SetTicked(_all.Where(row => row.IsSelected), false);
    }

    /// <summary>Shift+click: ticks the non-Risky, selectable rows from <paramref name="anchor"/> to <paramref name="target"/> in the order <paramref name="shown"/> lists them.</summary>
    public void SelectRange(IReadOnlyList<UninstallRowViewModel> shown, UninstallRowViewModel anchor, UninstallRowViewModel target)
    {
        if (IsRunning)
            return;

        var from = shown.ToList().IndexOf(anchor);
        var to = shown.ToList().IndexOf(target);
        if (from < 0 || to < 0)
            return;

        var (low, high) = from <= to ? (from, to) : (to, from);
        SetTicked(Bulk(shown.Skip(low).Take(high - low + 1)), true);
    }

    /// <summary>
    /// Space on the highlighted rows. One row toggles like its own checkbox (a Risky row included); several toggle
    /// together, Risky ones left alone: all of the rest already ticked unticks them, otherwise they get ticked.
    /// </summary>
    public void ToggleRows(IReadOnlyList<UninstallRowViewModel> rows)
    {
        if (IsRunning || rows.Count == 0)
            return;

        if (rows.Count == 1)
        {
            if (rows[0].Selectable)
                rows[0].IsSelected = !rows[0].IsSelected;

            return;
        }

        var eligible = Bulk(rows);
        if (eligible.Count > 0)
            SetTicked(eligible, !eligible.All(row => row.IsSelected));
    }

    /// <summary>The ticked rows that need the extra acknowledgement before a run.</summary>
    public IReadOnlyList<UninstallRowViewModel> SelectedRiskyRows => SelectedRows.Where(row => row.IsRisky).ToList();
}
