using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;
using static UltimateUpdater.Tests.UninstallerViewModelTests;

namespace UltimateUpdater.Tests;

/// <summary>The launcher label of an entry, the shortened folder, the empty Steam folder, and the rule that look-alike names always show both.</summary>
[Collection("UiLanguage")]
public class UninstallEntryOriginTests
{
    private const string UninstallRoot = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static InstalledProgramEntry Entry(
        string name = "Apex Legends", string key = "{D7FBF176-1111-2222-3333-444455556666}", string uninstall = "", string quiet = "",
        string location = "", string publisher = "", string icon = "", UninstallEntryKind kind = UninstallEntryKind.Program) =>
        new()
        {
            Key = UninstallRoot + key, Kind = kind, Name = name, Publisher = publisher, InstallLocation = location,
            UninstallString = uninstall, QuietUninstallString = quiet, DisplayIcon = icon,
        };

    [Theory]
    [InlineData("Steam App 1172470")]
    [InlineData("steam app 7")]
    public void A_steam_app_key_is_labelled_steam(string key) =>
        Assert.Equal(UninstallEntryOrigin.Steam, UninstallEntryOrigin.Label(Entry(key: key)));

    [Theory]
    [InlineData("Steam App ")]
    [InlineData("Steam App 12x")]
    [InlineData("Steam")]
    public void A_key_that_only_looks_like_a_steam_app_key_is_not(string key) =>
        Assert.Null(UninstallEntryOrigin.Label(Entry(name: "SteamWorld Dig 2", key: key)));

    [Fact]
    public void A_game_named_after_steam_still_gets_the_label()
    {
        Assert.Equal(UninstallEntryOrigin.Steam, UninstallEntryOrigin.Label(Entry(name: "SteamWorld Dig 2", key: "Steam App 1")));
    }

