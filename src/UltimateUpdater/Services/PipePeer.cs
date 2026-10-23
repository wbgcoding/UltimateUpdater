using System.IO.Pipes;

namespace UltimateUpdater.Services;

/// <summary>
/// Who is on the other end of a worker pipe. Both elevated workers (install, cleanup) authenticate their
/// server the same way, so the check lives in one place rather than being copied per worker.
/// Pipe names are enumerable by any process running as this user, and when the app gives up on a name it
/// becomes free again while the elevated worker is still connecting: so "I connected to the name I was
/// given" proves nothing on its own.
/// </summary>
internal static class PipePeer
{
    // DllImport rather than LibraryImport: the source generator would require unsafe code to be enabled
    // for the whole assembly, which is a poor trade for one call.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    /// <summary>True when the pipe is served by the process that started this worker, and that process is
    /// this same program.</summary>
    public static bool IsExpectedServer(NamedPipeClientStream pipe, int expectedServerProcessId)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverProcessId)
                || serverProcessId != (uint)expectedServerProcessId)
                return false;

            // The id alone is not enough: if the app died, Windows can hand its number to the next
            // process, and nothing here holds a handle that would keep it reserved. The server must
            // also BE this same program.
            using var server = System.Diagnostics.Process.GetProcessById((int)serverProcessId);
            return string.Equals(server.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                      or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
