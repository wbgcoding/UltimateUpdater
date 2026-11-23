using System.IO;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>One third-party driver package as the uninstaller lists it: what it is and which present devices use it.</summary>
public sealed record DriverUninstallCandidate(DriverPackageInfo Package, IReadOnlyList<string> UsedBy)
{
    /// <summary>True when a present device is bound to the package; such a package is never removed here.</summary>
    public bool InUse => UsedBy.Count > 0;
}

/// <summary>
/// Decides which driver packages the uninstaller lists. A pure function over a device and package inventory, so
/// tests need no hardware. Only third-party packages (<c>oem*.inf</c>) appear: an inbox driver sits under its own
/// name in the Windows INF folder and never matches, and a package whose provider is Microsoft is left out as well.
/// The same function runs again in the elevated worker against a fresh inventory right before a removal, so a
/// package that a device has started to use since the list was drawn is refused.
/// </summary>
public static class DriverUninstallDiscovery
{
    /// <summary>The most device names written into the "in use by" note.</summary>
    private const int MaxNamesInNote = 2;

    public static IReadOnlyList<DriverUninstallCandidate> List(
        IReadOnlyList<DriverDeviceInfo> devices, IReadOnlyList<DriverPackageInfo> packages)
    {
        // A present device whose driver path names the package is a user of it. The name is matched on the file
        // name only, so a full path and a bare name agree.
        var users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices.Where(d => d.Present && !string.IsNullOrWhiteSpace(d.InfPath)))
        {
            var name = Path.GetFileName(device.InfPath!);
            if (!users.TryGetValue(name, out var list))
                users[name] = list = [];

            var label = DeviceLabel(device);
            if (!list.Contains(label, StringComparer.OrdinalIgnoreCase))
                list.Add(label);
        }

        return packages
            .Where(p => DriverCleanupDiscovery.IsOemPackageName(p.PackageName)
                        && !DriverCleanupDiscovery.IsNeverOffered(p)
                        && !IsMicrosoftProvider(p.Provider))
            .Select(p => new DriverUninstallCandidate(
                p, users.TryGetValue(p.PackageName, out var list) ? list : []))
            .OrderBy(c => c.Package.Provider, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Package.ClassName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Package.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"Microsoft", "Microsoft Corporation" and the like: Windows own drivers. An empty provider counts too: a package of unknown origin is never offered.</summary>
    internal static bool IsMicrosoftProvider(string? provider)
    {
        var text = provider?.Trim();
        return string.IsNullOrEmpty(text)
               || text.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Microsoft,", StringComparison.OrdinalIgnoreCase);
    }

    private static string DeviceLabel(DriverDeviceInfo device) =>
        string.IsNullOrWhiteSpace(device.FriendlyName)
            ? (string.IsNullOrWhiteSpace(device.ClassName) ? device.InstanceId : device.ClassName)
            : device.FriendlyName.Trim();

    /// <summary>The device names of the in use note: the first few, then how many more there are.</summary>
    internal static string UsedByText(IReadOnlyList<string> usedBy)
    {
        if (usedBy.Count <= MaxNamesInNote)
            return string.Join(", ", usedBy);

        return string.Join(", ", usedBy.Take(MaxNamesInNote)) + $" (+{usedBy.Count - MaxNamesInNote})";
    }

    /// <summary>The key of a driver entry in the uninstaller list.</summary>
    internal const string KeyPrefix = "driver:";

    /// <summary>The package name of a driver entry key, or null when the key is not exactly the prefix and an
    /// <c>oem*.inf</c> name.</summary>
    internal static string? PackageNameOf(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && DriverCleanupDiscovery.IsOemPackageName(key[KeyPrefix.Length..])
            ? key[KeyPrefix.Length..]
            : null;

    /// <summary>The uninstaller entry of one candidate.</summary>
    public static InstalledProgramEntry ToEntry(DriverUninstallCandidate candidate)
    {
        var package = candidate.Package;
        var title = string.IsNullOrWhiteSpace(package.ClassName)
            ? package.Provider.Trim()
            : $"{package.Provider.Trim()} {package.ClassName.Trim()}";
        return new InstalledProgramEntry
        {
            Key = KeyPrefix + package.PackageName,
            Kind = UninstallEntryKind.Driver,
            Name = $"{title} ({package.PackageName})",
            Publisher = package.Provider.Trim(),
            Version = package.DriverVersion ?? string.Empty,
            InstallDate = package.DriverDate is { } date ? DateOnly.FromDateTime(date.LocalDateTime) : null,
            SizeBytes = package.StoreFolderSizeBytes > 0 ? package.StoreFolderSizeBytes : null,
            Scope = UninstallScope.Machine,
            InUseBy = candidate.InUse ? UsedByText(candidate.UsedBy) : null,
        };
    }
}
