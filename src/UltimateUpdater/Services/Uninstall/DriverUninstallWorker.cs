using System.IO;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>What removing one driver package came to: the pnputil exit code (null when nothing ran) and, when the
/// worker refused or could not go on, the plain reason.</summary>
internal sealed record DriverRemovalOutcome(int? ExitCode, string? Refusal);

/// <summary>
/// The driver branch of the "uninstall-program" job inside the elevated worker. Only the package name crosses the
/// pipe, and it is a request: the worker checks the name's shape, takes a FRESH inventory, lists the package through
/// <see cref="DriverUninstallDiscovery"/> (third-party only, no Microsoft package, not a class that is never
/// offered) and plans it again; a package a present device uses is refused there. Then the package is exported to
/// the protected staging folder (no export, no removal), and pnputil deletes it with <c>/delete-driver</c>:
/// never <c>/uninstall</c> and never <c>/force</c>, so pnputil's own in-use check is a second guard.
/// pnputil is started by its absolute System32 path.
/// </summary>
internal static class DriverUninstallWorker
{
    public static async Task<DriverRemovalOutcome> RunAsync(
        string? key, IDriverInventoryProvider inventory, IUninstallFileProbe probe, LoggingService logging,
        CancellationToken cancellationToken)
    {
        if (DriverUninstallDiscovery.PackageNameOf(key) is not { } packageName)
            return Refuse("the key is not a driver package name");

        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        if (!File.Exists(pnputil))
            return Refuse("pnputil.exe was not found");

        var devices = inventory.GatherDevices(cancellationToken);
        var packages = inventory.GatherPackages(cancellationToken, name => string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase));
        var candidate = DriverUninstallDiscovery.List(devices, packages)
            .FirstOrDefault(c => string.Equals(c.Package.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
            return Refuse("the package is not a third-party driver package this app removes");

        var plan = UninstallPlanner.Plan(DriverUninstallDiscovery.ToEntry(candidate), probe);
        if (!plan.IsRunnable)
            return Refuse(plan.Refusal == UninstallRefusal.DriverInUse
                ? "a present device uses the package"
                : $"the package cannot be removed ({plan.Refusal})");

        if (plan.Method != UninstallMethod.Driver || !plan.Elevated)
            return Refuse("the package did not plan as a driver removal");

        // Held open to the end of the job: the folder cannot be renamed or replaced meanwhile. With no safe place
        // to export the package to, nothing is removed.
        using var staging = DriverBackupStaging.PrepareStagingRoot(logging);
        if (staging is null)
            return Refuse("no safe backup folder for the driver package");

        var exported = false;
        try
        {
            if (!await DriverCleanupExecutor.ExportPackageAsync(pnputil, packageName, staging, logging, CancellationToken.None))
                return Refuse("the driver package could not be backed up, so it was not removed");

            exported = true;

            // The plan's own arguments: a fixed verb and the package name checked above.
            var code = await DriverCleanupExecutor.RunPnputilForCodeAsync(
                pnputil, plan.Arguments, logging, CancellationToken.None);
            return new DriverRemovalOutcome(code, null);
        }
        finally
        {
            staging.Finish(exported, logging);
        }
    }

    private static DriverRemovalOutcome Refuse(string reason) => new(null, reason);
}
