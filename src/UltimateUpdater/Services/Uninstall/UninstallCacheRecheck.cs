using System.IO;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The cheap first check of cached entries, before the full read: drops the ones that are certainly gone. A program is
/// gone when its key no longer lists under its Uninstall root, a Store app when its package is no longer registered
/// for the user, a driver when its published INF is gone, a Windows update when the servicing stack no longer lists it
/// as installed. Anything that cannot be told (an empty or unreadable list, a package manager entry) stays: the full read
/// that follows is the judge, this only keeps rows that vanished from showing for the seconds it takes.
/// </summary>
internal static class UninstallCacheRecheck
{
    /// <summary>Where the current user's registered Store packages are listed, one key per package full name.</summary>
    internal const string AppxRepositoryKey =
        @"HKCU\SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static IReadOnlyList<InstalledProgramEntry> Keep(
        IReadOnlyList<InstalledProgramEntry> entries, IRegistryView registry, string windowsDirectory,
        Func<string, bool> fileExists)
    {
        // Read once: a list of every Store package is not asked again per entry.
        HashSet<string>? packages = null;
        if (entries.Any(entry => entry.Kind == UninstallEntryKind.StoreApp))
        {
            var names = registry.SubKeyNames(AppxRepositoryKey);
            packages = names.Count == 0 ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        var kept = new List<InstalledProgramEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!IsGone(entry, registry, windowsDirectory, fileExists, packages))
                kept.Add(entry);
        }

        return kept;
    }

    private static bool IsGone(
        InstalledProgramEntry entry, IRegistryView registry, string windowsDirectory, Func<string, bool> fileExists,
        HashSet<string>? packages)
    {
        switch (entry.Kind)
        {
            case UninstallEntryKind.Driver:
                return DriverUninstallDiscovery.PackageNameOf(entry.Key) is not { } packageName
                       || !fileExists(Path.Combine(windowsDirectory, "INF", packageName));

            case UninstallEntryKind.WindowsUpdate:
                return WindowsUpdateDiscovery.IdentityOf(entry.Key) is not { } identity
                       || WindowsUpdateDiscovery.IsGone(registry, identity);

            case UninstallEntryKind.StoreApp:
                return packages is not null && !string.IsNullOrEmpty(entry.PackageFullName) && !packages.Contains(entry.PackageFullName);

            case UninstallEntryKind.Package:
                return false;

            default:
                var split = entry.Key.LastIndexOf('\\');
                if (split <= 0)
                    return false;

                var parent = entry.Key[..split];
                var siblings = registry.SubKeyNames(parent);
                return siblings.Count > 0 && !siblings.Contains(entry.Key[(split + 1)..], StringComparer.OrdinalIgnoreCase);
        }
    }
}
