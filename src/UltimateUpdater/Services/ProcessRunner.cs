using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>An elevated launch was refused because the executable is not signed (see ProcessRunner).</summary>
public sealed class UntrustedToolException(string path, string? reason = null)
    : Exception($"Refusing to run an unsigned executable with administrator rights: {path}"
        + (string.IsNullOrEmpty(reason) ? string.Empty : $" ({reason})"))
{
    public string Path { get; } = path;
}

/// <summary>
/// A call given an idle timeout (see <see cref="ProcessRunner.RunAsync"/>) went that long without any
/// stdout/stderr activity at all; distinct from the ordinary hard-timeout result so a caller can tell a
/// hung tool apart from one that simply ran past the overall deadline: a source is judged by silence,
/// not total runtime.
/// </summary>
public sealed class ProcessIdleTimeoutException(TimeSpan idleTimeout)
    : Exception($"No output for {idleTimeout.TotalSeconds:0} seconds.")
{
    public TimeSpan IdleTimeout { get; } = idleTimeout;
}

/// <summary>
/// An executable resolved for a launch, together with the open handle its trust check was made against.
/// Keeping the two together is what lets the caller hold the checked file open until the process has
/// started: dispose it only after <see cref="Process.Start()"/>, never between the check and the launch.
/// <see cref="Pin"/> is null when the path itself was not pinned: an unelevated launch, which is not
/// trust-checked at all, or the winget app-execution alias, whose own handle is <see cref="AliasPin"/> and
/// whose verified target is held in <see cref="ShimTargetPin"/>. Pin stays null for the alias on purpose: a
/// pinned tool launches from its own folder, and the alias folder is user-writable.
/// </summary>
internal sealed class ResolvedExecutable(
    string path, FileStream? pin, FileStream? shimTargetPin = null, SafeFileHandle? aliasPin = null) : IDisposable
{
    public string Path { get; } = path;

    public FileStream? Pin { get; } = pin;

    /// <summary>The image a known shim starts (chocolatey's bin\choco.exe starts the real choco.exe, the winget
    /// alias starts the App Installer's winget.exe), pinned and trust-checked like the shim itself; null when
    /// the tool is not such a shim.</summary>
    public FileStream? ShimTargetPin { get; } = shimTargetPin;

    /// <summary>The open reparse-point handle of a verified app-execution alias; null for everything else.</summary>
    public SafeFileHandle? AliasPin { get; } = aliasPin;

    public void Dispose()
    {
        Pin?.Dispose();
        ShimTargetPin?.Dispose();
        AliasPin?.Dispose();
    }
}

