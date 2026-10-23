using System.IO;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>Which kind of date backs a device candidate's <see cref="DriverDeviceCandidate.LastConnected"/>:
/// a real connect/disconnect event, or a fallback to when the driver was installed. The row's text picks
/// its wording from this rather than guessing from which property happened to carry a value.</summary>
public enum DriverLastSeenKind
{
    Unknown,
    Connected,
    Installed,
}

/// <summary>Group A row: a device that is not connected anymore and used a third-party driver package. The
/// facts beyond name/date come from the device's own bound package (joined by <see cref="PackageName"/>),
/// null when that package is not found in the store. <see cref="NewerVersion"/>/<see
/// cref="NewerDate"/> carry a confidently newer package's own facts when the bound package has been
/// superseded, null otherwise (see <see cref="DriverCleanupDiscovery.NewerPackageFor"/>).</summary>
public sealed record DriverDeviceCandidate(
    string InstanceId,
    string DisplayName,
    string ClassName,
    string PackageName,
    DateTimeOffset? LastConnected,
    DriverLastSeenKind LastConnectedKind,
    string? Provider,
    string? OriginalName,
    string? StorePath,
    string? Version,
    DateTimeOffset? Date,
    long SizeBytes,
    string? NewerVersion,
    DateTimeOffset? NewerDate);

/// <summary>Group B row: a third-party driver package unused by any device, or superseded by a newer one
/// with the same original name and provider. <see cref="NewerVersion"/>/<see cref="NewerDate"/> are set only
/// when this package IS superseded, carrying the newer package's own version/date.</summary>
public sealed record DriverPackageCandidate(
    string PackageName,
    string Provider,
    string ClassName,
    string? Version,
    DateTimeOffset? Date,
    long SizeBytes,
    string? OriginalName,
    string? StorePath,
    string? NewerVersion,
    DateTimeOffset? NewerDate);

/// <summary>
/// Pure judgment over already-gathered device/package facts: which rows the driver cleanup category
/// offers, in what order. Kept apart from the real SetupAPI/CfgMgr32/INF reads (<see
/// cref="IDriverInventoryProvider"/>) the same way <see cref="LeftoverInstallSafety"/> is kept apart from
/// <see cref="LeftoverInstallDiscovery"/>'s real directory walk: this class is what a unit test drives with
/// fabricated device and package lists, no real hardware and no elevation required. The elevated worker
/// calls this again, immediately before deleting, against a freshly gathered inventory (see
/// <see cref="DriverCleanupExecutor"/>) so a row the user selected minutes ago is only acted on if it still
/// matches right now.
/// </summary>
public static class DriverCleanupDiscovery
{
    /// <summary>
    /// Device classes never offered even when non-present and driven by a third-party package: removing
    /// one of these is not an unplugged device, it is a piece of the system itself.
    /// </summary>
    private static readonly string[] ExcludedClasses =
        ["System", "Computer", "Processor", "Volume", "SoftwareDevice"];

