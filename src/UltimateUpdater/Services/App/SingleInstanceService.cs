using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltimateUpdater.Services.App;

/// <summary>Mutex and named-pipe focus signal. The main window always runs unelevated, so the pipe
/// carries no privileged data.</summary>
public static class SingleInstanceService
{
    private const string MutexBaseName = "Local\\UltimateUpdater-SingleInstance-Mutex";
    private const string PipeBaseName = "UltimateUpdater-SingleInstance-Pipe";
    private const string FocusRequest = "focus";
    private const string FocusAck = "focused";

    /// <summary>The only request is the word "focus"; anything longer is not from this app.</summary>
    internal const int MaxRequestChars = 64;

    /// <summary>How long a connected client gets to send its line before the server drops it, so one idle
    /// connection cannot hold the single listener hostage.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    // Current user only, on both ends: the pipe name is enumerable by every local account, and the name
    // itself carries the session and the user so two sessions or two accounts never share one.
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private static Mutex? _mutex;
    private static string? _pipeName;

    /// <summary>The pipe name for one logon session and one user. The mutex is Local (per session), so the
    /// pipe has to be as well, or a second session's launch would find the first session's pipe.</summary>
    internal static string BuildPipeName(int sessionId, string userSid)
    {
        var safeSid = new string([.. userSid.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')]);
        return $"{PipeBaseName}-{sessionId}-{safeSid}";
    }

    /// <summary>The mutex name for one user. "Local" already separates logon sessions; the SID keeps another
    /// account in the same session (run as, a shared desktop) from holding the name and so locking this
    /// user out of starting the app.</summary>
    internal static string BuildMutexName(string userSid) =>
        $"{MutexBaseName}-{new string([.. userSid.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')])}";

    private static string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "unknown";
        }
    }

