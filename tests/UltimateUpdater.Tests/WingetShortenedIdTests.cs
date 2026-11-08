using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A row whose Id winget shortened is completed by a name lookup instead of vanishing; a row that
/// cannot be completed is left out but reported through the source's scan note.
/// </summary>
public class WingetShortenedIdTests
{
    private const int NoInstalledPackage = unchecked((int)0x8A150014);

    private const string UpgradeTable = """
        Name                  Id                      Version  Available  Source
        -----------------------------------------------------------------------
        Visual Studio 2022    Microsoft.VisualStudio… 17.10    17.11      winget
        Git                   Git.Git                 2.54.0   2.55.0     winget
        """;

    private const string ListTable = """
        Name                Id                                   Version  Source
        --------------------------------------------------------------------------
        Visual Studio 2022  Microsoft.VisualStudio.2022.Community  17.10    winget
        """;

    private static async Task<(List<UpdateItem> Rows, WingetSource Source, List<string> Calls)> ScanAsync(string listOutput, int listExit = 0)
    {
        var calls = new List<string>();
        var source = new WingetSource(new LoggingService())
        {
            RunOverride = args =>
            {
                calls.Add(string.Join(' ', args));
                if (args[0] == "list")
                    return Task.FromResult(new ProcessResult(listExit, listOutput, ""));
                if (args.Contains("msstore"))
                    return Task.FromResult(new ProcessResult(NoInstalledPackage, "", ""));
                return Task.FromResult(new ProcessResult(0, UpgradeTable, ""));
            },
        };

        var rows = new List<UpdateItem>();
        await foreach (var row in source.ScanAsync(CancellationToken.None))
            rows.Add(row);
        return (rows, source, calls);
    }

    [Fact]
    public void ParseCliTable_hands_the_whole_shortened_row_to_the_caller()
    {
        var rows = new List<WingetUpgradeEntry>();

        WingetUpgradeParser.ParseCliTable(UpgradeTable, null, rows.Add);

        var row = Assert.Single(rows);
        Assert.Equal("Visual Studio 2022", row.Name);
        Assert.Equal("17.10", row.InstalledVersion);
        Assert.Equal("17.11", row.AvailableVersion);
    }

    [Fact]
    public void ParseListIdForName_reads_the_full_id_of_the_named_package()
    {
        Assert.Equal(
            "Microsoft.VisualStudio.2022.Community",
            WingetUpgradeParser.ParseListIdForName(ListTable, "visual studio 2022"));
    }

    [Fact]
    public void ParseListIdForName_gives_nothing_for_a_missing_name_or_a_still_shortened_id()
    {
        Assert.Null(WingetUpgradeParser.ParseListIdForName(ListTable, "Other"));
        Assert.Null(WingetUpgradeParser.ParseListIdForName(UpgradeTable, "Visual Studio 2022"));
        Assert.Null(WingetUpgradeParser.ParseListIdForName("", "Visual Studio 2022"));
    }

    [Fact]
    public void ParseListIdForName_refuses_two_different_ids_for_one_name()
    {
        const string table = """
            Name   Id             Version  Source
            ---------------------------------------
            Tool   Vendor.ToolA   1.0      winget
            Tool   Vendor.ToolB   1.0      winget
            """;

        Assert.Null(WingetUpgradeParser.ParseListIdForName(table, "Tool"));
    }

    [Fact]
    public async Task A_shortened_id_is_completed_by_a_name_lookup_and_the_row_stays()
    {
        var (rows, source, calls) = await ScanAsync(ListTable);

        Assert.Equal(["Git.Git", "Microsoft.VisualStudio.2022.Community"], rows.Select(r => r.Id).Order());
        var vs = rows.Single(r => r.Name == "Visual Studio 2022");
        Assert.Equal("17.10", vs.InstalledVersion);
        Assert.Equal("17.11", vs.AvailableVersion);
        Assert.Null(source.ScanNote);
        Assert.Contains(calls, c => c.StartsWith("list --name Visual Studio 2022 --exact"));
    }

    [Fact]
    public async Task A_shortened_id_that_cannot_be_completed_leaves_the_row_out_and_sets_the_note()
    {
        var (rows, source, _) = await ScanAsync("", listExit: NoInstalledPackage);

        Assert.Equal("Git.Git", Assert.Single(rows).Id);
        Assert.False(string.IsNullOrWhiteSpace(source.ScanNote));
    }

    [Fact]
    public async Task The_note_is_cleared_by_the_next_clean_scan()
    {
        var (_, source, _) = await ScanAsync("", listExit: NoInstalledPackage);
        Assert.NotNull(source.ScanNote);

        source.RunOverride = _ => Task.FromResult(new ProcessResult(0, "Name Id Version Available Source\n----\nGit Git.Git 1 2 winget\n", ""));
        await foreach (var _ in source.ScanAsync(CancellationToken.None))
        {
        }

        Assert.Null(source.ScanNote);
    }

    [Fact]
    public void The_pill_tooltip_carries_the_note_and_the_pill_counts_as_noteworthy()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Done, Note = "A caveat." });

        Assert.Equal("A caveat.", pill.TooltipText);
        Assert.True(pill.IsNoteworthy);

        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Done });

        Assert.Null(pill.TooltipText);
        Assert.False(pill.IsNoteworthy);
    }
}
