using System.Xml.Linq;

namespace UltimateUpdater.Tests;

/// <summary>The EN and DE wording that was unified across the app: one word per idea, so the same thing
/// never reads two ways.</summary>
public class ConsistentWordingTests
{
    private static readonly string[] RejectedKeys =
    [
        "SettingsDataFolderRejectedLink", "SettingsDataFolderRejectedSystem", "SettingsDataFolderRejectedProgram",
        "SettingsDataFolderRejectedCurrent", "SettingsDataFolderRejectedTempOrCache", "SettingsDataFolderRejectedWritable",
        "SettingsDataFolderRejectedNonEmpty",
    ];

    [Theory]
    [InlineData("Strings.de.resx", "SettingsCloseToTray", "In den Infobereich schließen")]
    [InlineData("Strings.de.resx", "SettingsStartupGroup", "Start und Beenden")]
    [InlineData("Strings.de.resx", "CleanupRescan", "Neu prüfen")]
    [InlineData("Strings.resx", "TrayScan", "Check for updates")]
    [InlineData("Strings.de.resx", "SettingsCheckNow", "Jetzt prüfen")]
    [InlineData("Strings.de.resx", "SettingsResetDesc", "Die App auf den Werkszustand zurücksetzen.")]
    [InlineData("Strings.de.resx", "SettingsResetDoneBody", "Die App ist wieder im Werkszustand.")]
    [InlineData("Strings.resx", "SettingsMeteredTitle", "Don't search automatically on a metered connection")]
    [InlineData("Strings.de.resx", "SettingsMeteredTitle", "Bei getakteter Verbindung nicht automatisch suchen")]
    public void Unified_values_are_exact(string file, string key, string expected) =>
        Assert.Equal(expected, Read(file)[key]);

    [Fact]
    public void The_downgrade_confirmation_speaks_of_a_search_not_a_scan_in_german()
    {
        var body = Read("Strings.de.resx")["DowngradeConfirmBody"];

        Assert.Contains("nächsten Suche", body);
        Assert.DoesNotContain("Scan", body);
    }

    [Fact]
    public void Admin_rights_is_one_spelling_in_German_and_English()
    {
        Assert.DoesNotContain(Read("Strings.de.resx"), e => e.Value.Contains("Adminrechte"));
        Assert.DoesNotContain(Read("Strings.resx"), e => e.Value.Contains("administrator rights", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Read("Strings.de.resx"), e => e.Value.Contains("Admin-Rechte"));
    }

    [Fact]
    public void Every_rejected_data_folder_message_asks_for_another_folder()
    {
        var english = Read("Strings.resx");
        var german = Read("Strings.de.resx");

        foreach (var key in RejectedKeys)
        {
            Assert.EndsWith(" Please choose another folder.", english[key]);
            Assert.EndsWith(" Bitte einen anderen Ordner wählen.", german[key]);
        }
    }

    [Fact]
    public void The_catalogue_failure_tells_the_user_what_to_do_next()
    {
        Assert.EndsWith(" Close and reopen this window. Details are in the log folder.", Read("Strings.resx")["CleanupCatalogLoadFailed"]);
        Assert.EndsWith(" Schließe das Fenster und öffne es erneut. Details stehen im Protokollordner.", Read("Strings.de.resx")["CleanupCatalogLoadFailed"]);
    }

    /// <summary>The settings card, the crash message and the error details all say "Protokoll"; a stray
    /// "Log-Ordner" made the same folder read two ways.</summary>
    [Fact]
    public void The_log_folder_is_one_word_in_German()
    {
        Assert.DoesNotContain(Read("Strings.de.resx"), e => e.Value.Contains("Log-Ordner", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Read("Strings.de.resx"), e => e.Value.Contains("Protokollordner"));
    }

    private static Dictionary<string, string> Read(string fileName) =>
        XDocument.Load(Path.Combine(ResourcesDirectory(), fileName)).Root!
            .Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? string.Empty);

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
