using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>
/// The installed Store (MSIX) packages, read from the per-user package repository. A package is matched by
/// its display name, exactly, never by similarity. Everything the manifest names (an executable, an
/// application id) is untrusted text until it has been checked: an executable counts only when it lies
/// inside the package's own folder, with no link on the way there.
/// </summary>
public sealed class PackagedApps
{
    internal const string PackagesKey =
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const long MaxManifestBytes = 4 * 1024 * 1024;

    private static readonly Regex PublisherId = new("^[a-z0-9]{13}$", RegexOptions.CultureInvariant);

    private readonly IRegistryView _registry;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private IReadOnlyList<Package>? _packages;
    private DateTime _readAt;

    /// <summary>One installed package: its display name, its folder and its package family name (null when
    /// the registry key is not shaped like a package full name; such a package still gives its icon).</summary>
    public sealed record Package(string Name, string Root, string? FamilyName);

    /// <summary>An Application of a package whose executable was verified to be a real file inside the package folder.</summary>
    public sealed record Application(string Id, string ExecutablePath);

    /// <summary>How long a read of the package list is reused: an app installed or removed while this one runs shows up soon.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    public PackagedApps(IRegistryView registry, Func<DateTime>? now = null)
    {
        _registry = registry;
        _now = now ?? (() => DateTime.UtcNow);
    }

    private IReadOnlyList<Package> Packages()
    {
        lock (_gate)
        {
            if (_packages is null || _now() - _readAt > CacheLifetime)
            {
                _packages = ReadPackages();
                _readAt = _now();
            }

            return _packages;
        }
    }

    /// <summary>The packages whose display name is exactly this one (a trailing bracketed note ignored), newest first.</summary>
    public IEnumerable<Package> FindAll(string displayName)
    {
        var wanted = IconCacheService.CleanName(displayName);
        return wanted.Length == 0
            ? []
            : Packages().Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public Package? Find(string displayName) => FindAll(displayName).FirstOrDefault();

    /// <summary>
    /// The launch target of the package's first Application, <c>shell:AppsFolder\&lt;family&gt;!&lt;app id&gt;</c>, which
    /// starts the app the way the Start menu does (also for apps whose manifest names no usable executable).
    /// Null when the family name is unknown, the manifest has no Application, or the pair is not well formed.
    /// </summary>
    public string? StartTargetOf(Package package)
    {
        if (package.FamilyName is null)
            return null;

        foreach (var (id, _) in ReadApplications(package.Root))
        {
            if (PackagedAppLauncher.TargetFor(package.FamilyName, id) is { } target)
                return target;
        }

        return null;
    }

    /// <summary>
    /// "Contoso.Pad_26.1.0.0_x64__2p2nqsd0c76g0" (the registry key) to "Contoso.Pad_2p2nqsd0c76g0".
    /// Null for a key that does not have the five-part full-name shape.
    /// </summary>
    internal static string? FamilyNameOf(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length == 5 && parts[0].Length > 0 && PublisherId.IsMatch(parts[4])
            ? parts[0] + "_" + parts[4]
            : null;
    }

    private IReadOnlyList<Package> ReadPackages()
    {
        var found = new List<Package>();

        // Newest first, so an old version left behind never wins over the current one. The version is
        // compared as a version: as text, 9.0 would sort above 10.0.
        var keys = _registry.SubKeyNames(PackagesKey)
            .OrderByDescending(VersionOf)
            .ThenByDescending(k => k, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var entry = $@"{PackagesKey}\{key}";
            if (_registry.Value(entry, "DisplayName") is { Length: > 0 } name &&
                _registry.Value(entry, "PackageRootFolder") is { Length: > 0 } root &&
                IsPackageFolder(root))
            {
                found.Add(new Package(name.Trim(), root, FamilyNameOf(key)));
            }
        }

        return found;
    }

    private static Version VersionOf(string packageFullName) =>
        packageFullName.Split('_') is { Length: >= 2 } parts && Version.TryParse(parts[1], out var version)
            ? version
            : new Version(0, 0);

    /// <summary>
    /// The registry value is user-writable text: only a full path below a drive or share root counts as a
    /// package folder. A relative path would follow the working directory, and a bare root would make every
    /// file and process on that drive look like part of the package (its processes offered for closing).
    /// </summary>
    internal static bool IsPackageFolder(string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root))
                return false;

            var full = Path.GetFullPath(root).TrimEnd('\\', '/');
            var volume = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
            return full.Length > 0 && !string.Equals(full, volume, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first Application of the package whose Executable is a .exe that really lies inside the package
    /// folder and passes <paramref name="accept"/> (for instance: carries an icon). Null when none does.
    /// </summary>
    public Application? FirstApplication(Package package, Func<string, bool>? accept = null)
    {
        foreach (var (id, executable) in ReadApplications(package.Root))
        {
            if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ContainedFile(package.Root, executable) is { } path && (accept is null || accept(path)))
                return new Application(id, path);
        }

        return null;
    }

    private static List<(string Id, string Executable)> ReadApplications(string root)
    {
        var applications = new List<(string, string)>();
        try
        {
            var manifest = Path.Combine(root, "AppxManifest.xml");
            var info = new FileInfo(manifest);
            if (!info.Exists || info.Length > MaxManifestBytes)
                return applications;

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(manifest, settings);
            foreach (var element in XDocument.Load(reader).Descendants().Where(e => e.Name.LocalName == "Application"))
            {
                // An application without an Executable (some UWP manifests) still has an id to start it by.
                if (element.Attribute("Id")?.Value is { Length: > 0 } id)
                    applications.Add((id, element.Attribute("Executable")?.Value ?? ""));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
        {
            applications.Clear();
        }

        return applications;
    }

    /// <summary>
    /// The full path of <paramref name="relative"/> under <paramref name="root"/>, or null when it is not a
    /// plain existing file inside the folder: an absolute or drive-relative path, a ".." that climbs out,
    /// and any folder or file on the way that is a link (junction, symlink) are all refused, because a
    /// link would put the real file somewhere else.
    /// </summary>
    internal static string? ContainedFile(string root, string relative)
    {
        try
        {
            var text = relative.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(text) || text.Contains(':'))
                return null;

            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(rootFull, text));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;

            var current = rootFull;
            foreach (var part in full[(rootFull.Length + 1)..].Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    return null;
            }

            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
