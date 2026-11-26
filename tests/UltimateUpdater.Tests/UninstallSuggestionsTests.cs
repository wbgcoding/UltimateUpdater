using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;
using static UltimateUpdater.Tests.UninstallerViewModelTests;

namespace UltimateUpdater.Tests;

/// <summary>The uninstaller's suggestions: each rule, what is never suggested, what stays hidden and the size of the list.</summary>
public class UninstallSuggestionsTests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;
    private const string Uninstall = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, DateTime> NoUsage = new Dictionary<string, DateTime>();
    private static readonly string[] NothingDismissed = [];

    private static UninstallListing Steam(string name, long? size, int appId = 1, bool emptyFolder = false) =>
        Listing(name, size: size) with
        {
            Entry = Listing(name, size: size).Entry with
            {
                Key = Uninstall + "Steam App " + appId,
                InstallLocation = $@"D:\Steam\steamapps\common\{name}",
                InstallFolderEmpty = emptyFolder,
            },
        };

    private static UninstallListing Ea(string name, long? size) =>
        Listing(name, size: size) with
        {
            Entry = Listing(name, size: size).Entry with
            {
                Key = Uninstall + "{D7FBF176-1111-2222-3333-444455556666}",
                InstallLocation = $@"D:\EA Games\{name}",
            },
        };

    private static IReadOnlyList<UninstallSuggestion> Suggest(
        IReadOnlyList<UninstallListing> listings,
        IReadOnlyDictionary<string, DateTime>? usage = null,
        IReadOnlyCollection<string>? dismissed = null) =>
        UninstallSuggestions.Build(listings, usage ?? NoUsage, dismissed ?? NothingDismissed, Now);

    private static IReadOnlyDictionary<string, DateTime> LastUsed(UninstallListing listing, int daysAgo) =>
        new Dictionary<string, DateTime> { [listing.Entry.Key] = Now.AddDays(-daysAgo) };

    [Fact]
    public void The_same_game_from_two_launchers_with_files_is_a_duplicate_and_frees_the_smaller_copy()
    {
        var suggestions = Suggest([Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB)]);

        var duplicate = Assert.Single(suggestions);
        Assert.Equal(UninstallSuggestionKind.DuplicateInstall, duplicate.Kind);
        Assert.Equal("Apex Legends", duplicate.Name);
        Assert.Equal(72 * GB, duplicate.ReclaimableBytes);
        Assert.Equal(["Steam", "EA app"], duplicate.Entries.Select(copy => copy.Source).ToArray());
        Assert.Equal([72 * GB, 104 * GB], duplicate.Entries.Select(copy => copy.Bytes ?? 0).ToArray());
        Assert.Equal("dup:apex legends", duplicate.DismissKey);
    }

    [Fact]
    public void The_name_match_ignores_case_and_extra_spaces()
    {
        var suggestions = Suggest([Steam("Apex  Legends", 10 * GB), Ea(" apex legends", 12 * GB)]);

        Assert.Equal(UninstallSuggestionKind.DuplicateInstall, Assert.Single(suggestions).Kind);
    }

    [Fact]
    public void Two_entries_of_one_launcher_are_no_duplicate()
    {
        Assert.Empty(Suggest([Steam("Apex Legends", 72 * GB, appId: 1), Steam("Apex Legends", 72 * GB, appId: 2)]));
    }

    [Fact]
    public void Two_plain_programs_with_one_name_are_no_duplicate_install()
    {
        Assert.Empty(Suggest([Listing("Tool", size: 2 * GB), Listing("Tool", size: 2 * GB)]));
    }

    [Fact]
    public void A_copy_without_files_makes_it_an_empty_entry_and_no_duplicate()
    {
        var suggestions = Suggest([Steam("Apex Legends", 0, emptyFolder: true), Ea("Apex Legends", 104 * GB)]);

        var empty = Assert.Single(suggestions);
        Assert.Equal(UninstallSuggestionKind.EmptyEntry, empty.Kind);
        Assert.Equal(0, empty.ReclaimableBytes);
        Assert.Equal("empty:" + Uninstall + "Steam App 1", empty.DismissKey);
        Assert.Equal("Steam", Assert.Single(empty.Entries).Source);
    }

    [Fact]
    public void A_large_program_unused_for_over_180_days_is_suggested()
    {
        var game = Listing("Old Game", size: 40 * GB);

        var suggestion = Assert.Single(Suggest([game], LastUsed(game, 200)));

        Assert.Equal(UninstallSuggestionKind.UnusedLarge, suggestion.Kind);
        Assert.Equal(40 * GB, suggestion.ReclaimableBytes);
        Assert.Equal(Now.AddDays(-200), suggestion.LastUsed);
        Assert.Equal("unused:" + game.Entry.Key, suggestion.DismissKey);
    }

    [Fact]
    public void A_program_used_within_180_days_is_not_unused()
    {
        var game = Listing("Recent Game", size: 40 * GB);

        Assert.Empty(Suggest([game], LastUsed(game, 100)));
        Assert.Empty(Suggest([game], LastUsed(game, 180)));
    }

    [Fact]
    public void A_small_program_is_never_called_unused_however_old()
    {
        var tool = Listing("Small Tool", size: 499 * MB);

        Assert.Empty(Suggest([tool], LastUsed(tool, 900)));
        Assert.Single(Suggest([Listing("Big Tool", size: 500 * MB)], new Dictionary<string, DateTime> { [Listing("Big Tool").Entry.Key] = Now.AddDays(-900) }));
    }


    [Fact]
    public void A_game_of_a_launcher_other_than_steam_is_never_called_unused()
    {
        var ea = Ea("Old Shooter", 40 * GB);
        var steam = Steam("Old Shooter Two", 40 * GB, appId: 2);

        var suggestions = Suggest([ea, steam], LastUsed(ea, 900).Concat(LastUsed(steam, 900)).ToDictionary(pair => pair.Key, pair => pair.Value));

        var unused = Assert.Single(suggestions);
        Assert.Equal("Old Shooter Two", unused.Name);
    }
    [Fact]
    public void Without_usage_data_nothing_is_unused()
    {
        Assert.Empty(Suggest([Listing("Huge", size: 80 * GB)]));
        Assert.Empty(Suggest([Listing("Huge", size: 80 * GB)], new Dictionary<string, DateTime> { ["some other key"] = Now.AddDays(-999) }));
    }

    [Theory]
    [InlineData(CleanupRiskLevel.Considered, true, true)]
    [InlineData(CleanupRiskLevel.Risky, true, true)]
    [InlineData(CleanupRiskLevel.Safe, false, true)]
    [InlineData(CleanupRiskLevel.Safe, true, false)]
    public void A_row_the_window_would_not_let_the_user_remove_as_a_plain_safe_program_is_never_suggested(
        CleanupRiskLevel level, bool selectable, bool runnable)
    {
        var game = Listing("Guarded", level: level, size: 40 * GB, selectable: selectable, runnable: runnable);

        Assert.Empty(Suggest([game], LastUsed(game, 400)));
    }

    [Theory]
    [InlineData(UninstallEntryKind.Driver)]
    [InlineData(UninstallEntryKind.WindowsUpdate)]
    [InlineData(UninstallEntryKind.Package)]
    [InlineData(UninstallEntryKind.StoreApp)]
    public void Only_a_classic_program_is_ever_suggested(UninstallEntryKind kind)
    {
        var entry = Listing("Other Kind", kind: kind, size: 40 * GB);

        Assert.Empty(Suggest([entry], LastUsed(entry, 400)));
    }

    [Fact]
    public void A_risky_duplicate_is_not_suggested()
    {
        var steam = Steam("Apex Legends", 72 * GB);
        var ea = Ea("Apex Legends", 104 * GB);
        ea = ea with { Risk = ea.Risk with { Level = CleanupRiskLevel.Risky } };

        Assert.Empty(Suggest([steam, ea]));
    }

    [Theory]
    [InlineData("dup:apex legends")]
    [InlineData("DUP:Apex Legends")]
    public void A_dismissed_duplicate_never_comes_back(string key)
    {
        Assert.Empty(Suggest([Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB)], dismissed: [key]));
    }

    [Fact]
    public void A_dismissed_empty_entry_and_a_dismissed_unused_program_never_come_back()
    {
        var empty = Steam("skate.", 0, appId: 7, emptyFolder: true);
        var old = Listing("Old Game", size: 40 * GB);

        var all = Suggest([empty, old], LastUsed(old, 300));
        Assert.Equal(2, all.Count);

        var rest = Suggest([empty, old], LastUsed(old, 300), dismissed: [all.First(suggestion => suggestion.Kind == UninstallSuggestionKind.EmptyEntry).DismissKey]);
        Assert.Equal(UninstallSuggestionKind.UnusedLarge, Assert.Single(rest).Kind);

        Assert.Empty(Suggest([empty, old], LastUsed(old, 300), dismissed: all.Select(suggestion => suggestion.DismissKey).ToArray()));
    }

    [Fact]
    public void At_most_five_come_back_the_largest_first()
    {
        var games = Enumerable.Range(1, 8).Select(i => Listing($"Game {i}", size: (i + 1) * GB)).ToList();
        var usage = games.ToDictionary(game => game.Entry.Key, _ => Now.AddDays(-365));

        var suggestions = Suggest(games, usage);

        Assert.Equal(UninstallSuggestions.MaxSuggestions, suggestions.Count);
        Assert.Equal(["Game 8", "Game 7", "Game 6", "Game 5", "Game 4"], suggestions.Select(suggestion => suggestion.Name).ToArray());
        Assert.Equal(suggestions.Select(suggestion => suggestion.ReclaimableBytes).OrderByDescending(bytes => bytes), suggestions.Select(suggestion => suggestion.ReclaimableBytes));
    }

    [Fact]
    public void The_three_rules_sort_together_by_reclaimable_space()
    {
        var old = Listing("Old Game", size: 40 * GB);
        var listings = new[] { Steam("skate.", 0, appId: 7, emptyFolder: true), old, Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB) };

        var suggestions = Suggest(listings, LastUsed(old, 300));

        Assert.Equal(
            [UninstallSuggestionKind.DuplicateInstall, UninstallSuggestionKind.UnusedLarge, UninstallSuggestionKind.EmptyEntry],
            suggestions.Select(suggestion => suggestion.Kind).ToArray());
    }

    [Fact]
    public void A_row_is_part_of_one_suggestion_only()
    {
        var steam = Steam("Apex Legends", 72 * GB);
        var ea = Ea("Apex Legends", 104 * GB);
        var usage = new Dictionary<string, DateTime>
        {
            [steam.Entry.Key] = Now.AddDays(-400),
            [ea.Entry.Key] = Now.AddDays(-400),
        };

        var suggestion = Assert.Single(Suggest([steam, ea], usage));

        Assert.Equal(UninstallSuggestionKind.DuplicateInstall, suggestion.Kind);
    }

    [Fact]
    public void An_empty_list_has_no_suggestions()
    {
        Assert.Empty(Suggest([]));
    }
}
