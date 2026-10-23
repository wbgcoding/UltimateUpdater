namespace UltimateUpdater.Services.Cleanup;

/// <summary>How much one target could free, and how many of its files could not even be measured.
/// <paramref name="Candidates"/> is populated only for <see cref="CleanupKind.LeftoverInstalls"/>. every
/// other kind reports itself fully through <paramref name="Bytes"/>/<paramref name="Files"/> alone.</summary>
public readonly record struct CleanupScanResult(long Bytes, int Files, int Skipped)
{
    public IReadOnlyList<LeftoverCandidate>? Candidates { get; init; }

    /// <summary>Populated only for <see cref="CleanupKind.RegistryCleanup"/>: the actual findings behind
    /// <paramref name="Files"/>'s count, carried forward so a later Clean works this exact list instead of
    /// scanning the category again.</summary>
    public IReadOnlyList<RegistryFinding>? RegistryFindings { get; init; }

    /// <summary>
    /// The detail list behind this target's total, grouped, never one row per file.
    /// Populated for <see cref="CleanupKind.Directories"/>/<see cref="CleanupKind.AppCacheSweep"/> (one row
    /// per program/cache folder) and <see cref="CleanupKind.RegistryCleanup"/> (one row per finding).
    /// Capped at <see cref="CleanupService.MaxEntries"/>; <see cref="OmittedEntryCount"/>/
    /// <see cref="OmittedEntryBytes"/> cover whatever did not fit, so the pipe never has to carry an
    /// unbounded list. Null for a kind that has no meaningful breakdown (Disk Cleanup, System Repair).
    /// </summary>
    public IReadOnlyList<CleanupEntry>? Entries { get; init; }

    public int OmittedEntryCount { get; init; }

    public long OmittedEntryBytes { get; init; }

    /// <summary>Populated only for <see cref="CleanupKind.RecycleBin"/>. see
    /// <see cref="RecycleBinDriveSummary"/>. Never crosses the elevated pipe: the recycle bin needs no
    /// administrator and is always scanned and emptied in-process.</summary>
    public IReadOnlyList<RecycleBinDriveSummary>? RecycleBinDrives { get; init; }

    /// <summary>Populated only for <see cref="CleanupKind.DriverCleanup"/>: Group A, disconnected
    /// devices.</summary>
    public IReadOnlyList<DriverDeviceCandidate>? DriverDeviceCandidates { get; init; }

    /// <summary>Populated only for <see cref="CleanupKind.DriverCleanup"/>: Group B, old driver
    /// packages.</summary>
    public IReadOnlyList<DriverPackageCandidate>? DriverPackageCandidates { get; init; }

    /// <summary>Populated only for <see cref="CleanupKind.OldPythonVersions"/>: one row per interpreter
    /// older than the newest of its own architecture.</summary>
    public IReadOnlyList<PythonVersionCandidate>? PythonVersionCandidates { get; init; }
}
