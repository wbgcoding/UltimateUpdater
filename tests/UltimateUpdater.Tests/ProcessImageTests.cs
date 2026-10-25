using System.Runtime.InteropServices;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// A process that has not finished starting has no module list yet, so reading its path through
/// <c>Process.MainModule</c> throws. A suspended child stands in for that moment deterministically:
/// its loader never ran.
/// </summary>
public class ProcessImageTests
{
    [Fact]
    public void A_process_that_is_still_starting_is_recognised_as_running_from_the_windows_folder()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var info = StartSuspended(ping);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(info.ProcessId);

            Assert.Equal(ping, ProcessImage.TryGetPath(info.ProcessId), ignoreCase: true);
            Assert.True(MainViewModel.RunsFromWindowsFolder(process));
        }
        finally
        {
            TerminateProcess(info.Process, 1);
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
    }

    [Fact]
    public void A_process_that_is_gone_has_no_path()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        var pid = process.Id;
        process.WaitForExit();
        process.Dispose();

        Assert.Null(ProcessImage.TryGetPath(pid));
    }

    private const uint CreateSuspended = 0x4;
    private const uint CreateNoWindow = 0x08000000;

    private static ProcessInformation StartSuspended(string path)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(path, $"\"{path}\" -n 30 127.0.0.1", IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspended | CreateNoWindow, IntPtr.Zero, null, ref startup, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Pointer, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
