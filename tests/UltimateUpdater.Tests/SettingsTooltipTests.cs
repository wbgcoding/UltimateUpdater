using System.Xml.Linq;

namespace UltimateUpdater.Tests;

/// <summary>Every setting explains itself on hover: each card of the settings page carries a tooltip on its title block or on something inside its own row.</summary>
public class SettingsTooltipTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Every_settings_card_has_a_tooltip()
    {
        var xaml = XDocument.Load(ViewSources.Find(Path.Combine(FindRoot(), "src", "UltimateUpdater", "Views"), "SettingsWindow.xaml"));

        // A card is an icon, a title with a description line under it and its control; group headings and one-line cards are not.
        var titles = xaml.Descendants(Wpf + "TextBlock")
            .Where(t => (string?)t.Attribute("Style") == "{StaticResource CardTitle}"
                        && t.Parent!.Elements(Wpf + "TextBlock").Any(s => (string?)s.Attribute("Style") == "{StaticResource CardDesc}")
                        && t.Parent!.Parent!.Elements().Any(i => (string?)i.Attribute("Style") == "{StaticResource CardIcon}"))
            .ToList();
        Assert.NotEmpty(titles);

        var without = new List<string>();
        foreach (var title in titles)
        {
            // The card's own row is the nearest Grid above the title's column panel.
            var row = title.Ancestors(Wpf + "Grid").FirstOrDefault();
            var covered = row is not null
                && row.DescendantsAndSelf().Any(e => e.Attribute("ToolTip") is not null || e.Element(Wpf + (e.Name.LocalName + ".ToolTip")) is not null);
            if (!covered)
                without.Add((string?)title.Attribute("Text") ?? "?");
        }

        Assert.True(without.Count == 0, "settings cards without a tooltip: " + string.Join(", ", without));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "UltimateUpdater")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
