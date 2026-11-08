using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>One area inside a past run, as the expanded detail shows it.</summary>
public sealed class CleanupHistoryAreaViewModel : ObservableObject
{
    private readonly CleanupHistoryArea _area;

    public CleanupHistoryAreaViewModel(CleanupHistoryArea area)
    {
        _area = area;
    }

    /// <summary>The area's name in the CURRENT language, the record stores the key, not the text. A
    /// per-drive defragmentation area also stores its own drive display name as a formatting argument
    /// (<see cref="CleanupHistoryArea.TitleArguments"/>), the same shape <see
    /// cref="CleanupItemViewModel.Title"/> already formats live during a run, so a history row shows the
    /// drive too instead of the raw "{0}" placeholder.</summary>
    public string Title => FormatTitle(_area.TitleKey, _area.TitleArguments);

    /// <summary>The name for a key plus its formatting arguments in the current language; shared with the
    /// history pie so both read an area the same way.</summary>
    public static string FormatTitle(string titleKey, IReadOnlyList<string> titleArguments) => titleArguments.Count == 0
        ? LocalizationService.Instance[titleKey]
        : string.Format(
            CultureInfo.CurrentUICulture,
            LocalizationService.Instance[titleKey],
            titleArguments.Cast<object>().ToArray());

    public override string ToString() => Title;

    /// <summary>The raw resource key, kept alongside <see cref="Title"/> so the history overview can group
    /// areas from different runs by category without re-localizing first.</summary>
    public string TitleKey => _area.TitleKey;

    public long FreedBytes => _area.FreedBytes;

    public string FreedText => ByteSize.Format(_area.FreedBytes);

    public bool HasSkipped => !IsNotDone && _area.SkippedBytes > 0;

    /// <summary>The area was skipped, stopped or failed: the row says why instead of a freed figure, never
    /// "0,0 B".</summary>
    public bool IsNotDone => _area.NotDone;

    /// <summary>False only for an area that was not done and freed nothing; a stopped area that freed part
    /// of its size keeps that figure beside its note.</summary>
    public bool ShowsFreed => !_area.NotDone || _area.FreedBytes > 0;

    /// <summary>Why the area was not done, in the current language. An area with no recorded reason (a
    /// stopped run) reads as the plain word "skipped".</summary>
    public string NoteText => CleanupSkipReason.Format(_area.NoteKey ?? "CleanupStateSkipped", _area.NoteArguments);

    public string SkippedText => CleanupItemViewModel.FormatSkippedShares(
        LocalizationService.Instance["CleanupHistorySkipped"], _area.SkippedBytes, _area.ProtectedBytes);

    /// <summary>An area stores the resource KEY of its name, so its text follows the current language.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FreedText));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(NoteText));
    }
}

/// <summary>
/// One past cleanup, collapsed to a line and expandable to what every area gave up. A cleanup cannot be
/// undone, so this is the only record of what left the machine, deleting the RECORD does not
/// touch the files it describes, only the memory of the run.
/// </summary>
public sealed partial class CleanupHistoryRowViewModel : ObservableObject
{
    private readonly CleanupHistoryEntry _entry;
    private readonly Action<CleanupHistoryRowViewModel>? _onDelete;

    /// <param name="onDelete">Null in render/test setups that never touch <see cref="DeleteCommand"/>, the
    /// real callers (CleanupViewModel's constructor and RecordRun) always pass one.</param>
    public CleanupHistoryRowViewModel(CleanupHistoryEntry entry, Action<CleanupHistoryRowViewModel>? onDelete = null)
    {
        _entry = entry;
        _onDelete = onDelete;
        // The cleaned areas by size, the ones that were not done after them in the order the run listed them.
        Areas = entry.Areas.Where(a => !a.NotDone).OrderByDescending(a => a.FreedBytes)
            .Concat(entry.Areas.Where(a => a.NotDone))
            .Select(a => new CleanupHistoryAreaViewModel(a))
            .ToList();
    }

    public IReadOnlyList<CleanupHistoryAreaViewModel> Areas { get; }

    /// <summary>Whether the row shows its areas. Bound two-way to the history window's expander; the view
    /// model owns it so the window that opens from the cleanup report can start with the newest run open.</summary>
    [ObservableProperty]
    private bool isExpanded;

    public override string ToString() => $"{WhenText}, {FreedText}";

    /// <summary>The stored run, for the overview's arithmetic.</summary>
    public CleanupHistoryEntry Entry => _entry;

    /// <summary>The identity <see cref="CleanupHistoryService.Remove"/> matches on.</summary>
    public DateTimeOffset Timestamp => _entry.Timestamp;

    /// <summary>What this one run freed, for the history overview's total across every run, the same
    /// figure <see cref="FreedText"/> already formats.</summary>
    public long FreedBytes => _entry.FreedBytes;

    /// <summary>Local time, to the minute, a cleanup is not a sub-second event. The date follows the
    /// chosen UI language's own short-date order;
    /// the 24h time stays fixed either way.</summary>
    public string WhenText => FormatWhen(_entry.Timestamp);

    public static string FormatWhen(DateTimeOffset timestamp) => timestamp.LocalDateTime.ToString(
        $"{CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern} HH:mm", CultureInfo.CurrentUICulture);

    public string FreedText =>
        string.Format(LocalizationService.Instance["CleanupHistoryFreed"], ByteSize.Format(_entry.FreedBytes));

    /// <summary>Counts only the areas that were done, the same number the run's own headline reported.</summary>
    public string SummaryText
    {
        get
        {
            var cleaned = _entry.Areas.Count(a => !a.NotDone);
            return string.Format(
                LocalizationService.Instance[cleaned == 1 ? "CleanupHistoryEntrySummaryOne" : "CleanupHistoryEntrySummary"],
                cleaned);
        }
    }

    /// <summary>A run the user stopped freed what it freed and no more, worth saying, not worth shouting.</summary>
    public bool WasStopped => _entry.Stopped;

    public string StoppedText => LocalizationService.Instance["CleanupStop"];

    public string DeleteTooltip => LocalizationService.Instance["CleanupHistoryDeleteEntry"];

    /// <summary>The language changed; this row and its areas read their text from the resources in code.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(WhenText));
        OnPropertyChanged(nameof(FreedText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(StoppedText));
        OnPropertyChanged(nameof(DeleteTooltip));

        foreach (var area in Areas)
            area.RefreshLocalizedText();
    }

    /// <summary>A small per-row remove, no confirmation, losing one line of a bounded, 50-entry
    /// log is a much lighter action than the "clear everything" button, which does confirm.</summary>
    [RelayCommand]
    private void Delete() => _onDelete?.Invoke(this);
}
