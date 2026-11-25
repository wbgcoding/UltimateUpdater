using System.IO;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Services.App;

/// <summary>
/// Writes a small minidump of this process when it dies of an unhandled exception, into the folder that also
/// holds the logs, and keeps only the newest few. A dump holds a copy of some process memory, so it stays on
/// this PC: nothing uploads it, and "delete my data" removes it with the rest of the local folder.
/// Never throws: this runs while the app is already crashing.
/// </summary>
internal static class CrashDumpWriter
{
    /// <summary>How many dumps are kept; older ones are deleted after each new one.</summary>
    internal const int Keep = 3;

    internal const string Prefix = "crash-";
    internal const string Extension = ".dmp";

    // MiniDumpWithDataSegs | MiniDumpWithThreadInfo: the modules' data and the thread list, without the full
    // heap, which keeps the file small.
    private const int DumpType = 0x00000001 | 0x00001000;

    /// <summary>Writes the dump; returns its path, or null when it could not be written.</summary>
    internal static string? TryWrite(string directory, DateTime? now = null)
    {
        string? path = null;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{Prefix}{now ?? DateTime.Now:yyyyMMdd-HHmmss}{Extension}");

            bool written;
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                written = MiniDumpWriteDump(
                    GetCurrentProcess(), (uint)Environment.ProcessId, stream.SafeFileHandle.DangerousGetHandle(),
                    DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }

            if (!written)
            {
                TryDelete(path);
                return null;
            }

            Prune(directory, Keep);
            return path;
        }
        catch (Exception)
        {
            if (path is not null)
                TryDelete(path);

            return null;
        }
    }

    /// <summary>Deletes every dump in <paramref name="directory"/> but the newest <paramref name="keep"/>. A
    /// link is never touched, and a file that cannot be deleted is skipped.</summary>
    internal static void Prune(string directory, int keep)
    {
        try
        {
            var dumps = new DirectoryInfo(directory)
                .EnumerateFiles(Prefix + "*" + Extension)
                .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(keep)
                .ToList();

            foreach (var dump in dumps)
                TryDelete(dump.FullName);
        }
        catch (Exception)
        {
            // an unreadable folder keeps its dumps; the crash report goes on
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr process, uint processId, IntPtr file, int dumpType, IntPtr exception, IntPtr userStream, IntPtr callback);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();
}