    [Theory]
    [InlineData(@"""C:\ProgramData\EAInstaller\Cleanup.exe"" /uninstall", "")]
    [InlineData("", @"D:\EA Games\Apex")]
    [InlineData("", @"D:\ea games\Battlefield 6")]
    public void An_installer_under_eainstaller_or_a_folder_under_ea_games_is_labelled_ea_app(string uninstall, string location) =>
        Assert.Equal(UninstallEntryOrigin.EaApp, UninstallEntryOrigin.Label(Entry(uninstall: uninstall, location: location)));

    [Fact]
    public void A_folder_that_only_contains_the_words_ea_games_inside_another_name_is_not_ea_app() =>
        Assert.Null(UninstallEntryOrigin.Label(Entry(location: @"D:\Games\Not EA Games Fan Pack\Apex")));

    [Theory]
    [InlineData(@"""C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"" -uninstall", "")]
    [InlineData("", @"D:\Epic Games\Fortnite")]
    public void Epic_is_recognised_by_its_launcher_command_or_folder(string uninstall, string location) =>
        Assert.Equal(UninstallEntryOrigin.Epic, UninstallEntryOrigin.Label(Entry(uninstall: uninstall, location: location)));

    [Theory]
    [InlineData(@"""D:\GOG Games\Witcher 3\unins000.exe""", "", "")]
    [InlineData("", @"C:\Program Files (x86)\GOG Galaxy\Games\Cyberpunk", "")]
    [InlineData("", "", "GOG.com")]
    public void Gog_is_recognised_by_its_folders_or_publisher(string uninstall, string location, string publisher) =>
        Assert.Equal(UninstallEntryOrigin.Gog, UninstallEntryOrigin.Label(Entry(uninstall: uninstall, location: location, publisher: publisher)));

    [Fact]
    public void Ubisoft_is_recognised_by_the_uplay_install_key_or_its_launcher_folder()
    {
        Assert.Equal(UninstallEntryOrigin.Ubisoft, UninstallEntryOrigin.Label(Entry(key: "Uplay Install 3539")));
        Assert.Equal(
            UninstallEntryOrigin.Ubisoft,
            UninstallEntryOrigin.Label(Entry(location: @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Anno")));
    }

    [Fact]
    public void Battle_net_is_recognised_by_its_agent_uninstall_command() =>
        Assert.Equal(
            UninstallEntryOrigin.BattleNet,
            UninstallEntryOrigin.Label(Entry(name: "Overwatch", uninstall: @"""C:\ProgramData\Battle.net\Agent\Agent.exe"" --uninstall --game=pro")));

    [Fact]
    public void An_ordinary_program_has_no_label_and_nor_has_anything_that_is_not_a_classic_program()
    {
        Assert.Null(UninstallEntryOrigin.Label(Entry(name: "7-Zip", key: "7-Zip", uninstall: @"""C:\Program Files\7-Zip\Uninstall.exe""")));
        Assert.Null(UninstallEntryOrigin.Label(Entry(key: "Steam App 1", kind: UninstallEntryKind.StoreApp)));
    }

    [Theory]
    [InlineData("Steam", "Steam App 7")]
    [InlineData("Epic Games Launcher", "{ABC}")]
    [InlineData("Battle.net", "Battle.net")]
    public void The_launcher_client_itself_is_no_game_from_that_launcher(string name, string key) =>
        Assert.Null(UninstallEntryOrigin.Label(Entry(name: name, key: key, location: @"C:\Program Files (x86)\Epic Games\Launcher")));

    [Fact]
    public void A_short_folder_shows_whole_and_a_long_one_as_its_last_two_folders()
    {
        Assert.Equal(@"D:\EA Games\Apex", UninstallEntryOrigin.ShortFolder(Entry(location: @"D:\EA Games\Apex\")));
        Assert.Equal(
            "\u2026\\common\\Apex Legends",
            UninstallEntryOrigin.ShortFolder(Entry(location: @"D:\SteamLibrary\steamapps\common\Apex Legends")));
        Assert.Equal(string.Empty, UninstallEntryOrigin.ShortFolder(Entry()));
        Assert.Equal(string.Empty, UninstallEntryOrigin.ShortFolder(Entry(location: @"C:\x", kind: UninstallEntryKind.StoreApp)));
    }

    [Fact]
    public void A_steam_entry_with_an_empty_folder_is_marked_and_the_others_are_left_alone()
    {
        var empty = Entry(name: "skate.", key: "Steam App 1", location: @"D:\Steam\skate");
        var filled = Entry(name: "Apex Legends", key: "Steam App 2", location: @"D:\Steam\apex");
        var noFolder = Entry(name: "Gone", key: "Steam App 3");
        var otherLauncher = Entry(name: "Fortnite", location: @"D:\Epic Games\Fortnite");

        var marked = UninstallEntryOrigin.MarkEmptyFolders([empty, filled, noFolder, otherLauncher], path => path.EndsWith("skate"));

        Assert.Equal([true, false, false, false], marked.Select(entry => entry.InstallFolderEmpty));
        Assert.Same(filled, marked[1]);
    }

    [Fact]
    public void A_folder_test_that_throws_leaves_the_entry_unmarked_and_the_list_whole()
    {
        var entries = new[]
        {
            Entry(name: "A", key: "Steam App 1", location: @"D:\Steam\a"),
            Entry(name: "B", key: "Steam App 2", location: @"D:\Steam\b"),
        };

        var marked = UninstallEntryOrigin.MarkEmptyFolders(
            entries, path => path.EndsWith('a') ? throw new UnauthorizedAccessException() : true);

        Assert.Equal([false, true], marked.Select(entry => entry.InstallFolderEmpty));
    }

    [Fact]
    public void A_stale_mark_is_cleared_when_the_folder_has_files_again()
    {
        var stale = Entry(key: "Steam App 1", location: @"D:\Steam\a") with { InstallFolderEmpty = true };

        Assert.False(Assert.Single(UninstallEntryOrigin.MarkEmptyFolders([stale], _ => false)).InstallFolderEmpty);
    }

    [Fact]
    public void The_real_folder_test_tells_an_empty_tree_from_one_with_a_file_and_a_missing_folder_from_both()
    {
        var root = Path.Combine(Path.GetTempPath(), "uu-origin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            Assert.True(UninstallEntryOrigin.IsEmptyFolder(root));

            File.WriteAllText(Path.Combine(root, "a", "b", "game.dat"), "x");
            Assert.False(UninstallEntryOrigin.IsEmptyFolder(root));

            Assert.False(UninstallEntryOrigin.IsEmptyFolder(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_empty_steam_folder_shows_the_note_and_the_row_stays_removable()
    {
        LocalizationService.Instance.Apply("en");
        var entry = Entry(name: "skate.", key: "Steam App 1", location: @"D:\Steam\skate") with { InstallFolderEmpty = true };
        var row = new UninstallRowViewModel(Listing("x") with { Entry = entry });

        Assert.True(row.HasNote);
        Assert.Equal("Folder empty, game probably no longer installed", row.NoteText);
        Assert.True(row.Selectable);

        LocalizationService.Instance.Apply("de");
        try
        {
            row.RefreshText();
            Assert.Equal("Ordner leer, Spiel vermutlich nicht mehr installiert", row.NoteText);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public async Task A_row_with_a_launcher_shows_the_label_and_the_short_folder_with_the_whole_path_as_tooltip()
    {
        var (vm, _) = await LoadedAsync(
            Listing("Apex Legends") with
            {
                Entry = Entry(key: "Steam App 1172470", location: @"D:\SteamLibrary\steamapps\common\Apex Legends"),
            });

        var row = Assert.Single(vm.AllRows);

        Assert.Equal("Steam", row.SourceLabel);
        Assert.Equal("\u2026\\common\\Apex Legends", row.OriginText);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Apex Legends", row.FolderPath);
        Assert.True(row.HasOrigin);
    }

    [Fact]
    public async Task A_plain_program_with_a_unique_name_shows_no_origin_line()
    {
        var (vm, _) = await LoadedAsync(Listing("7-Zip") with { Entry = Entry(name: "7-Zip", key: "7-Zip", location: @"C:\Program Files\7-Zip") });

        Assert.False(Assert.Single(vm.AllRows).HasOrigin);
    }

    [Fact]
    public async Task Two_visible_rows_with_one_name_always_show_label_and_folder_even_without_a_label()
    {
        var (vm, _) = await LoadedAsync(
            Listing("Apex Legends") with { Entry = Entry(key: "Steam App 1172470", location: @"D:\Steam\apex") },
            Listing("Apex Legends") with { Entry = Entry(key: "{D7FBF176}", location: @"D:\EA Games\Apex") },
            Listing("Tool A") with { Entry = Entry(name: "Tool A", key: "ToolA1", location: @"C:\Tools\A") },
            Listing("Tool A") with { Entry = Entry(name: "tool a", key: "ToolA2", location: @"C:\Other\A") });

        var rows = vm.AllRows.ToList();

        Assert.Equal(@"D:\Steam\apex", rows[0].OriginText);
        Assert.Equal(@"D:\EA Games\Apex", rows[1].OriginText);
        Assert.Equal(@"C:\Tools\A", rows[2].OriginText);
        Assert.Equal(@"C:\Other\A", rows[3].OriginText);
    }

    [Fact]
    public async Task The_shared_name_rule_follows_the_search_because_it_counts_visible_rows_only()
    {
        var (vm, _) = await LoadedAsync(
            Listing("Apex Legends", publisher: "One") with { Entry = Entry(key: "{A}", location: @"D:\One\Apex") with { Publisher = "One" } },
            Listing("Apex Legends", publisher: "Two") with { Entry = Entry(key: "{B}", location: @"D:\Two\Apex") with { Publisher = "Two" } });
        Assert.All(vm.AllRows, row => Assert.True(row.HasOrigin));

        vm.SearchText = "One";

        Assert.Single(vm.Sections[0].Rows);
        Assert.All(vm.AllRows, row => Assert.False(row.HasOrigin));

        vm.SearchText = string.Empty;

        Assert.All(vm.AllRows, row => Assert.True(row.HasOrigin));
    }
}
