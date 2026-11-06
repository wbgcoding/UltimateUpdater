using System.Runtime.InteropServices;

namespace UltimateUpdater.Services;

/// <summary>
/// An installer that has done nothing measurable for the whole stall window: no CPU, no I/O, no process
/// started or ended anywhere below the tool. Thrown by <see cref="ProcessRunner"/> after the tool's tree is
/// stopped, so the row can say what happened instead of waiting out the full install timeout.
/// </summary>
public sealed class InstallerStalledException(string tool)
    : Exception($"{tool}: the installer stopped responding and was stopped.")
{
}

/// <summary>How a running installer is judged to be hung. <paramref name="CpuFraction"/> is the share of ONE
/// core the whole process tree must stay below, <paramref name="Window"/> how long it must do so without any
/// I/O either, <paramref name="SampleInterval"/> how often the tree is looked at.</summary>
public sealed record StallWatchOptions(TimeSpan Window, double CpuFraction, TimeSpan SampleInterval)
{
    public static StallWatchOptions Standard { get; } =
        new(TimeSpan.FromMinutes(5), 0.01, TimeSpan.FromSeconds(5));

    /// <summary>Test seam: a short window so a test does not wait five minutes.</summary>
    internal static readonly AsyncLocal<StallWatchOptions?> OverrideForTest = new();

    /// <summary>The options every install call passes along.</summary>
    public static StallWatchOptions ForInstall => OverrideForTest.Value ?? Standard;
}

/// <summary>One reading of a whole process tree. <paramref name="Complete"/> is false when some process in
/// it could not be read, in which case nothing can be concluded from the numbers.</summary>
public readonly record struct TreeActivity(TimeSpan Cpu, ulong IoBytes, int ProcessCount, bool Complete);

/// <summary>
/// The decision, apart from the clock and the system: fed one <see cref="TreeActivity"/> per sample, it says
/// whether the tree has been idle for the whole window. Any I/O byte, any change in the number of processes
/// or a CPU share of the configured size counts as life and restarts the window; a reading that is not
/// complete counts as life too, since a tree that cannot be seen is never declared dead.
/// </summary>
public sealed class StallDetector(StallWatchOptions options)
{
    private bool _hasPrevious;
    private long _previousAtMs;
    private TreeActivity _previous;
    private long _lastLifeAtMs;

    /// <summary>True once the tree has shown no life for <see cref="StallWatchOptions.Window"/>.</summary>
    public bool Observe(long nowMs, TreeActivity activity)
    {
        if (!_hasPrevious)
        {
            _hasPrevious = true;
            _previous = activity;
            _previousAtMs = nowMs;
            _lastLifeAtMs = nowMs;
            return false;
        }

        var elapsedMs = nowMs - _previousAtMs;
        var cpuMs = (activity.Cpu - _previous.Cpu).TotalMilliseconds;

        var alive = !activity.Complete
            || !_previous.Complete
            || activity.IoBytes != _previous.IoBytes
            || activity.ProcessCount != _previous.ProcessCount
            || (elapsedMs > 0 && cpuMs / elapsedMs >= options.CpuFraction);

        _previous = activity;
        _previousAtMs = nowMs;
        if (alive)
            _lastLifeAtMs = nowMs;

        return nowMs - _lastLifeAtMs >= options.Window.TotalMilliseconds;
    }
}

/// <summary>Reads CPU time and I/O of a process and everything below it.</summary>
internal static class ProcessTreeSampler
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint Th32csSnapProcess = 0x00000002;

    public static TreeActivity Sample(int rootId)
    {
        var children = ReadParentMap();
        if (children is null)
            return new TreeActivity(TimeSpan.Zero, 0, 0, Complete: false);

        var cpu = TimeSpan.Zero;
        ulong io = 0;
        var count = 0;
        var complete = true;

        var queue = new Queue<(int Id, long ParentStart)>();
        queue.Enqueue((rootId, 0));
        var seen = new HashSet<int> { rootId };

        while (queue.Count > 0)
        {
            var (id, parentStart) = queue.Dequeue();
            if (!TryRead(id, out var start, out var processCpu, out var processIo))
            {
                // The root being unreadable means nothing is known; a child that vanished in between is just
                // gone, but one that cannot be opened is a process this side is not allowed to look at.
                if (id == rootId || Exists(id))
                    complete = false;
                continue;
            }

            // A child cannot be older than its parent: an older one only carries a parent id that was reused.
            if (parentStart != 0 && start < parentStart)
                continue;

            count++;
            cpu += processCpu;
            io += processIo;

            if (!children.TryGetValue(id, out var ids))
                continue;

            foreach (var child in ids)
            {
                if (seen.Add(child))
                    queue.Enqueue((child, start));
            }
        }

        return new TreeActivity(cpu, io, count, complete);
    }

    private static bool Exists(int id)
    {
        var handle = OpenProcess(0x1000, false, (uint)id);
        if (handle == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == 5;

        CloseHandle(handle);
        return true;
    }

    private static bool TryRead(int id, out long start, out TimeSpan cpu, out ulong io)
    {
        start = 0;
        cpu = TimeSpan.Zero;
        io = 0;

        var handle = OpenProcess(ProcessQueryInformation, false, (uint)id);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out var kernel, out var user)
                || !GetProcessIoCounters(handle, out var counters))
                return false;

            start = created;
            cpu = TimeSpan.FromTicks(kernel + user);
            io = counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount;
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static Dictionary<int, List<int>>? ReadParentMap()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1))
            return null;

        try
        {
            var map = new Dictionary<int, List<int>>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                var parent = (int)entry.ParentProcessId;
                if (!map.TryGetValue(parent, out var list))
                    map[parent] = list = [];
                list.Add((int)entry.ProcessId);
            }

            return map;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
}
