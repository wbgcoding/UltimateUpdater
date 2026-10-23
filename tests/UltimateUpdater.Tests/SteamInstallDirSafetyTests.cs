using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// SteamSource resolves a game's "open folder" location as steamapps\common\&lt;installdir&gt;, where
/// installdir comes straight out of the manifest text Steam itself wrote, but a manifest is still
/// untrusted input for the purposes of this check, so an escaping value must never reach Path.Combine.
/// </summary>
public class SteamInstallDirSafetyTests
{
    private static string WriteLibrary(string installDir)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wu-steam-" + Guid.NewGuid())).FullName;
        var steamApps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;

        var manifest = "\"AppState\"\n{\n"
            + "    \"appid\"        \"440\"\n"
            + "    \"name\"         \"Team Fortress 2\"\n"
            + "    \"StateFlags\"   \"6\"\n"
            + "    \"buildid\"      \"12345\"\n"
            + "    \"TargetBuildID\" \"12999\"\n"
            + $"    \"installdir\"   \"{installDir}\"\n"
            + "}\n";
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_440.acf"), manifest);
        return root;
    }

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:\Windows")]
    [InlineData("../../etc")]
    public void An_escaping_installdir_produces_no_install_path(string maliciousInstallDir)
    {
        var root = WriteLibrary(maliciousInstallDir);
        try
        {
            var source = new SteamSource(new LoggingService());
            var apps = source.ReadPendingUpdates(root, CancellationToken.None);

            var app = Assert.Single(apps);
            Assert.Null(app.InstallPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_ordinary_installdir_still_resolves_inside_the_library()
    {
        var root = WriteLibrary("Team Fortress 2");
        try
        {
            var source = new SteamSource(new LoggingService());
            var apps = source.ReadPendingUpdates(root, CancellationToken.None);

            var app = Assert.Single(apps);
            Assert.Equal(
                Path.Combine(root, "steamapps", "common", "Team Fortress 2"),
                app.InstallPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
