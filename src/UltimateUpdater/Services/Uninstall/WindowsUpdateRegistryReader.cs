using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The installed Windows updates, read from the component store's package keys
/// (<c>HKLM\...\Component Based Servicing\Packages</c>), which any user can read: no DISM, no elevated helper, no
/// prompt. A package counts when it is a wrapper (<see cref="WindowsUpdateDiscovery.IsWrapper"/>) whose
/// <c>CurrentState</c> says installed; a staged or superseded package of the same family (another state) is skipped,
/// so only the installed one is listed. The registry holds no release type and no permanence, so every row reads as a
/// plain update and only a KB package may be removable; the elevated worker checks permanence again before it removes
/// anything. The package key names and values are untrusted text and are used only as the pure identity.
/// </summary>
internal static class WindowsUpdateRegistryReader
{
    /// <summary>The release type every registry row carries: the registry does not say which kind of update it is.</summary>
    internal const string ReleaseType = "Update";

    /// <summary>The installed update packages, in registry order. Empty when the key cannot be read.</summary>
    public static IReadOnlyList<WindowsUpdateRow> Read(IRegistryView registry)
    {
        var rows = new List<WindowsUpdateRow>();
        foreach (var name in registry.SubKeyNames(WindowsUpdateDiscovery.CbsPackagesKey))
        {
            if (!WindowsUpdateDiscovery.IsWrapper(name))
                continue;

            var path = WindowsUpdateDiscovery.CbsPackagesKey + "\\" + name;
            if (registry.DwordValue(path, "CurrentState") != WindowsUpdateDiscovery.InstalledPackageState)
                continue;

            rows.Add(new WindowsUpdateRow
            {
                Identity = name,
                ReleaseType = ReleaseType,
                InstalledOn = InstalledOn(registry, path),
                Removable = WindowsUpdateDiscovery.KbNumberOf(name) is not null,
            });
        }

        return rows;
    }

    /// <summary>The install date from the FILETIME the package stores as two DWORDs; null when either half is missing
    /// or the value is no date.</summary>
    internal static DateOnly? InstalledOn(IRegistryView registry, string packageKey)
    {
        if (registry.DwordValue(packageKey, "InstallTimeHigh") is not { } high
            || registry.DwordValue(packageKey, "InstallTimeLow") is not { } low)
            return null;

        var fileTime = ((long)(uint)high << 32) | (uint)low;
        return fileTime is > 0 and < 2650467743999999999
            ? DateOnly.FromDateTime(DateTime.FromFileTime(fileTime))
            : null;
    }
}
