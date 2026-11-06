namespace UltimateUpdater.ViewModels;

/// <summary>
/// What one area looks like in the step bar of a run. Each kind has its own glyph as well as its own
/// colour, so the bar still reads for someone who cannot tell the colours apart.
/// </summary>
public enum CleanupStepKind
{
    Waiting,
    Running,
    Done,
    NothingFound,
    Stopped,
}

/// <summary>Which of the three faces the status block shows once a run exists.</summary>
public enum CleanupStatusKind
{
    Cleaning,
    Done,
    Stopped,
}
