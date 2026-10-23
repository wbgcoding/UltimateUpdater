using System.Xml.Linq;

namespace UltimateUpdater.Tests;

/// <summary>
/// The hint banner on the main window (the "Installed, reboot pending" notice) set the tone every other
/// panel surface in the app is meant to share: default card background, default card stroke, an 8px corner
/// radius and the dark-theme glass top highlight. Several windows had drifted from it (a secondary, dimmer
/// background on one cleanup card; a missing highlight on another), so the tone now lives in one shared
/// App.xaml style, "PanelSurfaceBorder", and every card style either derives from it or repeats its setters
/// verbatim.
/// Asserted against the XAML source rather than a live rendered brush, the same reasoning as
/// <see cref="ButtonStyleTests"/>: a resolved brush depends on whichever theme happens to be loaded in the
/// shared test Application, while the contract that actually matters, "this Border uses the shared tone",
/// lives in the markup itself.
/// </summary>
public class PanelSurfaceStyleTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace X =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly XNamespace Views =
        "clr-namespace:UltimateUpdater.Views";

    private static string? Key(XElement element) => (string?)element.Attribute(X + "Key");

    private static string? GlassCardHighlightShow(XElement element) =>
        (string?)element.Attribute(Views + "GlassCardHighlight.Show");

    private static XElement LoadXaml(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", relativePath);
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate).Root
                    ?? throw new InvalidOperationException($"{relativePath} has no root element.");
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from the test output path.");
    }

    private static string? SetterValue(XElement style, string property) =>
        style.Descendants(Presentation + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == property)
            ?.Attribute("Value")?.Value;

    [Fact]
    public void App_xaml_defines_the_shared_panel_surface_style_in_the_banner_tone()
    {
        var style = LoadXaml("App.xaml").Descendants(Presentation + "Style")
            .FirstOrDefault(s => Key(s) == "PanelSurfaceBorder");

        Assert.NotNull(style);
        Assert.Equal("{DynamicResource CardBackgroundFillColorDefaultBrush}", SetterValue(style!, "Background"));
        Assert.Equal("{DynamicResource CardStrokeColorDefaultBrush}", SetterValue(style!, "BorderBrush"));
        Assert.Equal("8", SetterValue(style!, "CornerRadius"));
        Assert.Equal("True", SetterValue(style!, "views:GlassCardHighlight.Show"));
    }

    [Fact]
    public void Main_window_section_card_derives_from_the_shared_panel_surface_style()
    {
        var style = LoadXaml("Views/MainWindow.xaml").Descendants(Presentation + "Style")
            .FirstOrDefault(s => Key(s) == "SectionCard");

        Assert.NotNull(style);
        Assert.Equal("{StaticResource PanelSurfaceBorder}", (string?)style!.Attribute("BasedOn"));
    }

    [Fact]
    public void Settings_window_card_derives_from_the_shared_panel_surface_style()
    {
        var style = LoadXaml("Views/SettingsWindow.xaml").Descendants(Presentation + "Style")
            .FirstOrDefault(s => Key(s) == "Card");

        Assert.NotNull(style);
        Assert.Equal("{StaticResource PanelSurfaceBorder}", (string?)style!.Attribute("BasedOn"));
    }

    [Fact]
    public void Cleanup_window_group_card_template_carries_the_glass_highlight()
    {
        var style = LoadXaml("Views/CleanupWindow.xaml").Descendants(Presentation + "Style")
            .FirstOrDefault(s => Key(s) == "CleanupGroupCard");

        Assert.NotNull(style);
        var border = style!.Descendants(Presentation + "Border").FirstOrDefault();

        Assert.NotNull(border);
        Assert.Equal("{DynamicResource CardBackgroundFillColorDefaultBrush}",
            (string?)border!.Attribute("Background"));
        Assert.Equal("True", GlassCardHighlightShow(border));
    }
}
