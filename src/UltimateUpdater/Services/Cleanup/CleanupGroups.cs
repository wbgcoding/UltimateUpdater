namespace UltimateUpdater.Services.Cleanup;

/// <summary>Which section of the cleanup dialog a target's card sits under.</summary>
public enum CleanupGroupKey
{
    TempFiles,
    Caches,
    WindowsMaintenance,
    RecycleBin,
    SystemRepair,
    Registry,
    Drivers,
}

/// <summary>
/// Groups the flat <see cref="CleanupCatalog"/> for display; view-only:
/// the elevated pipe still selects by target id, one at a time, precisely as before (<see
/// cref="CleanupCommand.TargetIds"/> is untouched by this file). A fixed id→group table rather than
/// something derived from <see cref="CleanupKind"/>: several kinds (<see cref="CleanupKind.Directories"/>
/// above all) span more than one group, so the id is the only reliable key.
/// <see cref="CleanupTarget.Kind"/> IS trusted for one case: every <see cref="CleanupKind.RegistryCleanup"/>
/// target is Registry, whatever its id; <see cref="RegistryCleanupCatalog"/> builds a category per
/// registry area and this file would otherwise need updating every time one is added.
/// No entry exists for <see cref="CleanupKind.LeftoverInstalls"/>: that catalogue entry is deliberately
/// pulled back out (see the comment in <see cref="CleanupCatalog.Build"/>), so there is nothing to
/// group yet; add its mapping when that entry returns, not before. <c>CleanupGroupsTests</c> fails
/// loudly if <see cref="CleanupCatalog.Build"/> ever produces a target this table does not know about,
/// the same "a new entry must be reviewed, never silently miscategorised" discipline
/// <c>CleanupSafetyTests</c> already applies to the deletion gate itself.
/// </summary>
public static class CleanupGroups
{
    private static readonly Dictionary<string, CleanupGroupKey> ByTargetId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user-temp"] = CleanupGroupKey.TempFiles,
        ["all-users-temp"] = CleanupGroupKey.TempFiles,
        ["windows-temp"] = CleanupGroupKey.TempFiles,
        ["crash-dumps"] = CleanupGroupKey.TempFiles,
        ["memory-dumps"] = CleanupGroupKey.TempFiles,

        ["thumbnail-cache"] = CleanupGroupKey.Caches,
        ["shader-cache"] = CleanupGroupKey.Caches,
        ["package-manager-cache"] = CleanupGroupKey.Caches,
        ["build-tool-cache"] = CleanupGroupKey.Caches,
        ["app-cache-sweep"] = CleanupGroupKey.Caches,
        ["pip-install-leftovers"] = CleanupGroupKey.Caches,

        ["windows-update-cache"] = CleanupGroupKey.WindowsMaintenance,
        ["delivery-optimization"] = CleanupGroupKey.WindowsMaintenance,
        ["windows-logs"] = CleanupGroupKey.WindowsMaintenance,
        ["disk-cleanup"] = CleanupGroupKey.WindowsMaintenance,

        // Housekeeping of the Windows component store, so it sits with the other Windows-owned
        // maintenance targets; the card of component-cleanup also carries the read-only store analysis.
        // They still run in the repair lane (the lane is chosen by kind, never by group).
        ["component-cleanup"] = CleanupGroupKey.WindowsMaintenance,
        ["component-resetbase"] = CleanupGroupKey.WindowsMaintenance,

        ["recycle-bin"] = CleanupGroupKey.RecycleBin,

        ["system-repair-dism"] = CleanupGroupKey.SystemRepair,
        ["system-repair-sfc"] = CleanupGroupKey.SystemRepair,
    };

    /// <summary>The order groups appear in the dialog.</summary>
    public static readonly IReadOnlyList<CleanupGroupKey> Order =
    [
        CleanupGroupKey.TempFiles,
        CleanupGroupKey.Caches,
        CleanupGroupKey.WindowsMaintenance,
        CleanupGroupKey.RecycleBin,
        CleanupGroupKey.SystemRepair,
        CleanupGroupKey.Registry,
        CleanupGroupKey.Drivers,
    ];

    /// <summary>Null when nothing here knows this target's group: the caller must never guess and fall
    /// back to a default section instead (a test exists precisely so this never happens unnoticed).</summary>
    public static CleanupGroupKey? For(CleanupTarget target) => target.Kind switch
    {
        CleanupKind.RegistryCleanup => CleanupGroupKey.Registry,
        CleanupKind.DriverCleanup => CleanupGroupKey.Drivers,

        // No section of its own was given for this either; it shares the driver row's own shape
        // exactly (off by default, red risk, elevated, individually selectable rows), so it sits
        // beside it rather than inventing a new heading nobody asked for.
        CleanupKind.OldPythonVersions => CleanupGroupKey.Drivers,

        // One dynamic id per fixed drive, same reason RegistryCleanup is trusted by kind above
        // rather than by id; grouped with Disk Cleanup and the other Windows-owned maintenance targets
        // rather than a new section of its own, since no new group header copy was given for one.
        CleanupKind.Defragmentation => CleanupGroupKey.WindowsMaintenance,

        _ => ByTargetId.GetValueOrDefault(target.Id),
    };

    public static string TitleKey(CleanupGroupKey key) => key switch
    {
        CleanupGroupKey.TempFiles => "CleanupGroupTempFiles",
        CleanupGroupKey.Caches => "CleanupGroupCaches",
        CleanupGroupKey.WindowsMaintenance => "CleanupGroupWindowsMaintenance",
        CleanupGroupKey.RecycleBin => "CleanupGroupRecycleBin",
        CleanupGroupKey.SystemRepair => "CleanupGroupSystemRepair",
        CleanupGroupKey.Registry => "CleanupGroupRegistry",
        CleanupGroupKey.Drivers => "CleanupGroupDrivers",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
}
