using System.IO;
using UltimateUpdater.Services.App;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Elevation;
using UltimateUpdater.Services.Install;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Security;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Runs one planned removal. Shared by the app (user-scope entries) and the elevated worker (machine-scope
/// entries); the plan is always one the planner made, and the program is checked again here just before it
/// starts: a program that runs elevated must sit where only administrators can change it.
/// </summary>
internal static class UninstallExecution
{
    /// <summary>A silent removal that has not finished by now is stuck.</summary>
    internal static readonly TimeSpan SilentTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A vendor window waits for a person, so the limit is long.</summary>
    internal static readonly TimeSpan InteractiveTimeout = TimeSpan.FromHours(2);

    public static async Task<ProcessResult> RunAsync(
        UninstallPlan plan, Action<string> onOutput, CancellationToken cancellationToken, LoggingService? logging = null,
        CancellationToken stopWhenIdle = default)
    {
        if (!plan.IsRunnable)
            throw new InvalidOperationException("A refused plan is never run.");

        var timeout = plan.Interactive ? InteractiveTimeout : SilentTimeout;

        // Strict when the plan says elevated and also whenever this process is the elevated worker, so the
        // worker never launches by the lighter rule whatever a plan claims.
        var elevated = plan.Elevated || ElevationService.IsElevatedWorkerProcess;

        // An administrator run never uses the user's temp folder: a setup copies itself there and starts the copy.
        using var protectedTemp = elevated ? AcquireProtectedTemp(logging ?? new LoggingService(_ => { })) : null;
        var environment = protectedTemp is null ? plan.Environment : WithProtectedTemp(plan.Environment, protectedTemp.Folder);

        // winget, msiexec and PowerShell resolve under the runner's own rules (signed, in a protected folder
        // when elevated); only a program named by a registry command needs the uninstaller rules below.
        if (plan.Method is UninstallMethod.Winget or UninstallMethod.Msi or UninstallMethod.StoreApp)
        {
            // winget starts a vendor uninstaller without a silent switch when the manifest offers none; its window is
            // invisible in an elevated session, so the run would sit still until the timeout. A tree with no CPU and no
            // I/O for the stall window is ended with an explained failure instead, and so is one that is idle when
            // the person presses Stop (a working removal is left to finish).
            var stallWatch = plan.Method == UninstallMethod.Winget ? StallWatchOptions.ForUninstall : null;
            return await ProcessRunner.RunStreamingAsync(
                plan.Executable, plan.Arguments, timeout, onOutput, cancellationToken, environment,
                stallWatch: stallWatch, stallIsUninstall: stallWatch is not null, stopWhenIdle: stopWhenIdle);
        }

        if (CommandDenyList.IsDenied(plan.Executable, plan.Arguments, out var reason))
            throw new DeniedCommandException(reason);

        using var executable = ProcessRunner.ResolveUninstaller(plan.Executable, elevated);
        var result = await RunCommandAsync(
            executable, plan.Arguments, timeout, elevated, environment, onOutput, cancellationToken, stopWhenIdle,
            interactive: plan.Interactive);

        KeepTempWhileRunning(protectedTemp, result);
        return result;
    }

    /// <summary>A program left running keeps using its temp folder: after the time is up, and also when the wait
    /// gave up on an idle leftover (an NSIS copy of the uninstaller lives in that folder).</summary>
    internal static void KeepTempWhileRunning(ProtectedTemp? protectedTemp, ProcessResult result)
    {
        if (result.TimedOut || result.TreeLeftRunning)
            protectedTemp?.Keep();
    }

    /// <summary>
    /// The run of a program named by a registry command. It is not killed when the time is up (ending a removal
    /// halfway leaves a program neither installed nor gone); it is left running and the result says it timed out.
    /// A stop by the person ends it only while its whole tree sits idle (a window waiting for input that nobody can
    /// see); one that is still working finishes. The wait is for the whole tree: a setup that hands its work to a copy
    /// of itself and exits has not finished when its first process has. A leftover that sits idle does not hold
    /// a silent removal for the whole timeout; an interactive one (a vendor window a person reads) is waited for
    /// until it is gone, the timeout or a stop.
    /// </summary>
    internal static Task<ProcessResult> RunCommandAsync(
        ResolvedExecutable executable, IReadOnlyList<string> arguments, TimeSpan timeout, bool elevated,
        IReadOnlyDictionary<string, string>? environment, Action<string> onOutput, CancellationToken cancellationToken,
        CancellationToken stopWhenIdle = default, bool interactive = false) =>
        ProcessRunner.RunResolvedAsync(
            executable, arguments, timeout, cancellationToken, environment, onOutput,
            idleTimeout: null, outputEncoding: null, elevated,
            scrubEnvironment: elevated || ElevationService.IsElevated,
            stallWatch: StallWatchOptions.StopOnly, killOnTimeout: false, waitForTree: true,
            stallIsUninstall: true, stopWhenIdle: stopWhenIdle, endTreeWhenIdle: !interactive);

    /// <summary>
    /// The environment with TEMP and TMP pointing at <paramref name="folder"/>, a folder only administrators can write.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> WithProtectedTemp(
        IReadOnlyDictionary<string, string>? environment, string folder) =>
        new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = folder,
            ["TMP"] = folder,
        };

    /// <summary>
    /// The temp folder of an administrator run: the system's own temp folder under the Windows folder, which only
    /// administrators can write. Windows 10 before the 2022 servicing has no such folder; there a fresh folder
    /// under the ProgramData root is made the way the leftover quarantine root is (owner Administrators,
    /// administrator-only ACL, inheritance off, pinned) and held to the same trust check. A system temp folder that
    /// exists but is not protected is refused, and so is a fallback that fails any check: never a folder the user owns.
    /// </summary>
    internal static ProtectedTemp AcquireProtectedTemp(
        LoggingService logging, string? windowsDirectory = null, Func<string, bool>? isTrustedDirectory = null)
    {
        var isTrusted = isTrustedDirectory ?? ElevatedPathTrust.IsTrustedDirectory;
        var systemTemp = Path.Combine(windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemTemp");
        if (Directory.Exists(systemTemp))
        {
            if (!isTrusted(systemTemp))
                throw new InvalidOperationException("The system temp folder of this Windows is not protected, so the removal is not run with administrator rights.");

            return new ProtectedTemp(systemTemp, null);
        }

        var staging = DriverBackupStaging.PrepareStagingRoot(
            logging, "Temp", usersCanRead: false, folderName: Guid.NewGuid().ToString("N"), label: "Uninstall temp");
        if (staging is null)
            throw new InvalidOperationException("This Windows has no protected temp folder and none could be made, so the removal is not run with administrator rights.");

        if (!isTrusted(staging.BackupRoot))
        {
            staging.Dispose();
            throw new InvalidOperationException("The temp folder made for the removal failed the trust check, so it is not run with administrator rights.");
        }

        logging.Info("This Windows has no system temp folder; the removal gets its own administrator-only temp folder.");
        return new ProtectedTemp(staging.BackupRoot, staging);
    }

    /// <summary>The temp folder of one administrator run. A folder made for the run is removed again at the end.</summary>
    internal sealed class ProtectedTemp(string folder, DriverBackupStaging.StagingFolder? staging) : IDisposable
    {
        private bool _keep;

        public string Folder { get; } = folder;

        /// <summary>Leaves a folder made for the run in place (a program started in it is still running).</summary>
        public void Keep() => _keep = true;

        public void Dispose()
        {
            if (staging is null)
                return;

            var path = staging.BackupRoot;
            var safe = staging.IsStillSafe();
            staging.Dispose();
            if (!safe || _keep)
                return;

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A program that is still running keeps its files; the folder is administrator-only and harmless.
                TryNote(ex);
            }
        }

        private static void TryNote(Exception ex) => System.Diagnostics.Debug.WriteLine(ex.Message);
    }
}
