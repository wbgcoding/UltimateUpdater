using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services.Processes;

/// <summary>An elevated launch was refused because the executable is not signed (see ProcessRunner).</summary>
public sealed class UntrustedToolException(string path, string? reason = null)
    : Exception($"Refusing to run an unsigned executable with administrator rights: {path}"
        + (string.IsNullOrEmpty(reason) ? string.Empty : $" ({reason})"))
{
    public string Path { get; } = path;
}

/// <summary>
/// A call given an idle timeout (see <see cref="ProcessRunner.RunAsync"/>) went that long without any
/// stdout/stderr activity. Distinct from the hard-timeout result so a caller can tell a hung tool from one
/// that ran past the overall deadline.
/// </summary>
public sealed class ProcessIdleTimeoutException(TimeSpan idleTimeout)
    : Exception($"No output for {idleTimeout.TotalSeconds:0} seconds.")
{
    public TimeSpan IdleTimeout { get; } = idleTimeout;
}

/// <summary>
/// An executable resolved for a launch, together with the open handle its trust check was made against.
/// The caller holds the checked file open until the process has started: dispose it only after
/// <see cref="Process.Start()"/>, never between the check and the launch.
/// <see cref="Pin"/> is null when the path itself was not pinned: an unelevated launch (not trust-checked),
/// or the winget app-execution alias, whose own handle is <see cref="AliasPin"/> and whose verified target
/// is held in <see cref="ShimTargetPin"/>. Pin stays null for the alias on purpose: a pinned tool launches
/// from its own folder, and the alias folder is user-writable.
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
/// shell-interpolated command string; no shell, no window, explicit UTF-8, hard timeout, output size cap.
/// Callers treat everything returned here as hostile input.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Guards against a runaway tool filling memory; output beyond this is discarded.</summary>
    private const int MaxOutputChars = 10 * 1024 * 1024;

    /// <param name="environment">
    /// Extra environment variables for the child. Use this to hand a value to a script instead of
    /// interpolating it into the command text: an interpolated value is an injection surface.
    /// </param>
    /// <param name="idleTimeout">
    /// When given, the call fails with <see cref="ProcessIdleTimeoutException"/> once this much time passes
    /// with no stdout/stderr activity, even while <paramref name="timeout"/> has not elapsed. A tool that
    /// keeps producing output, however slowly, is never cut off by this. Null: <paramref name="timeout"/>
    /// alone decides.
    /// </param>
    /// <param name="outputEncoding">
    /// Overrides the UTF-8 decode. The one legitimate reason is <see cref="Encoding.Latin1"/>, which keeps a
    /// tool's raw bytes (1:1 byte-to-char) for a caller that decodes them itself, such as
    /// <see cref="Cleanup.OemTextRecovery"/>.
    /// </param>
    /// <param name="killOnTimeout">
    /// False: when <paramref name="timeout"/> runs out the tool is not killed. The call returns a timed-out
    /// result, the tool finishes on its own, and its handle is released once it exits. For a tool that must
    /// not be cut off mid-work (a cache clean that would leave half a cache). A caller-requested cancel leaves
    /// it running the same way, then surfaces as cancellation.
    /// </param>
    public static Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? idleTimeout = null,
        Encoding? outputEncoding = null,
        bool killOnTimeout = true) =>
        ExecuteAsync(fileName, arguments, timeout, cancellationToken, environment, onOutput: null, idleTimeout, outputEncoding,
            killOnTimeout: killOnTimeout);

    /// <summary>
    /// Same safety contract as <see cref="RunAsync"/>, but stdout is streamed segment by segment to
    /// <paramref name="onOutput"/>. Segments split on both '\n' and '\r' because package managers animate
    /// their progress line with a carriage return and never a newline. The full output is still captured and
    /// returned as <see cref="RunAsync"/> does. With <paramref name="pseudoConsole"/> the tool runs in a hidden
    /// pseudo console (see <see cref="PseudoConsoleHost"/>) so one that prints its progress bar only to a
    /// console (winget) does; terminal sequences are removed from the output, there is no separate error
    /// stream, and where no pseudo console can be made the run uses pipes.
    /// </summary>
    public static Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string> onOutput,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? idleTimeout = null,
        StallWatchOptions? stallWatch = null,
        bool pseudoConsole = false,
        Encoding? outputEncoding = null,
        bool stallIsUninstall = false,
        CancellationToken stopWhenIdle = default) =>
        ExecuteAsync(fileName, arguments, timeout, cancellationToken, environment, onOutput, idleTimeout, outputEncoding, stallWatch,
            pseudoConsole: pseudoConsole, stallIsUninstall: stallIsUninstall, stopWhenIdle: stopWhenIdle);

    private static async Task<ProcessResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onOutput,
        TimeSpan? idleTimeout = null,
        Encoding? outputEncoding = null,
        StallWatchOptions? stallWatch = null,
        bool killOnTimeout = true,
        bool pseudoConsole = false,
        bool stallIsUninstall = false,
        CancellationToken stopWhenIdle = default)
    {
        // Defense in depth, checked before anything else and regardless of elevation or caller
        // (Security.CommandDenyList); every current caller only issues package-manager commands.
        if (Security.CommandDenyList.IsDenied(fileName, arguments, out var deniedReason))
            throw new Security.DeniedCommandException(deniedReason);

        // The untrusted-binary gate below keys off IsElevatedWorkerProcess, never IsElevated.
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
            scrubEnvironment, stallWatch, killOnTimeout, pseudoConsole, stallIsUninstall: stallIsUninstall,
            stopWhenIdle: stopWhenIdle);
    }

    /// <summary>The launch and the wait for an already resolved executable. Separate from
    /// <see cref="ExecuteAsync"/> so a test can drive the elevated route without being an elevated worker.</summary>
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
        bool? scrubEnvironment = null,
        StallWatchOptions? stallWatch = null,
        bool killOnTimeout = true,
        bool pseudoConsole = false,
        bool waitForTree = false,
        bool stallIsUninstall = false,
        CancellationToken stopWhenIdle = default,
        bool endTreeWhenIdle = true)
    {
        var startInfo = BuildStartInfo(executable, arguments, environment, outputEncoding, elevated, scrubEnvironment: scrubEnvironment);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        // Owns the child, and with killOnTimeout false its own pump token source too: both outlive this call
        // when the tool is left running. Not linked to the caller's token, or a cancel would stop the readers
        // while the tool still writes.
        var process = ChildFactoryForTest.Value?.Invoke() ?? StartChild(executable, startInfo, ScanScope.IsActive, pseudoConsole, waitForTree);
        using var lease = new ChildLease(process, killOnTimeout ? null : new CancellationTokenSource());
        var pumpCts = lease.PumpCts ?? timeoutCts;

        // Ticks, not a DateTime: written from whichever pump last saw a byte and read from the idle watchdog,
        // so it must be a single value both sides can read atomically.
        var lastActivityTicks = Environment.TickCount64;
        void OnActivity() => Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);

        // Both streams go through the same bounded, awaitable reader. DataReceived's line reader has no cap on
        // a line's length (a tool redrawing with '\b' and no '\r'/'\n' could grow its buffer unbounded), and
        // CancelOutputRead does not guarantee a dispatched callback has finished appending before the caller
        // reads the StringBuilder. An awaited Task does.
        var stdoutPump = PumpAsync(process.StandardOutput, stdout, onOutput ?? NoOp, pumpCts.Token, OnActivity);
        var stderrPump = PumpAsync(process.StandardError, stderr, NoOp, pumpCts.Token, OnActivity);
        var pumps = Task.WhenAll(stdoutPump, stderrPump);

        // Runs only when the caller asked for idle detection; stopped in the finally below.
        using var idleWatchStopCts = new CancellationTokenSource();
        var idleTriggered = false;
        var idleWatchdog = idleTimeout is { } idle
            ? WatchForIdleAsync(idle, () => Interlocked.Read(ref lastActivityTicks), () => idleTriggered = true, timeoutCts, idleWatchStopCts.Token)
            : Task.CompletedTask;

        // Same lifetime as the idle watchdog. Judges the whole tree below the tool, not its output: an
        // installer that prints nothing for minutes but works is alive.
        var stallTriggered = false;
        var stallWatchTask = stallWatch is { } stall
            ? WatchForStallAsync(stall, process.ProcessId, () => stallTriggered = true, timeoutCts, idleWatchStopCts.Token, stopWhenIdle,
                watchInstallerService: stallIsUninstall)
            : Task.CompletedTask;

        try
        {
            try
            {
                await (waitForTree ? process.WaitForTreeAsync(timeoutCts.Token, endTreeWhenIdle) : process.WaitForExitAsync(timeoutCts.Token));

                // WaitForExitAsync does not drain the pumps; EOF must flush the last segment of each before the
                // output is read. Bounded: EOF never arrives if a grandchild (an MSI custom action, a vendor updater,
                // node behind npm.cmd) inherited a handle, and the wait would run to the full install timeout.
                await DrainPumpsAsync(pumps, pumpCts, DrainTimeout, CancelDrainTimeout);
            }
            catch (OperationCanceledException)
            {
                if (!killOnTimeout && !idleTriggered && !stallTriggered)
                {
                    // Out of time or asked to stop: the tool keeps running and the pumps keep draining so it never
                    // blocks on a full pipe.
                    lease.Detach();
                    _ = FinishDetachedAsync(lease, pumps, executable.Path);

                    // a caller-requested cancel must surface as cancellation; a timeout is a normal result
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ProcessResult(-1, ReadSink(stdout), ReadSink(stderr)) { TimedOut = true };
                }

                process.Kill();

                // Killing the process does not drain the async readers: without this the pumps can still be appending
                // while ToString() walks the same StringBuilder.
                await StopPumpsAsync(pumps, process, CancelDrainTimeout, executable.Path);

                // a caller-requested cancel must surface as cancellation; a timeout is a normal result
                cancellationToken.ThrowIfCancellationRequested();

                if (idleTriggered)
                    throw new ProcessIdleTimeoutException(idleTimeout!.Value);

                if (stallTriggered)
                    throw new InstallerStalledException(Path.GetFileNameWithoutExtension(executable.Path), stallIsUninstall);

                return new ProcessResult(-1, ReadSink(stdout), ReadSink(stderr)) { TimedOut = true };
            }

            return new ProcessResult(process.ExitCode, ReadSink(stdout), ReadSink(stderr))
            {
                TreeLeftRunning = waitForTree && process.ActiveJobProcesses > 0,
            };
        }
        finally
        {
            await idleWatchStopCts.CancelAsync();
            await SwallowAsync(idleWatchdog);
            await SwallowAsync(stallWatchTask);
        }
    }

    /// <summary>How long a tool left running after a timeout keeps its handle and pumps (the tool itself is
    /// never ended).</summary>
    private static readonly TimeSpan DetachedLifetime = TimeSpan.FromHours(1);

    /// <summary>Test seam for <see cref="DetachedLifetime"/>, per async flow.</summary>
    internal static readonly AsyncLocal<TimeSpan?> DetachedLifetimeForTest = new();

    /// <summary>The child of one run, plus its own pump token source when the run may leave the child
    /// running. Disposed with the run unless <see cref="Detach"/> handed it to the background release.</summary>
    private sealed class ChildLease(IChildProcess child, CancellationTokenSource? pumpCts) : IDisposable
    {
        private bool _detached;
        private int _released;

        public IChildProcess Child => child;

        public CancellationTokenSource? PumpCts => pumpCts;

        public void Detach() => _detached = true;

        public void Dispose()
        {
            if (!_detached)
                Release();
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            try
            {
                child.Dispose();
            }
            catch
            {
                // best effort: the child may be gone already
            }

            pumpCts?.Dispose();
        }
    }

    /// <summary>
    /// Lets a tool that ran past its timeout finish on its own: waits for it to exit (at most
    /// <see cref="DetachedLifetime"/>), drains its output, then releases the handle and the readers.
    /// Never throws, never ends the tool.
    /// </summary>
    private static async Task FinishDetachedAsync(ChildLease lease, Task pumps, string tool)
    {
        try
        {
            using var lifetime = new CancellationTokenSource(DetachedLifetimeForTest.Value ?? DetachedLifetime);
            try
            {
                await lease.Child.WaitForExitAsync(lifetime.Token);
                await DrainPumpsAsync(pumps, lease.PumpCts!, DrainTimeout, CancelDrainTimeout);
            }
            catch (OperationCanceledException)
            {
                await lease.PumpCts!.CancelAsync();
                await StopPumpsAsync(pumps, lease.Child, CancelDrainTimeout, tool);
            }
        }
        catch
        {
            // nobody waits for this task; the release below is all that is left to do
        }
        finally
        {
            lease.Release();
        }
    }

    /// <summary>
    /// The launch description, built without starting anything so the elevated shape can be tested. An
    /// ELEVATED child inherits neither the working directory nor PATH: both are writable by the user, and a
    /// phantom or delay-loaded DLL lookup, or a .cmd tool resolving its sub-commands, ends up in them. It
    /// starts in the verified tool's own directory (System32 when nothing was pinned, i.e. the
    /// app-execution alias, whose folder is user-writable) and gets the MACHINE path cut down to directories
    /// only administrators can change. The caller's variables apply on top, except PATH, which an elevated
    /// launch never takes from anywhere else.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        ResolvedExecutable executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        Encoding? outputEncoding,
        bool elevated,
        Func<string?>? machinePath = null,
        Func<string, bool>? trustedDirectory = null,
        bool? scrubEnvironment = null,
        Func<string, string?>? machineVariable = null)
    {
        var startInfo = new ProcessStartInfo
        {
            // A full path with extension: without UseShellExecute, Process.Start only resolves bare names to
            // an .exe, so shim-based tools (npm.cmd, scoop.cmd) would fail to start.
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

        // Applied after the caller's variables: nothing may name the install root of a package manager
        // that runs scripts from it, whoever supplies it.
        if (scrubEnvironment ?? elevated)
            ApplyChocolateyInstallRoot(startInfo.Environment, machineVariable ?? ElevatedPathTrust.ReadMachineVariable, trustedDirectory ?? ElevatedPathTrust.IsTrustedDirectory);

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

    /// <summary>
    /// Chocolatey takes its install root (lib, helpers, extensions: PowerShell it runs as the caller) from
    /// ChocolateyInstall, its tools folder from ChocolateyToolsLocation, and falls back to ChocolateyBinRoot
    /// for that one. All normally come from HKCU\Environment, which any unelevated program of the user can
    /// write, so every inherited Chocolatey variable is dropped. The two folder variables are then set again,
    /// each only to its MACHINE value and only when that is a fully qualified path without variables and
    /// every directory on its chain passes <paramref name="isTrustedDirectory"/>; otherwise it stays unset
    /// and Chocolatey uses its own default.
    /// </summary>
    internal static void ApplyChocolateyInstallRoot(
        IDictionary<string, string?> environment, Func<string, string?> machineVariable, Func<string, bool> isTrustedDirectory)
    {
        foreach (var key in environment.Keys.Where(key => key.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)).ToList())
            environment.Remove(key);

        foreach (var name in new[] { "ChocolateyInstall", "ChocolateyToolsLocation" })
        {
            var folder = machineVariable(name)?.Trim().Trim('"');
            if (string.IsNullOrEmpty(folder) || folder.Contains('%') || !Path.IsPathFullyQualified(folder))
                continue;

            folder = Path.TrimEndingDirectorySeparator(folder);
            if (isTrustedDirectory(folder))
                environment[name] = folder;
        }
    }

    /// <summary>How long the pumps get to drain after the process has exited, before the wait for EOF is
    /// given up and the token cancelled.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the pumps get after that cancel before this stops waiting on them. Cancelling
    /// <paramref name="timeoutCts"/> does not reliably unblock an in-flight read: a grandchild (an MSI custom
    /// action, a vendor updater, node behind npm.cmd) can inherit the pipe's write handle and hold it open
    /// after the started process is gone, and the pipe read beneath
    /// <see cref="TextReader.ReadAsync(Memory{char}, CancellationToken)"/> does not abort on cancel.
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
    /// unbounded wait would hang a stop or a timeout. The pumps get <paramref name="bound"/> to end on their
    /// own; if they do not, the readers are disposed (which ends the reads with an exception the pump
    /// absorbs) and they get the same bound again. The sinks are only read after this returns.
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
    /// the process has exited normally, bounded twice so it can never hang: first by
    /// <paramref name="drainTimeout"/> for the ordinary "EOF has not quite arrived" case, then by
    /// <paramref name="cancelDrainTimeout"/> after cancelling <paramref name="timeoutCts"/>. Internal so a
    /// test can use a pump that never completes without waiting out the production timeouts.
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
            // The process is gone; whatever still holds a pipe is not ours. The pumps still append to the same
            // StringBuilders read afterwards, the same torn-text race as on the cancel path, so stop them here too.
            await timeoutCts.CancelAsync();
            await SwallowAsync(pumps.WaitAsync(cancelDrainTimeout, CancellationToken.None));
        }
    }

    /// <summary>
    /// Polls the shared last-activity timestamp the stdout/stderr pumps update on every byte; once it has
    /// been silent for <paramref name="idleTimeout"/>, marks it and cancels <paramref name="timeoutCts"/> so
    /// the caller's wait ends instead of running to the full hard deadline.
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

    /// <summary>Test seam: stands in for the system look at the process tree.</summary>
    internal static readonly AsyncLocal<Func<int, TreeActivity>?> TreeSamplerForTest = new();

    /// <summary>Test seam: stands in for the look at whether the Windows Installer is busy.</summary>
    internal static readonly AsyncLocal<Func<bool>?> InstallerBusyProbeForTest = new();

    /// <summary>True while a Windows Installer transaction is running anywhere on the machine (the
    /// <c>_MSIExecute</c> mutex is held). The work of an MSI removal runs in the installer service, outside the
    /// tool's tree, so a tree that only waits for it looks idle although the removal is mid-transaction.</summary>
    internal static bool IsWindowsInstallerBusy()
    {
        if (InstallerBusyProbeForTest.Value is { } probe)
            return probe();

        try
        {
            if (!Mutex.TryOpenExisting(@"Global\_MSIExecute", out var mutex))
                return false;

            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and cannot be opened: the installer is busy.
            return true;
        }
        catch (Exception ex) when (ex is IOException or WaitHandleCannotBeOpenedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Looks at the tool's whole process tree every sample interval; once <see cref="StallDetector"/> says
    /// it has shown no life for the whole window (or <paramref name="stopWhenIdle"/> was cancelled and it has shown none
    /// for <see cref="StallWatchOptions.StopIdle"/>), marks it and cancels <paramref name="timeoutCts"/> so the
    /// tree is stopped instead of running to the full install deadline.
    /// </summary>
    private static async Task WatchForStallAsync(
        StallWatchOptions options, int rootId, Action markStalled, CancellationTokenSource timeoutCts,
        CancellationToken stopToken, CancellationToken stopWhenIdle, bool watchInstallerService = false)
    {
        var detector = new StallDetector(options);
        var sampler = TreeSamplerForTest.Value ?? ProcessTreeSampler.Sample;
        try
        {
            using var ticker = new PeriodicTimer(options.SampleInterval);
            do
            {
                var activity = await Task.Run(() => sampler(rootId), CancellationToken.None);
                var now = Environment.TickCount64;
                var stalled = detector.Observe(now, activity, externalLife: watchInstallerService && IsWindowsInstallerBusy());

                // A stop by the person ends only a tree that is waiting right now: a removal that is still working
                // is left to finish (ending it halfway leaves a program neither installed nor gone).
                if (!stalled && !(stopWhenIdle.IsCancellationRequested && detector.IdleFor(now) >= options.StopIdle))
                    continue;

                markStalled();
                await timeoutCts.CancelAsync();
                return;
            }
            while (await ticker.WaitForNextTickAsync(stopToken));
        }
        catch (OperationCanceledException)
        {
            // the call ended before the window ran out
        }
        catch (ObjectDisposedException)
        {
            // timeoutCts was already disposed by the caller returning
        }
    }

    private static void NoOp(string segment)
    {
    }

    /// <summary>
    /// Reads output character by character and hands each completed segment (text terminated by '\r', '\n'
    /// or '\b') to the callback, while accumulating the whole output for the final result. A throwing
    /// callback must never fault the read loop, so it is guarded.
    /// </summary>
    /// <param name="onActivity">Raised on every raw chunk read, before it is split into segments: the
    /// idle-timeout watchdog's liveness signal.</param>
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

                    // '\b' as well as '\r'/'\n': DISM redraws its progress bar with backspaces and emits no newline
                    // until the step is over.
                    if (c is '\r' or '\n' or '\b')
                        Emit(segment, sink, onSegment);
                    else
                    {
                        segment.Append(c);

                        // A delimiter-free stream would grow this buffer without bound; MaxOutputChars only guards the
                        // sink after a segment completes. Force a flush once one segment alone exceeds the cap.
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

        // The bounded drain can give up on a pump that is still running (a grandchild holding the pipe
        // open), and the result is read off this same StringBuilder afterwards (ReadSink). StringBuilder is not
        // thread-safe for a concurrent read and append, so both lock on the sink itself.
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

    /// <summary>The final read of a pump's sink, under the same lock <see cref="Emit"/> appends through.</summary>
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
        if (!elevated)
            return new ResolvedExecutable(Resolve(fileName) ?? fileName, null);

        // A bare name that is not found must not fall back to itself: pinned relative, it would resolve
        // against the working directory.
        var resolved = ResolveTrusted(fileName);
        if (resolved is null)
        {
            if (!Path.IsPathFullyQualified(fileName))
                throw new UntrustedToolException(fileName, "not found in a trusted location");

            resolved = fileName;
        }

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
            // Inside the user's own profile a signature proves nothing about what is being run: catalog signing
            // is over the file's hash, so a signed binary copied under another name keeps its signature (Windows'
            // own notepad.exe renamed to winget.exe verifies as Valid/Catalog), and a signed host next to an
            // attacker's DLL is code execution as admin. Nothing in the profile is accepted on a signature, and a
            // file there that could be opened at all is not the alias. Outside the profile a directory is not
            // admin-owned just because it is not under C:\Users: a folder created under the volume root inherits
            // Authenticated Users Modify. So the location must also be one only administrators can change
            // (ElevatedPathTrust), and then the signature of the pinned bytes is the right question. The
            // locationTrust parameter lets a test vary the location alone; production never passes it.
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
    /// An uninstaller program named by a registry command, for a launch with or without administrator rights.
    /// It is not signed as a rule (most uninstallers are not), so the location carries the trust instead: for an
    /// elevated launch the file is pinned first, must lie outside every user profile, and must sit where only
    /// administrators can write (<see cref="ElevatedPathTrust"/>), judged against the pinned handle.
    /// An unelevated launch crosses no boundary and only needs an existing absolute path.
    /// </summary>
    internal static ResolvedExecutable ResolveUninstaller(
        string path, bool elevated, Func<string, FileStream, bool>? locationTrust = null)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new UntrustedToolException(path, "not an existing absolute path");

        if (!elevated)
            return new ResolvedExecutable(path, null);

        var pin = TryPin(path) ?? throw new UntrustedToolException(path, "cannot be pinned");
        try
        {
            if (IsUnderUserProfile(path) || !(locationTrust ?? ElevatedPathTrust.IsTrustedLocation)(path, pin))
                throw new UntrustedToolException(path, "not in a location only administrators can change");
        }
        catch
        {
            pin.Dispose();
            throw;
        }

        return new ResolvedExecutable(path, pin);
    }

    /// <summary>True when <see cref="ResolveUninstaller"/> would accept the file for an elevated launch now.</summary>
    internal static bool IsTrustedUninstallerLocation(string path)
    {
        try
        {
            using var executable = ResolveUninstaller(path, elevated: true);
            return true;
        }
        catch (UntrustedToolException)
        {
            return false;
        }
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
    /// The whole Users root, not only this account's own folder: C:\Users\Public is writable by every
    /// ordinary user, and so is any other profile's tree for its owner. Anything under Users has to be the
    /// genuine app-execution alias, whatever signature it carries.
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
    /// that target is pinned and held to the same signature rule as any elevated tool, plus a Microsoft
    /// publisher. The alias handle stays open, but that does not freeze the link: a reparse point can be
    /// rewritten with write-attributes access, which share modes do not block. The check is a snapshot, and
    /// the launch (<see cref="StartChild"/>) compares the image the system really mapped with the pinned
    /// target before the process may run. Null with a reason on any failure.
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
    /// True when a value can be handed over as a positional argument. An argument array keeps the shell
    /// from seeing the value, but not the target tool from reading one that begins with '-' or '/' as an
    /// option, which can redirect a package manager's download source.
    /// cmd.exe's metacharacters (&amp; | &lt; &gt; ^ %) are refused too: many of these tools are .cmd or .bat
    /// wrappers (npm, pnpm, composer, gem, pyenv) that run through cmd.exe, and .NET does not escape those
    /// characters for a batch target, so "pkg&amp;calc" would run a second command. No package id or version of
    /// those ecosystems contains them. <paramref name="allowCmdMetacharacters"/> is only for a caller whose
    /// tool is a native executable (winget), whose ids may legally contain them.
    /// </summary>
    public static bool IsSafePositionalArgument(string? value, bool allowCmdMetacharacters = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains(".."))
            return false;

        if (value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '`'))
            return false;

        if (!allowCmdMetacharacters && value.Any(c => c is '&' or '|' or '<' or '>' or '^' or '%' or '!' or '(' or ')'))
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
    public static string? Resolve(string fileName) =>
        Resolve(fileName, Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"), AliasDirectory);

    internal static string? Resolve(string fileName, string? pathVariable, string? pathExtensions, string? aliasDirectory)
    {
        if (Path.IsPathRooted(fileName))
            return File.Exists(fileName) ? fileName : null;

        var extensions = (pathExtensions ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in SearchDirectories(pathVariable, aliasDirectory, requireFullyQualified: false))
        {
            foreach (var extension in extensions)
            {
                if (ProbeCandidate(directory, fileName, extension) is { } found)
                    return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The lookup an elevated launch uses. PATHEXT is never read: in the elevated worker it comes from the
    /// user's own registry hive, and an entry such as <c>\..\cmd.exe</c> turns any tool name into a
    /// different binary in a trusted folder, which would then pass the location and signature checks and run
    /// as admin with the tool's arguments. Relative PATH entries are skipped as well, since they would
    /// resolve against the working directory.
    /// </summary>
    internal static string? ResolveTrusted(string fileName) =>
        ResolveTrusted(fileName, Environment.GetEnvironmentVariable("PATH"), AliasDirectory);

    internal static string? ResolveTrusted(string fileName, string? pathVariable, string? aliasDirectory)
    {
        if (Path.IsPathRooted(fileName))
            return Path.IsPathFullyQualified(fileName) && File.Exists(fileName) ? fileName : null;

        // A name that already carries one of the launch extensions is probed as written ("tool.exe" must
        // not turn into "tool.exe.EXE"); anything else gets the fixed set appended.
        var extension = Path.GetExtension(fileName);
        var literal = TrustedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

        foreach (var directory in SearchDirectories(pathVariable, aliasDirectory, requireFullyQualified: true))
        {
            if (literal)
            {
                if (ProbeCandidate(directory, fileName, string.Empty) is { } direct)
                    return direct;

                continue;
            }

            foreach (var suffix in TrustedExtensions)
            {
                if (ProbeCandidate(directory, fileName, suffix) is { } found)
                    return found;
            }
        }

        return null;
    }

    private static readonly string[] TrustedExtensions = [".exe", ".cmd", ".bat"];

    private static List<string> SearchDirectories(string? pathVariable, string? aliasDirectory, bool requireFullyQualified)
    {
        var directories = (pathVariable ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (aliasDirectory is { Length: > 0 })
            directories.Add(aliasDirectory);

        if (requireFullyQualified)
            directories.RemoveAll(directory => !Path.IsPathFullyQualified(directory));

        return directories;
    }

    /// <summary>
    /// The candidate path when it exists and is exactly <paramref name="directory"/> plus one file name. A
    /// name or extension carrying a separator or <c>..</c> would let the probe leave the directory it
    /// claims to search, so it is skipped.
    /// </summary>
    private static string? ProbeCandidate(string directory, string fileName, string extension)
    {
        try
        {
            var name = fileName + extension;
            var candidate = Path.Combine(directory, name);
            if (!string.Equals(Path.GetFileName(candidate), name, StringComparison.Ordinal))
                return null;

            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            // malformed PATH entry; ignore and keep probing
            return null;
        }
    }

    /// <summary>
    /// Where Windows puts the app-execution aliases of packaged apps. Probed after %PATH%, because it
    /// normally is on %PATH% and a real entry there must keep winning. winget is reachable only through its
    /// alias, so without this a %PATH% that lost this directory would make the winget source report nothing,
    /// indistinguishable from "everything is up to date". A user can write this folder, so the elevated path
    /// never trusts a stub by its place: it goes through <see cref="ResolveForPrivilege"/>, which reads the
    /// alias's content (<see cref="VerifyWingetAlias"/>).
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

        // Only what still fits: checking the cap and then appending a whole segment could take the buffer to
        // nearly twice the ceiling when a tool prints one enormous line.
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
    /// a parallel launch could inherit the write ends of a suspended launch's pipes and that launch's reader
    /// would never see the end of its output. Any launcher that inherits handles must take it too.</summary>
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
    private static IChildProcess StartChild(
        ResolvedExecutable executable, ProcessStartInfo startInfo, bool lowPriority, bool pseudoConsole = false,
        bool inJob = false)
    {
        if (pseudoConsole)
        {
            try
            {
                return StartSuspended(executable, startInfo, lowPriority, pseudoConsole: true);
            }
            catch (Exception ex) when (ex is not UntrustedToolException)
            {
                // Old Windows or any failure of the console route: the pipe launch below still works, only
                // without the progress bar.
                Diagnostics?.Invoke($"No pseudo console for '{Path.GetFileName(executable.Path)}' ({ex.GetType().Name}: {ex.Message}); using pipes.");
            }
        }

        // The job is what tells a wait for the whole tree when it is over; only the suspended launch has one.
        if (executable.AliasPin is not null || inJob)
            return StartSuspended(executable, startInfo, lowPriority, pseudoConsole: false);

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

    /// <summary>The start through <see cref="SuspendedChildProcess"/>. For the verified alias the image the
    /// system maps is checked against the pinned target; any other executable was resolved and pinned by the
    /// caller already, so only the alias carries that check.</summary>
    private static IChildProcess StartSuspended(
        ResolvedExecutable executable, ProcessStartInfo startInfo, bool lowPriority, bool pseudoConsole)
    {
        if (executable.AliasPin is null)
            return SuspendedChildProcess.Start(startInfo, null, null, lowPriority, pseudoConsole);

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

        return SuspendedChildProcess.Start(startInfo, expected, expectedPackage, lowPriority, pseudoConsole);
    }
}
