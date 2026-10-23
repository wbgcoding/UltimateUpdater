namespace UltimateUpdater.ViewModels;

/// <summary>Where one target stands in a run, as its status capsule says it. <see cref="Idle"/> is "no run
/// has touched this row", and shows no capsule at all.</summary>
public enum CleanupTargetState
{
    Idle,
    Waiting,
    Running,
    Done,
    Stopped,
    Skipped,
}
