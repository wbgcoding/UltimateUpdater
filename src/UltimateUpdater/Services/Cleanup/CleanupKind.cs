namespace UltimateUpdater.Services.Cleanup;

/// <summary>What a cleanup target does, so the UI can label and order it without knowing the mechanics.</summary>
public enum CleanupKind
{
    /// <summary>Delete the contents of one or more directories.</summary>
    Directories,

    /// <summary>Empty the recycle bin (shell API, not file deletion).</summary>
    RecycleBin,

    /// <summary>Hand over to Windows' own Disk Cleanup.</summary>
    DiskCleanup,

    /// <summary>Repair Windows' own files: sfc, then the DISM restore-health sequence.</summary>
    SystemRepair,

    /// <summary>Remove broken references from one registry category; see <see cref="RegistryCleanupCategory"/>.</summary>
    RegistryCleanup,

    /// <summary>
    /// Same deletion mechanics as <see cref="Directories"/>, but the directory list is DISCOVERED fresh
    /// at scan/clean time rather than fixed on the target; see <see cref="AppCacheDiscovery"/>.
    /// </summary>
    AppCacheSweep,

    /// <summary>
    /// Whole top-level folders that plausibly belong to an uninstalled program; see
    /// <see cref="LeftoverInstallDiscovery"/>. Unlike every other kind here, deletion goes through the
    /// recycle bin (<see cref="RecycleBinDeleter"/>), never a permanent delete, and the UI must offer each
    /// candidate individually with none pre-selected.
    /// </summary>
    LeftoverInstalls,

    /// <summary>
    /// Windows' own drive optimisation: defragmentation on a rotational disk, retrim on an SSD.
    /// One target PER FIXED DRIVE, built at runtime by <see cref="CleanupCatalog"/> (see
    /// <see cref="CleanupTarget.DriveLetter"/>/<see cref="CleanupTarget.MediaType"/>), never a fixed catalogue
    /// row. Frees no measurable space, same as <see cref="SystemRepair"/>.
    /// </summary>
    Defragmentation,

    /// <summary>
    /// Disconnected devices and old driver packages; see <see cref="DriverCleanupDiscovery"/>.
    /// Every row is individually selectable and none is pre-selected, the same rule as
    /// <see cref="LeftoverInstalls"/>; deletion goes through pnputil in the elevated worker, never a
    /// direct file delete.
    /// </summary>
    DriverCleanup,

    /// <summary>
    /// Folders pip itself leaves behind under a "~"-prefixed name after an install got interrupted
    /// mid-rename; see <see cref="PythonCleanupDiscovery.DiscoverPipLeftovers"/>. Shown as an ordinary
    /// detail list (one row per folder, all pre-selected) and hard-deleted, the same shape as
    /// <see cref="AppCacheSweep"/>, just discovered through
    /// <see cref="UltimateUpdater.Services.Sources.PackageManagers.PythonInterpreters"/> instead of a
    /// filesystem walk under LOCALAPPDATA.
    /// </summary>
    PipInstallLeftovers,

    /// <summary>
    /// Installed Python interpreters superseded by a newer one of the same architecture; see
    /// <see cref="PythonCleanupDiscovery.DiscoverOldVersions"/>. Every row is individually selectable and
    /// none is pre-selected, the same rule as <see cref="DriverCleanup"/>; removal runs the interpreter's
    /// own registered uninstaller in the elevated worker (see <see cref="PythonVersionCleanupExecutor"/>),
    /// never a direct delete of its install folder.
    /// </summary>
    OldPythonVersions,
}
