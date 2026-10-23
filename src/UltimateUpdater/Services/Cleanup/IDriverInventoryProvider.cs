namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The seam between <see cref="DriverCleanupDiscovery"/>'s pure judgment and the real machine: every
/// device node (present or not) and every third-party driver package in the driver store. A test stands in
/// a fake here instead of touching SetupAPI/CfgMgr32 or a real driver store, the same role <see
/// cref="LeftoverInstallDiscovery"/> plays for its own file-system walk.
/// </summary>
public interface IDriverInventoryProvider
{
    IReadOnlyList<DriverDeviceInfo> GatherDevices(CancellationToken cancellationToken);

    IReadOnlyList<DriverPackageInfo> GatherPackages(CancellationToken cancellationToken);
}
