using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class SteamAcfParserTests
{
    private const string ManifestUpdatePending = """
        "AppState"
        {
            "appid"        "440"
            "name"         "Team Fortress 2"
            "StateFlags"   "6"
            "buildid"      "12345"
            "TargetBuildID" "12999"
        }
        """;

    private const string ManifestUpToDate = """
        "AppState"
        {
            "appid"        "570"
            "name"         "Dota 2"
            "StateFlags"   "4"
            "buildid"      "500"
        }
        """;

    [Fact]
    public void Update_required_when_stateflags_bit_is_set()
    {
        var app = SteamAcfParser.ParseAppManifest(ManifestUpdatePending);

        Assert.NotNull(app);
        Assert.Equal("440", app!.AppId);
        Assert.Equal("Team Fortress 2", app.Name);
        Assert.Equal("12345", app.BuildId);
        Assert.Equal("12999", app.TargetBuildId);
        Assert.True(app.UpdateRequired);
    }

    [Fact]
    public void BytesToDownload_becomes_the_download_size_when_positive()
    {
        const string manifest = """
            "AppState"
            {
                "appid"             "440"
                "name"              "Team Fortress 2"
                "StateFlags"        "6"
                "buildid"           "12345"
                "TargetBuildID"     "12999"
                "BytesToDownload"   "734003200"
            }
            """;

        Assert.Equal(734003200, SteamAcfParser.ParseAppManifest(manifest)!.DownloadSize);
    }

    [Fact]
    public void BytesToDownload_of_zero_or_absent_is_not_a_fabricated_size()
    {
        Assert.Null(SteamAcfParser.ParseAppManifest(ManifestUpdatePending)!.DownloadSize);

        const string zero = """
            "AppState"
            {
                "appid"             "440"
                "StateFlags"        "6"
                "BytesToDownload"   "0"
            }
            """;
        Assert.Null(SteamAcfParser.ParseAppManifest(zero)!.DownloadSize);
    }

    [Fact]
    public void Install_dir_is_read_when_present()
    {
        const string manifest = """
            "AppState"
            {
                "appid"        "440"
                "name"         "Team Fortress 2"
                "StateFlags"   "6"
                "buildid"      "12345"
                "TargetBuildID" "12999"
                "installdir"   "Team Fortress 2"
            }
            """;

        Assert.Equal("Team Fortress 2", SteamAcfParser.ParseAppManifest(manifest)!.InstallDir);
    }

    [Fact]
    public void Install_dir_is_null_when_absent() =>
        Assert.Null(SteamAcfParser.ParseAppManifest(ManifestUpdatePending)!.InstallDir);

    [Fact]
    public void Fully_installed_app_reports_no_update()
    {
        var app = SteamAcfParser.ParseAppManifest(ManifestUpToDate);

        Assert.NotNull(app);
        Assert.False(app!.UpdateRequired);
    }

    [Fact]
    public void Differing_target_build_also_signals_an_update()
    {
        const string manifest = """
            "AppState"
            {
                "appid"         "620"
                "name"          "Portal 2"
                "StateFlags"    "4"
                "buildid"       "100"
                "TargetBuildID" "200"
            }
            """;

        Assert.True(SteamAcfParser.ParseAppManifest(manifest)!.UpdateRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a manifest at all")]
    [InlineData("\"AppState\"\n{\n\"name\" \"missing appid\"\n}")]
    public void Malformed_manifest_yields_null_instead_of_throwing(string input) =>
        Assert.Null(SteamAcfParser.ParseAppManifest(input));

    [Fact]
    public void Library_folders_are_read_and_unescaped()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                }
                "1"
                {
                    "path"      "D:\\SteamLibrary"
                }
            }
            """;

        var paths = SteamAcfParser.ParseLibraryFolders(vdf);

        Assert.Equal(2, paths.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", paths[0]);
        Assert.Equal(@"D:\SteamLibrary", paths[1]);
    }

    [Fact]
    public void Library_folder_unc_path_is_unescaped_exactly_once()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "\\\\server\\share\\SteamLibrary"
                }
            }
            """;

        var paths = SteamAcfParser.ParseLibraryFolders(vdf);

        Assert.Equal([@"\\server\share\SteamLibrary"], paths);
    }

    [Fact]
    public void Escaped_quotes_in_a_value_are_unescaped()
    {
        const string manifest = """
            "AppState"
            {
                "appid"  "1"
                "name"   "Foo \"Bar\""
            }
            """;

        Assert.Equal("Foo \"Bar\"", SteamAcfParser.ParseAppManifest(manifest)!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    public void Malformed_library_file_yields_no_paths(string input) =>
        Assert.Empty(SteamAcfParser.ParseLibraryFolders(input));

    [Fact]
    public void Target_build_of_zero_is_reported_as_unknown_not_as_a_version()
    {
        // Steam writes 0 when it has no pending target; showing "-> 0" would be a fabricated version
        const string manifest = """
            "AppState"
            {
                "appid"         "255710"
                "name"          "Cities: Skylines"
                "StateFlags"    "6"
                "buildid"       "22724702"
                "TargetBuildID" "0"
            }
            """;

        var app = SteamAcfParser.ParseAppManifest(manifest);

        Assert.NotNull(app);
        Assert.True(app!.UpdateRequired);        // StateFlags still says an update is due
        Assert.Equal("22724702", app.BuildId);
        Assert.Null(app.TargetBuildId);
    }

    /// <summary>
    /// FromUnixTimeSeconds throws outside its range, and SteamSource only catches I/O failures, one
    /// corrupt manifest would have taken the entire Steam source down.
    /// </summary>
    [Fact]
    public void An_absurd_lastupdated_value_is_ignored_instead_of_throwing()
    {
        const string manifest = """
            "AppState"
            {
                "appid"         "440"
                "name"          "Team Fortress 2"
                "StateFlags"    "6"
                "buildid"       "100"
                "lastupdated"   "999999999999999"
            }
            """;

        var app = SteamAcfParser.ParseAppManifest(manifest);

        Assert.NotNull(app);
        Assert.Null(app!.LastUpdated);
    }

    [Fact]
    public void Zero_target_alone_does_not_claim_an_update()
    {
        const string manifest = """
            "AppState"
            {
                "appid"         "440"
                "name"          "Team Fortress 2"
                "StateFlags"    "4"
                "buildid"       "100"
                "TargetBuildID" "0"
            }
            """;

        Assert.False(SteamAcfParser.ParseAppManifest(manifest)!.UpdateRequired);
    }
}
