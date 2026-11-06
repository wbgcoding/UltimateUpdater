using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// The status block beside the ring: which area is being cleaned, a step bar of every area in the run,
// what has been freed and for how long, and (once it is over) the summary.
public partial class CleanupViewModel
{
    private IReadOnlyList<CleanupItemViewModel> _stepItems = [];

    /// <summary>
    /// The areas of the current or last run, in list order, for the step bar. Unlike the run's own working
    /// list it survives the end of the run, so the finished picture still shows what became of each area.
    /// </summary>
    public IReadOnlyList<CleanupItemViewModel> StepItems
    {
        get => _stepItems;
        internal set
        {
            _stepItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DoneCountText));
            OnPropertyChanged(nameof(StatusHeadlineText));
        }
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

    /// <summary>The small caption over the headline: "Aktuell" while it works, the result afterwards.</summary>
    public string StatusCaptionText => Phase switch
    {
        CleanupPhase.Cleaning => Loc["CleanupCurrentLabel"],
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
