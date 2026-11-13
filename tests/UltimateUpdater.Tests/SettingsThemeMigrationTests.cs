using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>Values an older or hand-edited settings.json can carry in the two string settings the UI compares by name.</summary>
public sealed class SettingsThemeMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uu-settings-theme-").FullName;

    private SettingsService Load(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return new SettingsService(new LoggingService(), path);
    }

    [Theory]
    [InlineData("aipurple")]
    [InlineData("AIPurple")]
    public void The_renamed_violet_theme_id_maps_to_its_new_name(string stored) =>
        Assert.Equal("neon", Load($"{{\"ui\":{{\"theme\":\"{stored}\"}}}}").Current.Ui.Theme);

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("neon")]
    public void Any_other_theme_id_is_left_as_stored(string stored) =>
        Assert.Equal(stored, Load($"{{\"ui\":{{\"theme\":\"{stored}\"}}}}").Current.Ui.Theme);

    [Fact]
    public void A_null_theme_and_a_null_language_fall_back_to_system()
    {
        var settings = Load("{\"ui\":{\"theme\":null,\"language\":null}}").Current;

        Assert.Equal("system", settings.Ui.Theme);
        Assert.Equal("system", settings.Ui.Language);
    }

    [Fact]
    public void The_migrated_theme_is_what_the_next_save_writes()
    {
        var service = Load("{\"ui\":{\"theme\":\"aipurple\"}}");

        service.Save();

        var written = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains("\"neon\"", written);
        Assert.DoesNotContain("aipurple", written, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
