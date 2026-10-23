using System.IO;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The real, impure edge behind <see cref="IDriverInventoryProvider"/>: SetupAPI/CfgMgr32 for device nodes
/// (<see cref="GatherDevices"/>, <see cref="DIGCF_ALLCLASSES"/> without <c>DIGCF_PRESENT</c> so a removed
/// device still shows up), plus a plain directory walk of the driver store for packages
/// (<see cref="GatherPackages"/>). Exercised only through a live run (VM), never a unit test, same role as
/// <see cref="RegistryCleanupScanner"/>; every branch it feeds is what <see cref="DriverCleanupDiscovery"/>
/// actually decides on, and that class is what the unit tests drive.
/// </summary>
public sealed class SetupApiDriverInventoryProvider : IDriverInventoryProvider
{
    private const int DIGCF_ALLCLASSES = 0x00000004;
    private const int SPDRP_DEVICEDESC = 0x00000000;
    private const int SPDRP_FRIENDLYNAME = 0x0000000C;
    private const int SPDRP_CLASS = 0x00000007;
    private const int CR_SUCCESS = 0;

    private static readonly Guid GUID_NULL = Guid.Empty;

    // DEVPROPKEYs from devpkey.h; each is a (fmtid, pid) pair, never looked up by name at runtime.
    private static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath = new(
        new Guid(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6), 5);
    private static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion = new(
        new Guid(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6), 3);
    private static readonly DEVPROPKEY DEVPKEY_Device_DriverDate = new(
        new Guid(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0xc, 0x75, 0xd6), 2);
    // The fmtid/pid pair this file used before was wrong (neither matched devpkey.h at all), so every
    // single lookup failed silently and every device showed "time unknown" regardless of real history -
    // confirmed on a live probe (343 of 343 devices, including non-present ones that must have a real
    // removal date). Verified against the Windows SDK's own devpkey.h (fmtid 83da6326-97a6-4088-9453-
    // a1923f573b29): InstallDate=100, FirstInstallDate=101, LastArrivalDate=102, LastRemovalDate=103.
    private static readonly Guid DevPropKeyDeviceFmtId = new(0x83da6326, 0x97a6, 0x4088, 0x94, 0x53, 0xa1, 0x92, 0x3f, 0x57, 0x3b, 0x29);
    private static readonly DEVPROPKEY DEVPKEY_Device_InstallDate = new(DevPropKeyDeviceFmtId, 100);
    private static readonly DEVPROPKEY DEVPKEY_Device_FirstInstallDate = new(DevPropKeyDeviceFmtId, 101);
    private static readonly DEVPROPKEY DEVPKEY_Device_LastArrivalDate = new(DevPropKeyDeviceFmtId, 102);
    private static readonly DEVPROPKEY DEVPKEY_Device_LastRemovalDate = new(DevPropKeyDeviceFmtId, 103);
    private static readonly DEVPROPKEY DEVPKEY_Device_Class = new(
        new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 9);

