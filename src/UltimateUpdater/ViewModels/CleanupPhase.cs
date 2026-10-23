namespace UltimateUpdater.ViewModels;

/// <summary>What the dialog's centre is showing.</summary>
public enum CleanupPhase
{
    /// <summary>Measuring / waiting for the user: the ring shows the selected share of the total.</summary>
    Choosing,

    /// <summary>Running: the ring shows progress and the broom sweeps.</summary>
    Cleaning,

    /// <summary>Finished: the ring is full and the centre shows "+X GB".</summary>
    Finished,
}
