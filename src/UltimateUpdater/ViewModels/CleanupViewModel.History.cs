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
    /// <summary>Date and amount of the most recent past run; only during selection, the same
    /// convention <see cref="HasFreeSpaceNow"/> uses, since a run in progress or just finished already
    /// has its own dedicated content on this side of the ring.</summary>
    public bool HasLastCleanup => IsChoosing && History.Count > 0;

    public string LastCleanupText => History.Count == 0
        ? string.Empty
        : $"{History[0].WhenText} · {History[0].FreedText}";

    /// <summary>
    /// Writes what this run did into the history and puts it at the top of the list. A cleanup cannot be
    /// undone, so the record of what it removed is the only way to answer "what happened to that folder"
    /// afterwards. Never allowed to throw: the run has already happened.
    /// </summary>
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
                Areas = ran.Select(i => new CleanupHistoryArea
                {
                    Id = i.Target.Id,
                    TitleKey = i.Target.TitleKey,
                    TitleArguments = [.. i.Target.TitleArguments],
                    FreedBytes = i.FreedBytes,
                    SkippedBytes = i.SkippedBytes,
                    ProtectedBytes = i.ProtectedBytes,
                    RepairResult = i.RepairResult,
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
            // History is a convenience record, not the source of truth for what the run actually did; a
            // write failure here must not make an otherwise-successful cleanup look like it errored.
            _logging.Warn($"The cleanup could not be recorded in the history: {ex.Message}");
        }
    }

    /// <summary>Earlier runs, newest first. Each row expands to what every single area gave up.</summary>
    public ObservableCollection<CleanupHistoryRowViewModel> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    /// <summary>Unfolds the newest run and folds every other one. The rows are shared with the cleanup
    /// window's own history window, so a row the user unfolded earlier would otherwise stay open.</summary>
    public void ExpandNewestHistoryRow()
    {
        for (var i = 0; i < History.Count; i++)
            History[i].IsExpanded = i == 0;
    }

    /// <summary>The inverse, for <see cref="UltimateUpdater.Views.CleanupHistoryWindow"/>'s empty state; a plain bool
    /// property rather than an inverting converter, the same shape HistoryViewModel.IsEmpty already
    /// uses for the same purpose.</summary>
    public bool HasNoHistory => !HasHistory;

    public string HistoryEmptyText => LocalizationService.Instance["CleanupHistoryEmpty"];

    /// <summary>The overview card above the history list: everything freed across every run still on
    /// record. Computed fresh from the rows already held, no separate rollup to keep in sync.</summary>
    public string HistoryTotalFreedText => string.Format(
        LocalizationService.Instance["CleanupHistoryTotalFreed"], ByteSize.Format(History.Sum(h => h.FreedBytes)));

    /// <summary>The label and value lines under the total: the last system file and image check, run count,
    /// average per run, the last 30 days and the system drive's free space (read live each time).</summary>
    public IReadOnlyList<CleanupHistoryStatViewModel> HistoryStats => CleanupHistoryStatViewModel.Build(
        History.Select(h => h.Entry).ToList(), DateTimeOffset.Now, CleanupHistoryStatViewModel.ReadSystemDrive());

    /// <summary>Whether any run actually freed anything; a history of runs that all found nothing has
    /// no categories to rank, so the card says that plainly instead of showing an empty list.</summary>
    public bool HasFreedAnything => History.Sum(h => h.FreedBytes) > 0;

    /// <summary>The inverse, for the empty-overview sentence; same plain-bool-property convention
    /// <see cref="HasNoHistory"/> already uses instead of an inverting converter.</summary>
    public bool HasNotFreedAnything => !HasFreedAnything;

    public string HistoryEmptyOverviewText => LocalizationService.Instance["CleanupHistoryEmptyOverview"];

    /// <summary>The pie and its legend: areas summed across every run on record, the six biggest as slices
    /// of their own and the rest as one "other" slice (see <see cref="CleanupHistoryOverview"/>).</summary>
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

    /// <summary>One row's own delete button; the file on disk is rewritten right away, same as
    /// every other history write, so a crash right after does not resurrect it.</summary>
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

    /// <summary>"Alle löschen"; the confirmation lives in CleanupHistoryWindow's code-behind
    /// (same split as OnClean/RunCleanupAsync: a Window owns the dialog, the view model owns the action).</summary>
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
