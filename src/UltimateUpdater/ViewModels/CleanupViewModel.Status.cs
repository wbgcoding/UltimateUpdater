using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// The status block beside the ring: which area of how many is being cleaned, the file or command at work,
// the area that finished last, what has been freed and for how long, and (once it is over) the summary.
public partial class CleanupViewModel
{
    private IReadOnlyList<CleanupItemViewModel> _stepItems = [];

    /// <summary>The areas finished so far in this run, oldest first; the last one feeds the "last done" line.</summary>
    private readonly List<CleanupItemViewModel> _doneOrder = [];

    /// <summary>
    /// The areas of the current or last run, in list order. Unlike the run's own working list it survives
    /// the end of the run, so the finished summary still counts what the run got through.
    /// </summary>
    public IReadOnlyList<CleanupItemViewModel> StepItems
    {
        get => _stepItems;
        internal set
        {
            _stepItems = value;
            _doneOrder.Clear();
            OnPropertyChanged();
            RaiseAreaProgress();
        }
    }

    public double AreaTotalCount => Math.Max(1, StepItems.Count);

    public double AreaDoneCount => StepItems.Count(i => i.IsDone);

    /// <summary>"Area 3 of 10": the area being worked on is the one after those already done, never past the last.</summary>
    public string AreaOfTotalText => StepItems is { Count: > 0 } items
        ? string.Format(Loc["CleanupAreaOfTotal"], Math.Min(items.Count(i => i.IsDone) + 1, items.Count), items.Count)
        : string.Empty;

    /// <summary>"Last done: Caches · 1,2 GB"; empty until the first area of the run is through.</summary>
    public string LastDoneText => _doneOrder.Count > 0
        ? string.Format(Loc["CleanupLastDone"], _doneOrder[^1].Title, ByteSize.Format(_doneOrder[^1].FreedBytes))
        : string.Empty;

    /// <summary>The path or command at work, cut in the middle to fit one line of the card: the start (drive,
    /// program) and the end (file name) are the parts that tell it apart. The full text is the tooltip.</summary>
    public string CurrentPathText => TrimMiddle(CurrentDetailText, PathLineChars);

    // The card is 236 px wide and the line is monospace 11 px, about 6 px a character.
    private const int PathLineChars = 34;

    internal static string TrimMiddle(string text, int maxChars)
    {
        text = text.ReplaceLineEndings(" ");
        if (text.Length <= maxChars || maxChars < 5)
            return text;

        var keep = maxChars - 1;
        var head = keep / 3;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - (keep - head)));
    }

    /// <summary>Records which areas have newly finished, in the order they did, and tells the card.</summary>
    internal void RefreshAreaProgress()
    {
        foreach (var item in StepItems)
        {
            if (item.IsDone && !_doneOrder.Contains(item))
                _doneOrder.Add(item);
        }

        RaiseAreaProgress();
    }

    private void RaiseAreaProgress()
    {
        OnPropertyChanged(nameof(AreaTotalCount));
        OnPropertyChanged(nameof(AreaDoneCount));
        OnPropertyChanged(nameof(AreaOfTotalText));
        OnPropertyChanged(nameof(LastDoneText));
        OnPropertyChanged(nameof(StatusCaptionText));
        OnPropertyChanged(nameof(DoneCountText));
        OnPropertyChanged(nameof(StatusHeadlineText));
    }

    /// <summary>The status block takes the place of the drive facts as soon as a run exists.</summary>
    public bool ShowsRunStatus => Phase != CleanupPhase.Choosing;

    private bool _wasStopped;

    /// <summary>True when the last run ended because the user stopped it.</summary>
    public bool WasStopped
    {
        get => _wasStopped;
        internal set
        {
            if (!SetProperty(ref _wasStopped, value))
                return;

            OnPropertyChanged(nameof(StatusKind));
            OnPropertyChanged(nameof(StatusCaptionText));
        }
    }

    public CleanupStatusKind StatusKind => Phase switch
    {
        CleanupPhase.Finished => WasStopped ? CleanupStatusKind.Stopped : CleanupStatusKind.Done,
        _ => CleanupStatusKind.Cleaning,
    };

    /// <summary>The small caption over the headline: "Bereich 3 von 10" while it works, the result afterwards.</summary>
    public string StatusCaptionText => Phase switch
    {
        CleanupPhase.Cleaning => AreaOfTotalText,
        CleanupPhase.Finished => Loc[WasStopped ? "CleanupSummaryStopped" : "CleanupSummaryDone"],
        _ => string.Empty,
    };

    /// <summary>The line under the caption: the area at work, then how many areas the run got through.</summary>
    public string StatusHeadlineText => Phase == CleanupPhase.Cleaning ? CurrentItemText : DoneCountText;

    /// <summary>The section the area at work belongs to; null when none is running at the moment.</summary>
    [ObservableProperty]
    private CleanupGroupKey? currentGroupKey;

    /// <summary>How far the area at work has got, "62 %"; empty when none is running.</summary>
    [ObservableProperty]
    private string currentAreaPercentText = string.Empty;

    partial void OnCurrentItemTextChanged(string value) => OnPropertyChanged(nameof(StatusHeadlineText));

    /// <summary>Climbs only: the same figure the ring carries, so the two never disagree.</summary>
    public string StatusFreedText => IsFinished ? FreedText : LiveFreedText;

    private DateTime? _runStartedAt;
    private DateTime? _runEndedAt;
    private string _shownElapsed = string.Empty;

    /// <summary>"1:23": how long this run has taken, frozen once it is over.</summary>
    public string ElapsedText => _runStartedAt is { } started
        ? UpdateRowViewModel.FormatElapsed((_runEndedAt ?? Clock()) - started)
        : "0:00";

    internal void StartRunClock()
    {
        _runStartedAt = Clock();
        _runEndedAt = null;
        RaiseElapsedIfChanged();
    }

    internal void StopRunClock()
    {
        _runEndedAt = Clock();
        RaiseElapsedIfChanged();
    }

    /// <summary>Driven by the run's existing timer; only a changed second reaches the screen.</summary>
    internal void RefreshElapsed() => RaiseElapsedIfChanged();

    private void RaiseElapsedIfChanged()
    {
        var text = ElapsedText;
        if (text == _shownElapsed)
            return;

        _shownElapsed = text;
        OnPropertyChanged(nameof(ElapsedText));
    }

    /// <summary>Everything the status block builds from words, re-read after a language switch.</summary>
    private void RefreshStatusText()
    {
        OnPropertyChanged(nameof(StatusCaptionText));
        OnPropertyChanged(nameof(StatusHeadlineText));
    }
}
