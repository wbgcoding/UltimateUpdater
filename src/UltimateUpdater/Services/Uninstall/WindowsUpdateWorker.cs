using System.IO;
using System.Text;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>What removing one Windows update came to: the DISM exit code (null when nothing ran), DISM's own words,
/// and, when the worker refused or could not go on, the plain reason.</summary>
internal sealed record UpdateRemovalOutcome(int? ExitCode, string? Detail, string? Refusal);

/// <summary>
/// The Windows update branches of the "uninstall-program" job inside the elevated worker, both through DISM at its
/// absolute System32 path. Listing is read only (<c>get-packages</c>, <c>get-packageinfo</c>). Removal takes only the
/// package identity from the wire: the worker lists the installed updates itself, finds the identity there, works out
/// whether Windows allows its removal and plans the command again, so no path, name or switch from the pipe ever
/// reaches DISM, and a package that is no installed removable update is refused.
/// </summary>
internal static class WindowsUpdateWorker
{
    /// <summary>The job method that lists the installed updates instead of removing one.</summary>
    internal const string ListMethod = "ListWindowsUpdates";

    private const int PackageInfoParallelism = 4;

    /// <summary>Test seam: replaces the DISM process. Per async flow, so parallel tests never see each other's.</summary>
    internal static readonly AsyncLocal<Func<IReadOnlyList<string>, Task<ProcessResult>>?> DismForTest = new();

    private static string DismPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

    private static async Task<ProcessResult> RunDismAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (DismForTest.Value is { } fake)
            return await fake(arguments);

        // Latin1 keeps the bytes 1:1: only ASCII (package identities, hex codes) is read from the output. A removal
        // is not cut off when the time runs out: a half removed update is worse than a long wait.
        return await ProcessRunner.RunAsync(
            DismPath, arguments, timeout, cancellationToken, outputEncoding: Encoding.Latin1, killOnTimeout: false);
    }

    /// <summary>
    /// The installed Windows updates and whether each may be removed; null when DISM's package list cannot be read.
    /// With <paramref name="onlyIdentity"/> only that package's info is queried (the removal path needs no other).
    /// </summary>
    internal static async Task<IReadOnlyList<WindowsUpdateRow>?> ListAsync(
        IRegistryView registry, LoggingService logging, CancellationToken cancellationToken, string? onlyIdentity = null)
    {
        var table = await RunDismAsync(["/online", "/get-packages", "/format:table"], TimeSpan.FromMinutes(5), cancellationToken);
        if (table.ExitCode != 0 || table.TimedOut)
        {
            logging.Warn($"Windows update list: dism /get-packages failed with exit code {table.ExitCode}.");
            return null;
        }

        var packages = WindowsUpdateDiscovery.ParsePackages(table.StandardOutput);
        var wanted = packages
            .Where(p => WindowsUpdateDiscovery.IsWrapper(p.Identity)
                        && WindowsUpdateDiscovery.IsInstalled(p, registry)
                        && WindowsUpdateDiscovery.KbNumberOf(p.Identity) is not null
                        && WindowsUpdateDiscovery.IsUpdateReleaseType(p.ReleaseType)
                        && (onlyIdentity is null || string.Equals(p.Identity, onlyIdentity, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Identity)
            .ToList();

        var permanence = new System.Collections.Concurrent.ConcurrentDictionary<string, DismPermanence>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            wanted,
            new ParallelOptions { MaxDegreeOfParallelism = PackageInfoParallelism, CancellationToken = cancellationToken },
            async (identity, token) =>
            {
                try
                {
                    var info = await RunDismAsync(
                        ["/online", "/get-packageinfo", "/packagename:" + identity], TimeSpan.FromMinutes(2), token);
                    permanence[identity] = info.ExitCode == 0 && !info.TimedOut
                        ? WindowsUpdateDiscovery.ParsePermanence(info.StandardOutput)
                        : DismPermanence.NotRemovable;
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // a package whose info cannot be read is not offered
                    permanence[identity] = DismPermanence.NotRemovable;
                }
            });

        return WindowsUpdateDiscovery.Classify(
            packages, registry, identity => permanence.TryGetValue(identity, out var value) ? value : DismPermanence.Unknown);
    }

    /// <summary>Removes one update, as described on the class.</summary>
    internal static async Task<UpdateRemovalOutcome> RemoveAsync(
        string? key, IRegistryView registry, IUninstallFileProbe probe, LoggingService logging, CancellationToken cancellationToken)
    {
        if (WindowsUpdateDiscovery.IdentityOf(key) is not { } identity)
            return Refuse("the key is not an update package identity");

        if (DismForTest.Value is null && !File.Exists(DismPath))
            return Refuse("dism.exe was not found");

        var rows = await ListAsync(registry, logging, cancellationToken, identity);
        if (rows is null)
            return Refuse("the installed updates could not be read");

        var row = rows.FirstOrDefault(r => string.Equals(r.Identity, identity, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return Refuse("the package is not an installed update");

        // The plan comes from the worker's own row, never from the wire.
        var entry = WindowsUpdateDiscovery.ToEntries([row], [], name => name).Single();
        var plan = UninstallPlanner.Plan(entry, probe);
        if (!plan.IsRunnable)
            return Refuse(plan.Refusal == UninstallRefusal.UpdateNotRemovable
                ? "Windows does not allow removing this update"
                : $"the update cannot be removed ({plan.Refusal})");

        if (plan.Method != UninstallMethod.WindowsUpdate || !plan.Elevated)
            return Refuse("the update did not plan as a DISM removal");

        var result = await RunDismAsync(plan.Arguments, TimeSpan.FromMinutes(45), cancellationToken);
        var summary = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
        logging.Info($"Elevated update removal finished with exit code {result.ExitCode}.");
        return new UpdateRemovalOutcome(result.TimedOut ? null : result.ExitCode, summary, result.TimedOut ? "timed out" : null);
    }

    private static UpdateRemovalOutcome Refuse(string reason) => new(null, null, reason);
}
