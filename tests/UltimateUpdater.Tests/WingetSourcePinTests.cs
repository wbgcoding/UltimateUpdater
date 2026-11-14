using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A winget update is fetched from the source the scan found the package in: one broken extra source
/// otherwise fails every upgrade, because winget asks all configured sources.
/// </summary>
public class WingetSourcePinTests
{
    private const string ModuleJson = """
        [{"Id":"Git.Git","Name":"Git","InstalledVersion":"2.54.0","AvailableVersion":"2.55.0","Source":"winget"},
         {"Id":"Vendor.NoSource","Name":"No Source","InstalledVersion":"1.0","AvailableVersion":"1.1"}]
        """;

    private const string GermanTable = """
        Name     ID           Version  Verfügbar  Quelle
        --------------------------------------------------
        Git      Git.Git      2.54.0   2.55.0     winget
        Store    9NBLGGH5R558 2.1      2.2        msstore
        2 Aktualisierungen verfügbar.
        """;

    private const string EnglishTable = """
        Name     Id           Version  Available  Source
        --------------------------------------------------
        Git      Git.Git      2.54.0   2.55.0     winget
        1 upgrades available.
        """;

    private const string FourColumnTable = """
        Name     Id           Version  Available
        ------------------------------------------
        Git      Git.Git      2.54.0   2.55.0
        """;

