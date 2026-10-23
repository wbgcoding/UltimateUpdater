namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// One line back from the worker. A single flat shape rather than a type per message: the conversation is
/// short, and a discriminated payload would need custom converters for no gain.
/// </summary>
public sealed class CleanupWorkerMessage
{
    /// <summary>"scan" | "progress" | "result" | "log" | "restorePointCreated" | "componentStore" | "done";
    /// which of the fields below carry anything.</summary>
    public string Type { get; set; } = string.Empty;

    public string? Id { get; set; }

    public long Bytes { get; set; }

    public int Files { get; set; }

    public double Percent { get; set; }

    public long Freed { get; set; }

    public int Skipped { get; set; }

    public long SkippedBytes { get; set; }

    /// <summary>The refused part of <see cref="Skipped"/> (see <see cref="CleanupOutcome.ProtectedFiles"/>).</summary>
    public int ProtectedFiles { get; set; }

    public long ProtectedBytes { get; set; }

    /// <summary>Set only on a "result" message: the target swallowed a Stop and returned what it had
    /// finished so far (see <see cref="CleanupOutcome.WasStopped"/>).</summary>
    public bool Stopped { get; set; }

    /// <summary>Set only on a "result" message: Disk Cleanup was ended because it ran past its time limit
    /// (see <see cref="CleanupOutcome.TimedOut"/>).</summary>
    public bool TimedOut { get; set; }

    public string? Current { get; set; }

    /// <summary>A log line the worker could not write itself.</summary>
    public string? Text { get; set; }

    /// <summary>Populated only for a "scan" message on the leftover-installs target; every other target
    /// reports its findings as a plain count/byte total, which is all the UI needs for them.</summary>
    public List<LeftoverCandidateDto>? LeftoverCandidates { get; set; }

    /// <summary>Populated only for a "scan" message on a registry-cleanup target; carried through so a
    /// later "clean" works this exact list instead of re-scanning.</summary>
    public List<RegistryFindingDto>? RegistryFindings { get; set; }

    /// <summary>Populated only for a "scan" message on a target that has a detail list;
    /// capped at ~500 rows; <see cref="OmittedEntryCount"/>/<see cref="OmittedEntryBytes"/> cover the rest.</summary>
    public List<CleanupEntryDto>? Entries { get; set; }

    public int OmittedEntryCount { get; set; }

    public long OmittedEntryBytes { get; set; }

    /// <summary>
    /// Set only on a message with <c>Type == "restorePointCreated"</c> (mirroring
    /// <see cref="UltimateUpdater.Models.InstallProgressMessage.RestorePointCreatedAt"/>): the moment the
    /// elevated worker actually created one, so the app can record it for its own
    /// at-most-once-per-24-hours decision on the next run. The worker decides nothing from this itself, it
    /// only reports the fact.
    /// </summary>
    public DateTimeOffset? RestorePointCreatedAt { get; set; }

    /// <summary>Set only on a message with <c>Type == "componentStore"</c>; each is independently
    /// null exactly when <see cref="ComponentStoreAnalysis"/> left it null, dism.exe missing or its output
    /// not understood, never guessed from the others.</summary>
    public long? ComponentStoreActualSizeBytes { get; set; }

    public long? ComponentStoreSharedWithWindowsBytes { get; set; }

    public long? ComponentStoreBackupsBytes { get; set; }

    public bool? ComponentStoreCleanupRecommended { get; set; }

    /// <summary>Set only on a message with <c>Type == "componentStore"</c>: DISM was killed after running
    /// past its own 5-minute deadline rather than answering. Distinct from every field above being null,
    /// which can also mean a completed run that simply had nothing to report.</summary>
    public bool ComponentStoreTimedOut { get; set; }

    /// <summary>Populated only for a "scan" message on the driver-cleanup target: Group A, devices no
    /// longer connected.</summary>
    public List<DriverDeviceCandidateDto>? DriverDeviceCandidates { get; set; }

    /// <summary>Populated only for a "scan" message on the driver-cleanup target: Group B, old driver
    /// packages.</summary>
    public List<DriverPackageCandidateDto>? DriverPackageCandidates { get; set; }

    /// <summary>Populated only for a "scan" message on the old-python-versions target: one row per
    /// interpreter older than the newest of its own architecture.</summary>
    public List<PythonVersionCandidateDto>? PythonVersionCandidates { get; set; }

    /// <summary>Set only on a "result" message for the driver-cleanup target: the timestamped folder name
    /// <see cref="DriverBackupStaging"/> exported into under the elevated worker's own ProgramData staging
    /// root, so the unelevated app can copy that folder into the data folder afterwards.</summary>
    public string? DriverBackupFolderName { get; set; }
}
