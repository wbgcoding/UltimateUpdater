using System.Runtime.InteropServices;

namespace UltimateUpdater.Services;

/// <summary>The two thread-mode calls, behind a seam so tests can watch which thread they run on.</summary>
internal interface IThreadModeNative
{
    /// <summary>Puts the calling thread into background processing mode. False when refused, for
    /// example because the thread is already in it.</summary>
    bool Begin();

    /// <summary>Leaves background processing mode on the calling thread.</summary>
    void End();
}

/// <summary>
/// Runs a synchronous stretch of work in Windows background processing mode, which lowers the calling
/// thread's CPU, disk and memory priority so a long disk walk does not make the machine sluggish. The
/// mode belongs to the thread, not to the task, so the scope must end on the thread that began it:
/// a thread pool thread left in background mode would slow whatever work it picks up next.
/// Only wrap code with no await inside; an await can resume on another thread.
/// </summary>
internal static class BackgroundWork
{
    /// <summary>Test seam: replaces the real thread-mode calls for the current async flow.</summary>
    internal static readonly AsyncLocal<IThreadModeNative?> NativeForTest = new();

    public static IDisposable Enter() => Enter(NativeForTest.Value ?? Win32Native.Instance);

    internal static IDisposable Enter(IThreadModeNative native) => new Scope(native);

    private sealed class Scope : IDisposable
    {
        private readonly IThreadModeNative _native;
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private readonly bool _began;
        private bool _disposed;

        public Scope(IThreadModeNative native)
        {
            _native = native;
            _began = native.Begin();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Ending on a different thread would leave the beginning thread lowered and try to end a mode
            // the current one never entered, so this is refused and nothing is touched.
            if (Environment.CurrentManagedThreadId != _threadId)
                throw new InvalidOperationException("A background work scope must end on the thread that began it.");

            _disposed = true;
            if (_began)
                _native.End();
        }
    }

    private sealed class Win32Native : IThreadModeNative
    {
        public static readonly Win32Native Instance = new();

        private const uint ThreadModeBackgroundBegin = 0x00010000;
        private const uint ThreadModeBackgroundEnd = 0x00020000;

        public bool Begin() => SetThreadPriority(GetCurrentThread(), unchecked((int)ThreadModeBackgroundBegin));

        public void End() => SetThreadPriority(GetCurrentThread(), unchecked((int)ThreadModeBackgroundEnd));

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);
    }
}
