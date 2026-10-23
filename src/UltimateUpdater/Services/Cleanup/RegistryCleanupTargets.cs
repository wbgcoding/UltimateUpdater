namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Which part of the registry one row cleans. Deliberately NOT the full "standard registry cleaner"
/// category list (File Extensions, Help Files): those two either risk breaking how Windows opens files
/// (a file-extension association is user-visible and easy to get wrong) or have no single standardized
/// registry location this project could point at without guessing; the same "lieber fragen als raten"
/// rule that already kept rustup's date and Lenovo's CLI out; add them later only with a confirmed path.
/// </summary>
public enum RegistryCleanupCategory
{
    /// <summary>App Paths: each subkey's default value should be an existing executable.</summary>
    AppPaths,

    /// <summary>SharedDLLs: each VALUE NAME (not the value) is a DLL path; the value itself is a refcount.</summary>
    SharedDlls,

    /// <summary>ActiveX/COM: each CLSID's InprocServer32/LocalServer32 default value should exist.</summary>
    ActiveXCom,

    /// <summary>Run/RunOnce autostart entries, machine- and user-scope.</summary>
    Autostart,

    /// <summary>Uninstall entries whose own paths are gone.</summary>
    UninstallEntries,
}

/// <summary>
/// One broken reference this run's scan found; enough to show the user what it is and to delete exactly
/// that value/key later, nothing broader.
/// </summary>
public sealed record RegistryFinding(
    RegistryCleanupCategory Category,
    string KeyPath,
    string? ValueName,
    string DisplayName,
    string BrokenTarget);

/// <summary>The 2 user-visible registry-cleanup rows, alongside the file-cleanup ones in the
/// same catalogue. Merged from the 5 real scanners into 2 display rows: "orphaned references" folds
/// together App Paths, Shared DLLs, ActiveX/COM and Autostart, and "orphaned uninstall entries" stays its
/// own row; every one of the 5 scanners in <see cref="RegistryCleanupScanner"/> still runs exactly as
/// before, only which row shows its findings changed.</summary>
public static class RegistryCleanupCatalog
{
    /// <summary>
    /// Both OFF by default; a registry writer earns no default trust the way an obviously-safe
    /// temp folder does; and both elevated: App Paths/SharedDLLs/ActiveX-COM live
    /// under HKLM outright, and Autostart/Uninstall entries span both HKLM and HKCU; routing the whole
    /// row through the elevated worker uniformly is simpler than splitting by hive, and the elevated
    /// process still resolves HKCU to the invoking user's own hive (same token, same logon session).
    /// </summary>
    public static IReadOnlyList<CleanupTarget> Build() =>
    [
        new CleanupTarget(
            "registry-orphaned-references", "CleanupRegistryOrphans", "CleanupRegistryOrphansDesc",
            CleanupKind.RegistryCleanup, DefaultEnabled: false,
            RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "CleanupRegistryOrphansConsequence", NeedsElevation: true)
        {
            RegistryCategories =
            [
                RegistryCleanupCategory.AppPaths, RegistryCleanupCategory.SharedDlls,
                RegistryCleanupCategory.ActiveXCom, RegistryCleanupCategory.Autostart,
            ],
        },

        // This row maps 1:1 onto the one scanner it covers, so the description and consequence text
        // say exactly the right thing; only the title (and its id, which changes alongside the
        // display) is specific to this row.
        new CleanupTarget(
            "registry-orphaned-uninstall", "CleanupRegistryUninstall", "CleanupRegistryUninstallEntriesDesc",
            CleanupKind.RegistryCleanup, DefaultEnabled: false,
            RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "CleanupRegistryUninstallEntriesConsequence", NeedsElevation: true)
        {
            RegistryCategories = [RegistryCleanupCategory.UninstallEntries],
        },
    ];
}
