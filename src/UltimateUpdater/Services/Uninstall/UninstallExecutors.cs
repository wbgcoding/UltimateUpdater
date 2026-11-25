using System.IO;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Elevation;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Machine-scope removals, run by the elevated worker. Only the entry key, the method and (for winget) the id
/// cross the pipe; the worker reads the command itself and plans again (see <see cref="ProgramUninstallWorker"/>).
/// A session that cannot be started or that ended early is a result with no exit code, never a guess.
/// </summary>
public sealed class ElevatedUninstallExecutor(ElevatedHost host, LoggingService logging) : IUninstallExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<UninstallExecutionResult> RunAsync(
        InstalledProgramEntry entry, UninstallPlan plan, CancellationToken cancellationToken)
    {
        var job = new UninstallProgramJob
        {
            Key = entry.Key,
            Method = plan.Method.ToString(),
            WingetId = plan.Method == UninstallMethod.Winget ? entry.WingetId : null,
        };

        int? exitCode = null;
        string? detail = null;
        string? error = null;
        var reachedDone = false;

        bool OnLine(string line)
        {
            InstallProgressMessage? message = null;
            try
            {
                message = JsonSerializer.Deserialize<InstallProgressMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // hostile-input rule: a malformed worker line is ignored, never fatal
            }

            switch (message?.Type)
            {
                case "done":
                    reachedDone = true;
                    return false;
                case "log":
                    logging.Info($"worker: {message.Status}");
                    break;
                case "result":
                    exitCode = message.ExitCode;
                    detail = message.Detail;
                    error = message.Error;
                    break;
            }

            return true;
        }

        var ok = await host.RunAsync("uninstall-program", job, OnLine, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return new UninstallExecutionResult(exitCode, detail, Cancelled: true);

        if (!ok || !reachedDone)
            return new UninstallExecutionResult(null, "The elevated helper is not available.");

        return new UninstallExecutionResult(exitCode, detail ?? error);
    }
}

/// <summary>Removals that need no administrator rights (user-scope setups, Store apps), run in this process.</summary>
public sealed class InProcessUninstallExecutor(LoggingService logging) : IUninstallExecutor
{
    public async Task<UninstallExecutionResult> RunAsync(
        InstalledProgramEntry entry, UninstallPlan plan, CancellationToken cancellationToken)
    {
        // The machine lane owns every plan that needs elevation; an unelevated launch of one would simply fail
        // half way, so it is refused here instead.
        if (plan.Elevated)
            throw new InvalidOperationException("An elevated plan is not run in the unelevated process.");

        try
        {
            var result = await UninstallExecution.RunAsync(plan, line => logging.Info($"uninstall: {line}"), cancellationToken, logging);
            return result.TimedOut
                ? new UninstallExecutionResult(null, "The uninstaller did not finish in time.")
                : new UninstallExecutionResult(
                    result.ExitCode, ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode));
        }
        catch (Exception ex) when (ex is UntrustedToolException or Security.DeniedCommandException)
        {
            return new UninstallExecutionResult(null, ex.Message);
        }
    }
}

/// <summary>
/// Looks the entry up again after its removal: a program entry is gone when its key no longer lists under its
/// Uninstall root, a Store app when it is no longer in the package list. Some setups finish after they exit, so a
/// positive answer is awaited a few times; a list that cannot be read counts as not gone, never as removed.
/// </summary>
public sealed class UninstallGoneCheck(
    IRegistryView registry, IAppxPackageSource appx, int attempts = 5, TimeSpan? delay = null,
    string? windowsDirectory = null)
{
    private readonly TimeSpan _delay = delay ?? TimeSpan.FromMilliseconds(1500);

    public async Task<bool> IsGoneAsync(InstalledProgramEntry entry, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (await IsGoneOnceAsync(entry, cancellationToken))
                return true;

            if (attempt < attempts - 1)
                await Task.Delay(_delay, cancellationToken);
        }

        return false;
    }

    private async Task<bool> IsGoneOnceAsync(InstalledProgramEntry entry, CancellationToken cancellationToken)
    {
        // A driver package is published as %windir%\INF\oemNN.inf; pnputil removes that file with the package.
        if (entry.Kind == UninstallEntryKind.Driver)
        {
            return DriverUninstallDiscovery.PackageNameOf(entry.Key) is { } packageName
                   && !File.Exists(Path.Combine(
                       windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", packageName));
        }

        // An update is gone when the servicing stack no longer lists it as installed; an unreadable list is never gone.
        if (entry.Kind == UninstallEntryKind.WindowsUpdate)
        {
            return WindowsUpdateDiscovery.IdentityOf(entry.Key) is { } identity
                   && WindowsUpdateDiscovery.IsGone(registry, identity);
        }

        if (entry.Kind == UninstallEntryKind.StoreApp)
        {
            var apps = await appx.ReadAsync(cancellationToken);
            return apps is not null
                   && !apps.Any(a => string.Equals(a.PackageFullName, entry.PackageFullName, StringComparison.OrdinalIgnoreCase));
        }

        var split = entry.Key.LastIndexOf('\\');
        if (split <= 0)
            return false;

        var parent = entry.Key[..split];
        var leaf = entry.Key[(split + 1)..];
        return !registry.SubKeyNames(parent).Contains(leaf, StringComparer.OrdinalIgnoreCase);
    }
}
