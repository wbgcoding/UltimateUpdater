namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// One device node as SetupAPI/CfgMgr32 reports it, present or not. Pure data: nothing here decides
/// whether a device is offered, that judgment lives in <see cref="DriverCleanupDiscovery"/> so it can be
/// tested against fabricated devices with no real hardware and no elevation.
/// </summary>
public sealed record DriverDeviceInfo(
    string InstanceId,
    string FriendlyName,
    string ClassName,
    bool Present,
    string? InfPath,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    DateTimeOffset? LastArrivalDate,
    DateTimeOffset? LastRemovalDate,
    DateTimeOffset? InstallDate = null,
    DateTimeOffset? FirstInstallDate = null,
    DateTimeOffset? RegistryKeyWriteDate = null);

/// <summary>
/// One third-party driver package under <c>%windir%\INF\oem*.inf</c>. <see cref="PackageName"/> is always
/// the bare file name (e.g. "oem12.inf"), never a full path: it is what pnputil's own
/// <c>/delete-driver</c>/<c>/export-driver</c> switches take, and the only shape
/// <see cref="DriverCleanupExecutor"/> ever validates and passes on.
/// </summary>
public sealed record DriverPackageInfo(
    string PackageName,
    string Provider,
    string ClassName,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    string OriginalName,
    long StoreFolderSizeBytes,
    string? StorePath = null);
