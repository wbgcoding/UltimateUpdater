using System.IO;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Elevation;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>What the app asks the elevated worker to put back: the machine quarantine folder, by full path or by
/// name. Nothing here is a command, and the worker accepts only a folder it could have created itself.</summary>
public sealed class UninstallRestoreJob
{
    public string? Folder { get; set; }
}

/// <summary>
/// The "uninstall-restore" job inside the elevated worker: puts back the machine items (Program Files, ProgramData,
/// HKLM, all-users shortcuts) of one quarantine folder. The folder is resolved fully before anything is read: it
/// must be a direct child of the machine quarantine root with a quarantine folder name, so a <c>..</c> segment, a
/// junction or a path elsewhere is refused. Every target is checked again by <see cref="QuarantineRestorer"/>.
/// </summary>
internal static class QuarantineRestoreWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task RunJobAsync(
        JsonElement payload, StreamWriter writer, string jobId, LoggingService logging, CancellationToken cancellationToken,
        LeftoverRoots? roots = null, Func<string, CancellationToken, Task<bool>>? importRegistryFile = null)
    {
        UninstallRestoreJob? job;
        try
        {
            job = payload.Deserialize<UninstallRestoreJob>(JsonOptions);
        }
        catch (JsonException)
        {
            job = null;
        }

        var folder = QuarantineStore.ResolveFolder(QuarantineStore.MachineRoot(), job?.Folder);
        if (folder is null)
        {
            logging.Warn("Leftover restore refused: the folder is not one of the machine quarantine folders.");
            ElevatedWorker.Send(writer, jobId, new InstallProgressMessage
            {
                Type = "result", SourceId = "quarantine", Success = false, FailedCount = 1,
                Error = "the folder is not a machine quarantine folder",
            });
            ElevatedWorker.Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return;
        }

        var restorer = new QuarantineRestorer(logging, roots ?? LeftoverRoots.Current(), importRegistryFile);
        var result = await restorer.RestoreAsync(folder, LeftoverLane.Machine, cancellationToken);
        logging.Info($"Leftover restore: {result.Restored} restored, {result.AlreadyPresent} already present, {result.Failed} failed.");

        ElevatedWorker.Send(writer, jobId, new InstallProgressMessage
        {
            Type = "result",
            SourceId = "quarantine",
            Success = result.Failed == 0,
            RestoredCount = result.Restored,
            PresentCount = result.AlreadyPresent,
            FailedCount = result.Failed,
        });
        ElevatedWorker.Send(writer, jobId, new InstallProgressMessage { Type = "done" });
    }
}

/// <summary>
/// The app's side of "Restore leftovers": the user items of a quarantine are put back here, in this process; the
/// machine items go to the elevated helper as an "uninstall-restore" job (one UAC prompt at most, the same helper as
/// the removal). A folder that is gone (pruned after 14 days, deleted by hand) counts as nothing to restore.
/// </summary>
internal sealed class UninstallRestoreService(LoggingService logging, ElevatedHost? elevated)
{
    private static readonly JsonSerializerOptions WireJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="userFolder">The user quarantine folder name of the history entry, or null.</param>
    /// <param name="machineFolder">The machine quarantine folder name of the history entry, or null.</param>
    internal Task<UninstallRestoreOutcome> RestoreAsync(
        string? userFolder, string? machineFolder, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            var result = QuarantineRestoreResult.None;
            var gone = false;
            var machineSkipped = false;

            if (userFolder is not null)
            {
                if (QuarantineStore.ResolveFolder(QuarantineStore.UserRoot(), userFolder) is { } folder)
                    result = result.Plus(await new QuarantineRestorer(logging, LeftoverRoots.Current())
                        .RestoreAsync(folder, LeftoverLane.User, cancellationToken));
                else
                    gone = true;
            }

            if (machineFolder is not null)
            {
                if (elevated is null)
                {
                    machineSkipped = true;
                }
                else
                {
                    var machine = await RestoreMachineAsync(machineFolder, cancellationToken);
                    if (machine is null)
                        machineSkipped = true;
                    else
                        result = result.Plus(machine);
                }
            }

            return new UninstallRestoreOutcome(result, gone, machineSkipped);
        },
        cancellationToken);

    private async Task<QuarantineRestoreResult?> RestoreMachineAsync(string folderName, CancellationToken cancellationToken)
    {
        InstallProgressMessage? result = null;
        var reachedDone = false;

        bool OnLine(string line)
        {
            InstallProgressMessage? message = null;
            try
            {
                message = JsonSerializer.Deserialize<InstallProgressMessage>(line, WireJson);
            }
            catch (JsonException)
            {
                // a malformed worker line is ignored, never fatal
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
                    result = message;
                    break;
            }

            return true;
        }

        var job = new UninstallRestoreJob { Folder = Path.Combine(QuarantineStore.MachineRoot(), folderName) };
        var ok = await elevated!.RunAsync("uninstall-restore", job, OnLine, cancellationToken);
        if (!ok || !reachedDone || result is null)
        {
            logging.Warn("Leftovers in the machine-wide places could not be restored (helper unavailable or failed).");
            return null;
        }

        return new QuarantineRestoreResult(result.RestoredCount, result.PresentCount, result.FailedCount);
    }
}

/// <summary>What a restore did in total. <see cref="FolderGone"/>: the user quarantine folder no longer exists.
/// <see cref="MachineNotReached"/>: the machine items were not touched (helper declined or unavailable).</summary>
internal sealed record UninstallRestoreOutcome(QuarantineRestoreResult Result, bool FolderGone, bool MachineNotReached)
{
    /// <summary>Everything asked for was put back or was already there.</summary>
    internal bool IsClean => Result.Failed == 0 && !MachineNotReached;

    /// <summary>The text the user reads after a restore.</summary>
    internal string Describe()
    {
        var loc = LocalizationService.Instance;
        var lines = new List<string>();
        if (Result.Restored + Result.AlreadyPresent + Result.Failed > 0)
            lines.Add(string.Format(loc["HistoryRestoreResult"], Result.Restored, Result.AlreadyPresent, Result.Failed));
        else if (!MachineNotReached)
            lines.Add(loc["HistoryRestoreNothing"]);

        if (MachineNotReached)
            lines.Add(loc["HistoryRestoreMachineSkipped"]);

        return string.Join(" ", lines);
    }
}