    /// <summary>
    /// pnputil's own naming for a third-party driver package copied into the driver store; an inbox
    /// driver's own .inf (e.g. "usb.inf") never matches this and is therefore never touched, this whole
    /// category only ever reaches packages Windows itself copied in under this name.
    /// </summary>
    private static readonly Regex OemPackageName = new(@"^oem\d+\.inf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsOemPackageName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && OemPackageName.IsMatch(fileName);

    private static bool IsOemPackagePath(string? infPath) =>
        !string.IsNullOrWhiteSpace(infPath) && IsOemPackageName(Path.GetFileName(infPath));

    private static bool IsExcludedClass(string className) =>
        ExcludedClasses.Any(excluded => string.Equals(excluded, className, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Disconnected devices: non-present devices bound to a third-party (oem*.inf) package, never
    /// an inbox driver and never one of <see cref="ExcludedClasses"/>. Oldest last-use first: the whole
    /// point of this row is finding what has not been used in the longest time. A device that carries
    /// neither a last-removal nor a last-arrival date sorts to the end, never assumed to be the oldest.
    /// Package facts (provider, class, INF file, original name, store path, version, date, size) are
    /// joined in from <paramref name="packages"/> by the device's own bound package name, purely for
    /// display (the tooltip); a device whose package does not resolve still gets a row, just without them.
    /// </summary>
    public static IReadOnlyList<DriverDeviceCandidate> DiscoverDisconnectedDevices(
        IReadOnlyList<DriverDeviceInfo> devices, IReadOnlyList<DriverPackageInfo> packages)
    {
        var packagesByName = new Dictionary<string, DriverPackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
            packagesByName[package.PackageName] = package;

        return devices
            .Where(d => !d.Present && IsOemPackagePath(d.InfPath) && !IsExcludedClass(d.ClassName))
            .Select(d =>
            {
                var packageName = Path.GetFileName(d.InfPath!);
                var (lastConnected, kind) = ResolveLastSeen(d);
                packagesByName.TryGetValue(packageName, out var boundPackage);
                var newer = boundPackage is null ? null : NewerPackageFor(boundPackage, packages);

                return new DriverDeviceCandidate(
                    d.InstanceId, d.FriendlyName, d.ClassName, packageName, lastConnected, kind,
                    boundPackage?.Provider, boundPackage?.OriginalName, boundPackage?.StorePath,
                    boundPackage?.DriverVersion, boundPackage?.DriverDate, boundPackage?.StoreFolderSizeBytes ?? 0,
                    newer?.DriverVersion, newer?.DriverDate);
            })
            .OrderBy(c => c.LastConnected ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    /// <summary>The fallback chain for "when was this last seen": a real connect/disconnect event first
    /// (removal winning over arrival), then progressively weaker install-time
    /// signals, never a guess when none of them exist. <see cref="DriverLastSeenKind"/> lets the row's own
    /// text say which kind of date it is showing rather than always claiming "last connected".</summary>
    private static (DateTimeOffset? Date, DriverLastSeenKind Kind) ResolveLastSeen(DriverDeviceInfo device)
    {
        if (device.LastRemovalDate is { } removal)
            return (removal, DriverLastSeenKind.Connected);
        if (device.LastArrivalDate is { } arrival)
            return (arrival, DriverLastSeenKind.Connected);
        if (device.InstallDate is { } install)
            return (install, DriverLastSeenKind.Installed);
        if (device.FirstInstallDate is { } firstInstall)
            return (firstInstall, DriverLastSeenKind.Installed);
        if (device.RegistryKeyWriteDate is { } registryWrite)
            return (registryWrite, DriverLastSeenKind.Installed);

        return (null, DriverLastSeenKind.Unknown);
    }

    /// <summary>
    /// "Old driver packages": every oem*.inf package no device (present or not) references at all, plus
    /// every package that is not the newest one sharing its original name and provider, except one a present
    /// device is bound to. A package pair is
    /// only ever compared when both sides carry a confidently orderable date/version; two packages this
    /// method cannot order against each other are both left alone rather than one being guessed "older".
    /// </summary>
    public static IReadOnlyList<DriverPackageCandidate> DiscoverOldPackages(
        IReadOnlyList<DriverDeviceInfo> devices, IReadOnlyList<DriverPackageInfo> packages)
    {
        var boundPackageNames = new HashSet<string>(
            devices.Where(d => IsOemPackagePath(d.InfPath)).Select(d => Path.GetFileName(d.InfPath!)!),
            StringComparer.OrdinalIgnoreCase);

        // A superseded package a connected device is still bound to is in use (a driver rolled back, or one the
        // maker pinned), so "newer exists" never makes it an old package.
        var presentBoundPackageNames = new HashSet<string>(
            devices.Where(d => d.Present && IsOemPackagePath(d.InfPath)).Select(d => Path.GetFileName(d.InfPath!)!),
            StringComparer.OrdinalIgnoreCase);

        var superseded = SupersededPackageNames(packages);

        return packages
            .Where(p => !presentBoundPackageNames.Contains(p.PackageName)
                && (!boundPackageNames.Contains(p.PackageName) || superseded.Contains(p.PackageName)))
            .Select(p =>
            {
                var newer = superseded.Contains(p.PackageName) ? NewerPackageFor(p, packages) : null;
                return new DriverPackageCandidate(
                    p.PackageName, p.Provider, p.ClassName, p.DriverVersion, p.DriverDate, p.StoreFolderSizeBytes,
                    p.OriginalName, p.StorePath, newer?.DriverVersion, newer?.DriverDate);
            })
            .OrderBy(c => c.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Package names that are not the newest within their own (original name, provider) group.</summary>
    private static HashSet<string> SupersededPackageNames(IReadOnlyList<DriverPackageInfo> packages)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groups = packages
            .Where(p => !string.IsNullOrWhiteSpace(p.OriginalName))
            .GroupBy(p => (p.OriginalName, p.Provider), new OriginalNameProviderComparer());

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < 2)
                continue;

            foreach (var candidate in members)
            {
                if (members.Any(other => !ReferenceEquals(other, candidate) && IsNewerThan(other, candidate)))
                    result.Add(candidate.PackageName);
            }
        }

        return result;
    }

    /// <summary>
    /// The package, among every other one sharing <paramref name="package"/>'s (original name, provider),
    /// that is confidently newer than it - for the "installed now: version X" tooltip fact, not for the
    /// supersede/delete decision above (that one only needs to know THAT a newer package exists, this one
    /// needs to know WHICH). Never a guess: only <see cref="IsNewerThan"/>-confirmed comparisons count, so a
    /// package with no orderable rival returns null even when other packages share its name.
    /// </summary>
    private static DriverPackageInfo? NewerPackageFor(DriverPackageInfo package, IReadOnlyList<DriverPackageInfo> allPackages)
    {
        if (string.IsNullOrWhiteSpace(package.OriginalName))
            return null;

        DriverPackageInfo? best = null;
        foreach (var candidate in allPackages)
        {
            if (ReferenceEquals(candidate, package)
                || !string.Equals(candidate.OriginalName, package.OriginalName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.Provider, package.Provider, StringComparison.OrdinalIgnoreCase)
                || !IsNewerThan(candidate, package))
            {
                continue;
            }

            if (best is null || IsNewerThan(candidate, best))
                best = candidate;
        }

        return best;
    }

    /// <summary>True only when <paramref name="a"/> can be CONFIDENTLY judged newer than <paramref
    /// name="b"/>: a later driver date, or (with no date on either side, or an equal date) a higher
    /// parseable version. Anything this cannot order returns false, on both sides, never a guess.</summary>
    private static bool IsNewerThan(DriverPackageInfo a, DriverPackageInfo b)
    {
        if (a.DriverDate is { } dateA && b.DriverDate is { } dateB && dateA != dateB)
            return dateA > dateB;

        if (Version.TryParse(a.DriverVersion, out var versionA) && Version.TryParse(b.DriverVersion, out var versionB)
            && versionA != versionB)
            return versionA > versionB;

        return false;
    }

    private sealed class OriginalNameProviderComparer : IEqualityComparer<(string OriginalName, string Provider)>
    {
        public bool Equals((string OriginalName, string Provider) x, (string OriginalName, string Provider) y) =>
            string.Equals(x.OriginalName, y.OriginalName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Provider, y.Provider, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string OriginalName, string Provider) obj) =>
            HashCode.Combine(
                obj.OriginalName.ToUpperInvariant(),
                (obj.Provider ?? string.Empty).ToUpperInvariant());
    }
}
