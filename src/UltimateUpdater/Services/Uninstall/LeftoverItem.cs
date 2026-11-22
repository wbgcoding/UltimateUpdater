namespace UltimateUpdater.Services.Uninstall;

/// <summary>What a leftover is.</summary>
public enum LeftoverKind
{
    /// <summary>A folder (install folder, user data folder, program data folder).</summary>
    Folder,

    /// <summary>A registry key below Software\&lt;publisher&gt;\&lt;program&gt;.</summary>
    RegistryKey,

    /// <summary>A Start menu or desktop shortcut (.lnk) whose target is gone.</summary>
    Shortcut,
}

/// <summary>Who removes a leftover: the app itself, or the elevated worker (machine-wide places).</summary>
public enum LeftoverLane
{
    /// <summary>The user profile and HKCU: removed in-process, unelevated.</summary>
    User,

    /// <summary>Program Files, ProgramData, HKLM and all-users shortcuts: removed by the elevated worker.</summary>
    Machine,
}

/// <summary>
/// One thing a removed program left behind. <see cref="Preselected"/> is true only for a certain match (the
/// install folder, an exact publisher plus name folder or key, a shortcut into the install folder); every other
/// hit is listed unticked. A leftover is a snapshot: whoever removes it judges it again first.
/// </summary>
public sealed record LeftoverItem(
    LeftoverKind Kind,
    LeftoverLane Lane,
    string Path,
    string DisplayName,
    bool Preselected,
    long SizeBytes);

/// <summary>The removed program the scan looks for traces of. Name and publisher come from the program's
/// registry entry and are untrusted text; they only ever select what to look at, never what to run.</summary>
public sealed record LeftoverSubject(string Key, string Name, string Publisher, string InstallLocation)
{
    public static LeftoverSubject From(InstalledProgramEntry entry) =>
        new(entry.Key, entry.Name, entry.Publisher, entry.InstallLocation);
}