    private static UpdateItem Item(string? source) => new()
    {
        Id = "Git.Git",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Git",
        Meta = source is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [WingetSource.SourceMetaKey] = source },
    };

    [Fact]
    public void The_module_json_carries_the_source()
    {
        var entries = WingetUpgradeParser.ParseModuleJson(ModuleJson);

        Assert.Equal("winget", entries[0].Source);
        Assert.Null(entries[1].Source);
    }

    [Fact]
    public void A_German_table_yields_the_source_column()
    {
        var entries = WingetUpgradeParser.ParseCliTable(GermanTable);

        Assert.Equal(["winget", "msstore"], entries.Select(e => e.Source));
    }

    [Fact]
    public void An_English_table_yields_the_source_column()
    {
        var entries = WingetUpgradeParser.ParseCliTable(EnglishTable);

        Assert.Equal("winget", Assert.Single(entries).Source);
    }

    [Fact]
    public void A_table_without_a_fifth_column_has_no_source()
    {
        var entries = WingetUpgradeParser.ParseCliTable(FourColumnTable);

        Assert.Null(Assert.Single(entries).Source);
    }

    private static async Task<List<UpdateItem>> Scan(string table)
    {
        var source = new WingetSource(new LoggingService())
        {
            RunOverride = _ => Task.FromResult(new ProcessResult(0, table, "")),
        };

        var items = new List<UpdateItem>();
        await foreach (var item in source.ScanAsync(CancellationToken.None))
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task The_scan_stores_the_source_in_the_item_meta()
    {
        var items = await Scan(GermanTable);

        Assert.Equal("winget", items.Single(i => i.Id == "Git.Git").Meta[WingetSource.SourceMetaKey]);
    }

    [Fact]
    public async Task A_scanned_row_without_a_source_has_no_meta_entry()
    {
        var items = await Scan(FourColumnTable);

        Assert.DoesNotContain(WingetSource.SourceMetaKey, items.Single().Meta.Keys);
    }

    private static void AssertPinned(IReadOnlyList<string> arguments, string source)
    {
        var index = arguments.ToList().IndexOf("--source");
        Assert.True(index >= 0, "no --source in: " + string.Join(' ', arguments));
        Assert.Equal(source, arguments[index + 1]);
        Assert.Equal(1, arguments.Count(a => a == "--source"));
    }

    [Fact]
    public void Every_install_argument_list_carries_the_source()
    {
        AssertPinned(WingetSource.InstallArguments("Git.Git", "winget"), "winget");
        AssertPinned(WingetSource.InteractiveInstallArguments("Git.Git", "winget"), "winget");
        AssertPinned(WingetSource.VersionPinnedInstallArguments("Git.Git", "2.55.0", "winget"), "winget");
        AssertPinned(WingetSource.FreshInstallArguments("Git.Git", "user", "winget"), "winget");
        foreach (var rung in WingetSource.InstallLadder("Git.Git", "winget"))
            AssertPinned(rung.Arguments, "winget");
    }

    [Fact]
    public void Without_a_source_the_argument_lists_are_unchanged()
    {
        Assert.DoesNotContain("--source", WingetSource.InstallArguments("Git.Git"));
        Assert.DoesNotContain("--source", WingetSource.InteractiveInstallArguments("Git.Git"));
        Assert.DoesNotContain("--source", WingetSource.VersionPinnedInstallArguments("Git.Git", "2.55.0"));
        Assert.DoesNotContain("--source", WingetSource.FreshInstallArguments("Git.Git", "user"));
        foreach (var rung in WingetSource.InstallLadder("Git.Git"))
            Assert.DoesNotContain("--source", rung.Arguments);
    }

    [Theory]
    [InlineData("winget", "winget")]
    [InlineData("msstore", "msstore")]
    [InlineData("my_source-1.x", "my_source-1.x")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("-evil", null)]
    [InlineData("--accept", null)]
    [InlineData("a b", null)]
    [InlineData("a;b", null)]
    [InlineData("a&b", null)]
    [InlineData("a\"b", null)]
    [InlineData("a..b", null)]
    [InlineData("https://host/x", null)]
    [InlineData("ünï", null)]
    public void Only_a_plain_source_name_is_used(string? name, string? expected)
    {
        Assert.Equal(expected, WingetSource.SafeSource(Item(name)));
    }

    [Fact]
    public void A_source_name_over_64_characters_is_not_used()
    {
        Assert.Null(WingetSource.SafeSource(Item(new string('a', 65))));
        Assert.NotNull(WingetSource.SafeSource(Item(new string('a', 64))));
    }

    private static async Task<List<string>> RunInstall(string? source, int upgradeExit)
    {
        // The ladder stops early while the process is elevated (the user-scope retry belongs to the queue), so an
        // elevated test runner would see fewer calls than the unelevated run this test describes.
        ElevationService.IsElevatedForTest.Value = false;
        var calls = new List<string>();
        var winget = new WingetSource(new LoggingService())
        {
            RunOverride = args =>
            {
                calls.Add(string.Join(' ', args));
                return args[0] == "show"
                    ? Task.FromResult(new ProcessResult(0, "Version\n-------\n2.0.0\n", ""))
                    : Task.FromResult(new ProcessResult(upgradeExit, "", ""));
            },
        };
        try
        {
            await winget.InstallAsync(Item(source), new Progress<InstallProgress>(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotApplicableException)
        {
            // the scripted winget refuses every rung; only the recorded calls matter here
        }

        return calls;
    }

    private static async Task<List<string>> RunShowCalls(string? source)
    {
        var calls = new List<string>();
        var winget = new WingetSource(new LoggingService())
        {
            RunOverride = args =>
            {
                calls.Add(string.Join(' ', args));
                return Task.FromResult(new ProcessResult(0, "", ""));
            },
        };

        await winget.CanFetchAsync(Item(source), CancellationToken.None);
        await winget.EnrichAsync(Item(source), CancellationToken.None);
        return calls;
    }

    [Fact]
    public async Task The_reinstall_lookup_and_the_enrichment_show_name_the_source()
    {
        var calls = await RunShowCalls("winget");

        Assert.Equal(2, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.StartsWith("show ", call);
            Assert.Contains("--source winget", call);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-evil")]
    public async Task The_show_calls_without_a_safe_source_run_as_before(string? source)
    {
        var calls = await RunShowCalls(source);

        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.DoesNotContain("--source", call));
    }

    [Fact]
    public async Task Every_call_of_a_full_ladder_run_names_the_source()
    {
        var calls = await RunInstall("winget", unchecked((int)0x8A15002B));

        Assert.True(calls.Count >= 4);
        Assert.All(calls, call => Assert.Contains("--source winget", call));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-evil")]
    [InlineData("a b")]
    public async Task An_item_without_a_safe_source_runs_as_before(string? source)
    {
        var calls = await RunInstall(source, unchecked((int)0x8A15002B));

        Assert.True(calls.Count >= 4);
        Assert.All(calls, call => Assert.DoesNotContain("--source", call));
    }
}
