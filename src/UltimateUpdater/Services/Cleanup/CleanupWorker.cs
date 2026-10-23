using System.IO;
using System.Text.Json;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The cleanup half of the elevated worker (<see cref="ElevatedWorker"/>): measure these targets, clean
/// those targets. Shows no UI.
/// Why an elevated process is needed at all: sfc, the DISM sequence and Disk Cleanup's unattended profile
/// ALL need admin, and without it they were silently doing nothing: the cleanup looked like it ran and
/// freed almost nothing. Windows' own Temp and the update cache have the same problem.
/// This is a deliberate, narrow exception to the "the elevated worker writes NO files" rule: deleting files
/// IS this job. What keeps it safe is unchanged from the unelevated path: every path still goes through
/// <see cref="CleanupSafety"/>, reparse points are skipped rather than followed, the catalogue is
/// hard-coded, and an id arriving over the pipe can only SELECT from that catalogue, never add to it. It
/// still writes no log file: its lines go back over the pipe.
/// </summary>
public static class CleanupWorker
{
    /// <summary>
    /// How often the buffer is flushed purely on a clock, on top of the explicit drains around the restore
    /// point and each target below. Those explicit calls cover the boundary between steps; this covers the
    /// inside of one, a single slow target (a DISM repair pass) would otherwise sit on its own log lines
    /// for its entire duration.
    /// </summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Measures or cleans the elevation-needing targets named in one command. The token and the gate come
    /// from the app's Stop and Pause buttons, over the same pipe as the command itself.
    /// <paramref name="serviceOverride"/> is a test seam: it lets a test observe what the worker hands the
    /// service without running a real cleanup.
    /// </summary>
    public static async Task RunCommandAsync(
        CleanupCommand? command, StreamWriter writer, string jobId, LoggingService logging,
        CancellationToken cancellationToken, PauseGate pause, CleanupService? serviceOverride = null)
    {
        if (command is null || command.Command is not ("scan" or "clean" or "analyze"))
        {
            Send(writer, jobId, new CleanupWorkerMessage { Type = "done" });
            return;
        }

        LocalizationService.Instance.Apply(command.Language);

        // No target list, no registry backup settings, nothing to resolve against the catalogue: this
        // is a single read-only DISM call, answered and finished before the "scan"/"clean"
        // machinery below (which the rest of this method still is) ever runs.
        if (command.Command == "analyze")
        {
            var analysis = await new CleanupService(logging).AnalyzeComponentStoreAsync(cancellationToken);
            Send(writer, jobId, new CleanupWorkerMessage
            {
                Type = "componentStore",
                ComponentStoreActualSizeBytes = analysis.ActualSizeBytes,
                ComponentStoreSharedWithWindowsBytes = analysis.SharedWithWindowsBytes,
                ComponentStoreBackupsBytes = analysis.BackupsAndDisabledFeaturesBytes,
                ComponentStoreCleanupRecommended = analysis.CleanupRecommended,
                ComponentStoreTimedOut = analysis.TimedOut,
            });
            Send(writer, jobId, new CleanupWorkerMessage { Type = "done" });
            return;
        }

        // Runs for the rest of this method (disposed on every exit path): the explicit DrainLog calls
        // below still fire immediately at each step's own boundary, this only covers the time spent
        // INSIDE one.
        using var logFlushTimer = new System.Threading.Timer(
            _ => DrainLog(writer, jobId), null, LogFlushInterval, LogFlushInterval);

        var service = serviceOverride ?? new CleanupService(logging);
        var registrySettings = new Models.Settings.RegistryCleanupSettings
        {
            BackupFolder = command.RegistryBackupFolder,
            RetentionCount = command.RegistryBackupRetentionCount,
        };

        var targets = ResolveElevatedTargets(command.TargetIds);

        // A restore point before installs already exists (InstallWorker), and a cleaning run that
        // touches the registry, repairs Windows' own files, or removes a driver gets one too: those are
        // the kinds of change here that are hardest to undo by hand. Best effort, same as the install
        // path: a failure here must never block the cleanup the user already confirmed through the red
        // warning. command.CreateRestorePoint is the app's own at-most-once-per-24-hours decision, made
        // per RUN: this worker only ever obeys the flag, exactly like InstallWorker obeys
        // job.CreateRestorePoint, and never reads the timestamp file itself or second-guesses the
        // decision against ITS OWN target list. A restore-point-only job carries no targets
        // at all, on purpose.
        if (command.Command == "clean" && command.CreateRestorePoint)
        {
            // Flushed before the step starts, not only after: a repair pass can run for minutes, and
            // its own lines would otherwise sit behind it until it finished.
            DrainLog(writer, jobId);

            try
            {
                if (RestorePointService.TryCreate("Ultimate Updater, before registry cleanup / system repair / driver cleanup", logging))
                {
                    // The app owns every file this program ever writes, this worker writes none, so the
                    // fact travels back as data rather than the worker deciding anything from it.
                    Send(writer, jobId, new CleanupWorkerMessage
                    {
                        Type = "restorePointCreated",
                        RestorePointCreatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                logging.Warn($"Restore point could not be created, cleaning anyway: {ex.Message}");
            }

            DrainLog(writer, jobId);
        }

        foreach (var target in targets)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Flushed before this target starts, not only after it finishes below: an earlier target
            // can have run for a while, and its lines should not make a later target's own lines wait.
            DrainLog(writer, jobId);

            try
            {
                if (command.Command == "scan")
                    await ScanOneAsync(service, target, writer, jobId, cancellationToken);
                else
                    await CleanOneAsync(
                        service, target, writer, jobId, cancellationToken, pause, registrySettings,
                        command.SelectedLeftoverPaths, command.RegistryFindings, command.ExcludedEntryPaths,
                        command.SelectedDriverDeviceInstanceIds, command.SelectedDriverPackageNames,
                        command.SelectedPythonVersionPaths, command.ConfirmedEntryPaths);
            }
            catch (OperationCanceledException)
            {
                logging.Info($"Cleanup worker stopped at '{target.Id}'.");
                break;
            }
            catch (Exception ex)
            {
                // One target failing must never take the session down: the app is waiting for a "done"
                // and would otherwise sit there for ever.
                logging.Error($"Cleanup worker failed for '{target.Id}'", ex);
            }

            DrainLog(writer, jobId);
        }

        ElevatedWorker.StopLogFlushTimer(logFlushTimer);
        DrainLog(writer, jobId);
        Send(writer, jobId, new CleanupWorkerMessage { Type = "done" });
    }

    /// <summary>
    /// Which catalogue targets a "scan"/"clean" command actually reaches: only an id that exists in OUR
    /// catalogue, and only one that actually needs elevation (an elevated process has no business doing
    /// work the app could have done itself). Iterating the CATALOGUE and testing membership in
    /// <paramref name="targetIds"/> (never the other way around) is what makes an unknown id, a path-shaped
    /// id, or a value repeated several times in the command all resolve to nothing extra: the catalogue has
    /// exactly one entry per real id, so nothing arriving over the pipe can ever add a target, widen one, or
    /// select the same one twice. Pulled out of <see cref="RunCommandAsync"/> so this guarantee is provable
    /// on its own, without a pipe, a job id or a running worker.
    /// </summary>
    internal static IReadOnlyList<CleanupTarget> ResolveElevatedTargets(IReadOnlyList<string> targetIds) =>
        CleanupCatalog.Build()
            .Where(target => target.NeedsElevation && targetIds.Contains(target.Id, StringComparer.Ordinal))
            .ToList();

    private static async Task ScanOneAsync(
        CleanupService service, CleanupTarget target, StreamWriter writer, string jobId,
        CancellationToken cancellationToken)
    {
        var result = await service.ScanAsync(target, cancellationToken);
        Send(writer, jobId, ScanMessage(target.Id, result));
    }

    /// <summary>
    /// The wire form of one target's scan result. The app reuses it for targets it scans itself, so the
    /// driver and Python candidates reach the rows the same way whichever process measured them.
    /// </summary>
    internal static CleanupWorkerMessage ScanMessage(string targetId, CleanupScanResult result) =>
        new()
        {
            Type = "scan",
            Id = targetId,
            Bytes = result.Bytes,
            Files = result.Files,
            Skipped = result.Skipped,
            LeftoverCandidates = result.Candidates?
                .Select(c => new LeftoverCandidateDto
                {
                    Path = c.Path,
                    DisplayName = c.DisplayName,
                    SizeBytes = c.SizeBytes,
                    NewestWriteTime = c.NewestWriteTime,
                })
                .ToList(),
            RegistryFindings = result.RegistryFindings?
                .Select(f => new RegistryFindingDto
                {
                    Category = f.Category,
                    KeyPath = f.KeyPath,
                    ValueName = f.ValueName,
                    DisplayName = f.DisplayName,
                    BrokenTarget = f.BrokenTarget,
                })
                .ToList(),
            Entries = result.Entries?
                .Select(e => new CleanupEntryDto
                {
                    Path = e.Path,
                    DisplayName = e.DisplayName,
                    Bytes = e.Bytes,
                    Files = e.Files,
                    OldestWriteTime = e.OldestWriteTime,
                    NewestWriteTime = e.NewestWriteTime,
                    Type = e.Type,
                    OriginalPath = e.OriginalPath,
                })
                .ToList(),
            OmittedEntryCount = result.OmittedEntryCount,
            OmittedEntryBytes = result.OmittedEntryBytes,
            DriverDeviceCandidates = result.DriverDeviceCandidates?
                .Select(c => new DriverDeviceCandidateDto
                {
                    InstanceId = c.InstanceId,
                    DisplayName = c.DisplayName,
                    ClassName = c.ClassName,
                    PackageName = c.PackageName,
                    LastConnected = c.LastConnected,
                    LastConnectedKind = c.LastConnectedKind,
                    Provider = c.Provider,
                    OriginalName = c.OriginalName,
                    StorePath = c.StorePath,
                    Version = c.Version,
                    Date = c.Date,
                    SizeBytes = c.SizeBytes,
                    NewerVersion = c.NewerVersion,
                    NewerDate = c.NewerDate,
                })
                .ToList(),
            DriverPackageCandidates = result.DriverPackageCandidates?
                .Select(c => new DriverPackageCandidateDto
                {
                    PackageName = c.PackageName,
                    Provider = c.Provider,
                    ClassName = c.ClassName,
                    Version = c.Version,
                    Date = c.Date,
                    SizeBytes = c.SizeBytes,
                    OriginalName = c.OriginalName,
                    StorePath = c.StorePath,
                    NewerVersion = c.NewerVersion,
                    NewerDate = c.NewerDate,
                })
                .ToList(),
            PythonVersionCandidates = result.PythonVersionCandidates?
                .Select(c => new PythonVersionCandidateDto
                {
                    PythonExe = c.PythonExe,
                    DisplayLabel = c.DisplayLabel,
                    SitePackages = c.SitePackages,
                    SizeBytes = c.SizeBytes,
                    PackageCount = c.PackageCount,
                    PerUser = c.PerUser,
                })
                .ToList(),
        };

    private static async Task CleanOneAsync(
        CleanupService service, CleanupTarget target, StreamWriter writer, string jobId,
        CancellationToken cancellationToken, PauseGate pause, Models.Settings.RegistryCleanupSettings registrySettings,
        List<string> selectedLeftoverPaths, List<RegistryFindingDto> registryFindings,
        List<string> excludedEntryPaths, List<string> selectedDriverDeviceInstanceIds,
        List<string> selectedDriverPackageNames, List<string> selectedPythonVersionPaths,
        List<string>? confirmedEntryPaths)
    {
        var progress = new Progress<CleanupProgress>(report => Send(writer, jobId, new CleanupWorkerMessage
        {
            Type = "progress",
            Id = target.Id,
            Percent = report.Percent,
            Freed = report.FreedBytes,
            Current = report.CurrentItem,
        }));

        var findings = registryFindings
            .Select(f => new RegistryFinding(f.Category, f.KeyPath, f.ValueName, f.DisplayName, f.BrokenTarget))
            .ToList();

        // Stopping IS honoured here; deleting files is safe to abandon at any point. The one thing that is
        // never killed mid-write is a repair step: CleanupService runs sfc and DISM with no token of their
        // own and checks between steps instead, because killing DISM can leave the component store
        // inconsistent. Stop is therefore instant everywhere except inside a running repair step.
        var outcome = await service.CleanAsync(
            target, progress, cancellationToken, pause, registrySettings, selectedLeftoverPaths, findings,
            excludedEntryPaths, selectedDriverDeviceInstanceIds: selectedDriverDeviceInstanceIds,
            selectedDriverPackageNames: selectedDriverPackageNames,
            selectedPythonVersionPaths: selectedPythonVersionPaths, confirmedEntryPaths: confirmedEntryPaths);

        Send(writer, jobId, new CleanupWorkerMessage
        {
            Type = "result",
            Id = target.Id,
            Freed = outcome.FreedBytes,
            Files = outcome.DeletedFiles,
            Skipped = outcome.SkippedFiles,
            SkippedBytes = outcome.SkippedBytes,
            ProtectedFiles = outcome.ProtectedFiles,
            ProtectedBytes = outcome.ProtectedBytes,
            Stopped = outcome.WasStopped,
            TimedOut = outcome.TimedOut,
            DriverBackupFolderName = outcome.DriverBackupFolderName,
        });
    }

    private static void DrainLog(StreamWriter writer, string jobId)
    {
        while (ElevatedWorker.TryDequeueLog(out var line))
            Send(writer, jobId, new CleanupWorkerMessage { Type = "log", Text = line });
    }

    /// <summary>Every reply goes through the worker's single write lock; jobs run concurrently.</summary>
    private static bool Send(StreamWriter writer, string jobId, CleanupWorkerMessage message) =>
        ElevatedWorker.Send(writer, jobId, message);
}
