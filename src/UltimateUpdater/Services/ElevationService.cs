using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace UltimateUpdater.Services;

/// <summary>
/// Least privilege: the main app ALWAYS runs unelevated. Work that needs admin is
/// pushed into a short-lived elevated copy of this same exe (one UAC prompt per batch, not per item).
/// </summary>
public static class ElevationService
{
    /// <summary>
    /// The one elevated mode. Installs, uninstalls and cleanup all run in the same worker so the user is
    /// asked for administrator rights once per session rather than once per action.
    /// </summary>
    public const string WorkerArgument = "--elevated-worker";

    /// <summary>Every mode that must stay headless and must not touch the user's data directory.</summary>
    public static readonly string[] WorkerArguments = [WorkerArgument];

    /// <summary>
    /// True only for the dedicated elevated worker process: the one signal <see cref="ProcessRunner"/>'s
    /// untrusted-binary gate must key off. NOT the same as <see cref="IsElevated"/>: that reflects the
    /// current process's OS token, which is also true for the ordinary, never-worker main process on a
    /// machine where UAC is off or the account is the built-in Administrator: a real, observed case (a
    /// scan on such a machine hit "Refusing to run an unsigned executable with administrator rights" for
    /// every profile-local CLI tool: pnpm, mise, npm, gcloud, pyenv-win, none of which ever touch the
    /// elevated worker). Computed once, before <c>OnStartup</c>, the same way <c>App.RunningAsWorker</c>
    /// always has: this is that same check, now centralized so <c>ProcessRunner</c> can reach it too.
    /// </summary>
    public static readonly bool IsElevatedWorkerProcess = IsWorkerInvocation(Environment.GetCommandLineArgs().Skip(1));

    /// <summary>
    /// True when the FIRST argument (the executable path already removed) is a worker flag. Only the first:
    /// the app starts its worker with the flag leading, and a flag further along is just data, for example
    /// a path that happens to be spelled the same, which must never put a normal start into headless mode.
    /// The one place both the early process-wide decision and <c>OnStartup</c> take it from.
    /// </summary>
    internal static bool IsWorkerInvocation(IEnumerable<string> argumentsWithoutExecutable) =>
        argumentsWithoutExecutable.FirstOrDefault() is { } first && WorkerArguments.Contains(first, StringComparer.Ordinal);

