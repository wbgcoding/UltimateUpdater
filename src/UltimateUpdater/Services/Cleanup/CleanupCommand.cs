namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// One instruction from the app to the elevated cleanup worker. The worker validates every id against its
/// own <see cref="CleanupCatalog"/> and ignores anything it does not recognise, so nothing that arrives over
/// the pipe can widen what an elevated process touches.
/// </summary>
public sealed class CleanupCommand
{
    /// <summary>"scan" (measure only), "clean", or "analyze" (a read-only DISM component store
    /// measurement, which needs no target ids at all). The session ends by closing the pipe, not by a
    /// message.</summary>
    public string Command { get; set; } = string.Empty;

    public List<string> TargetIds { get; set; } = new();

    /// <summary>
    /// The app's own at-most-once-per-24-hours decision, mirroring the install-side rule via
    /// <see cref="RestorePointThrottle"/>, carried as a plain flag exactly like <see
    /// cref="UltimateUpdater.Models.InstallJobFile.CreateRestorePoint"/>: the worker obeys it and never reads the timestamp
    /// file itself. Ignored unless the command actually reaches a <see cref="CleanupKind.SystemRepair"/> or
    /// <see cref="CleanupKind.RegistryCleanup"/> target.
    /// </summary>
    public bool CreateRestorePoint { get; set; }

    /// <summary>The worker is a separate process and would otherwise report in the OS language.</summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// Registry-cleanup backup settings, carried over the pipe rather than read by the worker itself, for the
    /// same reason as <see cref="Language"/>: the elevated process has no business reaching into the
    /// unelevated user's settings.json on its own.
    /// </summary>
    public string RegistryBackupFolder { get; set; } = string.Empty;

    public int RegistryBackupRetentionCount { get; set; }

    /// <summary>
    /// Which leftover-install candidates to actually delete on a "clean". never "everything the last
    /// scan found", because that category is the one this app never bulk-selects. Ignored
    /// for every other target.
    /// </summary>
    public List<string> SelectedLeftoverPaths { get; set; } = new();

    /// <summary>
    /// What the dialog's own earlier scan already found for a registry-cleanup "clean" command. The
    /// worker cleans exactly this list instead of re-scanning the whole category itself.
    /// A "clean" command always targets one item at a time, so this needs no per-target key. Ignored for
    /// every other target.
    /// </summary>
    public List<RegistryFindingDto> RegistryFindings { get; set; } = new();

    /// <summary>
    /// Detail-list rows deselected for a "clean": the run deletes everything under the
    /// target EXCEPT what these paths name: only what stayed checked. Ignored for
    /// every target that has no detail list.
    /// </summary>
    public List<string> ExcludedEntryPaths { get; set; } = new();

    /// <summary>Recycle-bin "clean" only: the rows the user saw in the dialog and left ticked. An item that
    /// reached the bin after the scan is not in it and is never deleted. Null for every other command.</summary>
    public List<string>? ConfirmedEntryPaths { get; set; }

    /// <summary>Which "disconnected devices" rows to actually remove on a "clean"; never "everything
    /// the last scan found", same none-pre-selected rule as <see cref="SelectedLeftoverPaths"/>. Ignored
    /// for every other target.</summary>
    public List<string> SelectedDriverDeviceInstanceIds { get; set; } = new();

    /// <summary>Which "old driver packages" rows to actually delete on a "clean". Ignored for every other
    /// target.</summary>
    public List<string> SelectedDriverPackageNames { get; set; } = new();

    /// <summary>Which "older Python versions" rows to actually uninstall on a "clean" (each identified by
    /// its interpreter's python.exe path); never "everything the last scan found", same none-pre-selected
    /// rule as <see cref="SelectedDriverDeviceInstanceIds"/>. Ignored for every other target.</summary>
    public List<string> SelectedPythonVersionPaths { get; set; } = new();
}
