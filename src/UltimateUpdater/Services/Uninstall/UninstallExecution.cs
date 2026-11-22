using System.IO;
using UltimateUpdater.Services.Elevation;
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

    public static async Task<ProcessResult> RunAsync(UninstallPlan plan, Action<string> onOutput, CancellationToken cancellationToken)
    {
        if (!plan.IsRunnable)
            throw new InvalidOperationException("A refused plan is never run.");

        var timeout = plan.Interactive ? InteractiveTimeout : SilentTimeout;

        // Strict when the plan says elevated and also whenever this process is the elevated worker, so the
        // worker never launches by the lighter rule whatever a plan claims.
        var elevated = plan.Elevated || ElevationService.IsElevatedWorkerProcess;

        // An administrator run never uses the user's temp folder: a setup copies itself there and starts the copy.
        var environment = elevated ? WithProtectedTemp(plan.Environment) : plan.Environment;

        // winget, msiexec and PowerShell resolve under the runner's own rules (signed, in a protected folder
        // when elevated); only a program named by a registry command needs the uninstaller rules below.
        if (plan.Method is UninstallMethod.Winget or UninstallMethod.Msi or UninstallMethod.StoreApp)
        {
            return await ProcessRunner.RunStreamingAsync(
                plan.Executable, plan.Arguments, timeout, onOutput, cancellationToken, environment);
        }

        if (CommandDenyList.IsDenied(plan.Executable, plan.Arguments, out var reason))
            throw new DeniedCommandException(reason);

        using var executable = ProcessRunner.ResolveUninstaller(plan.Executable, elevated);
        return await RunCommandAsync(executable, plan.Arguments, timeout, elevated, environment, onOutput, cancellationToken);
    }

    /// <summary>
    /// The run of a program named by a registry command. It is not killed when the time is up or a stop comes
    /// (ending a removal halfway leaves a program neither installed nor gone); it is left running and the result
    /// says it timed out. The wait is for the whole tree: a setup that hands its work to a copy of itself and
    /// exits has not finished when its first process has.
    /// </summary>
    internal static Task<ProcessResult> RunCommandAsync(
        ResolvedExecutable executable, IReadOnlyList<string> arguments, TimeSpan timeout, bool elevated,
        IReadOnlyDictionary<string, string>? environment, Action<string> onOutput, CancellationToken cancellationToken) =>
        ProcessRunner.RunResolvedAsync(
            executable, arguments, timeout, cancellationToken, environment, onOutput,
            idleTimeout: null, outputEncoding: null, elevated,
            scrubEnvironment: elevated || ElevationService.IsElevated,
            killOnTimeout: false, waitForTree: true);

    /// <summary>
    /// TEMP and TMP of an administrator run: the system's own temp folder under the Windows folder, which only
    /// administrators can write. Refused when it is missing or not protected, never replaced by a folder the user owns.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> WithProtectedTemp(
        IReadOnlyDictionary<string, string>? environment, string? windowsDirectory = null,
        Func<string, bool>? isTrustedDirectory = null)
    {
        var folder = Path.Combine(windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemTemp");
        if (!(isTrustedDirectory ?? ElevatedPathTrust.IsTrustedDirectory)(folder))
            throw new InvalidOperationException("This Windows has no protected temp folder, so the removal is not run with administrator rights.");

        var result = new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = folder,
            ["TMP"] = folder,
        };
        return result;
    }
}
