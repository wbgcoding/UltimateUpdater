using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Judges whether a top-level Program Files/LOCALAPPDATA\Programs/ProgramData folder is a leftover of an
/// uninstalled program, a DIFFERENT question from <see cref="CleanupSafety.IsSafeToEmpty"/>, which needs
/// the folder NAME to look like "Temp"/"Cache" and would never match a folder named after the app itself.
/// The one real risk this whole feature exists to manage: a genuine, still-used PORTABLE program, never
/// registered by an installer on purpose, looks IDENTICAL to an orphaned leftover from here. There is no
/// way to tell them apart with certainty, so this never claims to. Every candidate this
/// produces is, by construction, "plausible, not proven", the caller must never pre-select one.
/// </summary>
public static class LeftoverInstallSafety
{
    /// <summary>
    /// Windows itself, or a runtime it ships, owns these, none of them was ever installed through an
    /// uninstall entry, so the registry cross-check alone cannot rule them out. Found live on a real,
    /// heavily-used machine: "ModifiableWindowsApps" (MSIX dev-mode symlink folder),
    /// "Windows Sidebar" (a Vista/7-era OS component still present on modern Windows), "Microsoft.NET"
    /// (the.NET Framework install root) all came back as "plausible leftovers", untouched for years,
    /// no ARP entry, exactly the pattern a real leftover has, except deleting any of them would damage
    /// the OS rather than free disk space. Matched by name only, case-insensitive, at any of the four
    /// roots, the list is deliberately generous; a name added here can only ever prevent a delete, never
    /// cause one.
    /// </summary>
    private static readonly string[] KnownSystemFolderNames =
    [
        "ModifiableWindowsApps", "WindowsApps", "Windows Sidebar", "Windows Mail", "Windows NT",
        "Windows Media Player", "Windows Multimedia Platform", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "WindowsPowerShell", "Common Files", "Internet Explorer", "Microsoft.NET", "MSBuild", "dotnet",
        "PackageManagement", "SoftwareDistribution", "Package Cache", "InstallShield Installation Information",
        "Uninstall Information",
    ];

    /// <summary>
    /// True only when NO registered program still claims this folder (as its own install directory, or as
    /// the directory holding its uninstall/icon executable, either direction, so a real app installed
    /// INSIDE what looks like a leftover folder still protects it) AND nothing inside the folder has been
    /// touched more recently than <paramref name="minimumAge"/>. Recency is the one signal that actually
    /// tells a leftover apart from a portable app someone still runs, a portable app gets touched when it
    /// is used, a true leftover does not.
    /// </summary>
    public static bool IsPlausibleLeftover(
        string folderPath,
        IReadOnlyList<string> registeredInstallPaths,
        DateTimeOffset newestFileWriteTime,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return false;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (CleanupSafety.IsOwnPath(full))
            return false;

        if (KnownSystemFolderNames.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
            return false;

        if (registeredInstallPaths.Any(registered => Overlaps(full, registered)))
            return false;

        return now - newestFileWriteTime >= minimumAge;
    }

    /// <summary>Two paths "overlap" when one is the other, or an ancestor of the other, either direction,
    /// since a real registered install could sit above OR below the candidate folder.</summary>
    private static bool Overlaps(string candidate, string? registered)
    {
        if (string.IsNullOrWhiteSpace(registered))
            return false;

        string other;
        try
        {
            other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(candidate, other, StringComparison.OrdinalIgnoreCase))
            return true;

        return other.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
