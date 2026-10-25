using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>
/// The full image path of another process. <c>Process.MainModule</c> reads the module list, which throws
/// for a process that is still starting up, an elevated one, or one of the other bitness; this asks the
/// kernel with only limited query rights instead, which works in all of those cases.
/// </summary>
internal static class ProcessImage
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The drive-letter path of the process image, or null when the process is gone or cannot be opened.</summary>
    public static string? TryGetPath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
            return null;

        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder exeName, ref int size);
}
