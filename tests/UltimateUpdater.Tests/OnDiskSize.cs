using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Tests;

/// <summary>The space a file occupies on disk, which is what a clean reports as freed (a small file takes
/// a whole cluster, a compressed one less than its length).</summary>
internal static class OnDiskSize
{
    private const int FileStandardInfoClass = 1;

    public static long Of(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandleEx(handle, FileStandardInfoClass, out var info, (uint)Marshal.SizeOf<StandardInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        return info.AllocationSize;
    }

    public static long Of(IEnumerable<string> paths) => paths.Sum(Of);

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
        public ushort Padding;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int informationClass, out StandardInfo info, uint bufferSize);
}
