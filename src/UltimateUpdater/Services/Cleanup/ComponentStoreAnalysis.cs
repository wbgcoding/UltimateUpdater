namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// What DISM's own <c>/Online /Cleanup-Image /AnalyzeComponentStore</c> reported about the WinSxS
/// component store. Each field is independently null when <see cref="ComponentStoreAnalysisParser"/>
/// could not find or understand that one line in DISM's output, never guessed from the others.
/// Read-only information: nothing in this app deletes anything from this result, the existing Windows
/// Disk Cleanup entry stays the only way to act on it.
/// </summary>
public sealed record ComponentStoreAnalysis(
    long? ActualSizeBytes,
    long? SharedWithWindowsBytes,
    long? BackupsAndDisabledFeaturesBytes,
    bool? CleanupRecommended,
    bool TimedOut = false)
{
    /// <summary>Every field unknown: dism.exe missing, the call failed, or nothing in its output was
    /// understood.</summary>
    public static readonly ComponentStoreAnalysis Unknown = new(null, null, null, null);

    /// <summary>DISM itself was killed after running past its own hard deadline (<see
    /// cref="CleanupService.AnalyzeComponentStoreAsync"/>'s 5-minute <see cref="ProcessRunner.RunAsync"/>
    /// timeout) rather than crashing or returning early on its own. Distinct from <see cref="Unknown"/>: the
    /// dialog must say THIS happened rather than silently showing dashes as if DISM had simply printed
    /// nothing.</summary>
    public static readonly ComponentStoreAnalysis TimedOutResult = new(null, null, null, null, TimedOut: true);
}
