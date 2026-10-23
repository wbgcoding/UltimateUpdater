namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Live progress while cleaning one target: what is being deleted now, how far along this target is, and
/// how much of its measured total has actually gone. The row shows all three.
/// </summary>
public readonly record struct CleanupProgress(string CurrentItem, double Percent, long FreedBytes);
