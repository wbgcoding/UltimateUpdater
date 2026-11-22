namespace UltimateUpdater.Services.Uninstall;

/// <summary>What an uninstaller list entry is. The kind drives the removal route and the risk rating.</summary>
public enum UninstallEntryKind
{
    /// <summary>A classic program from the Windows uninstall registry.</summary>
    Program,

    /// <summary>A Store (MSIX) package.</summary>
    StoreApp,

    /// <summary>A package of a package manager (npm, pip, scoop and so on).</summary>
    Package,

    /// <summary>A driver package from the driver store.</summary>
    Driver,

    /// <summary>An installed Windows update.</summary>
    WindowsUpdate,
}

/// <summary>Whose registry hive an entry lives in: the whole machine or the current user only.</summary>
public enum UninstallScope
{
    Machine,
    User,
}
