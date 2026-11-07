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
internal sealed class PackagedApps
{
    internal const string PackagesKey =
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const long MaxManifestBytes = 4 * 1024 * 1024;

    private static readonly Regex PublisherId = new("^[a-z0-9]{13}$", RegexOptions.CultureInvariant);

    private readonly IRegistryView _registry;
    private readonly Lazy<IReadOnlyList<Package>> _packages;

    /// <summary>One installed package: its display name, its folder and its package family name (null when
    /// the registry key is not shaped like a package full name; such a package still gives its icon).</summary>
    internal sealed record Package(string Name, string Root, string? FamilyName);

    /// <summary>An Application of a package whose executable was verified to be a real file inside the package folder.</summary>
    internal sealed record Application(string Id, string ExecutablePath);

    public PackagedApps(IRegistryView registry)
    {
        _registry = registry;
        _packages = new Lazy<IReadOnlyList<Package>>(ReadPackages);
    }

    /// <summary>The packages whose display name is exactly this one (a trailing bracketed note ignored), newest first.</summary>
    public IEnumerable<Package> FindAll(string displayName)
    {
        var wanted = IconCacheService.CleanName(displayName);
        return wanted.Length == 0
            ? []
            : _packages.Value.Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public Package? Find(string displayName) => FindAll(displayName).FirstOrDefault();

    /// <summary>
    /// "OpenAI.Codex_26.1.0.0_x64__2p2nqsd0c76g0" (the registry key) to "OpenAI.Codex_2p2nqsd0c76g0".
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

        // Newest first, so an old version left behind never wins over the current one.
        foreach (var key in _registry.SubKeyNames(PackagesKey).OrderByDescending(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var entry = $@"{PackagesKey}\{key}";
            if (_registry.Value(entry, "DisplayName") is { Length: > 0 } name &&
                _registry.Value(entry, "PackageRootFolder") is { Length: > 0 } root)
            {
                found.Add(new Package(name.Trim(), root, FamilyNameOf(key)));
            }
        }

        return found;
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
                if (element.Attribute("Id")?.Value is { Length: > 0 } id
                    && element.Attribute("Executable")?.Value is { Length: > 0 } executable)
                {
                    applications.Add((id, executable));
                }
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
