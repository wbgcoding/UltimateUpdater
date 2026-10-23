namespace UltimateUpdater.Services;

/// <summary>
/// The registry roots more than one part of this app has to walk. They lived as four verbatim copies;
/// in the cleanup scanner, the install-location resolver, the GitHub-release source and the launcher
/// resolver, which is the same shape of defect that already cost this project twice: one list edited,
/// the others left behind, and two features quietly disagreeing about what exists (the elevation source
/// list, and SettingsService's Reset/Normalize sections).
/// </summary>
internal static class WindowsRegistryRoots
{
    /// <summary>
    /// Where Windows records installed programs. Addressed by path rather than by RegistryView: this app
    /// is 64-bit, so the 32-bit hive is reached through WOW6432Node, and per-user installs live under
    /// HKCU where the machine hive cannot see them.
    /// </summary>
    public static readonly string[] Uninstall =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];
}
