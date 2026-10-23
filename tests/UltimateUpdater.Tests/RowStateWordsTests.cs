using System.Xml.Linq;

namespace UltimateUpdater.Tests;

/// <summary>The state column shows one word per outcome; two outcomes sharing a word (a stuck row reading
/// "Failed" like a failed one) hides the difference the extra states exist for.</summary>
public class RowStateWordsTests
{
    private static readonly string[] Keys = ["RowFailed", "RowStuck", "RowNeedsManualWork"];

    public static IEnumerable<object[]> LocaleFiles() =>
        Directory.EnumerateFiles(ResourcesDirectory(), "Strings*.resx")
            .Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(LocaleFiles))]
    public void Failed_stuck_and_needs_manual_work_read_differently_in_every_locale(string fileName)
    {
        var words = XDocument.Load(Path.Combine(ResourcesDirectory(), fileName)).Root!
            .Elements("data")
            .Where(e => Keys.Contains(e.Attribute("name")?.Value))
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

        Assert.Equal(Keys.Length, words.Count);
        Assert.Equal(Keys.Length, words.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("Strings.resx", "Stuck", "Needs you")]
    [InlineData("Strings.de.resx", "Hängt fest", "Braucht dich")]
    public void The_stuck_and_needs_manual_work_words_match_the_decided_wording(
        string fileName, string stuck, string needsYou)
    {
        var entries = XDocument.Load(Path.Combine(ResourcesDirectory(), fileName)).Root!
            .Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

        Assert.Equal(stuck, entries["RowStuck"]);
        Assert.Equal(needsYou, entries["RowNeedsManualWork"]);
    }

    private static string ResourcesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Resources");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Resources directory from the test output path.");
    }
}
