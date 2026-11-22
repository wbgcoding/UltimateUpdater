namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Measures and frees disk space for the cleanup dialog. Two rules run through all of
/// it: every path passes <see cref="CleanupSafety"/> before anything is deleted, and a file that is locked
/// or refused is COUNTED and skipped rather than aborting the run; on a live Windows machine that happens
/// constantly and is entirely normal.
/// </summary>
public partial class CleanupService(LoggingService logging)
{
    /// <summary>Measures one target without changing anything. <paramref name="recycleBinMinAgeDays"/>
    /// only matters for <see cref="CleanupKind.RecycleBin"/>; every other kind ignores it. Virtual so
    /// tests can stand in a fake that returns instantly instead of measuring the real machine:
    /// the real implementation walks real directories, the real registry and the real recycle bin, which
    /// on a live dev machine can run for minutes, far past a test's own patience.</summary>
    public virtual Task<CleanupScanResult> ScanAsync(
        CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
        Task.Run(() =>
        {
            // A scan nobody is waiting on (the startup prescan, no window attached) runs in background
            // thread mode to keep the rest of the machine responsive. A scan for an open window does not:
            // somebody is looking at its spinner. The walks are fully synchronous, so the mode ends on the
            // same pool thread before it is handed back.
            using (BackgroundScan.Value ? BackgroundWork.Enter() : null)
            {
                return target.Kind switch
                {
                    CleanupKind.Directories or CleanupKind.AppCacheSweep => ScanDirectories(target, cancellationToken),
                    CleanupKind.RecycleBin => ScanRecycleBin(cancellationToken, recycleBinMinAgeDays),
                    CleanupKind.RegistryCleanup => ScanRegistry(target, cancellationToken),
                    CleanupKind.LeftoverInstalls => ScanLeftovers(cancellationToken),
                    CleanupKind.DriverCleanup => ScanDrivers(cancellationToken),
                    CleanupKind.PipInstallLeftovers => ScanPipLeftovers(cancellationToken),
                    CleanupKind.OldPythonVersions => ScanOldPythonVersions(CallerProfilePath, cancellationToken),
                    // Disk Cleanup reports nothing before it runs, and guessing a figure would be a lie.
                    _ => new CleanupScanResult(0, 0, 0),
                };
            }
        }, cancellationToken);

    /// <summary>Carries the caller's priority choice into <see cref="ScanAsync(CleanupTarget, CancellationToken, int)"/>
    /// without changing its signature (tests override it). <c>Task.Run</c> copies the value into the pool
    /// thread's context.</summary>
    private static readonly AsyncLocal<bool> BackgroundScan = new();

    /// <summary>How often a file pass reports what it has freed so far: about four times a second.</summary>
    internal TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The signed-in user's profile folder when this runs in the elevated worker (which may be another
    /// account); null in the app itself, where the own profile is the right one. Only used to count virtual
    /// environments for the old-Python rows.</summary>
    internal string? CallerProfilePath { get; set; }

    /// <summary>Test seam: what the caller asked for, readable from inside an overridden
    /// <see cref="ScanAsync(CleanupTarget, CancellationToken, int)"/> that stands in for the real walk.</summary>
    internal static bool BackgroundRequestedForTest => BackgroundScan.Value;

    /// <summary>
    /// Measures one target and says whether it may run in background thread mode: true only for a scan
    /// nobody waits on (the startup prescan). The plain overload always runs at normal priority.
    /// </summary>
    public Task<CleanupScanResult> ScanAsync(
        CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays, bool background)
    {
        BackgroundScan.Value = background;
        try
        {
            return ScanAsync(target, cancellationToken, recycleBinMinAgeDays);
        }
        finally
        {
            BackgroundScan.Value = false;
        }
    }

    /// <summary>
    /// Frees one target. NEVER call this without the user having confirmed the warning first: that
    /// confirmation is the whole safety model.
    /// <paramref name="registrySettings"/> only matters for <see cref="CleanupKind.RegistryCleanup"/>;
    /// <paramref name="selectedLeftoverPaths"/> only for <see cref="CleanupKind.LeftoverInstalls"/>; every
    /// other kind ignores the one that is not its own. <paramref name="recycleInsteadOfDelete"/> only applies
    /// to a not-<see cref="CleanupTarget.NeedsElevation"/> Directories/AppCacheSweep target
    /// (<see cref="CleanDirectories"/> re-checks that itself, so passing true elsewhere does nothing).
    /// <paramref name="excludedEntryPaths"/> applies to EVERY kind with a detail list: it is the user's own
    /// "leave this one alone". <paramref name="confirmedEntryPaths"/> (recycle bin only) is the set of rows the
    /// user saw and kept ticked; null means the caller gives no such set. <paramref name="recycleBinMinAgeDays"/> only matters for
    /// <see cref="CleanupKind.RecycleBin"/> and must be the SAME value the scan behind
    /// <paramref name="excludedEntryPaths"/> used, or the fresh re-scan right before deleting could
    /// rediscover an item the user never saw and delete it unseen.
    /// </summary>
    public virtual Task<CleanupOutcome> CleanAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
        PauseGate? pause = null, Models.Settings.RegistryCleanupSettings? registrySettings = null,
        IReadOnlyCollection<string>? selectedLeftoverPaths = null,
        IReadOnlyList<RegistryFinding>? registryFindings = null,
        IReadOnlyCollection<string>? excludedEntryPaths = null,
        bool recycleInsteadOfDelete = false,
        int recycleBinMinAgeDays = 0,
        IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
        IReadOnlyCollection<string>? selectedDriverPackageNames = null,
        IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
        IReadOnlyCollection<string>? confirmedEntryPaths = null) =>
        Task.Run(async () => target.Kind switch
        {
            CleanupKind.Directories or CleanupKind.AppCacheSweep =>
                await CleanDirectoriesAsync(target, progress, cancellationToken, pause, excludedEntryPaths, recycleInsteadOfDelete),
            CleanupKind.RecycleBin => EmptyRecycleBin(
                excludedEntryPaths, confirmedEntryPaths, recycleBinMinAgeDays, progress, cancellationToken, pause),
            CleanupKind.DiskCleanup => await RunDiskCleanupAsync(cancellationToken),
            CleanupKind.SystemRepair => await RunSystemRepairAsync(target, progress, cancellationToken, pause),
            CleanupKind.RegistryCleanup =>
                await CleanRegistryAsync(target, registrySettings, registryFindings, excludedEntryPaths, cancellationToken),
            CleanupKind.LeftoverInstalls => CleanLeftovers(selectedLeftoverPaths, cancellationToken),
            CleanupKind.Defragmentation => await RunDefragAsync(target, progress, cancellationToken),
            CleanupKind.DriverCleanup =>
                await CleanDriversAsync(selectedDriverDeviceInstanceIds, selectedDriverPackageNames, cancellationToken),
            CleanupKind.PipInstallLeftovers => CleanPipLeftovers(excludedEntryPaths, cancellationToken),
            CleanupKind.OldPythonVersions => await CleanOldPythonVersionsAsync(selectedPythonVersionPaths, cancellationToken),
            _ => new CleanupOutcome(0, 0, 0),
        }, cancellationToken);

    /// <summary>
    /// Kinds that only an administrator can run at all: started unelevated, DISM and sfc end at once with
    /// exit code 740, defrag and pnputil refuse, and Disk Cleanup cannot write its profile. The cleanup
    /// dialog skips and flags such a target instead of reporting it done with nothing freed. Everything
    /// else frees what it can without admin and still runs.
    /// </summary>
    internal static bool SkipsWithoutAdmin(CleanupKind kind, bool processElevated) =>
        !processElevated && kind is CleanupKind.SystemRepair or CleanupKind.Defragmentation
            or CleanupKind.DriverCleanup or CleanupKind.DiskCleanup;

    /// <summary>
    /// The target-level answer: the kinds above plus a folder an ordinary user cannot delete from at all
    /// (<see cref="CleanupTarget.AdminOnlyDelete"/>), where an unelevated pass would report "done" with
    /// nothing freed.
    /// </summary>
    internal static bool SkipsWithoutAdmin(CleanupTarget target, bool processElevated) =>
        !processElevated && (SkipsWithoutAdmin(target.Kind, processElevated: false) || target.AdminOnlyDelete);
}