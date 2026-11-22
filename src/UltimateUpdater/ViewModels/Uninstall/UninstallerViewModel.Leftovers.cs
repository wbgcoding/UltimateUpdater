using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>
/// After a run: what the removed programs left behind (folders, registry keys, shortcuts), listed per program with a
/// checkbox each (only a certain match starts ticked), and the one-line summary of the whole run.
/// </summary>
public sealed partial class UninstallerViewModel
{
    /// <summary>The task of the current leftover scan or removal, so a test (or a closing app) can await it.</summary>
    public Task? LeftoverTask { get; private set; }

    public ObservableCollection<UninstallLeftoverGroupViewModel> LeftoverGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLeftovers))]
    private bool isScanningLeftovers;

    [ObservableProperty]
    private bool isRemovingLeftovers;

    /// <summary>How many leftovers this run's removals took away; part of the summary.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int leftoversRemoved;

    /// <summary>True once this run's leftover removal moved something into the quarantine: the line that says so is shown.</summary>
    [ObservableProperty]
    private bool leftoversRestorable;

    public string RestorableText => Loc["UninstallerLeftoversRestorable"];

    /// <summary>True from the end of a run on: the summary line is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary), nameof(SummaryText))]
    private bool runFinished;

    /// <summary>Anything is going on that the window must not be rebuilt over: the removals, or the leftover scan or removal after them.</summary>
    public bool IsBusy => IsRunning || IsScanningLeftovers || IsRemovingLeftovers;

    public bool HasLeftovers => LeftoverGroups.Count > 0;

    public bool ShowsLeftovers => HasLeftovers || IsScanningLeftovers;

    public bool HasSummary => RunFinished;

    public int SelectedLeftoverCount => LeftoverGroups.Sum(group => group.Items.Count(item => item.IsSelected));

    public bool CanRemoveLeftovers => SelectedLeftoverCount > 0 && !IsBusy;

    /// <summary>"3 removed, 1 failed, 2 leftovers removed": entries gone (a pending restart counts as gone), entries that failed, leftovers taken away since.</summary>
    public string SummaryText => string.Format(
        Loc["UninstallerSummary"],
        _all.Count(row => row.State is UninstallRowState.Removed or UninstallRowState.NeedsRestart),
        _all.Count(row => row.State == UninstallRowState.Failed),
        LeftoversRemoved);

    private void OnLeftoverSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedLeftoverCount));
        OnPropertyChanged(nameof(CanRemoveLeftovers));
        RemoveLeftoversCommand.NotifyCanExecuteChanged();
    }

    internal void UpdateLeftoverState()
    {
        OnPropertyChanged(nameof(HasLeftovers));
        OnPropertyChanged(nameof(ShowsLeftovers));
        OnLeftoverSelectionChanged();
    }

    private void ClearLeftovers()
    {
        LeftoverGroups.Clear();
        LeftoversRemoved = 0;
        LeftoversRestorable = false;
        RunFinished = false;
        UpdateLeftoverState();
    }

    /// <summary>
    /// Fills the list from a scan. <paramref name="unticked"/> names the items the user had switched off before a rescan,
    /// which stay off; every other item starts ticked only when the scan was certain of it.
    /// </summary>
    private void SetLeftovers(IReadOnlyList<UninstallLeftoverGroup> groups, ISet<string>? unticked = null)
    {
        LeftoverGroups.Clear();
        foreach (var group in groups)
        {
            var items = group.Items.Select(item =>
            {
                var key = $"{item.Kind}|{item.Path}";
                return new UninstallLeftoverItemViewModel(item, item.Preselected && unticked?.Contains(key) != true, OnLeftoverSelectionChanged);
            });
            LeftoverGroups.Add(new UninstallLeftoverGroupViewModel(group.Entry, items));
        }

        UpdateLeftoverState();
    }

    /// <summary>Looks for what the removed entries left behind. A failed scan shows no leftovers and says nothing: the run itself already succeeded.</summary>
    private async Task ScanLeftoversAsync(IReadOnlyList<UninstallRowViewModel> removedRows)
    {
        if (removedRows.Count == 0)
            return;

        IsScanningLeftovers = true;
        UpdateLeftoverState();
        try
        {
            var groups = await _backend.FindLeftoversAsync([.. removedRows.Select(row => row.Entry)], CancellationToken.None);
            SetLeftovers(groups);
        }
        catch (Exception ex)
        {
            _logging.Warn($"Leftover scan failed: {ex.Message}");
            SetLeftovers([]);
        }
        finally
        {
            IsScanningLeftovers = false;
            UpdateLeftoverState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveLeftovers))]
    private Task RemoveLeftovers()
    {
        if (!CanRemoveLeftovers)
            return Task.CompletedTask;

        return LeftoverTask = RemoveLeftoversAsync();
    }

    private async Task RemoveLeftoversAsync()
    {
        var picks = LeftoverGroups
            .Select(group => new UninstallLeftoverGroup(
                group.Entry, [.. group.Items.Where(item => item.IsSelected).Select(item => item.Item)]))
            .Where(group => group.Items.Count > 0)
            .ToList();
        var unticked = LeftoverGroups.SelectMany(group => group.Items).Where(item => !item.IsSelected).Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = LeftoverGroups.Select(group => group.Entry).ToList();

        IsRemovingLeftovers = true;
        OnLeftoverSelectionChanged();
        try
        {
            var result = await _backend.RemoveLeftoversAsync(picks, CancellationToken.None);
            LeftoversRemoved += result.Removed;
            if (result.QuarantinedOrEmpty.Count > 0)
                LeftoversRestorable = true;
            if (result.Skipped > 0)
                _logging.Warn($"Uninstaller: {result.Skipped} leftover(s) could not be removed and stay listed.");

            // Whatever is still there (kept, failed, unticked) is shown again; what is gone drops out.
            var rest = await _backend.FindLeftoversAsync(entries, CancellationToken.None);
            SetLeftovers(rest, unticked);
        }
        catch (Exception ex)
        {
            _logging.Warn($"Leftover removal failed: {ex.Message}");
        }
        finally
        {
            IsRemovingLeftovers = false;
            OnLeftoverSelectionChanged();
        }
    }

    private void RefreshLeftoverText()
    {
        foreach (var item in LeftoverGroups.SelectMany(group => group.Items))
            item.RefreshText();

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(RestorableText));
    }
}