    public static bool IsElevated
    {
        get
        {
            if (IsElevatedForTest.Value is { } overridden)
                return overridden;

            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Test seam for <see cref="IsElevated"/>, per async flow, so a test proves the unelevated and
    /// elevated paths whatever rights the test runner itself has. Null (the default) reads the real token.</summary>
    internal static readonly AsyncLocal<bool?> IsElevatedForTest = new();

    /// <summary>
    /// Held for the app's entire lifetime once <see cref="LockSelfImage"/> runs. <see cref="StartWorker"/>
    /// re-resolves <c>Environment.ProcessPath</c> by NAME every time it launches the elevated worker:
    /// a running exe can be renamed out from under itself, and a portable/per-user install sits in a
    /// location the same user can write to, so another process running as this user could swap the file,
    /// and the next admin job would launch the swap-in WITH admin rights on the back of an already-approved
    /// UAC prompt. <see cref="FileShare.Read"/> (no <see cref="FileShare.Delete"/>) makes Windows itself
    /// refuse any rename or delete of this exact file for as long as the handle stays open; prevention,
    /// not detection, so there is nothing left to check before <see cref="Process.Start"/>.
    /// </summary>
    private static FileStream? _selfImageLock;

    /// <summary>
    /// Call once, as early in startup as possible and only in the normal (non-worker) process, before
    /// anything could start an elevated job. Best effort: a machine where the image cannot be opened this
    /// way (unusual filesystem, read-only media) still runs, just without this extra protection.
    /// </summary>
    public static void LockSelfImage(LoggingService logging, string? path = null) =>
        LockSelfImageAt(logging, path ?? Environment.ProcessPath);

    internal static void LockSelfImageAt(LoggingService logging, string? path)
    {
        if (path is null)
        {
            RefuseElevation(logging, "the path of the running executable is unknown, so it cannot be locked");
            return;
        }

        try
        {
            _selfImageLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fail closed. The lock is what stops anyone renaming this exe aside and putting their own in
            // its place between the UAC prompt and the launch, and the worker is started BY PATH. Without
            // it, elevation is refused for the rest of the session rather than run on trust.
            logging.Error($"Could not lock the running executable against replacement, so elevation stays off: {ex.Message}");
            _elevationRefused = true;
        }
    }

    /// <summary>
    /// True once <see cref="LockSelfImage"/> has failed or <see cref="RefuseElevation"/> was called: no
    /// elevated worker may be started for the rest of this process's life. Production code never resets it
    /// (releasing the image lock does not), only the test seam below does.
    /// </summary>
    private static bool _elevationRefused;

    /// <summary>Whether elevation is switched off for this process; test-visible because there is no other getter.</summary>
    internal static bool IsElevationRefused => _elevationRefused;

    /// <summary>Switches elevation off for the rest of the session, for a state in which the image the
    /// worker would be started from can no longer be vouched for.</summary>
    internal static void RefuseElevation(LoggingService logging, string reason)
    {
        logging.Error($"Elevation stays off for this session: {reason}.");
        _elevationRefused = true;
    }

    /// <summary>Test seam: static state would otherwise leak between tests.</summary>
    internal static void ResetElevationRefusalForTests() => _elevationRefused = false;

    /// <summary>
    /// Releases the lock above. <see cref="SelfUpdateService"/>'s portable path renames THIS SAME FILE on
    /// purpose (moves the running exe aside, copies the verified update into its place) and must call this
    /// first, or that rename fails with the very sharing violation the lock exists to cause for everyone
    /// else. Safe to call even if the lock was never taken.
    /// </summary>
    public static void ReleaseSelfImageLock()
    {
        _selfImageLock?.Dispose();
        _selfImageLock = null;
    }

    /// <summary>
    /// Starts the elevated worker. Returns null if the user declined the UAC prompt: a declined
    /// prompt is a normal outcome, not an error to throw on.
    /// The worker is told nothing but the pipe to call back on and which process is serving it. What
    /// it should install arrives over that pipe, so there is no job file on disk for another process
    /// running as this user to swap out between being written and being read by the elevated copy.
    /// The process id is not a secret and does not need to be: it lets the worker refuse a pipe served
    /// by anyone other than the app that started it, and no other process can own that id.
    /// </summary>
    public static Process? StartWorker(string pipeName, LoggingService logging) =>
        StartWorker(WorkerArgument, pipeName, logging);

    /// <summary>
    /// Set true ONLY by the test assembly's module initializer, never by product code. A test
    /// host that reaches this far despite every seam meant to stop it first would otherwise raise a real
    /// UAC prompt on whoever's machine is running the suite; this makes that outcome impossible instead
    /// of merely unlikely.
    /// </summary>
    internal static bool BlockRealLaunchForTests { get; set; }

    /// <inheritdoc cref="StartWorker(string, LoggingService)"/>
    public static Process? StartWorker(string workerArgument, string pipeName, LoggingService logging)
    {
        if (BlockRealLaunchForTests)
            throw new InvalidOperationException(
                "Refusing to start a real elevated worker from the test process.");

        if (_elevationRefused)
        {
            logging.Error("Refusing to start the elevated worker: this copy could not be locked against replacement.");
            return null;
        }

        // The elevated copy must not come up with a profiler, a standalone GC or a startup hook named by the
        // user's environment: the runtime would load it into the administrator process before any of our code
        // runs. Dropped from this process first, so nothing is handed on.
        var scrubbed = NativeLoadEnvironment.Scrub();
        if (scrubbed.Count > 0)
            logging.Warn($"Removed runtime load variables from the environment before starting the elevated worker: {string.Join(", ", scrubbed)}");

        if (Environment.ProcessPath is not { } selfPath)
        {
            RefuseElevation(logging, "the path of the running executable is unknown");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = selfPath,
            UseShellExecute = true, // required for the runas verb
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(workerArgument);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        try
        {
            return Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED, the user dismissed the UAC prompt
            if (ex.NativeErrorCode == 1223)
            {
                logging.Info("Elevation declined by user.");
                return null;
            }

            logging.Error("Failed to start elevated worker", ex);
            throw;
        }
    }
}
