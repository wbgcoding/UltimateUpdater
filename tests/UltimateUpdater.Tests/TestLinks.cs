using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Tests;

/// <summary>
/// The one place the suite creates directory junctions and hard links. It used to be sixteen private
/// copies that each started <c>cmd /c mklink</c>: one hidden console process per link, several hundred
/// per full run. This does the same work with two system calls and no child process, and the link it
/// writes is the same reparse record <c>mklink /J</c> writes (a dangling target is accepted too, like
/// mklink does).
/// </summary>
internal static class TestLinks
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    /// <summary>Creates the directory <paramref name="link"/> as a junction to <paramref name="target"/>.</summary>
    internal static void CreateJunction(string link, string target)
    {
        var fullLink = Path.GetFullPath(link);
        var fullTarget = Path.GetFullPath(target).TrimEnd('\\');

        if (Directory.Exists(fullLink) || File.Exists(fullLink))
            throw new IOException($"Cannot create a junction, the path already exists: {fullLink}");

        Directory.CreateDirectory(fullLink);
        try
        {
            using var handle = CreateFileW(fullLink, GenericWrite, FileShareAll, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {fullLink} to make it a junction");

            var buffer = BuildMountPointBuffer(@"\??\" + fullTarget, fullTarget);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot make {fullLink} a junction to {fullTarget}");
        }
        catch
        {
            try { Directory.Delete(fullLink); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Creates <paramref name="link"/> as a second name for the existing file <paramref name="existing"/>.</summary>
    internal static void CreateHardLink(string link, string existing)
    {
        if (!CreateHardLinkW(Path.GetFullPath(link), Path.GetFullPath(existing), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot hard-link {link} to {existing}");
    }

    private static byte[] BuildMountPointBuffer(string substituteName, string printName)
    {
        var substitute = Encoding.Unicode.GetBytes(substituteName);
        var print = Encoding.Unicode.GetBytes(printName);

        // header (tag, data length, reserved) + four name offsets/lengths + both names, each NUL-terminated
        var pathBytes = substitute.Length + 2 + print.Length + 2;
        var dataLength = 8 + pathBytes;
        var buffer = new byte[8 + dataLength];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        return buffer;
    }
}