    public IReadOnlyList<DriverDeviceInfo> GatherDevices(CancellationToken cancellationToken)
    {
        var devices = new List<DriverDeviceInfo>();
        var deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            return devices;

        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            for (uint index = 0; SetupDiEnumDeviceInfo(deviceInfoSet, index, ref data); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var instanceId = GetDeviceInstanceId(deviceInfoSet, ref data);
                if (instanceId is null)
                    continue;

                var friendlyName = GetStringProperty(deviceInfoSet, ref data, SPDRP_FRIENDLYNAME)
                    ?? GetStringProperty(deviceInfoSet, ref data, SPDRP_DEVICEDESC) ?? instanceId;
                var className = GetDevicePropertyString(deviceInfoSet, ref data, DEVPKEY_Device_Class)
                    ?? GetStringProperty(deviceInfoSet, ref data, SPDRP_CLASS) ?? string.Empty;
                var infPath = GetDevicePropertyString(deviceInfoSet, ref data, DEVPKEY_Device_DriverInfPath);
                var driverVersion = GetDevicePropertyString(deviceInfoSet, ref data, DEVPKEY_Device_DriverVersion);
                var driverDate = GetDevicePropertyFileTime(deviceInfoSet, ref data, DEVPKEY_Device_DriverDate);
                var lastArrival = GetDevicePropertyFileTime(deviceInfoSet, ref data, DEVPKEY_Device_LastArrivalDate);
                var lastRemoval = GetDevicePropertyFileTime(deviceInfoSet, ref data, DEVPKEY_Device_LastRemovalDate);
                var installDate = GetDevicePropertyFileTime(deviceInfoSet, ref data, DEVPKEY_Device_InstallDate);
                var firstInstallDate = GetDevicePropertyFileTime(deviceInfoSet, ref data, DEVPKEY_Device_FirstInstallDate);
                // Last resort, and only opened when every DEVPKEY date above came back empty: the device's
                // own driver registry key's last-write time, read-only, never written to.
                var registryWriteDate = lastArrival is null && lastRemoval is null && installDate is null && firstInstallDate is null
                    ? GetRegistryKeyLastWriteDate(deviceInfoSet, ref data)
                    : null;
                var present = IsPresent(instanceId);

                devices.Add(new DriverDeviceInfo(
                    instanceId, friendlyName, className, present, infPath, driverVersion, driverDate,
                    lastArrival, lastRemoval, installDate, firstInstallDate, registryWriteDate));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return devices;
    }

    /// <summary>
    /// A driver store FileRepository folder is named after the package's ORIGINAL inf file, never after the
    /// published <c>oem*.inf</c> name pnputil and <see cref="DriverCleanupExecutor"/> both need, so looking
    /// for an <c>oem*.inf</c> file INSIDE that folder (the previous shape of this method) never finds one on
    /// a real machine (none of the published <c>%windir%\INF\oem*.inf</c> files lives inside its own
    /// FileRepository folder), so the whole old-driver-packages group came
    /// back empty on every install. Enumerating <c>%windir%\INF\oem*.inf</c> directly and reading each one's
    /// own [Version] section (it is a byte-identical copy of the original inf's content) gives Provider/Class
    /// /DriverVer without ever touching pnputil's own localised enumeration text. <see
    /// cref="SetupGetInfDriverStoreLocation"/>, the real SetupAPI mapping from a published inf to its
    /// DriverStore file, is what then finds the FileRepository folder for a size and the original file name
    /// -  never a folder-name guess, and never pnputil text either. <see cref="DriverPackageInfo.PackageName"/>
    /// stays the published <c>oem*.inf</c> name throughout: that is the only name pnputil's
    /// <c>/export-driver</c>/<c>/delete-driver</c> accept.
    /// </summary>
    public IReadOnlyList<DriverPackageInfo> GatherPackages(CancellationToken cancellationToken)
    {
        var packages = new List<DriverPackageInfo>();
        var infDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

        if (!Directory.Exists(infDirectory))
            return packages;

        foreach (var oemInfPath in SafeEnumerate.Files(infDirectory)
                     .Where(f => DriverCleanupDiscovery.IsOemPackageName(Path.GetFileName(f))))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DriverInfVersionInfo? info;
            try
            {
                var bytes = File.ReadAllBytes(oemInfPath);
                info = DriverPackageInfReader.Parse(DriverPackageInfReader.DecodeInfBytes(bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (info is null)
                continue;

            var storeInfPath = ResolveDriverStoreLocation(oemInfPath);
            var storeFolder = storeInfPath is null ? null : Path.GetDirectoryName(storeInfPath);
            // Recursive: a package's files live in sub-folders (language packs, catalogs), a top-level sum
            // would understate what removing it frees. No file cap, the folder is a driver package.
            var size = storeFolder is null
                ? 0
                : DirectorySizeWalker.Measure(storeFolder, DirectorySizeWalker.DefaultMaxDepth, int.MaxValue, cancellationToken);
            // Falls back to the published name itself when the store mapping fails (an orphaned oem*.inf
            // with no FileRepository folder any more): still a real, pnputil-usable name, just not able to
            // say what it was called before it was published.
            var originalName = storeInfPath is null ? Path.GetFileName(oemInfPath) : Path.GetFileName(storeInfPath);

            packages.Add(new DriverPackageInfo(
                Path.GetFileName(oemInfPath), info.Provider, info.ClassName, info.DriverVersion, info.DriverDate,
                originalName, size, storeFolder));
        }

        return packages;
    }

    /// <summary>
    /// The real SetupAPI mapping from a published <c>%windir%\INF\oem*.inf</c> file to the DriverStore's own
    /// copy of it, the same lookup pnputil performs internally. Returns null when the package has no
    /// live DriverStore entry (removed by hand outside pnputil, or a transient error) rather than throwing.
    /// </summary>
    private static string? ResolveDriverStoreLocation(string oemInfPath)
    {
        var buffer = new char[4096];
        if (!SetupGetInfDriverStoreLocation(oemInfPath, IntPtr.Zero, null, buffer, buffer.Length, out var required)
            || required <= 0)
        {
            return null;
        }

        return new string(buffer, 0, Math.Min(required - 1, buffer.Length));
    }

    private static bool IsPresent(string instanceId)
    {
        var result = CM_Locate_DevNode(out var devInst, instanceId, 0);
        return result == CR_SUCCESS;
    }

    /// <summary>
    /// Last resort for "when was this last seen", read-only: the device's own driver registry key
    /// (<c>DIREG_DRV</c>) carries a last-write time the registry itself maintains, close enough to "last
    /// touched" when nothing more specific survived. Opened for <c>KEY_READ</c> only and always closed.
    /// </summary>
    private static DateTimeOffset? GetRegistryKeyLastWriteDate(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA data)
    {
        var key = SetupDiOpenDevRegKey(deviceInfoSet, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DRV, KEY_READ);
        if (key == IntPtr.Zero || key == InvalidHandleValue)
            return null;

        try
        {
            if (RegQueryInfoKey(
                    key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var fileTime) != 0)
                return null;

            return fileTime <= 0 ? null : DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static string? GetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA data)
    {
        var buffer = new char[1024];
        return SetupDiGetDeviceInstanceId(deviceInfoSet, ref data, buffer, buffer.Length, out var required)
            ? new string(buffer, 0, required - 1)
            : null;
    }

    private static string? GetStringProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA data, int property)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceInfoSet, ref data, property, out _, buffer, buffer.Length, out var required))
            return null;

        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, required);
        return text.TrimEnd('\0');
    }

    private static string? GetDevicePropertyString(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceProperty(
                deviceInfoSet, ref data, ref key, out var propertyType, buffer, buffer.Length, out var required, 0)
            || propertyType != DEVPROP_TYPE_STRING)
            return null;

        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, required);
        return text.TrimEnd('\0');
    }

    private static DateTimeOffset? GetDevicePropertyFileTime(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var buffer = new byte[8];
        if (!SetupDiGetDeviceProperty(
                deviceInfoSet, ref data, ref key, out var propertyType, buffer, buffer.Length, out _, 0)
            || propertyType != DEVPROP_TYPE_FILETIME)
            return null;

        try
        {
            var fileTime = BitConverter.ToInt64(buffer, 0);
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const int DEVPROP_TYPE_STRING = 0x00000012;
    private const int DEVPROP_TYPE_FILETIME = 0x00000010;

    private const uint DICS_FLAG_GLOBAL = 0x00000001;
    private const uint DIREG_DRV = 0x00000001;
    private const uint KEY_READ = 0x00020019;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, char[] deviceInstanceId, int deviceInstanceIdSize,
        out int requiredSize);

    // CharSet.Unicode is required: the default (Ansi) resolves to the "A" entry point
    // (setupapi.dll exports both SetupDiGetDeviceRegistryPropertyA and ...W), which hands back
    // single-byte text that GetStringProperty below would decode two bytes at a time as UTF-16, turning
    // Latin letters into CJK garbage. The W entry point matches GetStringProperty's UTF-16 decode and
    // SetupDiGetDeviceProperty's Unicode declaration below.
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, int property, out uint propertyRegDataType,
        byte[] propertyBuffer, int propertyBufferSize, out int requiredSize);

    // setupapi.dll exports only the Unicode SetupDiGetDevicePropertyW, never a plain or ANSI
    // entry point (the device property API postdates SetupDiGetDeviceRegistryProperty above and was
    // never given one). Without CharSet.Unicode here the runtime looked for the exact, nonexistent
    // "SetupDiGetDeviceProperty" symbol and threw EntryPointNotFoundException on every single call,
    // which crashed device gathering entirely: every device (present or not) still needed this for its
    // class name, so the whole driver cleanup category always ended up with nothing to show.
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey,
        out int propertyType, byte[] propertyBuffer, int propertyBufferSize, out int requiredSize, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint devInst, string deviceId, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupGetInfDriverStoreLocation(
        string fileName, IntPtr altPlatformInfo, string? locale, char[] returnBuffer, int returnBufferSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint scope, uint hwProfile, uint keyType,
        uint samDesired);

    // Every "out" pointer this class does not need is declared IntPtr so IntPtr.Zero (a real null) can be
    // passed for it; only the one FILETIME this call actually wants is a real out parameter.
    [DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(
        IntPtr hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved, IntPtr lpcSubKeys,
        IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
