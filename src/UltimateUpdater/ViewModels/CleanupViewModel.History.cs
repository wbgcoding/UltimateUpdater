using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// The record of past runs: writing one at the end of a run, and the rows the history list shows.
public partial class CleanupViewModel
{
    /// <summary>Date and amount of the most recent past run; only during selection, like <see cref="HasFreeSpaceNow"/>.</summary>
    public bool HasLastCleanup => IsChoosing && History.Count > 0;

    public string LastCleanupText => History.Count == 0
        ? string.Empty
        : $"{History[0].WhenText} · {History[0].FreedText}";

    /// <summary>Writes what this run did into the history and puts it at the top. A cleanup cannot be undone, so this record is the only answer to "what happened to that folder". Never throws: the run has already happened.</summary>
    private void RecordRun(IReadOnlyList<CleanupItemViewModel> ran, bool stopped)
    {
        try
        {
            var entry = new CleanupHistoryEntry
            {
                Timestamp = DateTimeOffset.Now,
                FreedBytes = FreedBytes,
                SkippedFiles = SkippedFiles,
                SkippedBytes = SkippedBytes,
                Stopped = stopped,
                // An area the run never put on its list (folded into another one) is not part of the run.
                Areas = ran.Where(i => i.RunState != CleanupTargetState.Idle).Select(i => new CleanupHistoryArea
                {
                    Id = i.Target.Id,
                    TitleKey = i.Target.TitleKey,
                    TitleArguments = [.. i.Target.TitleArguments],
                    FreedBytes = i.FreedBytes,
                    SkippedBytes = i.SkippedBytes,
                    ProtectedBytes = i.ProtectedBytes,
                    RepairResult = i.RepairResult,
                    NotDone = i.RunState != CleanupTargetState.Done,
                    NoteKey = i.SkipReason?.Key,
                    NoteArguments = i.SkipReason?.Argument is { } argument ? [argument] : [],
                }).ToList(),
            };

            _cleanupHistory.Append(entry);
            History.Insert(0, new CleanupHistoryRowViewModel(entry, RemoveHistoryEntry));
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HasNoHistory));
            OnPropertyChanged(nameof(LastCleanupText));
            OnPropertyChanged(nameof(HasLastCleanup));
            RaiseHistoryOverviewChanged();
        }
        catch (Exception ex)
        {
            // History is a convenience record; a write failure must not make a successful cleanup look like an error.
            _logging.Warn($"The cleanup could not be recorded in the history: {ex.Message}");
        }
    }

    /// <summary>Earlier runs, newest first. Each row expands to what every single area gave up.</summary>
    public ObservableCollection<CleanupHistoryRowViewModel> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    /// <summary>Unfolds the newest run and folds every other one; the rows are shared with the history window, so an earlier unfolded row would stay open.</summary>
    public void ExpandNewestHistoryRow()
    {
        for (var i = 0; i < History.Count; i++)
            History[i].IsExpanded = i == 0;
    }

    /// <summary>The inverse, for the history window's empty state; a plain bool property rather than an inverting converter.</summary>
    public bool HasNoHistory => !HasHistory;

    public string HistoryEmptyText => LocalizationService.Instance["CleanupHistoryEmpty"];

    /// <summary>The overview card above the history list: everything freed across every run on record, computed from the rows already held.</summary>
    public string HistoryTotalFreedText => string.Format(
        LocalizationService.Instance["CleanupHistoryTotalFreed"], ByteSize.Format(History.Sum(h => h.FreedBytes)));

    /// <summary>The label and value lines under the total: last system file and image check, run count, average per run, last 30 days and the system drive's free space (read live).</summary>
    public IReadOnlyList<CleanupHistoryStatViewModel> HistoryStats => CleanupHistoryStatViewModel.Build(
        History.Select(h => h.Entry).ToList(), DateTimeOffset.Now, CleanupHistoryStatViewModel.ReadSystemDrive());

    /// <summary>Whether any run freed anything; if none did there are no categories to rank and the card says so.</summary>
    public bool HasFreedAnything => History.Sum(h => h.FreedBytes) > 0;

    /// <summary>The inverse, for the empty-overview sentence.</summary>
    public bool HasNotFreedAnything => !HasFreedAnything;

    public string HistoryEmptyOverviewText => LocalizationService.Instance["CleanupHistoryEmptyOverview"];

    /// <summary>The pie and its legend: areas summed across every run, the six biggest as slices and the rest as one "other" slice (see <see cref="CleanupHistoryOverview"/>).</summary>
    public IReadOnlyList<CleanupHistorySliceViewModel> HistorySlices
    {
        get
        {
            var slices = CleanupHistoryOverview.BuildSlices(History.Select(h => h.Entry));
            var total = slices.Sum(s => s.Bytes);
            return slices.Select((s, i) => new CleanupHistorySliceViewModel(s, i, total)).ToList();
        }
    }

    private void RaiseHistoryOverviewChanged()
    {
        OnPropertyChanged(nameof(HistoryTotalFreedText));
        OnPropertyChanged(nameof(HistoryStats));
        OnPropertyChanged(nameof(HasFreedAnything));
        OnPropertyChanged(nameof(HasNotFreedAnything));
        OnPropertyChanged(nameof(HistorySlices));
    }

    /// <summary>One row's delete button; the file is rewritten right away, so a crash right after does not resurrect it.</summary>
    private void RemoveHistoryEntry(CleanupHistoryRowViewModel row)
    {
        _cleanupHistory.Remove(row.Timestamp);
        History.Remove(row);
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasNoHistory));
        OnPropertyChanged(nameof(LastCleanupText));
        OnPropertyChanged(nameof(HasLastCleanup));
        RaiseHistoryOverviewChanged();
    }

    /// <summary>"Delete all"; the confirmation lives in CleanupHistoryWindow's code-behind (the window owns the dialog, the view model the action).</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        _cleanupHistory.Clear();
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasNoHistory));
        OnPropertyChanged(nameof(LastCleanupText));
        OnPropertyChanged(nameof(HasLastCleanup));
        RaiseHistoryOverviewChanged();
    }
}
