using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>
/// The logo of an installed Store (MSIX) app. Such an app has no Uninstall entry and no Start-menu
/// shortcut file, so nothing else on the machine names its icon; its package does: the manifest's
/// <c>Square44x44Logo</c>. Many packages ship that logo as a white mark on a transparent background and
/// leave the colour to the manifest's <c>BackgroundColor</c>, which Windows draws as a plate behind it.
/// An application executable inside the package that carries an icon is preferred to that logo.
/// </summary>
internal sealed class PackagedAppLogos
{
    internal const string PackagesKey = PackagedApps.PackagesKey;

    /// <summary>The icon is drawn about this big; the nearest variant at or above it keeps the mark sharp.</summary>
    private const int WantedSize = 64;

    private const int BaseLogoSize = 44;

    private readonly PackagedApps _apps;
    private readonly Func<string, bool> _hasIcon;

    /// <summary>
    /// What to draw for the app: a logo file and the plate colour to draw it on (null: draw it as it is),
    /// or, with <paramref name="IsExecutable"/>, the app's own executable whose icon is taken as it is.
    /// </summary>
    internal sealed record Logo(string Path, System.Windows.Media.Color? Plate, bool IsExecutable = false);

    public PackagedAppLogos(IRegistryView registry, Func<string, bool>? hasIcon = null)
        : this(new PackagedApps(registry), hasIcon)
    {
    }

    public PackagedAppLogos(PackagedApps apps, Func<string, bool>? hasIcon = null)
    {
        _apps = apps;
        _hasIcon = hasIcon ?? (path => IconCacheService.HasIconResource(path, 0));
    }

    public Logo? Find(string displayName)
    {
        foreach (var package in _apps.FindAll(displayName))
        {
            // The app's own executable carries the icon the vendor made for it, finished and opaque; the
            // manifest logo is only a white mark that needs a plate. So the executable wins when it has one.
            if (_apps.FirstApplication(package, _hasIcon) is { } application)
                return new Logo(application.ExecutablePath, null, IsExecutable: true);

            if (ReadLogo(package.Root) is { } logo)
                return logo;
        }

        return null;
    }

    private static Logo? ReadLogo(string root)
    {
        try
        {
            var manifest = Path.Combine(root, "AppxManifest.xml");
            if (!File.Exists(manifest))
                return null;

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(manifest, settings);
            var visual = XDocument.Load(reader).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "VisualElements" && e.Attribute("Square44x44Logo") is not null);
            if (visual?.Attribute("Square44x44Logo")?.Value is not { Length: > 0 } relative)
                return null;

            var logoPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!logoPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return null;

            var folder = Path.GetDirectoryName(logoPath);
            if (folder is null || !Directory.Exists(folder))
                return null;

            var file = Pick(Directory.EnumerateFiles(folder).Select(Path.GetFileName)!, Path.GetFileNameWithoutExtension(logoPath));
            return file is null
                ? null
                : new Logo(Path.Combine(folder, file), ParsePlate(visual.Attribute("BackgroundColor")?.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The plain variants beat the "unplated" ones, and the size nearest above <see cref="WantedSize"/> wins.
    /// The light-background and high-contrast variants are for other surfaces and are never taken.
    /// </summary>
    internal static string? Pick(IEnumerable<string> fileNames, string stem)
    {
        var pattern = new Regex(
            $@"^{Regex.Escape(stem)}(?:\.scale-(?<scale>\d{{1,4}}))?(?:\.targetsize-(?<size>\d{{1,4}}))?(?:_altform-(?<form>unplated))?\.png$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return fileNames
            .Select(name => (Name: name, Match: pattern.Match(name)))
            .Where(c => c.Match.Success)
            .Select(c =>
            {
                var size = c.Match.Groups["size"].Success
                    ? int.Parse(c.Match.Groups["size"].Value)
                    : c.Match.Groups["scale"].Success
                        ? BaseLogoSize * int.Parse(c.Match.Groups["scale"].Value) / 100
                        : BaseLogoSize;
                return (c.Name, Unplated: c.Match.Groups["form"].Success, Size: size);
            })
            .OrderBy(c => c.Unplated)
            .ThenBy(c => c.Size >= WantedSize ? 0 : 1)
            .ThenBy(c => c.Size >= WantedSize ? c.Size : -c.Size)
            .Select(c => c.Name)
            .FirstOrDefault();
    }

    /// <summary>Only the "#RRGGBB" form a manifest normally uses; "transparent" and named colours mean no plate.</summary>
    internal static System.Windows.Media.Color? ParsePlate(string? value)
    {
        if (value is null || !Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$"))
            return null;

        return System.Windows.Media.Color.FromRgb(
            Convert.ToByte(value.Substring(1, 2), 16),
            Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16));
    }

    /// <summary>The logo laid over its plate, opaque, at the size the cache works in.</summary>
    internal static BitmapSource? OnPlate(BitmapSource logo, System.Windows.Media.Color plate)
    {
        try
        {
            var converted = new FormatConvertedBitmap(logo, PixelFormats.Pbgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var behind = 255 - pixels[i + 3];
                pixels[i] = (byte)Math.Min(255, pixels[i] + plate.B * behind / 255);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + plate.G * behind / 255);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + plate.R * behind / 255);
                pixels[i + 3] = 255;
            }

            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
