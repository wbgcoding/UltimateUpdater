using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The elevated half of the driver cleanup category: re-runs <see cref="DriverCleanupDiscovery"/> against a
/// FRESH inventory and acts only on rows that still match, never on the names the unelevated app sent over
/// the pipe. Every package name is re-validated against <see
/// cref="DriverCleanupDiscovery.IsOemPackageName"/> immediately before it reaches pnputil, and every
/// instance id must still be a live member of a freshly discovered "disconnected devices" row: nothing
/// arriving over the pipe is ever passed to pnputil unchecked. pnputil always runs by its absolute
/// <c>%windir%\System32</c> path, never resolved from PATH, and never with <c>/force</c>: a package
/// genuinely still in use is reported as skipped, not forced out. This class runs AS ADMIN and must never
/// resolve <see cref="AppPaths"/> or take an export destination from its caller: both would trust a path the
/// unelevated user controls. Exports go into its own ACL-protected staging root under ProgramData instead
/// (<see cref="DriverBackupStaging"/>); the unelevated caller copies that folder into the data folder
/// afterwards, best effort.
/// </summary>
public static class DriverCleanupExecutor
{
    public static async Task<CleanupOutcome> CleanAsync(
        IDriverInventoryProvider inventoryProvider,
        IReadOnlyCollection<string> selectedDeviceInstanceIds,
        IReadOnlyCollection<string> selectedPackageNames,
        LoggingService logging,
        CancellationToken cancellationToken)
    {
        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        if (!File.Exists(pnputil))
        {
            logging.Error("Driver cleanup: pnputil.exe was not found; nothing was removed.");
            return new CleanupOutcome(0, 0, selectedDeviceInstanceIds.Count + selectedPackageNames.Count);
        }

        var devices = inventoryProvider.GatherDevices(cancellationToken);
        var packages = inventoryProvider.GatherPackages(cancellationToken);

        var devicesToRemove = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, packages)
            .Where(c => selectedDeviceInstanceIds.Contains(c.InstanceId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var packagesToDelete = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages)
            .Where(c => selectedPackageNames.Contains(c.PackageName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (devicesToRemove.Count == 0 && packagesToDelete.Count == 0)
            return new CleanupOutcome(0, 0, 0);

        // Held open to the end of the job: the folder cannot be renamed or replaced meanwhile.
        using var staging = DriverBackupStaging.PrepareStagingRoot(logging);
        if (staging is null)
        {
            // Fail closed: with nowhere safe to export a package first, nothing is removed. Every row is
            // reported as skipped rather than silently deleting without the backup the consequence text
            // promises.
            logging.Error("Driver cleanup: no safe backup staging folder; nothing was removed.");
            return new CleanupOutcome(0, 0, devicesToRemove.Count + packagesToDelete.Count);
        }

        var folderName = staging.FolderName;
        var boundDevices = devices.ToList();
        // Several disconnected devices often share one oemNN.inf: it is backed up once, not once per device
        // (a GPU package can be a gigabyte), and a package the device loop already removed is not exported
        // again, and miscounted as "could not back up", when it is also selected as superseded.
        var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        var skipped = 0;

        foreach (var device in devicesToRemove)
        {
            // Returned, not thrown: the worker discards a cancelled call's outcome, and devices already
            // removed plus the backup made for them are real work the user must be told about.
            if (cancellationToken.IsCancellationRequested)
                return new CleanupOutcome(0, deleted, skipped, WasStopped: true, DriverBackupFolderName: folderName);

            if (!DriverCleanupDiscovery.IsOemPackageName(device.PackageName))
            {
                logging.Warn($"Driver cleanup: '{device.PackageName}' does not look like a driver package name; skipping '{device.InstanceId}'.");
                skipped++;
                continue;
            }

            if (!exported.Contains(device.PackageName)
                && !await ExportPackageAsync(pnputil, device.PackageName, staging, logging, CancellationToken.None))
            {
                skipped++;
                continue;
            }

            exported.Add(device.PackageName);

            var removed = await RunPnputilAsync(pnputil, ["/remove-device", device.InstanceId], logging, CancellationToken.None);
            if (!removed)
            {
                skipped++;
                continue;
            }

            deleted++;
            boundDevices.RemoveAll(d => string.Equals(d.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase));

            // The device's own package may now be unused (no other device still bound to it); a package
            // that still is stays exactly where it is, pnputil's own in-use check refuses it below and
            // that refusal is counted as skipped, never as a failure of this step. The bindings come from
            // the inventory taken once above, minus the devices removed since: a device that bound to the
            // package meanwhile is still caught by that same in-use check, so enumerating every device
            // again after each removal would buy nothing.
            var stillBound = boundDevices
                .Any(d => !string.IsNullOrEmpty(d.InfPath)
                    && string.Equals(Path.GetFileName(d.InfPath), device.PackageName, StringComparison.OrdinalIgnoreCase));

            if (!stillBound && await RunPnputilAsync(pnputil, ["/delete-driver", device.PackageName], logging, CancellationToken.None))
                deletedPackages.Add(device.PackageName);
        }

        foreach (var package in packagesToDelete)
        {
            // Returned, not thrown: the worker discards a cancelled call's outcome, and devices already
            // removed plus the backup made for them are real work the user must be told about.
            if (cancellationToken.IsCancellationRequested)
                return new CleanupOutcome(0, deleted, skipped, WasStopped: true, DriverBackupFolderName: folderName);

            if (!DriverCleanupDiscovery.IsOemPackageName(package.PackageName))
            {
                logging.Warn($"Driver cleanup: '{package.PackageName}' does not look like a driver package name; skipping.");
                skipped++;
                continue;
            }

            if (deletedPackages.Contains(package.PackageName))
            {
                // Already removed together with its last device above.
                deleted++;
                continue;
            }

            if (!exported.Contains(package.PackageName)
                && !await ExportPackageAsync(pnputil, package.PackageName, staging, logging, CancellationToken.None))
            {
                skipped++;
                continue;
            }

            // No /force: a package a device still quietly depends on is reported by pnputil as skipped
            // rather than being torn out from under it.
            var removed = await RunPnputilAsync(pnputil, ["/delete-driver", package.PackageName], logging, CancellationToken.None);
            if (removed)
                deleted++;
            else
                skipped++;
        }

        return new CleanupOutcome(0, deleted, skipped, DriverBackupFolderName: folderName);
    }

    private static async Task<bool> ExportPackageAsync(
        string pnputil, string packageName, DriverBackupStaging.StagingFolder staging, LoggingService logging,
        CancellationToken cancellationToken)
    {
        // The staging root itself, with its explicit ACL, was already created (and verified free of any
        // reparse point) by DriverBackupStaging.PrepareStagingRoot before the first row was ever attempted;
        // re-creating it here would only risk quietly replacing that ACL with an inherited one. It is checked
        // again before EVERY export, though: a row whose destination fails verification is skipped, never
        // removed without its backup.
        if (!staging.IsStillSafe())
        {
            logging.Error($"Driver cleanup: the backup folder failed verification; '{packageName}' was not backed up and not removed.");
            return false;
        }

        return await RunPnputilAsync(
            pnputil, ["/export-driver", packageName, staging.BackupRoot], logging, cancellationToken,
            failureMessage: $"Driver cleanup: could not back up '{packageName}'; it was not removed.");
    }

    /// <summary>pnputil passes the setup API's two "done, restart pending" codes through (3010
    /// ERROR_SUCCESS_REBOOT_REQUIRED, 1641 ERROR_SUCCESS_REBOOT_INITIATED); the change itself happened, so a
    /// device removed that way must still get its now-unbound package deleted.</summary>
    internal static bool IsPnputilSuccess(int exitCode) => exitCode is 0 or 3010 or 1641;

    private static async Task<bool> RunPnputilAsync(
        string pnputil, IReadOnlyList<string> arguments, LoggingService logging, CancellationToken cancellationToken,
        string? failureMessage = null)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(pnputil, arguments, TimeSpan.FromMinutes(2), cancellationToken);
            if (IsPnputilSuccess(result.ExitCode))
            {
                if (result.ExitCode != 0)
                    logging.Info($"Driver cleanup: pnputil {string.Join(' ', arguments)} succeeded; a restart is needed to finish it (exit code {result.ExitCode}).");

                return true;
            }

            logging.Warn(failureMessage ?? $"Driver cleanup: pnputil {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logging.Warn($"Driver cleanup: pnputil call failed: {ex.Message}");
            return false;
        }
    }
}
