namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// One installed program or Store app as the uninstaller lists it. Every text here comes from the registry or
/// from a tool and is untrusted: nothing is run or opened from it before the removal planner has checked it.
/// </summary>
public sealed record InstalledProgramEntry
{
    /// <summary>
    /// The stable identity of the entry: the full registry key path ("HKLM\...\Uninstall\&lt;name&gt;") for a
    /// program, "appx:&lt;package full name&gt;" for a Store app.
    /// </summary>
    public required string Key { get; init; }

    public UninstallEntryKind Kind { get; init; } = UninstallEntryKind.Program;

    public required string Name { get; init; }

    public string Publisher { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public DateOnly? InstallDate { get; init; }

    /// <summary>The size the entry itself states (EstimatedSize), in bytes; null when it states none. A missing
    /// size is measured lazily from <see cref="InstallLocation"/> by <see cref="InstalledProgramSize"/>.</summary>
    public long? SizeBytes { get; init; }

    public string InstallLocation { get; init; } = string.Empty;

    public UninstallScope Scope { get; init; } = UninstallScope.Machine;

    /// <summary>The registry's QuietUninstallString, verbatim.</summary>
    public string QuietUninstallString { get; init; } = string.Empty;

    /// <summary>The registry's UninstallString, verbatim.</summary>
    public string UninstallString { get; init; } = string.Empty;

    /// <summary>The MSI product code ("{GUID}") when one of the uninstall strings is an msiexec call.</summary>
    public string? MsiProductCode { get; init; }

    /// <summary>The DisplayIcon value, verbatim; the icon itself comes from the icon cache.</summary>
    public string DisplayIcon { get; init; } = string.Empty;

    /// <summary>The winget package id, set only on an exact name and version match against `winget list`.</summary>
    public string? WingetId { get; init; }

    public string? PackageFullName { get; init; }

    public string? PackageFamilyName { get; init; }
}
