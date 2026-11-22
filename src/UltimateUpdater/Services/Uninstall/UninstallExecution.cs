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

        // winget, msiexec and PowerShell resolve under the runner's own rules (signed, in a protected folder
        // when elevated); only a program named by a registry command needs the uninstaller rules below.
        if (plan.Method is UninstallMethod.Winget or UninstallMethod.Msi or UninstallMethod.StoreApp)
        {
            return await ProcessRunner.RunStreamingAsync(
                plan.Executable, plan.Arguments, timeout, onOutput, cancellationToken, plan.Environment);
        }

        if (CommandDenyList.IsDenied(plan.Executable, plan.Arguments, out var reason))
            throw new DeniedCommandException(reason);

        // Strict when the plan says elevated and also whenever this process is the elevated worker, so the
        // worker never launches by the lighter rule whatever a plan claims.
        var elevated = plan.Elevated || ElevationService.IsElevatedWorkerProcess;
        using var executable = ProcessRunner.ResolveUninstaller(plan.Executable, elevated);
        return await ProcessRunner.RunResolvedAsync(
            executable, plan.Arguments, timeout, cancellationToken, plan.Environment, onOutput,
            idleTimeout: null, outputEncoding: null, elevated,
            scrubEnvironment: elevated || ElevationService.IsElevated);
    }
}
