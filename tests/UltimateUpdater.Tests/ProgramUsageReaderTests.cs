using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>Last use of a program: the UserAssist decoding (against a value captured from a real PC), the matching to an install folder, and the Steam manifest.</summary>
public class ProgramUsageReaderTests
{
    private const string ProgramFilesId = "6D809377-6AF0-444B-8957-A3773F02200E";

    // A real value from a PC: ROT13 name and the 72 data bytes as read from the registry (a Program Files entry).
    private const string RealName = @"{6Q809377-6NS0-444O-8957-N3773S02200R}\GrnzFcrnx 3 Pyvrag\gf3pyvrag_jva64.rkr";

    private const string RealData =
        "52000000020000000600000050c70200b6c5fb3c7e74973d0b68203d77ea3f3d7844183ddd4d353d6777b53c1c94073dcfc9fb3c6d4cb43c01000000d01380dc807ddd0100000000";

    private static string? Folders(string id) => id == ProgramFilesId ? @"C:\Program Files" : null;

    private static InstalledProgramEntry Program(string key, string location, string name = "App") =>
        new() { Key = key, Kind = UninstallEntryKind.Program, Name = name, InstallLocation = location };

    [Fact]
    public void A_real_user_assist_value_decodes_to_the_program_path_and_the_last_run()
    {
        var record = ProgramUsageReader.DecodeUserAssist(RealName, Convert.FromHexString(RealData), Folders);

        Assert.NotNull(record);
        Assert.Equal(@"C:\Program Files\TeamSpeak 3 Client\ts3client_win64.exe", record.Path);
        Assert.Equal(new DateTime(2026, 11, 26, 8, 33, 49, 965, DateTimeKind.Utc), record.LastUsedUtc);
        Assert.Equal(DateTimeKind.Utc, record.LastUsedUtc.Kind);
    }

    [Fact]
    public void Rot13_is_its_own_inverse_and_leaves_digits_and_punctuation()
    {
        Assert.Equal(@"{6D809377}\TeamSpeak 3.exe", ProgramUsageReader.Rot13(@"{6Q809377}\GrnzFcrnx 3.rkr"));
        Assert.Equal("Hello, World 42", ProgramUsageReader.Rot13(ProgramUsageReader.Rot13("Hello, World 42")));
    }

    [Fact]
    public void A_plain_path_name_and_an_unknown_folder_id_keep_their_text()
    {
        var data = Convert.FromHexString(RealData);

        Assert.Equal(@"D:\Tools\run.exe", ProgramUsageReader.DecodeUserAssist(ProgramUsageReader.Rot13(@"D:\Tools\run.exe"), data, Folders)?.Path);

        var unknown = ProgramUsageReader.DecodeUserAssist(ProgramUsageReader.Rot13(@"{00000000-0000-0000-0000-000000000000}\a.exe"), data, Folders);
        Assert.Equal(@"{00000000-0000-0000-0000-000000000000}\a.exe", unknown?.Path);
    }

    [Fact]
    public void The_session_counter_a_short_value_and_an_empty_time_are_not_program_starts()
    {
        var data = Convert.FromHexString(RealData);
        var empty = (byte[])data.Clone();
        Array.Clear(empty, 60, 8);

        Assert.Null(ProgramUsageReader.DecodeUserAssist("HRZR_PGYFRFFVBA", data, Folders));
        Assert.Null(ProgramUsageReader.DecodeUserAssist(RealName, data[..40], Folders));
        Assert.Null(ProgramUsageReader.DecodeUserAssist(RealName, empty, Folders));
        Assert.Null(ProgramUsageReader.DecodeUserAssist(string.Empty, data, Folders));
    }

    [Fact]
    public void A_time_beyond_the_calendar_is_ignored_instead_of_throwing()
    {
        var data = Convert.FromHexString(RealData);
        BitConverter.GetBytes(long.MaxValue).CopyTo(data, 60);

        Assert.Null(ProgramUsageReader.DecodeUserAssist(RealName, data, Folders));
    }

    [Fact]
    public void A_program_gets_the_newest_start_of_anything_under_its_folder()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            new UserAssistRecord(@"C:\Program Files\Acme\acme.exe", older),
            new UserAssistRecord(@"C:\Program Files\Acme\tools\helper.exe", newer),
            new UserAssistRecord(@"C:\Program Files\Other\other.exe", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
        };

