using System.Text.Json;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Platform;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>What a removed program left behind: the program and every trace the scan found (or, for a removal, the ones the user ticked).</summary>
public sealed record UninstallLeftoverGroup(InstalledProgramEntry Entry, IReadOnlyList<LeftoverItem> Items);

/// <summary>What one leftover removal did: items removed, items kept (failed or no longer found) and bytes freed.</summary>
public sealed record UninstallLeftoverResult(int Removed, int Skipped, long FreedBytes);

/// <summary>
/// The uninstaller's use of the leftover scan and the leftover remover. The scan runs in this process (reading
/// only); the removal splits by lane: the user's own places are removed here, the machine-wide ones by the
/// elevated helper through the "leftovers" command, which scans again by itself. A failed or missing helper
/// leaves the machine items where they are.
/// </summary>
internal sealed class UninstallLeftoverService(
    LoggingService logging, ElevatedHost? elevated, Func<RegistryCleanupSettings>? registrySettings = null)
{
    private static readonly JsonSerializerOptions WorkerJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Func<RegistryCleanupSettings> _registrySettings = registrySettings ?? (() => new RegistryCleanupSettings());

    /// <summary>The traces of every removed program, one group per program that has any. Reads the machine: never on the UI thread.</summary>
    internal Task<IReadOnlyList<UninstallLeftoverGroup>> FindAsync(
        IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            var claims = await ReadClaimsAsync(cancellationToken);
            var scanner = LeftoverScanner.ForThisMachine();
            var groups = new List<UninstallLeftoverGroup>();
            foreach (var entry in removed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = scanner.Scan(LeftoverSubject.From(entry), claims, onlyLane: null, cancellationToken);
                if (items.Count > 0)
                    groups.Add(new UninstallLeftoverGroup(entry, items));
            }

            return (IReadOnlyList<UninstallLeftoverGroup>)groups;
        },
        cancellationToken);

    /// <summary>Removes the items of every group (the caller passes the ticked ones). Never throws for a lane that failed: its items count as skipped.</summary>
    internal Task<UninstallLeftoverResult> RemoveAsync(
        IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            var claims = await ReadClaimsAsync(cancellationToken);
            var settings = _registrySettings();
            var roots = LeftoverRoots.Current();
            var remover = new LeftoverRemover(LeftoverScanner.ForThisMachine(), roots, logging);
            int removed = 0, skipped = 0;
            long freed = 0;

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subject = LeftoverSubject.From(group.Entry);

                var userPicks = Picks(group, LeftoverLane.User);
                if (userPicks.Count > 0)
                {
                    try
                    {
                        var result = await remover.RemoveAsync(
                            subject, claims, LeftoverLane.User, userPicks,
                            settings.BackupFolder, settings.RetentionCount, cancellationToken);
                        removed += result.Removed;
                        skipped += result.Skipped;
                        freed += result.FreedBytes;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logging.Warn($"Leftovers of '{group.Entry.Name}' could not be removed: {ex.Message}");
                        skipped += userPicks.Count;
                    }
                }

                var machinePicks = Picks(group, LeftoverLane.Machine);
                if (machinePicks.Count > 0)
                {
                    var machine = await RemoveMachineAsync(subject, machinePicks, claims, settings, cancellationToken);
                    removed += machine.Removed;
                    skipped += machine.Skipped;
                    freed += machine.FreedBytes;
                }
            }

            return new UninstallLeftoverResult(removed, skipped, freed);
        },
        cancellationToken);

    private static List<LeftoverSelectionDto> Picks(UninstallLeftoverGroup group, LeftoverLane lane) =>
        [.. group.Items.Where(item => item.Lane == lane)
            .Select(item => new LeftoverSelectionDto { Kind = item.Kind, Path = item.Path })];

    private async Task<LeftoverClaims> ReadClaimsAsync(CancellationToken cancellationToken)
    {
        var inventory = new InstalledProgramInventory(
            WindowsRegistryView.Instance, new AppxPackageReader(), new WingetListReader());
        var entries = await inventory.ReadAsync(cancellationToken);
        return LeftoverClaims.FromRegistry(WindowsRegistryView.Instance).With(entries, excludeKey: null);
    }

    /// <summary>The command the elevated helper gets: the removed program, the ticked machine items (a pointer, the helper scans again itself) and what is still installed.</summary>
    internal static CleanupCommand BuildMachineCommand(
        LeftoverSubject subject, IReadOnlyList<LeftoverSelectionDto> picks, LeftoverClaims claims, RegistryCleanupSettings settings) => new()
    {
        Command = "leftovers",
        Language = LocalizationService.Instance.CurrentLanguage,
        RegistryBackupFolder = settings.BackupFolder,
        RegistryBackupRetentionCount = settings.RetentionCount,
        CallerSid = RecycleBinScanner.TryGetOwnSid(),
        LeftoverSubject = subject,
        SelectedLeftovers = [.. picks.Take(CleanupWorker.MaxSelectedLeftovers)],
        LeftoverClaimedNames = [.. claims.Names.Take(LeftoverClaims.MaxClaims)],
        LeftoverClaimedPublishers = [.. claims.Publishers.Take(LeftoverClaims.MaxClaims)],
        LeftoverClaimedPaths = [.. claims.Paths.Take(LeftoverClaims.MaxClaims)],
    };

    internal async Task<UninstallLeftoverResult> RemoveMachineAsync(
        LeftoverSubject subject, IReadOnlyList<LeftoverSelectionDto> picks, LeftoverClaims claims,
        RegistryCleanupSettings settings, CancellationToken cancellationToken)
    {
        if (elevated is null)
            return new UninstallLeftoverResult(0, picks.Count, 0);

        CleanupWorkerMessage? result = null;
        var command = BuildMachineCommand(subject, picks, claims, settings);

        bool OnLine(string line)
        {
            CleanupWorkerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<CleanupWorkerMessage>(line, WorkerJson);
            }
            catch (JsonException)
            {
                return true;    // a malformed worker line is ignored, never fatal
            }

            switch (message?.Type)
            {
                case "done":
                    return false;
                case "log":
                    logging.Info($"cleanup worker: {message.Text}");
                    break;
                case "result" when message.Id == CleanupWorker.LeftoversId:
                    result = message;
                    break;
            }

            return true;
        }

        var ok = await elevated.RunAsync("cleanup", command, OnLine, cancellationToken);
        if (!ok || result is null || result.Failed)
        {
            logging.Warn($"Leftovers of '{subject.Name}' in the machine-wide places could not be removed (helper unavailable or failed).");
            return new UninstallLeftoverResult(0, picks.Count, 0);
        }

        return new UninstallLeftoverResult(result.Files, result.Skipped, result.Freed);
    }
}
