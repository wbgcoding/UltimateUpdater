namespace UltimateUpdater.ViewModels;

/// <summary>One row of the top-3 bar list beside the ring, width already scaled against the
/// largest of the three, so the view has nothing left to compute.</summary>
public sealed record CleanupTopAreaViewModel(string Title, string SizeText, double BarWidth)
{
    public override string ToString() => $"{Title}, {SizeText}";
}