/// <summary>
/// Launches external tools safely (SECURITY.md trust boundary): argument arrays only, never a
/// shell-interpolated command string: no shell, no window, explicit UTF-8, hard timeout, output
/// size cap. Callers treat everything returned here as hostile input.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Guards against a runaway tool filling memory; output beyond this is discarded.</summary>
    private const int MaxOutputChars = 10 * 1024 * 1024;

    /// <param name="environment">
    /// Extra environment variables for the child process. Use this to hand a value to a script instead of
    /// interpolating it into the command text: an interpolated value is an injection surface, an
    /// environment variable never is.
    /// </param>
    /// <param name="idleTimeout">
    /// When given, the call fails with <see cref="ProcessIdleTimeoutException"/> once this much time passes
    /// with NO stdout/stderr activity at all, even while <paramref name="timeout"/> has not elapsed; a tool
    /// that keeps producing output, however slowly, is never cut off by this. Null: <paramref name="timeout"/>
    /// alone decides.
    /// </param>
    /// <param name="outputEncoding">
    /// Overrides the UTF-8 decode every other caller gets. The one legitimate reason is
    /// <see cref="Encoding.Latin1"/>, which preserves a tool's raw bytes losslessly (1:1 byte-to-char) for a
    /// caller that decodes them itself, such as <see cref="Cleanup.OemTextRecovery"/>. Null keeps UTF-8.
    /// </param>
    public static Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? idleTimeout = null,
        Encoding? outputEncoding = null) =>
        ExecuteAsync(fileName, arguments, timeout, cancellationToken, environment, onOutput: null, idleTimeout, outputEncoding);

    /// <summary>
    /// Same safety contract as <see cref="RunAsync"/>, but stdout is streamed segment by segment to
    /// <paramref name="onOutput"/> as the tool prints it. Segments split on BOTH '\n' and '\r' because
    /// the package managers animate their progress line with a carriage return and never a newline;
    /// a line-based reader would see nothing until the download finished. The full output is still
    /// captured and returned exactly as <see cref="RunAsync"/> does.
    /// </summary>
    public static Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string> onOutput,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? idleTimeout = null) =>
        ExecuteAsync(fileName, arguments, timeout, cancellationToken, environment, onOutput, idleTimeout, outputEncoding: null);

    private static async Task<ProcessResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onOutput,
        TimeSpan? idleTimeout = null,
        Encoding? outputEncoding = null)
    {
        // Defense in depth, checked before anything else and regardless of elevation or caller
        // (Security.CommandDenyList): every current caller only ever issues package-manager-
        // style commands, so this should never actually trigger.
        if (Security.CommandDenyList.IsDenied(fileName, arguments, out var deniedReason))
            throw new Security.DeniedCommandException(deniedReason);

        // The untrusted-binary gate below is for the elevated INSTALL WORKER specifically; must key off
        // IsElevatedWorkerProcess, never IsElevated.
        var elevated = ElevationService.IsElevatedWorkerProcess;

        // Any elevated process builds its environment from places the unelevated user can write, so the
        // child scrub applies to an app started with "Run as administrator" as well, not only to the worker.
        var scrubEnvironment = elevated || ElevationService.IsElevated;

        // Pinned from before the signature check until after the launch below, so the file that was
        // checked is provably the file that runs (see ResolveForPrivilege). FileShare.Read denies write
        // and delete while still letting the loader read the image.
        using var executable = ResolveForPrivilege(fileName, elevated);

        return await RunResolvedAsync(
            executable, arguments, timeout, cancellationToken, environment, onOutput, idleTimeout, outputEncoding, elevated,
            scrubEnvironment);
    }

    /// <summary>The launch and the wait for an already resolved executable. Separate from
    /// <see cref="ExecuteAsync"/> so a test can drive the elevated route (the suspended start of a verified
    /// alias) without being an elevated worker.</summary>
    internal static async Task<ProcessResult> RunResolvedAsync(
        ResolvedExecutable executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onOutput,
        TimeSpan? idleTimeout,
        Encoding? outputEncoding,
        bool elevated,
        bool? scrubEnvironment = null)
    {
        var startInfo = BuildStartInfo(executable, arguments, environment, outputEncoding, elevated, scrubEnvironment: scrubEnvironment);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var process = ChildFactoryForTest.Value?.Invoke() ?? StartChild(executable, startInfo, ScanScope.IsActive);

        // Ticks, not a DateTime: written from whichever pump last saw a byte and read from the idle
        // watchdog below, so it has to be a single value both sides can exchange/read atomically.
        var lastActivityTicks = Environment.TickCount64;
        void OnActivity() => Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);

        // BOTH streams go through the same bounded, awaitable reader. DataReceived's line reader has no cap
        // on a single line's length before the callback fires (a tool redrawing with '\b' and no '\r'/'\n'
        // could grow the framework's buffer unbounded before MaxOutputChars saw a byte), and
        // CancelOutputRead/CancelErrorRead does not guarantee an already-dispatched callback has finished
        // appending before the caller reads the same StringBuilder (torn text, or an exception out of
        // ToString()). An awaited Task gives that guarantee.
        var stdoutPump = PumpAsync(process.StandardOutput, stdout, onOutput ?? NoOp, timeoutCts.Token, OnActivity);
        var stderrPump = PumpAsync(process.StandardError, stderr, NoOp, timeoutCts.Token, OnActivity);
        var pumps = Task.WhenAll(stdoutPump, stderrPump);

        // Runs only when the caller asked for idle detection; stopped in the finally below regardless of
        // how this call ends, so it never outlives the process it is watching.
        using var idleWatchStopCts = new CancellationTokenSource();
        var idleTriggered = false;
        var idleWatchdog = idleTimeout is { } idle
            ? WatchForIdleAsync(idle, () => Interlocked.Read(ref lastActivityTicks), () => idleTriggered = true, timeoutCts, idleWatchStopCts.Token)
            : Task.CompletedTask;

        try
        {
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);

                // Neither pump is drained by WaitForExitAsync; let EOF flush the last segment of each before
                // the output is read, or the final progress line would be lost. BOUNDED, though: EOF never
                // arrives if a grandchild (an MSI custom action, a vendor updater, node behind npm.cmd)
                // inherited a handle, and without a bound the wait would run to the full install timeout;
                // the row sitting "installing" for half an hour and still reporting success.
                await DrainPumpsAsync(pumps, timeoutCts, DrainTimeout, CancelDrainTimeout);
            }
            catch (OperationCanceledException)
            {
                process.Kill();

                // WaitForExitAsync drains the async readers before it returns, but killing the process does
                // not: without this the pumps can still be appending while ToString() walks the same
                // StringBuilder, which returns torn text or throws out of the chunk walk.
                await StopPumpsAsync(pumps, process, CancelDrainTimeout, executable.Path);

                // a caller-requested cancel must surface as cancellation; a timeout is a normal result
                cancellationToken.ThrowIfCancellationRequested();

                if (idleTriggered)
                    throw new ProcessIdleTimeoutException(idleTimeout!.Value);

                return new ProcessResult(-1, ReadSink(stdout), ReadSink(stderr)) { TimedOut = true };
            }

            return new ProcessResult(process.ExitCode, ReadSink(stdout), ReadSink(stderr));
        }
        finally
        {
            await idleWatchStopCts.CancelAsync();
            await SwallowAsync(idleWatchdog);
        }
    }

    /// <summary>
    /// The launch description, built without starting anything so the elevated shape can be tested. For an
    /// ELEVATED child the working directory and PATH are not inherited: the directory the unelevated app
    /// happened to run in and the user's PATH are both writable by the user, and a phantom or delay-loaded
    /// DLL lookup, or a .cmd tool resolving its sub-commands, ends up in exactly those. It starts in the
    /// verified tool's own directory (System32 when nothing was pinned, i.e. the app-execution alias, whose
    /// folder is user-writable) and gets the MACHINE path cut down to directories only administrators can
    /// change. The caller's own variables apply on top, except PATH, which an elevated launch never takes
    /// from anywhere else.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        ResolvedExecutable executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        Encoding? outputEncoding,
        bool elevated,
        Func<string?>? machinePath = null,
        Func<string, bool>? trustedDirectory = null,
        bool? scrubEnvironment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            // Resolve to a full path WITH extension: without UseShellExecute, Process.Start only
            // resolves bare names to.exe, so shim-based tools (npm.cmd, scoop.cmd) would silently
            // fail to start.
            FileName = executable.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (scrubEnvironment ?? elevated)
            RemoveCodeLoadingVariables(startInfo.Environment);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        if (elevated)
        {
            startInfo.WorkingDirectory = executable.Pin is not null && Path.GetDirectoryName(executable.Path) is { Length: > 0 } toolDirectory
                ? toolDirectory
                : Environment.SystemDirectory;
            startInfo.Environment["PATH"] = ElevatedPathTrust.BuildTrustedPath(
                (machinePath ?? ElevatedPathTrust.ReadMachinePath)(), trustedDirectory ?? ElevatedPathTrust.IsTrustedDirectory);
        }

        return startInfo;
    }

    /// <summary>
    /// Inherited variables that make a runtime load code named by the user: .NET profilers and startup hooks,
    /// Node's preload options, Python's module path, the JVM's agent options. An elevated process builds its
    /// environment from HKCU\Environment, which every unelevated program of the same user can write, so an
    /// elevated child must never take them over. Variables the caller sets explicitly still apply.
    /// </summary>
    private static readonly string[] CodeLoadingVariablePrefixes = ["COR_", "CORECLR_", "COMPlus_", "DOTNET_"];

    private static readonly HashSet<string> CodeLoadingVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "NODE_OPTIONS", "NODE_PATH", "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONUSERBASE",
        "PERL5OPT", "PERL5LIB", "RUBYOPT", "RUBYLIB", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS",
    };

    internal static void RemoveCodeLoadingVariables(IDictionary<string, string?> environment)
    {
        var doomed = environment.Keys
            .Where(key => CodeLoadingVariables.Contains(key)
                || CodeLoadingVariablePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var key in doomed)
            environment.Remove(key);
    }

    /// <summary>How long the pumps are given to drain after the process itself has already exited, before
    /// this gives up waiting for EOF and cancels the token instead.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the pumps get AFTER that cancel before this stops waiting on them altogether. Cancelling
    /// <paramref name="timeoutCts"/> above does not reliably unblock an in-flight read: a grandchild process
    /// (an MSI custom action, a vendor updater, node behind npm.cmd) can inherit the pipe's write handle and
    /// keep it open long after the started process is gone, and the pipe read beneath
    /// <see cref="TextReader.ReadAsync(Memory{char}, CancellationToken)"/> does not abort just because its
    /// token is cancelled. Without this second bound, the unbounded hang the 5-second bound above prevents
    /// would merely wait for the cancel first.
    /// </summary>
    private static readonly TimeSpan CancelDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Receives the one line written when the pumps outlive a kill; wired to the application log at
    /// startup. Null (tests, tools) means the line is dropped.</summary>
    internal static Action<string>? Diagnostics { get; set; }

    /// <summary>Test seam: supplies the child instead of starting a real process.</summary>
    internal static readonly AsyncLocal<Func<IChildProcess>?> ChildFactoryForTest = new();

    /// <summary>
    /// Ends the output pumps after the child was killed. A descendant that escaped the kill (a breakaway
    /// child, a detached grandchild) still holds the inherited write end, so EOF never arrives and an
    /// unbounded wait would hang a stop or a timeout forever. The pumps get <paramref name="bound"/> to end
    /// on their own; if they do not, the readers are disposed (which ends the reads with an exception the
    /// pump already absorbs) and they get the same bound again. The sinks are only read after this returns,
    /// so no pump appends while they are walked.
    /// </summary>
    internal static async Task StopPumpsAsync(Task pumps, IChildProcess child, TimeSpan bound, string tool)
    {
        try
        {
            await pumps.WaitAsync(bound, CancellationToken.None);
            return;
        }
        catch (TimeoutException)
        {
            // fall through to the reader disposal below
        }
        catch
        {
            // a faulted pump is as ended as a finished one
            return;
        }

        Diagnostics?.Invoke($"Output of '{Path.GetFileName(tool)}' did not end after the stop; closing its readers.");

        DisposeQuietly(child.StandardOutput);
        DisposeQuietly(child.StandardError);
        await SwallowAsync(pumps.WaitAsync(bound, CancellationToken.None));
    }

    private static void DisposeQuietly(IDisposable reader)
    {
        try
        {
            reader.Dispose();
        }
        catch
        {
            // best effort on the cancel path
        }
    }

    /// <summary>
    /// Waits for <paramref name="pumps"/> (<c>Task.WhenAll</c> of the stdout/stderr readers) to finish once
    /// the process itself has exited normally, bounded twice so this can never hang no matter what still
    /// holds the pipe open: first by <paramref name="drainTimeout"/> for the ordinary "EOF has not quite
    /// arrived yet" case, then, only if that was not enough, by <paramref name="cancelDrainTimeout"/> after
    /// cancelling <paramref name="timeoutCts"/>. Internal so a test can exercise the composition with a pump
    /// that never completes, without waiting out the production timeouts.
    /// </summary>
    internal static async Task DrainPumpsAsync(
        Task pumps, CancellationTokenSource timeoutCts, TimeSpan drainTimeout, TimeSpan cancelDrainTimeout)
    {
        try
        {
            await pumps.WaitAsync(drainTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // The process itself is already gone; whatever is still holding a pipe is not ours. The pumps
            // are still running though, and they append to the same StringBuilders read afterwards - the
            // identical torn-text race the cancel path guards against, so stop it here too.
            await timeoutCts.CancelAsync();
            await SwallowAsync(pumps.WaitAsync(cancelDrainTimeout, CancellationToken.None));
        }
    }

    /// <summary>
    /// Polls the shared last-activity timestamp the stdout/stderr pumps update on every byte received;
    /// once it has been silent for <paramref name="idleTimeout"/>, marks it and cancels
    /// <paramref name="timeoutCts"/> so the caller's own wait ends immediately instead of running to the
    /// full hard deadline. A source that keeps producing output, however slowly, never trips this.
    /// </summary>
    private static async Task WatchForIdleAsync(
        TimeSpan idleTimeout,
        Func<long> readLastActivityTicks,
        Action markIdle,
        CancellationTokenSource timeoutCts,
        CancellationToken stopToken)
    {
        try
        {
            using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await ticker.WaitForNextTickAsync(stopToken))
            {
                var silentFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - readLastActivityTicks());
                if (silentFor < idleTimeout)
                    continue;

                markIdle();
                await timeoutCts.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // the call finished, or was cancelled/timed out some other way, before this noticed anything
        }
        catch (ObjectDisposedException)
        {
            // timeoutCts was already disposed by the caller returning before this loop's next tick
        }
    }

    private static void NoOp(string segment)
    {
    }

    /// <summary>
    /// Reads stdout character by character and hands each completed segment; text terminated by '\r',
    /// '\n' or '\b'; to the callback, while also accumulating the whole output for the final result. A
    /// throwing callback must never fault the read loop, so it is guarded.
    /// </summary>
    /// <param name="onActivity">Raised on every raw chunk read, before it is split into segments: the
    /// idle-timeout watchdog's liveness signal, independent of whether a segment ever completes.</param>
    internal static async Task PumpAsync(
        TextReader reader, StringBuilder sink, Action<string> onSegment, CancellationToken cancellationToken,
        Action? onActivity = null)
    {
        var buffer = new char[4096];
        var segment = new StringBuilder();

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                onActivity?.Invoke();

                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];

                    // '\b' as well as '\r'/'\n': DISM redraws its progress bar with backspaces and never
                    // emits a newline until the step is over, so a reader that only knows about carriage
                    // returns saw one enormous segment at the end and the cleanup dialog could show nothing
                    // but a flat "one third per step".
                    if (c is '\r' or '\n' or '\b')
                        Emit(segment, sink, onSegment);
                    else
                    {
                        segment.Append(c);

                        // A delimiter-free stream (no CR/LF/backspace at all) would otherwise grow this
                        // buffer without bound; MaxOutputChars only guards the sink AFTER a segment
                        // completes, never the segment itself. Force a flush once one segment alone would
                        // already exceed the cap.
                        if (segment.Length >= MaxOutputChars)
                            Emit(segment, sink, onSegment);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the process was killed; whatever was captured so far is enough
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the pipe closed under us on shutdown; not worth faulting the whole run
        }

        Emit(segment, sink, onSegment);
    }

    private static void Emit(StringBuilder segment, StringBuilder sink, Action<string> onSegment)
    {
        if (segment.Length == 0)
            return;

        var text = segment.ToString();
        segment.Clear();

        // The bounded drain above can give up on a pump that is still running (a grandchild holding the
        // pipe open), and the final result is still read off this same StringBuilder afterwards
        // (ReadSink): StringBuilder is not thread-safe for a concurrent read and append, which is exactly
        // torn text or an exception out of ToString() otherwise. Locking on the sink itself needs no extra
        // field; nothing outside this one call ever holds it.
        lock (sink)
            Append(sink, text);

        try
        {
            onSegment(text);
        }
        catch
        {
            // a parser fault must not stop the tool's output from being read
        }
    }

    /// <summary>The final read of a pump's sink, under the same lock <see cref="Emit"/> appends through -
    /// see its own comment for why a pump can still be running when this is called.</summary>
    private static string ReadSink(StringBuilder sink)
    {
        lock (sink)
            return sink.ToString();
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // best effort on the cancel path
        }
    }

    /// <summary>True if the executable resolves on PATH (or is an absolute path that exists).</summary>
    public static bool Exists(string fileName) => Resolve(fileName) is not null;

    /// <summary>What an elevated process may launch; accepted only if signed, since %PATH% is under the
    /// unelevated user's control.</summary>
    internal static ResolvedExecutable ResolveForPrivilege(
        string fileName, bool elevated, Func<string, FileStream, bool>? locationTrust = null)
    {
        var resolved = Resolve(fileName) ?? fileName;

        if (!elevated)
            return new ResolvedExecutable(resolved, null);

        // The ORDER here is the security property: the handle is pinned FIRST, the signature verified
        // against THAT handle.
        var pin = TryPin(resolved);
        FileStream? shimPin = null;
        if (pin is null)
        {
            // Nothing but the winget app-execution alias may be unpinnable, and only after its reparse data
            // has been read and checked.
            var alias = VerifyWingetAlias(resolved, out var aliasRefusal);
            if (alias is not null)
                return alias;

            throw new UntrustedToolException(resolved, aliasRefusal);
        }

        try
        {
            // Inside the user's own profile a signature proves nothing about WHAT is being run. Catalog
            // signing is over the file's hash, so a copy of any signed binary keeps its signature under any
            // name; measured: Windows' own notepad.exe, copied and renamed to winget.exe, verifies as
            // Valid/Catalog. A signed host placed next to an attacker's DLL is code execution as admin just
            // the same. Nothing in the profile is therefore accepted on the strength of a signature, and a
            // file there that could be opened at all is by definition not the alias. Outside the profile a
            // directory is NOT admin-owned just because it is not under C:\Users: a folder a user created
            // under the volume root inherits Authenticated Users Modify. So the location must also be one
            // only administrators can change (ElevatedPathTrust), and then the signature of the pinned
            // bytes is the right question. The locationTrust parameter exists so a test can vary the location alone;
            // production never passes it.
            var trustedLocation = locationTrust ?? ElevatedPathTrust.IsTrustedLocation;
            if (IsUnderUserProfile(resolved)
                || !trustedLocation(resolved, pin)
                || !TrustedBinary.IsSigned(resolved, pin))
                throw new UntrustedToolException(resolved);

            // A shim starts ANOTHER image that loads its own DLLs from its own directory, so the image it
            // starts is held to the same location and signature rules, pinned until the launch as well.
            if (KnownShimTarget(resolved) is { } shimTarget)
            {
                shimPin = TryPin(shimTarget);
                if (shimPin is null
                    || IsUnderUserProfile(shimTarget)
                    || !trustedLocation(shimTarget, shimPin)
                    || !TrustedBinary.IsSigned(shimTarget, shimPin))
                    throw new UntrustedToolException(shimTarget);
            }
        }
        catch
        {
            shimPin?.Dispose();
            pin.Dispose();
            throw;
        }

        return new ResolvedExecutable(resolved, pin, shimPin);
    }

    /// <summary>
    /// The real image behind a shim this app knows launches another executable: chocolatey's
    /// <c>bin\choco.exe</c> starts <c>..\choco.exe</c> in the chocolatey root. Null for everything else.
    /// A shim of that shape whose target is missing yields the missing path, which then cannot be pinned and
    /// is refused.
    /// </summary>
    internal static string? KnownShimTarget(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(full), "choco.exe", StringComparison.OrdinalIgnoreCase))
                return null;

            var directory = Path.GetDirectoryName(full);
            if (!string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(directory) is not { Length: > 0 } root)
                return null;

            return Path.Combine(root, "choco.exe");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The whole Users root, not only this account's own folder. C:\Users\Public is writable by every
    /// ordinary user, and so is any other profile's tree for whoever owns it, so "outside the profile the
    /// directory is administrator-owned" was true for Program Files and false right next door. Anything
    /// under Users now has to be the genuine app-execution alias, whatever signature it carries.
    /// </summary>
    private static bool IsUnderUserProfile(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var usersRoot = Path.GetDirectoryName(profile);

        if (string.IsNullOrEmpty(profile))
            return true;   // cannot tell where we are: treat as untrusted

        try
        {
            var full = Path.GetFullPath(path);

            return full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(usersRoot)
                    && full.StartsWith(usersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    /// <summary>
    /// The winget stub in %LOCALAPPDATA%\Microsoft\WindowsApps, accepted only on what it says it starts. The
    /// stub is a reparse point any process of this user can create, with any target, so being unopenable
    /// proves nothing. The alias is opened as a reparse point (no following), its APPEXECLINK data is read
    /// through that handle and must name the App Installer package and a winget.exe inside the package store;
    /// that target is then pinned and held to the same signature rule as any elevated tool, plus a Microsoft
    /// publisher. The alias handle stays open, but that does not freeze the link: a reparse point can be
    /// rewritten with write-attributes access, which share modes do not block. The check is therefore a
    /// snapshot, and the launch (<see cref="StartChild"/>) compares the image the system really mapped
    /// with the pinned target before the process may run. Null with a reason on any failure.
    /// </summary>
    private static ResolvedExecutable? VerifyWingetAlias(string path, out string? refusal)
    {
        refusal = null;

        if (!string.Equals(Path.GetDirectoryName(path), AliasDirectory, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "not in the app-execution alias folder";
            return null;
        }

        SafeFileHandle? aliasHandle = null;
        FileStream? targetPin = null;
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                refusal = "not a reparse point";
                return null;
            }

            aliasHandle = AppExecutionAlias.TryOpen(path, out var buffer);
            if (aliasHandle is null || buffer is null)
            {
                refusal = "the alias could not be read as a reparse point";
                return null;
            }

            if (!AppExecutionAlias.TryParse(buffer, out var link) || link is null)
            {
                refusal = "not an app-execution link";
                return null;
            }

            if (!AppExecutionAlias.IsWingetLink(link, AppExecutionAlias.PackageRoots()))
            {
                refusal = "the alias does not point to the App Installer's winget.exe";
                return null;
            }

            var target = Path.GetFullPath(link.Target);
            targetPin = TryPin(target);
            if (targetPin is null)
            {
                refusal = "the alias target could not be opened";
                return null;
            }

            if (!TrustedBinary.IsSigned(target, targetPin) || !WingetBootstrapService.IsMicrosoftPublisher(target))
            {
                refusal = "the alias target is not signed by Microsoft";
                return null;
            }

            var result = new ResolvedExecutable(path, null, targetPin, aliasHandle);
            aliasHandle = null;
            targetPin = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            refusal = $"the alias could not be verified: {ex.GetType().Name}";
            return null;
        }
        finally
        {
            targetPin?.Dispose();
            aliasHandle?.Dispose();
        }
    }

    /// <summary>
    /// True when a value can be handed over as a positional argument. Passing arguments as an array
    /// stops the shell from ever seeing them, but it does NOT stop the target tool from reading a
    /// value that begins with '-' or '/' as an option, which is enough to redirect a package
    /// manager's download source.
    /// cmd.exe's own metacharacters (&amp; | &lt; &gt; ^ %) are refused too: many of these tools are .cmd or .bat
    /// wrappers (npm, pnpm, composer, gem, pyenv), which run through cmd.exe, and .NET does not escape
    /// those characters for a batch target, so "pkg&amp;calc" would run a second command. No package id or
    /// version of those ecosystems contains them. <paramref name="allowCmdMetacharacters"/> is only for a
    /// caller whose tool is a native executable (winget), whose ids may legally contain them.
    /// </summary>
    public static bool IsSafePositionalArgument(string? value, bool allowCmdMetacharacters = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains(".."))
            return false;

        if (value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '`'))
            return false;

        if (!allowCmdMetacharacters && value.Any(c => c is '&' or '|' or '<' or '>' or '^' or '%'))
            return false;

        // An npm scope is the one legitimate leading '@'; "@angular/cli" is a real package name,
        // while a bare "@something" would be read as a response file by other tools.
        if (value[0] == '@')
        {
            var slash = value.IndexOf('/');
            return slash > 1 && slash < value.Length - 1 && !value.Contains('\\');
        }

        return value[0] is not ('-' or '/');
    }

    /// <summary>Full path of the executable including its extension, or null if it is not on PATH.</summary>
    public static string? Resolve(string fileName)
    {
        if (Path.IsPathRooted(fileName))
            return File.Exists(fileName) ? fileName : null;

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var directories = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (AliasDirectory is { Length: > 0 } aliases)
            directories = [.. directories, aliases];

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, fileName + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // malformed PATH entry; ignore and keep probing
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Where Windows puts the app-execution aliases of packaged apps. Probed AFTER %PATH%, because it
    /// normally IS on %PATH% and a real entry there must keep winning.
    /// Without this the app depends on one entry in the user's %PATH%: winget is reachable only through its
    /// alias, and on a machine whose %PATH% has lost this directory (it happens, on a real machine
    /// `winget` was unknown in PowerShell too) the winget source reported nothing at all, indistinguishable
    /// from "everything is up to date". A user can write this folder, so the elevated path never trusts a
    /// stub by its place: it goes through <see cref="ResolveForPrivilege"/>, which reads the alias's content
    /// (<see cref="VerifyWingetAlias"/>).
    /// </summary>
    private static string AliasDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
            ? Path.Combine(local, "Microsoft", "WindowsApps")
            : string.Empty;

    private static void Append(StringBuilder builder, string? line)
    {
        var room = MaxOutputChars - builder.Length;
        if (line is null || room <= 0)
            return;

        // Only what still fits. Checking the cap and then appending a whole segment let one final chunk
        // take the buffer to nearly twice the advertised ceiling, and a tool that prints a single
        // enormous line is exactly the case the cap exists for.
        builder.AppendLine(line.Length <= room ? line : line[..room]);
    }

    private static FileStream? TryPin(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Serialises every launch that inherits handles. The framework's own start lock is private, so
    /// without a shared one a parallel launch could inherit the write ends of a suspended launch's pipes and
    /// the reader of that launch would never see the end of its output. Any new launcher that inherits
    /// handles must take it too.</summary>
    internal static readonly object ChildStartLock = new();

    /// <summary>Test seam: the stand-in image of a suspended-launch test has no package identity.</summary>
    internal static readonly AsyncLocal<bool> UnpackagedImageForTest = new();

    /// <summary>
    /// Starts the child. The verified winget alias is the one thing never handed to
    /// <see cref="Process.Start()"/>: the alias is a reparse point that can be rewritten between the check
    /// and the launch even while the verified handle is held, so it is started suspended and the image the
    /// system mapped is compared with the pinned, verified target before it may run
    /// (<see cref="SuspendedChildProcess"/>).
    /// </summary>
    private static IChildProcess StartChild(ResolvedExecutable executable, ProcessStartInfo startInfo, bool lowPriority)
    {
        if (executable.AliasPin is not null)
        {
            var expected = executable.ShimTargetPin is { } targetPin
                ? SuspendedChildProcess.PlainFinalPath(targetPin.SafeFileHandle)
                : null;
            if (expected is null)
                throw new UntrustedToolException(executable.Path, "the verified target cannot be identified");

            // The seam lets a test run an unpackaged stand-in image through the same route; production
            // always demands the App Installer's identity.
            (string Family, string Aumid)? expectedPackage = UnpackagedImageForTest.Value
                ? null
                : (AppExecutionAlias.WingetPackageFamily, AppExecutionAlias.WingetAppUserModelId);

            return SuspendedChildProcess.Start(startInfo, expected, expectedPackage, lowPriority);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            // Process.Start hands every inheritable handle of this process to the child. The suspended
            // launch briefly owns inheritable pipe ends, so the two never overlap.
            lock (ChildStartLock)
                process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        if (lowPriority)
        {
            // Right after the start rather than at creation, which the managed route cannot express; what
            // the tool starts afterwards inherits it. The process may already be gone or refuse the change,
            // and neither is worth failing a scan for.
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return new ManagedChildProcess(process);
    }
}
