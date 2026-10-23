namespace UltimateUpdater.Services.Cleanup;

/// <summary>One <see cref="PythonVersionCandidate"/> as it crosses the pipe. Never carries the uninstall
/// command itself: the elevated worker re-derives that fresh from the registry immediately before running
/// it (see <see cref="PythonVersionCleanupExecutor"/>), the same reasoning as
/// <see cref="DriverPackageCandidateDto"/> never carrying anything pnputil is not re-asked to confirm.</summary>
public sealed class PythonVersionCandidateDto
{
    public string PythonExe { get; set; } = string.Empty;

    public string DisplayLabel { get; set; } = string.Empty;

    public string? SitePackages { get; set; }

    public long SizeBytes { get; set; }

    public int PackageCount { get; set; }

    /// <summary>Display-only: whether this interpreter is a per-user install (its own uninstaller needs no
    /// administrator rights, and the elevated worker refuses to touch it regardless of selection, see
    /// <see cref="PythonVersionCleanupExecutor"/>). The pipe never trusts this flag for anything other than
    /// showing the row; the executor re-derives it itself from a fresh registry read.</summary>
    public bool PerUser { get; set; }
}
