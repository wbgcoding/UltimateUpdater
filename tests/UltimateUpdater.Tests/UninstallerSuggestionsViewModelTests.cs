using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;
using static UltimateUpdater.Tests.UninstallerViewModelTests;

namespace UltimateUpdater.Tests;

/// <summary>The suggestions card of the uninstaller window: when it shows, what its buttons do and that a dismissal sticks.</summary>
[Collection("UiLanguage")]
public class UninstallerSuggestionsViewModelTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const string Uninstall = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static UninstallListing Steam(string name, long size) =>
        Listing(name, size: size) with
        {
            Entry = Listing(name, size: size).Entry with
            {
                Key = Uninstall + "Steam App 7",
                InstallLocation = $@"D:\Steam\steamapps\common\{name}",
            },
        };

    private static UninstallListing Ea(string name, long size) =>
        Listing(name, size: size) with
        {
            Entry = Listing(name, size: size).Entry with
            {
                Key = Uninstall + "{D7FBF176-1111-2222-3333-444455556666}",
                InstallLocation = $@"D:\EA Games\{name}",
            },
        };

    private static async Task<UninstallerViewModel> LoadAsync(
        Func<IReadOnlyList<InstalledProgramEntry>, CancellationToken, IReadOnlyDictionary<string, DateTime>>? reader,
        params UninstallListing[] listings)
    {
        LocalizationService.Instance.Apply("en");
        var vm = new UninstallerViewModel(new FakeBackend(listings), new LoggingService())
        {
            PostToUi = action => action(),
            UsageReader = reader,
            UtcNow = () => new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc),
        };
        await vm.LoadAsync();
        await vm.SuggestionsReady;
        return vm;
    }

    [Fact]
    public async Task The_card_is_hidden_when_nothing_is_worth_a_suggestion()
    {
        var vm = await LoadAsync(null, Listing("Small", size: 10 * 1024 * 1024));

        Assert.False(vm.HasSuggestions);
        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task A_duplicate_shows_without_any_usage_data_and_the_title_counts_the_lines()
    {
        var vm = await LoadAsync(null, Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB));

        var line = Assert.Single(vm.Suggestions);
        Assert.True(vm.HasSuggestions);
        Assert.True(line.IsShowAction);
        Assert.Equal(2, line.Rows.Count);
        Assert.Equal("Suggestions (1)", vm.SuggestionsTitle);
        Assert.Contains("Steam", line.ReasonText);
        Assert.Contains("EA app", line.ReasonText);
    }

    [Fact]
    public async Task An_unused_program_appears_once_the_usage_read_has_finished()
    {
        var big = Listing("Old Editor", size: 2 * GB);
        var vm = await LoadAsync(
            (_, _) => new Dictionary<string, DateTime> { [big.Entry.Key] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            big);

        var line = Assert.Single(vm.Suggestions);
        Assert.False(line.IsShowAction);
        Assert.Equal("Old Editor", line.Name);
    }

    [Fact]
    public async Task A_usage_read_that_throws_leaves_no_unused_suggestions_and_does_not_crash()
    {
        var vm = await LoadAsync((_, _) => throw new InvalidOperationException("registry unreadable"), Listing("Old Editor", size: 2 * GB));

        Assert.False(vm.HasSuggestions);
    }

    [Fact]
    public async Task Select_ticks_the_row_of_an_unused_program()
    {
        var big = Listing("Old Editor", size: 2 * GB);
        var vm = await LoadAsync((_, _) => new Dictionary<string, DateTime> { [big.Entry.Key] = DateTime.UnixEpoch }, big);

        vm.SelectSuggestionCommand.Execute(vm.Suggestions[0]);

        Assert.True(vm.AllRows.Single().IsSelected);
    }

    [Fact]
    public async Task Show_on_a_duplicate_ticks_nothing_but_opens_the_section_highlights_both_rows_and_asks_the_window_to_scroll()
    {
        var vm = await LoadAsync(null, Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB));
        foreach (var section in vm.Sections)
            section.IsExpanded = false;

        IReadOnlyList<UninstallRowViewModel>? revealed = null;
        vm.RowsRevealRequested += rows => revealed = rows;

        vm.SelectSuggestionCommand.Execute(vm.Suggestions[0]);

        Assert.All(vm.AllRows, row => Assert.False(row.IsSelected));
        Assert.All(vm.AllRows, row => Assert.True(row.IsHighlighted));
        Assert.True(vm.Sections[0].IsExpanded);
        Assert.Equal(2, revealed?.Count);
    }

    [Fact]
    public async Task Dismiss_removes_the_line_remembers_the_key_and_it_stays_away_after_a_reload()
    {
        var vm = await LoadAsync(null, Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB));

        vm.DismissSuggestionCommand.Execute(vm.Suggestions[0]);

        Assert.False(vm.HasSuggestions);

        vm.SetListings([Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB)]);
        await vm.SuggestionsReady;

        Assert.False(vm.HasSuggestions);
    }

    [Fact]
    public async Task Dismiss_writes_the_key_to_the_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), "uu-suggest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(new LoggingService(), path);
            var vm = await LoadAsync(null, Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB));
            vm.Settings = settings;
            vm.SetListings([Steam("Apex Legends", 72 * GB), Ea("Apex Legends", 104 * GB)]);
            await vm.SuggestionsReady;

            vm.DismissSuggestionCommand.Execute(vm.Suggestions[0]);

            Assert.Single(settings.Current.UninstallSuggestionsDismissed);
            Assert.StartsWith("dup:", settings.Current.UninstallSuggestionsDismissed[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
