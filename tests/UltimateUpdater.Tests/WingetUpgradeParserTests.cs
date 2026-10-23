using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class WingetUpgradeParserTests
{
    // Real shape of `Get-WinGetPackage... | ConvertTo-Json -Compress` output.
    private const string ModuleJsonArray = """
        [{"Id":"Git.Git","Name":"Git","InstalledVersion":"2.54.0","AvailableVersion":"2.55.0.3"},
         {"Id":"Discord.Discord","Name":"Discord","InstalledVersion":"1.0.9240","AvailableVersion":"1.0.9248"}]
        """;

    // ConvertTo-Json emits a bare object (not an array) for a single result.
    private const string ModuleJsonSingle = """
        {"Id":"Git.Git","Name":"Git","InstalledVersion":"2.54.0","AvailableVersion":"2.55.0.3"}
        """;

    // German winget output: localized header + summary, hence header text is never trusted.
    private const string CliTableGerman = """
        Name                              ID                        Version      Verfügbar     Quelle
        -------------------------------------------------------------------------------------------
        Git                               Git.Git                   2.54.0       2.55.0.3      winget
        Microsoft GameInput               Microsoft.GameInput       3.3.221.0    3.4.218       winget
        Warframe                          DigitalExtremes.Warframe  Unknown      1.0.0         winget
        3 Aktualisierungen verfügbar.
        """;

    // Second block ("explicit targeting required"), separate separator, still real upgrades.
    private const string CliTableTwoBlocks = """
        Name    ID                   Version   Verfügbar  Quelle
        --------------------------------------------------------
        Git     Git.Git              2.54.0    2.55.0.3   winget
        2 Aktualisierungen verfügbar.

        Für die folgenden Pakete ist ein Upgrade verfügbar:
        Name    ID                   Version   Verfügbar  Quelle
        --------------------------------------------------------
        Discord Discord.Discord      1.0.9240  1.0.9248   winget
        """;

    [Fact]
    public void ParseModuleJson_reads_all_entries()
    {
        var entries = WingetUpgradeParser.ParseModuleJson(ModuleJsonArray);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Git.Git", entries[0].Id);
        Assert.Equal("2.54.0", entries[0].InstalledVersion);
        Assert.Equal("2.55.0.3", entries[0].AvailableVersion);
        Assert.Equal("Discord.Discord", entries[1].Id);
    }

    [Fact]
    public void ParseModuleJson_handles_single_object_form()
    {
        var entries = WingetUpgradeParser.ParseModuleJson(ModuleJsonSingle);

        Assert.Single(entries);
        Assert.Equal("Git.Git", entries[0].Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all {{{")]
    [InlineData("[{\"Name\":\"no id present\"}]")]
    public void ParseModuleJson_never_throws_on_bad_input(string input)
    {
        var entries = WingetUpgradeParser.ParseModuleJson(input);

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseCliTable_reads_rows_ignoring_localized_text()
    {
        var entries = WingetUpgradeParser.ParseCliTable(CliTableGerman);

        Assert.Equal(3, entries.Count);
        Assert.Equal("Git.Git", entries[0].Id);
        Assert.Equal("2.55.0.3", entries[0].AvailableVersion);
        Assert.Equal("Microsoft.GameInput", entries[1].Id);
        // "Unknown" is passed through by the parser; the provider maps it to null
        Assert.Equal("Unknown", entries[2].InstalledVersion);
    }

    [Fact]
    public void ParseCliTable_reads_rows_from_both_blocks()
    {
        var entries = WingetUpgradeParser.ParseCliTable(CliTableTwoBlocks);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "Git.Git");
        Assert.Contains(entries, e => e.Id == "Discord.Discord");
    }

    [Fact]
    public void ParseCliTable_skips_summary_and_garbage_lines()
    {
        const string noise = """
            Name    ID          Version  Verfügbar  Quelle
            ----------------------------------------------
            Git     Git.Git     2.54.0   2.55.0.3   winget
            garbage line without columns
            5 Aktualisierungen verfügbar.
            """;

        var entries = WingetUpgradeParser.ParseCliTable(noise);

        Assert.Single(entries);
        Assert.Equal("Git.Git", entries[0].Id);
    }

    /// <summary>`--source msstore` rows carry Store product ids, which have no dot.</summary>
    [Fact]
    public void ParseCliTable_keeps_store_product_ids()
    {
        const string store = """
            Name             Id              Version  Available  Source
            ---------------------------------------------------------------
            Microsoft To Do  9NBLGGH5R558    2.1      2.2        msstore
            Some Store App   XP89DCGQ3K6VLD  1.0      1.1        msstore
            2 upgrades available.
            """;

        var entries = WingetUpgradeParser.ParseCliTable(store);

        Assert.Equal(["9NBLGGH5R558", "XP89DCGQ3K6VLD"], entries.Select(e => e.Id));
        Assert.Equal("2.2", entries[0].AvailableVersion);
    }

    /// <summary>A shortened Id names no package, so the row is dropped and reported rather than offered.</summary>
    [Fact]
    public void ParseCliTable_drops_a_row_whose_id_winget_shortened()
    {
        const string table = """
            Name             Id                      Version  Available  Source
            -----------------------------------------------------------------------
            Visual Studio    Microsoft.VisualStudio… 17.10    17.11      winget
            Git              Git.Git                 2.54.0   2.55.0     winget
            """;
        var reported = new List<string>();

        var entries = WingetUpgradeParser.ParseCliTable(table, reported.Add);

        Assert.Equal("Git.Git", Assert.Single(entries).Id);
        Assert.Equal("Microsoft.VisualStudio…", Assert.Single(reported));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Es sind keine Aktualisierungen verfügbar.")]
    public void ParseCliTable_never_throws_on_bad_input(string input)
    {
        var entries = WingetUpgradeParser.ParseCliTable(input);

        Assert.Empty(entries);
    }

    // Real captured `winget show --id Git.Git --exact` output, German locale, the multi-line
    // "Versionshinweise" field, two-space-indented continuation, ends at the next unindented "Label:"
    // line (here "URL der Versionshinweise:").
    private const string ShowGermanWithReleaseNotes = """
        Gefunden Git [Git.Git]
        Version: 2.55.0.3
        Herausgeber: The Git Development Community
        Beschreibung:
          Git is a free and open source distributed version control system.

          Second paragraph, still part of the description.
        Copyright: Copyright (C) 1989, 1991 Free Software Foundation, Inc.
        Versionshinweise:
          Changes since Git for Windows v2.55.0(2) (July 2nd 2026)
          New Features
          - Comes with Git Credential Manager v2.9.0.

          Bug Fixes
          - Fixes heap overflows in the credential helper wincred.
        URL der Versionshinweise: https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.3
        Markierungen:
          git
        """;

    [Fact]
    public void ParseShowReleaseNotes_reads_the_German_multi_line_field_and_its_url()
    {
        var (body, url) = WingetUpgradeParser.ParseShowReleaseNotes(ShowGermanWithReleaseNotes);

        Assert.NotNull(body);
        Assert.StartsWith("Changes since Git for Windows v2.55.0(2)", body);
        Assert.Contains("Fixes heap overflows", body);
        // the blank line inside the notes is real formatting, kept; the field stops at the next label
        Assert.DoesNotContain("Markierungen", body);
        Assert.Equal("https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.3", url);
    }

    private const string ShowEnglishWithReleaseNotes = """
        Found Sample [Sample.Sample]
        Version: 1.2.3
        Release Notes:
          First line of the notes.
          Second line.
        Release Notes Url: https://example.com/releases/1.2.3
        Tags:
          sample
        """;

    [Fact]
    public void ParseShowReleaseNotes_reads_the_English_field()
    {
        var (body, url) = WingetUpgradeParser.ParseShowReleaseNotes(ShowEnglishWithReleaseNotes);

        Assert.Equal("First line of the notes.\nSecond line.", body);
        Assert.Equal("https://example.com/releases/1.2.3", url);
    }

    /// <summary>Most packages' manifests carry neither field, a real "nothing to show" answer.</summary>
    [Fact]
    public void ParseShowReleaseNotes_returns_nothing_when_the_manifest_has_no_release_notes()
    {
        const string show = """
            Gefunden Microsoft Visual Studio Code [Microsoft.VisualStudioCode]
            Version: 1.130.0
            Herausgeber: Microsoft Corporation
            Beschreibung: A code editor.
            Installationsprogramm:
              Installertyp: inno
            """;

        var (body, url) = WingetUpgradeParser.ParseShowReleaseNotes(show);

        Assert.Null(body);
        Assert.Null(url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseShowReleaseNotes_never_throws_on_bad_input(string input)
    {
        var (body, url) = WingetUpgradeParser.ParseShowReleaseNotes(input);

        Assert.Null(body);
        Assert.Null(url);
    }

    /// <summary>
    /// This fix's re-check: `winget list --id &lt;id&gt; --exact` has no "Available" column at all once the
    /// package is fully up to date, unlike the upgrade table <see cref="ParseCliTable"/> reads.
    /// </summary>
    [Fact]
    public void ParseListVersion_reads_the_version_column_with_no_available_column()
    {
        const string list = """
            Name  Id       Version   Quelle
            --------------------------------
            Git   Git.Git  2.55.0.3  winget
            """;

        Assert.Equal("2.55.0.3", WingetUpgradeParser.ParseListVersion(list, "Git.Git"));
        Assert.Null(WingetUpgradeParser.ParseListVersion(list, "Discord.Discord"));
    }

    /// <summary>winget pads a column to its widest value plus ONE space, so when every name is shorter than the
    /// "Name" label the header has a single space after it.</summary>
    [Fact]
    public void ParseListVersion_splits_columns_winget_separated_by_a_single_space()
    {
        const string list = """
            Name Id      Version Source
            ---------------------------
            Git  Git.Git 2.45.1  winget
            """;

        Assert.Equal("2.45.1", WingetUpgradeParser.ParseListVersion(list, "Git.Git"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no table here at all")]
    public void ParseListVersion_never_throws_on_bad_input(string input) =>
        Assert.Null(WingetUpgradeParser.ParseListVersion(input, "Git.Git"));

    // winget pads by console cell width: a two-character CJK name fills four cells, so the Id column
    // starts four cells in, not two characters in.
    private const string WideName = "微信";

    [Fact]
    public void ParseCliTable_aligns_columns_by_cell_width_for_a_CJK_name()
    {
        var table =
            "Name        Id                Version   Available  Source\n" +
            "---------------------------------------------------------\n" +
            WideName + "        Tencent.WeChat    3.9.0     3.9.1      winget\n" +
            "Git         Git.Git           2.54.0    2.55.0.3   winget\n";

        var entries = WingetUpgradeParser.ParseCliTable(table);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new WingetUpgradeEntry("Tencent.WeChat", WideName, "3.9.0", "3.9.1"), entries[0]);
        Assert.Equal("Git.Git", entries[1].Id);
    }

    [Fact]
    public void ParseListVersion_aligns_columns_by_cell_width_for_a_CJK_name()
    {
        var list =
            "Name        Id                Version   Source\n" +
            "---------------------------------------------\n" +
            WideName + "        Tencent.WeChat    3.9.0     winget\n";

        Assert.Equal("3.9.0", WingetUpgradeParser.ParseListVersion(list, "Tencent.WeChat"));
    }
}
