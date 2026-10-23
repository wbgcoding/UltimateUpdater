namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// One external repair command. Kept as data on the target so "sfc" and "DISM" can be two separately
/// selectable rows without the service knowing either by name.
/// </summary>
public sealed record RepairStep(string Executable, string[] Arguments, string Label);

/// <summary>
/// One thing the cleanup dialog can remove. Mostly a fixed, hard-coded table: most
/// paths here are never composed from anything the user or a tool supplied. The one exception is
/// <see cref="CleanupKind.AppCacheSweep"/>, whose folders ARE discovered at runtime,
/// but only ever under <c>Environment.GetFolderPath</c> roots (never a literal machine-specific path) and
/// only ever what <see cref="CleanupSafety.IsSafeToEmpty"/> itself accepts, the same final gate every
/// other target already passes through. NOTHING here, fixed or discovered, may ever point at a real user
/// folder: no Documents, no Downloads, no Pictures. Only temp/cache locations, the recycle bin, and
/// Windows' own Disk Cleanup.
/// </summary>
public sealed record CleanupTarget(
    string Id,
    string TitleKey,
    string DescriptionKey,
    CleanupKind Kind,
    bool DefaultEnabled,
    CleanupRiskLevel RiskLevel,
    string ConsequenceKey,
    bool NeedsElevation = false)
{
    /// <summary>The directories whose CONTENTS are removed. Empty for the non-file targets.</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];

    /// <summary>The commands a <see cref="CleanupKind.SystemRepair"/> target runs, in order.</summary>
    public IReadOnlyList<RepairStep> RepairSteps { get; init; } = [];

    /// <summary>
    /// Which registry area(s) a <see cref="CleanupKind.RegistryCleanup"/> target scans. Empty otherwise.
    /// More than one entry is the merged display: a user-visible row can cover several of the
    /// underlying scanners at once (their own detection logic is untouched, only which catalogue row
    /// shows their findings changed), while <see cref="RegistryFinding.Category"/> on each individual
    /// finding always names the ONE real scanner that produced it.
    /// </summary>
    public IReadOnlyList<RegistryCleanupCategory> RegistryCategories { get; init; } = [];

    /// <summary>
    /// Formats <see cref="CleanupTarget.TitleKey"/>'s placeholders (the per-drive defragmentation
    /// rows: drive letter, volume label). Empty for every ordinary, statically-titled target, which is what
    /// keeps <c>CleanupItemViewModel.Title</c> a plain key lookup for all of them.
    /// </summary>
    public IReadOnlyList<string> TitleArguments { get; init; } = [];

    /// <summary>The drive this target optimises, e.g. "C:" (no trailing separator). Null for every kind but
    /// <see cref="CleanupKind.Defragmentation"/>.</summary>
    public string? DriveLetter { get; init; }

    /// <summary>The drive's own storage kind, decided once when the catalogue is built (<see
    /// cref="DriveMediaTypeReader"/>); only <see cref="CleanupKind.Defragmentation"/> ever sets this away
    /// from <see cref="DriveMediaType.Unknown"/>.</summary>
    public DriveMediaType MediaType { get; init; } = DriveMediaType.Unknown;

    /// <summary>
    /// Directories that lie INSIDE <see cref="Directories"/> but belong to a different target, and are
    /// therefore left untouched by this one. Filled in by <see cref="CleanupCatalog.WithDisjointPaths"/>.
    /// Without it, "user-temp" (%TEMP%) would empty the package-manager cache's %TEMP%\chocolatey even with
    /// that target deselected, and with both selected the same bytes would be measured and freed twice.
    /// </summary>
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];

    /// <summary>
    /// Resource keys for the WORDS a formatted description needs (e.g. "USB drive", "SSD"),
    /// kept apart from <see cref="DescriptionKey"/> itself so nothing has to guess whether a given argument
    /// is a resource key or a literal value. Empty for every ordinary, plain-lookup target.
    /// </summary>
    public IReadOnlyList<string> DescriptionWordKeys { get; init; } = [];

    /// <summary>
    /// A byte count formatted and appended as the LAST argument of <see cref="DescriptionKey"/>
    /// when <see cref="DescriptionWordKeys"/> is not empty (a drive row's size). Null for every target that
    /// does not carry one.
    /// </summary>
    public long? DescriptionBytes { get; init; }

    /// <summary>
    /// A target that finds nothing on the measured machines moves out of the default view,
    /// behind the cleanup window's own "show all categories" expander, rather than being deleted from the
    /// catalogue outright. True for everything until a two-machine measurement
    /// justifies setting one to false; each target keeps its own value.
    /// </summary>
    public bool DefaultVisible { get; init; } = true;
}
