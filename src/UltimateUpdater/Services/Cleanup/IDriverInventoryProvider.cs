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

    /// <summary>Every published third-party package. <paramref name="measure"/> is asked with the published
    /// <c>oem*.inf</c> name whether that package's store folder is size-walked (default: all); a declined
    /// package is still listed, with size 0. Lets the clean pass measure only the packages it acts on.</summary>
    IReadOnlyList<DriverPackageInfo> GatherPackages(CancellationToken cancellationToken, Func<string, bool>? measure = null);
}
