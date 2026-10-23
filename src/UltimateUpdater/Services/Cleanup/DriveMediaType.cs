using System.Management;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>What kind of storage a fixed drive sits on: the defragmentation row's own action word
/// (defragment vs optimise/trim) and its command both depend on this, and a drive this cannot be determined
/// for is never offered rather than guessed at.</summary>
public enum DriveMediaType
{
    Unknown,
    Hdd,
    Ssd,

    /// <summary>A physical disk WAS found and answered, but reported <c>MediaType</c> 0
    /// ("Unspecified" in the WMI schema) rather than 3 (HDD) or 4 (SSD). Distinct from <see cref="Unknown"/>,
    /// which means nothing could be determined at all; a row still exists for this one, with its own,
    /// neutral wording.</summary>
    Unspecified,
}

/// <summary>
/// Reads a fixed drive's media type through WMI: <c>Win32_LogicalDisk</c> to its partition to its physical
/// disk (root\cimv2, the classic association chain <c>Get-Partition</c>/<c>Get-Disk</c> also walk), then
/// that physical disk's own <c>MSFT_PhysicalDisk.MediaType</c> (root\Microsoft\Windows\Storage, the same
/// property PowerShell's <c>Get-PhysicalDisk</c> reports). Never measured against a real machine from this
/// worktree (no VM access here); wrapped end to end in a fail-safe so any missing class, permission problem
/// or WMI quirk on a virtual disk degrades to <see cref="DriveMediaType.Unknown"/>, exactly the "never guess
/// between the two" outcome this asks for rather than a thrown exception or a wrong guess.
/// </summary>
public static class DriveMediaTypeReader
{
    /// <summary>
    /// The raw <c>MSFT_PhysicalDisk.MediaType</c> values: 0 Unspecified, 3 HDD, 4 SSD (the same numbers
    /// <c>Get-PhysicalDisk</c> exposes as <c>MediaType</c>/<c>SpindleSpeed</c>). Pure, so the mapping itself
    /// is testable without any real WMI call.
    /// </summary>
    internal static DriveMediaType FromWmiMediaType(int rawMediaType) => rawMediaType switch
    {
        3 => DriveMediaType.Hdd,
        4 => DriveMediaType.Ssd,
        0 => DriveMediaType.Unspecified,
        _ => DriveMediaType.Unknown,
    };

    /// <summary>
    /// A drive's media type does not change while the app runs, and <see cref="CleanupCatalog.Build"/> is
    /// called far more often than that (once per dialog open, and once per test that touches the
    /// catalogue): without this, every one of those calls repeated the same two-namespace WMI walk for the
    /// same drives, which measured as most of a 1500-test suite's OWN run time on a machine with real fixed
    /// drives. Never invalidated: nothing here can hot-swap a drive's own storage medium.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DriveMediaType> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A logging seam, same shape as <see cref="ScanResultCache.PathOverride"/>. Does
    /// nothing by default; <c>App.xaml.cs</c> points it at the real <see cref="LoggingService"/>'s
    /// <c>Info</c> method once at startup, so a published build finally shows what this reader actually got
    /// per drive instead of failing silently.</summary>
    internal static Action<string> Log { get; set; } = _ => { };

    /// <summary>The WMI query itself, swappable so a test can make it throw any exception kind
    /// without a real WMI call. Returns the raw, unmapped <c>MediaType</c> value, or null when no physical
    /// disk could be resolved for the drive letter at all. Default is the real query.</summary>
    internal static Func<string, int?> QueryRawMediaType { get; set; } = RealQueryForTests;

    /// <summary>Best effort, drive letter WITHOUT a trailing separator (e.g. "C:"). Never throws.</summary>
    public static DriveMediaType TryGetMediaType(string driveLetter) =>
        Cache.GetOrAdd(driveLetter, TryGetMediaTypeUncached);

    private static DriveMediaType TryGetMediaTypeUncached(string driveLetter)
    {
        try
        {
            var rawMediaType = QueryRawMediaType(driveLetter);
            if (rawMediaType is null)
            {
                Log($"drive {driveLetter}: no physical disk could be resolved, media type unreadable");
                return DriveMediaType.Unknown;
            }

            var mediaType = FromWmiMediaType(rawMediaType.Value);
            Log($"drive {driveLetter}: WMI reported MediaType {rawMediaType.Value} ({mediaType})");
            return mediaType;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException
                                       or UnauthorizedAccessException or FormatException or InvalidCastException
                                       or DllNotFoundException or TypeInitializationException
                                       or PlatformNotSupportedException)
        {
            Log($"drive {driveLetter}: media type unreadable, {ex.GetType().Name}: {ex.Message}");
            return DriveMediaType.Unknown;
        }
    }

