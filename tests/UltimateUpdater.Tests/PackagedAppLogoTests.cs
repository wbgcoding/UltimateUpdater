using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// A Store (MSIX) app has no Uninstall entry and no shortcut file; its own package logo is the icon, a
/// white mark that only reads once it sits on the manifest's plate colour. Every package here is a temp
/// folder behind a table-backed registry, so nothing depends on the apps of the machine running the tests.
/// </summary>
public class PackagedAppLogoTests : IDisposable
{
    private const string PackageName = "Zq Packaged Chat";
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly List<string> _cachedIcons = [];

    public void Dispose()
    {
        foreach (var icon in _cachedIcons)
            File.Delete(icon);

        Directory.Delete(_root, recursive: true);
    }

    private sealed class PackageTable(Dictionary<string, (string Name, string Root)> packages) : IRegistryView
    {
        public IReadOnlyList<string> SubKeyNames(string keyPath) =>
            keyPath == PackagedAppLogos.PackagesKey ? [.. packages.Keys] : [];

        public string? Value(string keyPath, string valueName)
        {
            var key = keyPath[(PackagedAppLogos.PackagesKey.Length + 1)..];
            return !packages.TryGetValue(key, out var package)
                ? null
                : valueName switch
                {
                    "DisplayName" => package.Name,
                    "PackageRootFolder" => package.Root,
                    _ => null,
                };
        }
    }

    /// <summary>A package like the ChatGPT one: only white marks on transparent, the colour left to the manifest.</summary>
    private string WhiteMarkPackage(string background = "#3143FF", params string[] assetFiles)
    {
        var package = Path.Combine(_root, "pkg");
        var assets = Path.Combine(package, "assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(package, "AppxManifest.xml"),
            $"""
             <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                      xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
               <Applications><Application Id="App">
                 <uap:VisualElements DisplayName="{PackageName}" Square44x44Logo="assets\Square44x44Logo.png"
                                     BackgroundColor="{background}" />
               </Application></Applications>
             </Package>
             """);

        foreach (var file in assetFiles.Length > 0 ? assetFiles : ["Square44x44Logo.png", "Square44x44Logo.scale-200.png"])
            WriteWhiteMark(Path.Combine(assets, file));

        return package;
    }

    /// <summary>An 88 px image: a white square in the middle, fully transparent around it.</summary>
    private static void WriteWhiteMark(string path)
    {
        const int size = 88;
        var pixels = new byte[size * size * 4];
        for (var y = 30; y < 58; y++)
        {
            for (var x = 30; x < 58; x++)
                pixels[(y * size + x) * 4] = pixels[(y * size + x) * 4 + 1] = pixels[(y * size + x) * 4 + 2] = pixels[(y * size + x) * 4 + 3] = 255;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private IconCacheService Cache(string packageRoot, string registeredName = PackageName) =>
        new(new LoggingService(), new StartMenuShortcuts([]), null, [],
            new PackagedAppLogos(new PackageTable(new() { ["Zq.Chat_1.0_x64__abc"] = (registeredName, packageRoot) })));

    private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    private BitmapSource? Icon(IconCacheService cache, string name)
    {
        _cachedIcons.Add(Path.Combine(AppPaths.IconCacheDirectory, IconCacheService.CacheKey("winget", "Zq.PackagedChat") + ".png"));
        return cache.GetIcon("winget", "Zq.PackagedChat", name);
    }

    [Fact]
    public void A_store_app_shows_its_package_logo_on_the_plate_colour_of_its_manifest()
    {
        var icon = Icon(Cache(WhiteMarkPackage()), PackageName);

        Assert.NotNull(icon);
        var corner = PixelAt(icon, 1, 1);
        Assert.Equal((0xFF, 0x43, 0x31, 0xFF), (corner.B, corner.G, corner.R, corner.A));

        var middle = PixelAt(icon, icon.PixelWidth / 2, icon.PixelHeight / 2);
        Assert.Equal((255, 255, 255), (middle.B, middle.G, middle.R));
    }

    [Fact]
    public void A_manifest_without_a_plate_colour_leaves_the_logo_as_it_is()
    {
        var icon = Icon(Cache(WhiteMarkPackage(background: "transparent")), PackageName);

        Assert.NotNull(icon);
        Assert.Equal(0, PixelAt(icon, 1, 1).A);
    }

    [Theory]
    [InlineData("Zq Packaged")]
    [InlineData("Zq Packaged Chat Pro")]
    [InlineData("Zq")]
    public void A_package_is_matched_by_its_exact_name_never_by_similarity(string rowName)
    {
        Assert.Null(Icon(Cache(WhiteMarkPackage()), rowName));
    }

    [Fact]
    public void The_plated_variant_nearest_above_the_wanted_size_is_taken_and_never_the_light_one()
    {
        var picked = PackagedAppLogos.Pick(
            [
                "Square44x44Logo.png", "Square44x44Logo.scale-100.png", "Square44x44Logo.scale-200.png",
                "Square44x44Logo.scale-400.png",
                "Square44x44Logo.targetsize-48_altform-unplated.png",
                "Square44x44Logo.targetsize-64_altform-lightunplated.png",
                "Square44x44Logo.targetsize-256_altform-unplated.png",
                "Square44x44Logo.targetsize-64_contrast-white.png",
            ],
            "Square44x44Logo");

        Assert.Equal("Square44x44Logo.scale-200.png", picked);
    }

    [Fact]
    public void Without_a_plated_variant_the_unplated_one_is_taken()
    {
        var picked = PackagedAppLogos.Pick(
            ["Square44x44Logo.targetsize-16_altform-unplated.png", "Square44x44Logo.targetsize-48_altform-unplated.png",
             "Square44x44Logo.targetsize-64_altform-lightunplated.png"],
            "Square44x44Logo");

        Assert.Equal("Square44x44Logo.targetsize-48_altform-unplated.png", picked);
    }

    [Theory]
    [InlineData("#3143FF", 0x31, 0x43, 0xFF)]
    [InlineData("#000000", 0, 0, 0)]
    public void A_hex_plate_colour_is_read(string value, byte r, byte g, byte b) =>
        Assert.Equal(Color.FromRgb(r, g, b), PackagedAppLogos.ParsePlate(value));

    [Theory]
    [InlineData("transparent")]
    [InlineData("blue")]
    [InlineData("#FFF")]
    [InlineData(null)]
    public void Any_other_plate_value_means_no_plate(string? value) =>
        Assert.Null(PackagedAppLogos.ParsePlate(value));
}
