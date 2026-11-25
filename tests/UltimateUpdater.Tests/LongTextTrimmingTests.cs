using System.Xml.Linq;

namespace UltimateUpdater.Tests;

/// <summary>Text that can outgrow its lane (a long wording in another language, a long program or device name) ends in an ellipsis and shows in full on hover, instead of being cut at the edge or spilling over its neighbour.</summary>
public class LongTextTrimmingTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument Load(string file) =>
        XDocument.Load(Directory.EnumerateFiles(Path.Combine(FindRoot(), "src", "UltimateUpdater", "Views"), Path.GetFileName(file), SearchOption.AllDirectories).Single());

    [Fact]
    public void The_interactive_running_label_stays_inside_the_action_lane()
    {
        var labels = Load("UpdatesTable.xaml").Descendants(Wpf + "TextBlock")
            .Where(t => (string?)t.Attribute("Text") == "{Binding InteractiveRunningText}")
            .ToList();

        Assert.Equal(2, labels.Count);
        Assert.All(labels, label =>
        {
            Assert.NotNull(label.Attribute("MaxWidth"));
            Assert.Equal("CharacterEllipsis", (string?)label.Attribute("TextTrimming"));
            Assert.Equal("{Binding InteractiveRunningText}", (string?)label.Attribute("ToolTip"));
        });
    }

    [Theory]
    [InlineData("DisplayName", 3)]
    [InlineData("DisplayLabel", 1)]
    public void Candidate_names_in_the_cleanup_lists_are_trimmed_and_readable_on_hover(string property, int expectedCount)
    {
        var names = Load(Path.Combine("Cleanup", "CleanupTemplates.xaml")).Descendants(Wpf + "TextBlock")
            .Where(t => (string?)t.Attribute("Text") == "{Binding " + property + "}"
                        && (string?)t.Attribute("Grid.Column") == "1" && (string?)t.Attribute("Grid.Row") == "0")
            .ToList();

        Assert.Equal(expectedCount, names.Count);
        Assert.All(names, name =>
        {
            Assert.Equal("CharacterEllipsis", (string?)name.Attribute("TextTrimming"));
            Assert.Equal("{Binding " + property + "}", (string?)name.Attribute("ToolTip"));
        });
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "UltimateUpdater")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
