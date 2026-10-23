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
    public string Title => _area.TitleArguments.Count == 0
        ? LocalizationService.Instance[_area.TitleKey]
        : string.Format(
            CultureInfo.CurrentUICulture,
            LocalizationService.Instance[_area.TitleKey],
            _area.TitleArguments.Cast<object>().ToArray());

    public override string ToString() => Title;

    /// <summary>The raw resource key, kept alongside <see cref="Title"/> so the history overview can group
    /// areas from different runs by category without re-localizing first.</summary>
    public string TitleKey => _area.TitleKey;

    public long FreedBytes => _area.FreedBytes;

    public string FreedText => ByteSize.Format(_area.FreedBytes);

    public bool HasSkipped => _area.SkippedBytes > 0;

    public string SkippedText => CleanupItemViewModel.FormatSkippedShares(
        LocalizationService.Instance["CleanupHistorySkipped"], _area.SkippedBytes, _area.ProtectedBytes);

    /// <summary>An area stores the resource KEY of its name, so its text follows the current language.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SkippedText));
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
        Areas = entry.Areas
            .OrderByDescending(a => a.FreedBytes)
            .Select(a => new CleanupHistoryAreaViewModel(a))
            .ToList();
    }

    public IReadOnlyList<CleanupHistoryAreaViewModel> Areas { get; }

    public override string ToString() => $"{WhenText}, {FreedText}";

    /// <summary>The identity <see cref="CleanupHistoryService.Remove"/> matches on.</summary>
    public DateTimeOffset Timestamp => _entry.Timestamp;

    /// <summary>What this one run freed, for the history overview's total across every run, the same
    /// figure <see cref="FreedText"/> already formats.</summary>
    public long FreedBytes => _entry.FreedBytes;

    /// <summary>Local time, to the minute, a cleanup is not a sub-second event. The date follows the
    /// chosen UI language's own short-date order;
    /// the 24h time stays fixed either way.</summary>
    public string WhenText => _entry.Timestamp.LocalDateTime.ToString(
        $"{CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern} HH:mm", CultureInfo.CurrentUICulture);

    public string FreedText =>
        string.Format(LocalizationService.Instance["CleanupHistoryFreed"], ByteSize.Format(_entry.FreedBytes));

    public string SummaryText =>
        string.Format(
            LocalizationService.Instance[_entry.Areas.Count == 1 ? "CleanupHistoryEntrySummaryOne" : "CleanupHistoryEntrySummary"],
            _entry.Areas.Count);

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

/// <summary>One line of the history overview's "biggest categories" list, a category summed across
/// every run in the list, not just one. Built fresh whenever the overview is read, never stored, so it
/// is always in step with whichever runs are still in the history.</summary>
public sealed class CleanupHistoryTopCategoryViewModel
{
    public CleanupHistoryTopCategoryViewModel(string title, long bytes, long totalBytes)
    {
        Title = title;
        Bytes = bytes;
        FreedText = ByteSize.Format(bytes);
        ShareText = (totalBytes > 0 ? (double)bytes / totalBytes : 0).ToString("P0", CultureInfo.CurrentUICulture);
    }

    public string Title { get; }

    public long Bytes { get; }

    public string FreedText { get; }

    public string ShareText { get; }

    public override string ToString() => $"{Title}, {FreedText}";
}
