using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>
/// Reads and verifies the app-execution alias of winget (<c>%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe</c>).
/// The alias is a reparse point the user can write, so "it cannot be opened" proves nothing: anyone running
/// as this user can create an APPEXECLINK reparse point with any target. What the elevated launch needs is
/// the link's CONTENT: the App Installer package family and a target inside the package store. Reading it
/// through a held handle does NOT freeze it: rewriting a reparse point needs only write-attributes access,
/// which share modes do not block, so the content can change after the check. The launch therefore never
/// trusts the alias by name; it compares the image the system actually mapped with the verified target
/// (see <see cref="SuspendedChildProcess"/>).
/// </summary>
internal static class AppExecutionAlias
{
    internal const uint AppExecLinkTag = 0x8000001B;

    internal const string WingetPackageFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    /// <summary>The application id the alias starts; the suspended launch requires the same identity on the
    /// process token, so the link check and the launch check share this one definition.</summary>
    internal const string WingetAppUserModelId = WingetPackageFamily + "!winget";

    private const string PackageDirectoryPrefix = "Microsoft.DesktopAppInstaller_";
    private const string PackageDirectorySuffix = "_8wekyb3d8bbwe";
    private const string WingetFileName = "winget.exe";

    private const uint FsctlGetReparsePoint = 0x900A8;
    private const int ReparseHeaderSize = 8;
    private const int MaxReparseBytes = 16 * 1024;

    // GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS.
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint OpenReparseAndBackup = 0x00200000 | 0x02000000;

    /// <summary>What an APPEXECLINK reparse point says it starts.</summary>
    internal sealed record Link(string PackageFamily, string AppUserModelId, string Target);

    /// <summary>
    /// Parses a complete REPARSE_DATA_BUFFER (8-byte header: tag, data length, reserved) of an APPEXECLINK
    /// point. Version 3 data is four NUL-terminated UTF-16 strings: package family, AUMID, target path and
    /// an app type; the first three are required, the rest is ignored. Anything else is rejected.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> buffer, out Link? link)
    {
        link = null;

        if (buffer.Length < ReparseHeaderSize + 4)
            return false;

        if (BitConverter.ToUInt32(buffer[..4]) != AppExecLinkTag)
            return false;

        var dataLength = BitConverter.ToUInt16(buffer.Slice(4, 2));
        if (dataLength < 4 || ReparseHeaderSize + dataLength > buffer.Length)
            return false;

        var data = buffer.Slice(ReparseHeaderSize, dataLength);
        if (BitConverter.ToUInt32(data[..4]) != 3)
            return false;

        var strings = new List<string>(4);
        var rest = data[4..];
        while (rest.Length >= 2 && strings.Count < 3)
        {
            var end = -1;
            for (var i = 0; i + 1 < rest.Length; i += 2)
            {
                if (rest[i] == 0 && rest[i + 1] == 0)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
                return false;

            strings.Add(Encoding.Unicode.GetString(rest[..end]));
            rest = rest[(end + 2)..];
        }

        if (strings.Count < 3 || strings.Any(string.IsNullOrWhiteSpace))
            return false;

        link = new Link(strings[0], strings[1], strings[2]);
        return true;
    }

    /// <summary>
    /// True when the link is winget of the App Installer package: the exact package family, an AUMID that
    /// is that family plus "!winget", and a target that is <c>winget.exe</c> directly inside a
    /// <c>Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe</c> folder that is a direct child of one of the
    /// package store roots. The path is normalised first, so a <c>..</c> segment cannot step outside.
    /// </summary>
    internal static bool IsWingetLink(Link link, IEnumerable<string> packageRoots)
    {
        if (!string.Equals(link.PackageFamily, WingetPackageFamily, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(link.AppUserModelId, WingetAppUserModelId, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            // Only a plain drive path: no UNC share, no \\?\ or \\.\ device prefix.
            if (link.Target.Length < 3 || !char.IsAsciiLetter(link.Target[0]) || link.Target[1] != ':' || link.Target[2] != '\\')
                return false;

            var full = Path.GetFullPath(link.Target);
            if (!string.Equals(Path.GetFileName(full), WingetFileName, StringComparison.OrdinalIgnoreCase))
                return false;

            var packageDirectory = Path.GetDirectoryName(full);
            var packageName = Path.GetFileName(packageDirectory);
            if (string.IsNullOrEmpty(packageName)
                || !packageName.StartsWith(PackageDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                || !packageName.EndsWith(PackageDirectorySuffix, StringComparison.OrdinalIgnoreCase))
                return false;

            var root = Path.GetDirectoryName(packageDirectory);
            return !string.IsNullOrEmpty(root)
                && packageRoots.Any(candidate => !string.IsNullOrWhiteSpace(candidate)
                    && string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)),
                        Path.TrimEndingDirectorySeparator(root),
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The package store roots: the default, the one Windows records when "new apps save to"
    /// moved it to another volume, and the store of every further package volume (HKLM, writable by
    /// administrators only).</summary>
    internal static IReadOnlyList<string> PackageRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? packageRoot = null;
        var volumeStores = new List<string?>();

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var appx = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx");
            packageRoot = appx?.GetValue("PackageRoot") as string;

            // Each volume subkey names its store in SISPath (RegistryKey.GetValue ignores the case).
            using var volumes = appx?.OpenSubKey("PackageVolumes");
            foreach (var name in volumes?.GetSubKeyNames() ?? [])
            {
                using var volume = volumes!.OpenSubKey(name);
                volumeStores.Add(volume?.GetValue("SISPath") as string);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The default root alone still serves the normal install.
        }

        return PackageRootsFrom(programFiles, packageRoot, volumeStores);
    }

    /// <summary>The roots from what the registry said: the Program Files default, the recorded
    /// PackageRoot and each volume's store, keeping only plain drive-letter paths (no UNC share, no device
    /// prefix, no relative or empty value).</summary>
    internal static IReadOnlyList<string> PackageRootsFrom(
        string? programFiles, string? packageRoot, IEnumerable<string?> volumeStores)
    {
        var roots = new List<string>();
        if (!string.IsNullOrEmpty(programFiles))
            roots.Add(Path.Combine(programFiles, "WindowsApps"));

        foreach (var candidate in new[] { packageRoot }.Concat(volumeStores))
        {
            if (candidate is { Length: >= 3 } && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':' && candidate[2] == '\\'
                && !roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                roots.Add(candidate);
        }

        return roots;
    }

    /// <summary>
    /// Opens the alias itself (not what it points to) and reads its reparse data through that same handle.
    /// The returned handle denies data writers and deleters until it is disposed. It does not stop a
    /// rewrite of the reparse point itself (that needs only write-attributes access), so what was read is a
    /// snapshot, not a guarantee about what a later launch resolves. Null when the file cannot be opened or
    /// read this way.
    /// </summary>
    internal static SafeFileHandle? TryOpen(string path, out byte[]? reparseBuffer)
    {
        reparseBuffer = null;

        var handle = CreateFileW(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, OpenReparseAndBackup, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var buffer = new byte[MaxReparseBytes];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero)
            || returned == 0)
        {
            handle.Dispose();
            return null;
        }

        reparseBuffer = buffer[..(int)returned];
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, IntPtr inBuffer, uint inBufferSize,
        byte[] outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
