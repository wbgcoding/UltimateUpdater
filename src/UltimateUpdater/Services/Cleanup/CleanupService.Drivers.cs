namespace UltimateUpdater.Services.Cleanup;

public partial class CleanupService
{
    /// <summary>Disconnected devices plus old driver packages, gathered fresh every scan the same
    /// way <see cref="ScanRegistry"/> re-reads the registry every time. No byte total, same as a registry
    /// finding: the count travels in Files.</summary>
    private CleanupScanResult ScanDrivers(CancellationToken cancellationToken)
    {
        IDriverInventoryProvider provider = new SetupApiDriverInventoryProvider(logging.Warn);
        var devices = provider.GatherDevices(cancellationToken);
        var packages = provider.GatherPackages(cancellationToken);

        var disconnectedDevices = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, packages, logInfo: logging.Info);
        var oldPackages = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages);

        return new CleanupScanResult(0, disconnectedDevices.Count + oldPackages.Count, 0)
        {
            DriverDeviceCandidates = disconnectedDevices,
            DriverPackageCandidates = oldPackages,
        };
    }

    /// <summary>
    /// Cleans exactly the rows the user selected, re-checked against a FRESH inventory immediately before
    /// pnputil is ever called; see <see cref="DriverCleanupExecutor"/> for the full validation.
    /// </summary>
    private async Task<CleanupOutcome> CleanDriversAsync(
        IReadOnlyCollection<string>? selectedDeviceInstanceIds, IReadOnlyCollection<string>? selectedPackageNames,
        CancellationToken cancellationToken)
    {
        if (selectedDeviceInstanceIds is not { Count: > 0 } && selectedPackageNames is not { Count: > 0 })
            return new CleanupOutcome(0, 0, 0);

        // pnputil needs administrator rights, and staging a backup unelevated would leave a user-owned
        // ProgramData folder behind that every later elevated run then refuses to trust.
        if (!ElevationService.IsElevated)
        {
            var selected = (selectedDeviceInstanceIds?.Count ?? 0) + (selectedPackageNames?.Count ?? 0);
            logging.Warn($"Driver cleanup: {selected} selected item(s) skipped, administrator rights are required.");
            return new CleanupOutcome(0, 0, selected);
        }

        return await DriverCleanupExecutor.CleanAsync(
            new SetupApiDriverInventoryProvider(logging.Warn), selectedDeviceInstanceIds ?? [], selectedPackageNames ?? [],
            logging, cancellationToken);
    }
}
