using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UltimateUpdater.Tests;

/// <summary>
/// A settings row shows one short line under its title; the long explanation lives in a tooltip. The
/// German text is the longest locale, so it sets the cap for every description the page binds.
/// </summary>
public class SettingsDescriptionLengthTests
{
    private const int MaxChars = 55;

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly Regex LocKey = new(@"^\{Binding Loc\[(?<key>[A-Za-z0-9_]+)\]\}$", RegexOptions.Compiled);

    [Fact]
    public void Every_bound_card_description_fits_the_cap_in_german()
    {
        var root = FindRoot();
        var xaml = XDocument.Load(Path.Combine(root, "src", "UltimateUpdater", "Views", "SettingsWindow.xaml"));
        var german = ReadResx(Path.Combine(root, "src", "UltimateUpdater", "Resources", "Strings.de.resx"));

        var keys = xaml.Descendants(Wpf + "TextBlock")
            .Where(t => (string?)t.Attribute("Style") == "{StaticResource CardDesc}")
            .Select(t => LocKey.Match((string?)t.Attribute("Text") ?? ""))
            .Where(m => m.Success)
            .Select(m => m.Groups["key"].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);

        var tooLong = keys
            .Where(k => german.TryGetValue(k, out var text) && text.Length > MaxChars)
            .Select(k => $"{k} ({german[k].Length})")
            .ToList();
        var missing = keys.Where(k => !german.ContainsKey(k)).ToList();

        Assert.True(missing.Count == 0, "keys missing in the German file: " + string.Join(", ", missing));
        Assert.True(tooLong.Count == 0, $"descriptions over {MaxChars} characters: " + string.Join(", ", tooLong));
    }

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output path.");
    }
}