    /// <summary>The real WMI query, exposed so a test that replaces <see cref="QueryRawMediaType"/> can put
    /// it back exactly, rather than duplicating this logic or leaving a throwing stub behind for the next
    /// test that runs.</summary>
    internal static int? RealQueryForTests(string driveLetter)
    {
        var physicalDiskIndex = FindPhysicalDiskIndex(driveLetter);
        if (physicalDiskIndex is null)
            return null;

        var storageScope = new ManagementScope(@"root\Microsoft\Windows\Storage");
        storageScope.Connect();

        using var searcher = new ManagementObjectSearcher(
            storageScope,
            new ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{physicalDiskIndex}'"));

        using var results = searcher.Get();
        foreach (ManagementBaseObject disk in results)
        {
            using (disk)
            {
                if (disk["MediaType"] is { } rawMediaType)
                    return Convert.ToInt32(rawMediaType);
            }
        }

        return null;
    }

    /// <summary>
    /// A SECOND cached lookup, kept apart from <see cref="TryGetMediaType"/> rather than widening
    /// its return value, so every existing caller of that one is untouched. Reads <c>MSFT_PhysicalDisk.
    /// BusType</c>; 7 is USB (the same numbering <c>Get-PhysicalDisk</c> reports). Fails closed to false
    /// (not USB) on any WMI error, same reasoning as the media type lookup: guessing a drive is a removable
    /// USB stick when it might not be is worse than simply not treating it as one.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> UsbCache = new(StringComparer.OrdinalIgnoreCase);

    internal static Func<string, int?> QueryRawBusType { get; set; } = RealBusTypeQueryForTests;

    public static bool TryGetIsUsb(string driveLetter) => UsbCache.GetOrAdd(driveLetter, TryGetIsUsbUncached);

    private static bool TryGetIsUsbUncached(string driveLetter)
    {
        try
        {
            var busType = QueryRawBusType(driveLetter);
            return busType == 7;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException
                                       or UnauthorizedAccessException or FormatException or InvalidCastException
                                       or DllNotFoundException or TypeInitializationException
                                       or PlatformNotSupportedException)
        {
            Log($"drive {driveLetter}: bus type unreadable, {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static int? RealBusTypeQueryForTests(string driveLetter)
    {
        var physicalDiskIndex = FindPhysicalDiskIndex(driveLetter);
        if (physicalDiskIndex is null)
            return null;

        var storageScope = new ManagementScope(@"root\Microsoft\Windows\Storage");
        storageScope.Connect();

        using var searcher = new ManagementObjectSearcher(
            storageScope,
            new ObjectQuery($"SELECT BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{physicalDiskIndex}'"));

        using var results = searcher.Get();
        foreach (ManagementBaseObject disk in results)
        {
            using (disk)
            {
                if (disk["BusType"] is { } rawBusType)
                    return Convert.ToInt32(rawBusType);
            }
        }

        return null;
    }

    /// <summary>
    /// <c>Win32_LogicalDisk</c> -&gt; <c>Win32_LogicalDiskToPartition</c> -&gt; <c>Win32_DiskDriveToDiskPartition</c>
    /// -&gt; <c>Win32_DiskDrive.Index</c>, the classic association chain from a drive letter to the physical
    /// disk index that <c>MSFT_PhysicalDisk.DeviceId</c> uses. Only the FIRST physical disk a partition
    /// resolves to is used; a drive letter spanning more than one physical disk (a striped dynamic volume)
    /// is a real but rare case this app has no other special handling for either.
    /// </summary>
    private static string? FindPhysicalDiskIndex(string driveLetter)
    {
        using var logicalDisk = new ManagementObject($"Win32_LogicalDisk.DeviceID='{driveLetter}'");
        using var partitions = logicalDisk.GetRelated("Win32_DiskPartition");
        foreach (ManagementBaseObject partitionRef in partitions)
        {
            using (partitionRef)
            {
                using var drives = ((ManagementObject)partitionRef).GetRelated("Win32_DiskDrive");
                foreach (ManagementBaseObject driveRef in drives)
                {
                    using (driveRef)
                    {
                        if (driveRef["Index"] is { } index)
                            return Convert.ToInt32(index).ToString();
                    }
                }
            }
        }

        return null;
    }
}