    private static int CurrentSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.SessionId;
    }

    private static string PipeName => _pipeName ??= BuildPipeName(CurrentSessionId(), CurrentUserSid);

    /// <summary>
    /// Reads the request line a client sent, bounded in time and length. Null when the client was silent
    /// for <paramref name="timeout"/>, sent more than <paramref name="maxChars"/> characters without ending
    /// the line, or sent nothing at all.
    /// </summary>
    internal static async Task<string?> ReadRequestAsync(TextReader reader, int maxChars, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var read = ReadBoundedLineAsync(reader, maxChars, cts.Token);

        try
        {
            // WaitAsync is the backstop for a reader that ignores its token.
            return await read.WaitAsync(timeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The abandoned read ends with the connection; its failure must not surface as unobserved.
            _ = read.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var one = new char[1];

        while (true)
        {
            if (await reader.ReadAsync(one.AsMemory(), cancellationToken) == 0)
                return line.Length > 0 ? line.ToString() : null;

            if (one[0] == '\n')
                return line.ToString();

            if (one[0] == '\r')
                continue;

            if (line.Length >= maxChars)
                return null;

            line.Append(one[0]);
        }
    }

    /// <summary>
    /// True when this process is the first instance. The mutex is deliberately not owned: all that matters is
    /// whether the named object already exists, and an open handle is enough. Owning it would require
    /// ReleaseMutex on the way out, which throws ApplicationException from the wrong thread or after
    /// ownership is gone, taking the process down during shutdown.
    /// </summary>
    public static bool TryAcquire(LoggingService? logging = null)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            (mutex, createdNew) = (CreateMutexForTest.Value ?? CreateMutex)(BuildMutexName(CurrentUserSid));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Another account in this session created the name with an ACL that locks this user out, or as an object
            // of another type (an event). Refusing to start would let that account keep the app from running at all,
            // and the focus pipe this guard supports is per user anyway, so the app starts as a first instance.
            logging?.Warn(SquattedWarning);
            return true;
        }

        if (!createdNew)
        {
            // The name exists. Only a mutex this user (or this token's default owner) created means another
            // instance; a name that grants everyone access but belongs to someone else would make every launch exit
            // as "already running" while nothing answers on the pipe.
            var ownerSid = (ReadMutexOwnerForTest.Value ?? ReadMutexOwnerSid)(BuildMutexName(CurrentUserSid));
            mutex.Dispose();

            if (IsOwnInstanceOwner(ownerSid))
                return false;

            logging?.Warn(SquattedWarning);
            return true;
        }

        // Held open for the lifetime of the process; Windows drops the handle when it exits.
        _mutex = mutex;
        return true;
    }

    private const string SquattedWarning =
        "Single-instance mutex is held by another account; starting without the single-instance guard.";

    /// <summary>True only for a known owner that is this user, or the token's default owner (Administrators
    /// on a full admin token, where objects the user creates are owned by the group).</summary>
    internal static bool IsOwnInstanceOwner(string? ownerSid)
    {
        if (string.IsNullOrEmpty(ownerSid))
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return string.Equals(ownerSid, identity.User?.Value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ownerSid, identity.Owner?.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Owner SID of the named mutex, read through a second handle: the handle the .NET Mutex holds
    /// has no READ_CONTROL right, so asking it for security info always fails. Null on any failure, which
    /// the caller treats as foreign.</summary>
    internal static string? ReadMutexOwnerSid(string name)
    {
        const uint ReadControl = 0x00020000;
        const int SeKernelObject = 6;
        const uint OwnerSecurityInformation = 1;

        var handle = OpenMutexW(ReadControl, false, name);
        if (handle == IntPtr.Zero)
            return null;

        var descriptor = IntPtr.Zero;
        try
        {
            if (GetSecurityInfo(handle, SeKernelObject, OwnerSecurityInformation, out var ownerSid,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out descriptor) != 0 || ownerSid == IntPtr.Zero)
                return null;

            return new SecurityIdentifier(ownerSid).Value;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
                LocalFree(descriptor);
            CloseHandle(handle);
        }
    }

    // DllImport rather than LibraryImport: the source generator would require unsafe code to be enabled.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenMutexW(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
        out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    /// <summary>Test seam: replaces the owner read, which cannot see an unnamed test mutex.</summary>
    internal static readonly AsyncLocal<Func<string, string?>?> ReadMutexOwnerForTest = new();

    private static (Mutex Mutex, bool CreatedNew) CreateMutex(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        return (mutex, createdNew);
    }

    /// <summary>Test seam: replaces the mutex creation, so a squatted name can be simulated.</summary>
    internal static readonly AsyncLocal<Func<string, (Mutex Mutex, bool CreatedNew)>?> CreateMutexForTest = new();

    public static void Release()
    {
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>
    /// Asks the running instance to show itself. Returns false when it could not be reached, so the caller
    /// can say something: a launch that produces no window looks like the app failed to start.
    /// </summary>
    public static Task<bool> NotifyExistingInstanceAsync() => NotifyExistingInstanceAsync(PipeName);

    internal static async Task<bool> NotifyExistingInstanceAsync(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, Options);
            // Generous: the other instance may be busy scanning when the request arrives.
            await client.ConnectAsync(3000);

            await using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync(FocusRequest);

            // A successful write only proves that something owns the name. Any process running as this user could
            // create this pipe at logon and accept every request without doing anything, and every launch (the
            // autostart one included) would then exit with no window, so the machine would silently stop being
            // scanned. Only the real instance knows to answer, so an answer is what decides it.
            using var timeout = new CancellationTokenSource(3000);
            return await reader.ReadLineAsync(timeout.Token) == FocusAck;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The wait before the listener makes a new pipe instance, and the first wait after a failure.</summary>
    internal static readonly TimeSpan ListenerRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest wait between two failed attempts to create the pipe.</summary>
    internal static readonly TimeSpan ListenerMaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>The wait after <paramref name="consecutiveFailures"/> failed attempts in a row: the base delay
    /// for none or one, then doubling up to <see cref="ListenerMaxRetryDelay"/>.</summary>
    internal static TimeSpan NextRetryDelay(int consecutiveFailures, TimeSpan baseDelay)
    {
        if (consecutiveFailures <= 1)
            return baseDelay;

        var doublings = Math.Min(consecutiveFailures - 1, 20);
        var delay = TimeSpan.FromTicks(baseDelay.Ticks * (1L << doublings));
        return delay > ListenerMaxRetryDelay ? ListenerMaxRetryDelay : delay;
    }

    /// <summary>Runs for the app's lifetime; each connection is one focus request.</summary>
    public static void StartListening(Action onFocusRequested, LoggingService? logging = null) =>
        StartListening(onFocusRequested, PipeName, CancellationToken.None, logging);

    internal static void StartListening(
        Action onFocusRequested, string pipeName, CancellationToken stop, LoggingService? logging = null,
        TimeSpan? retryDelay = null)
    {
        var baseDelay = retryDelay ?? ListenerRetryDelay;

        _ = Task.Run(async () =>
        {
            var failures = 0;

            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, Options);
                }
                catch (Exception ex)
                {
                    // The name is taken (a stale or foreign server of this user) or the system refuses a pipe. Said
                    // once per streak, and each further attempt waits longer, so it neither floods the log nor
                    // burns a first-chance exception four times a second for the whole run.
                    if (failures++ == 0)
                        logging?.Warn($"The single-instance listener could not create its pipe ({ex.GetType().Name}: {ex.Message}); retrying with a growing delay.");
                }

                if (server is not null)
                {
                    if (failures > 0)
                    {
                        logging?.Info($"The single-instance listener is running again after {failures} failed attempts.");
                        failures = 0;
                    }

                    try
                    {
                        await using (server)
                        {
                            await server.WaitForConnectionAsync(stop);

                            await using var writer = new StreamWriter(server) { AutoFlush = true };
                            using var reader = new StreamReader(server, leaveOpen: true);

                            if (await ReadRequestAsync(reader, MaxRequestChars, RequestTimeout) == FocusRequest)
                            {
                                // Answered before the window is raised: raising it blocks on the dispatcher, and the waiting
                                // instance must not sit through a scan before it hears back.
                                await writer.WriteLineAsync(FocusAck);
                                onFocusRequested();
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // Must not escape: an unhandled exception here ends the loop for good, and every later launch would
                        // report the app as already running instead of raising its window. A client that vanished
                        // mid-request is routine, so it is not logged.
                    }
                }

                // Also stops a failure that repeats immediately from spinning a core.
                try
                {
                    await Task.Delay(NextRetryDelay(failures, baseDelay), stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }
}
