using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// winget starts installers outside the job its own launch sits in, so terminating that job left a timed-out
/// Git setup spinning at full CPU after its winget was gone, and the retry started a second one beside it.
/// The grandchild here breaks away the same way: created under the child, with the breakaway flag.
/// </summary>
public class SuspendedChildTreeKillTests
{
    [Fact]
    public void Kill_also_ends_a_grandchild_that_broke_away_from_the_job()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var info = new ProcessStartInfo
        {
            FileName = cmd,
            WorkingDirectory = Environment.SystemDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("ping -n 60 127.0.0.1 >nul");

        string image;
        using (var handle = File.OpenHandle(cmd))
            image = SuspendedChildProcess.PlainFinalPath(handle)!;

        using var child = SuspendedChildProcess.Start(info, image, expectedPackage: null);
        using var grandchild = StartBrokenAwayChildOf(child.ProcessId);
        try
        {
            Assert.False(grandchild.HasExited, "the stand-in installer did not start");

            child.Kill();

            Assert.True(grandchild.WaitForExit(TimeSpan.FromSeconds(10)), "the broken-away grandchild survived the kill");
        }
        finally
        {
            if (!grandchild.HasExited)
                grandchild.Kill();
        }
    }

    private const uint ProcessCreateProcess = 0x0080;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private static readonly IntPtr ProcThreadAttributeParentProcess = 0x00020000;

    private static Process StartBrokenAwayChildOf(int parentId)
    {
        var parent = OpenProcess(ProcessCreateProcess, false, parentId);
        if (parent == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var size = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var parentSlot = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            Marshal.WriteIntPtr(parentSlot, parent);
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeParentProcess, parentSlot, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            var commandLine = new StringBuilder($"\"{Path.Combine(Environment.SystemDirectory, "PING.EXE")}\" -n 60 127.0.0.1");

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateBreakawayFromJob | CreateNoWindow,
                    IntPtr.Zero, null, ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            CloseHandle(created.Thread);
            CloseHandle(created.Process);
            return Process.GetProcessById(created.ProcessId);
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(parentSlot);
            CloseHandle(parent);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Pointer, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
