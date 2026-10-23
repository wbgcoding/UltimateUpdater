using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltimateUpdater.Services;

/// <summary>Mutex + named-pipe focus signal. The main window always
/// runs unelevated, so the pipe carries no privileged data.</summary>
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

    private static string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value ?? "unknown";

    private static string PipeName => _pipeName ??= BuildPipeName(
        System.Diagnostics.Process.GetCurrentProcess().SessionId,
        CurrentUserSid);

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
    /// True when this process is the first instance.
    /// The mutex is deliberately NOT owned: all that matters is whether the named object already
    /// exists, and an open handle is enough for that. Owning it meant calling ReleaseMutex on the way
    /// out, which is the one thing in.NET that throws ApplicationException; from the wrong thread,
    /// or after ownership is already gone, it takes the process down during shutdown and buries
    /// whatever really happened. Nothing here needs ownership, so nothing here asks for it.
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
            // Another account in this session created the name with an ACL that locks this user out, or as
            // an object of another type (an event). Refusing to start would hand that account a way to keep
            // the app from running at all, and the focus pipe that this guard supports is per user anyway,
            // so the app starts as a first instance.
            logging?.Warn(SquattedWarning);
            return true;
        }

        if (!createdNew)
        {
            // The name exists. Only a mutex this user (or this token's default owner) created means another
            // instance; a name that grants everyone access but belongs to someone else would otherwise make
            // every launch exit as "already running" while nothing answers on the pipe.
            var ownerSid = (ReadMutexOwnerForTest.Value ?? ReadMutexOwnerSid)(BuildMutexName(CurrentUserSid));
            mutex.Dispose();

            if (IsOwnInstanceOwner(ownerSid))
                return false;

            logging?.Warn(SquattedWarning);
            return true;
        }

        // held open for the lifetime of the process; Windows drops the handle when it exits
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
    /// Asks the running instance to show itself. Returns false when it could not be reached, so the
    /// caller can say something instead of exiting silently; from the user's side, a launch that
    /// produces no window looks like the app failed to start.
    /// </summary>
    public static Task<bool> NotifyExistingInstanceAsync() => NotifyExistingInstanceAsync(PipeName);

    internal static async Task<bool> NotifyExistingInstanceAsync(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, Options);
            // generous: the other instance may be busy scanning when the request arrives
            await client.ConnectAsync(3000);

            await using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync(FocusRequest);

            // A write that succeeds only proves that SOMETHING owns the name. Any process running as
            // this user can create this pipe at logon and accept every request without doing anything,
            // and every launch (including the autostart one) would then quietly exit with no window:
            // the machine would silently stop being scanned, which is the whole purpose of the tool.
            // Only the real instance knows to answer, so an answer is what decides it.
            using var timeout = new CancellationTokenSource(3000);
            return await reader.ReadLineAsync(timeout.Token) == FocusAck;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Runs for the app's lifetime; each connection is one focus request.</summary>
    public static void StartListening(Action onFocusRequested) =>
        StartListening(onFocusRequested, PipeName, CancellationToken.None);

    internal static void StartListening(Action onFocusRequested, string pipeName, CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, Options);
                    await server.WaitForConnectionAsync(stop);

                    await using var writer = new StreamWriter(server) { AutoFlush = true };
                    using var reader = new StreamReader(server, leaveOpen: true);

                    if (await ReadRequestAsync(reader, MaxRequestChars, RequestTimeout) == FocusRequest)
                    {
                        // answered before the window is raised: raising it blocks on the dispatcher,
                        // and the waiting instance must not sit through a scan before it hears back
                        await writer.WriteLineAsync(FocusAck);
                        onFocusRequested();
                    }
                }
                catch (Exception)
                {
                    // Must not escape: an unhandled exception here ends the loop for good, and from then
                    // on every launch tells the user the app is already running instead of raising its
                    // window. Re-creating the pipe instance while the previous client handle is still
                    // open raises UnauthorizedAccessException, which is the case this guards against.
                }

                // Also stops a failure that repeats immediately from spinning a core.
                await Task.Delay(250);
            }
        });
    }
}