        var used = ProgramUsageReader.Match([Program("acme", @"C:\Program Files\Acme"), Program("other", @"C:\Program Files\Other\")], records);

        Assert.Equal(newer, used["acme"]);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), used["other"]);
    }

    [Fact]
    public void A_folder_that_only_starts_with_the_same_letters_is_not_a_match()
    {
        var records = new[] { new UserAssistRecord(@"C:\Apps\Foobar\run.exe", DateTime.UtcNow) };

        var used = ProgramUsageReader.Match([Program("foo", @"C:\Apps\Foo")], records);

        Assert.Empty(used);
    }

    [Fact]
    public void A_program_without_an_install_folder_or_without_a_record_has_no_usage_data()
    {
        var records = new[] { new UserAssistRecord(@"C:\Apps\Known\run.exe", DateTime.UtcNow) };

        var used = ProgramUsageReader.Match([Program("none", string.Empty), Program("unknown", @"C:\Apps\Unknown")], records);

        Assert.Empty(used);
    }

    [Fact]
    public void A_steam_game_finds_its_manifest_beside_the_common_folder()
    {
        var entry = Program(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1172470", @"D:\SteamLibrary\steamapps\common\Apex Legends");

        Assert.Equal(@"D:\SteamLibrary\steamapps\appmanifest_1172470.acf", ProgramUsageReader.SteamManifestPath(entry));
    }

    [Fact]
    public void An_entry_that_is_no_steam_game_or_sits_outside_a_library_has_no_manifest()
    {
        Assert.Null(ProgramUsageReader.SteamManifestPath(Program("7-Zip", @"C:\Program Files\7-Zip")));
        Assert.Null(ProgramUsageReader.SteamManifestPath(
            Program(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 5", @"D:\Games\Apex")));
    }

    [Fact]
    public void The_last_played_stamp_of_a_manifest_is_read_as_a_utc_time()
    {
        const string manifest = "\"AppState\"\n{\n\t\"appid\"\t\t\"1172470\"\n\t\"LastUpdated\"\t\t\"1700000000\"\n\t\"LastPlayed\"\t\t\"1750000000\"\n}\n";

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000).UtcDateTime, ProgramUsageReader.ParseSteamLastPlayed(manifest));
    }

    [Theory]
    [InlineData("\"LastPlayed\"\t\t\"0\"")]
    [InlineData("\"AppState\" { \"appid\" \"1\" }")]
    [InlineData("\"LastPlayed\" \"99999999999999999999\"")]
    [InlineData("")]
    public void A_manifest_never_played_or_without_the_stamp_has_no_usage_data(string manifest) =>
        Assert.Null(ProgramUsageReader.ParseSteamLastPlayed(manifest));
}

/// <summary>The reading side: the registry and the manifest files of this PC.</summary>
[Trait("Layer", "Integration")]
public class ProgramUsageReaderReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uu-usage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Reading_without_any_program_does_not_throw_and_finds_nothing()
    {
        Assert.Empty(ProgramUsageReader.Read([]));
    }

    [Fact]
    public void A_steam_game_with_a_played_manifest_gets_its_last_play_and_a_never_played_one_gets_none()
    {
        var steamapps = Path.Combine(_root, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "Played"));
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "Fresh"));
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_11.acf"), "\"AppState\"\n{\n\t\"LastPlayed\"\t\t\"1750000000\"\n}\n");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_12.acf"), "\"AppState\"\n{\n\t\"LastPlayed\"\t\t\"0\"\n}\n");
        InstalledProgramEntry Game(string appId, string folder) => new()
        {
            Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + appId,
            Kind = UninstallEntryKind.Program,
            Name = folder,
            InstallLocation = Path.Combine(steamapps, "common", folder),
        };

        var used = ProgramUsageReader.Read([Game("11", "Played"), Game("12", "Fresh"), Game("13", "Missing")]);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000).UtcDateTime, used[Game("11", "Played").Key]);
        Assert.False(used.ContainsKey(Game("12", "Fresh").Key));
        Assert.False(used.ContainsKey(Game("13", "Missing").Key));
    }
}
