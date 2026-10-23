using System.IO;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// An atomic write that was interrupted leaves "name.guid.tmp" beside its target. "Delete my data" and the
/// start-up sweep both have to find those by their exact shape, never by a wildcard that could match a
/// stranger's file.
/// </summary>
public class AppDataCleanerTempSiblingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wu-tempsib-test-").FullName;

    private static string Guid32() => Guid.NewGuid().ToString("N");

    private string Touch(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Delete_my_data_removes_a_leftover_temp_sibling_of_an_owned_file_and_nothing_else()
    {
        var leftover = Touch($"history.jsonl.{Guid32()}.tmp");
        var cacheLeftover = Touch($"enrichment-cache.json.{Guid32()}.tmp");
        var settings = Touch("settings.json", "keep");
        var settingsLeftover = Touch($"settings.json.{Guid32()}.tmp");
        var stranger = Touch("history.jsonl.notaguid.tmp");
        var upper = Touch($"history.jsonl.{Guid32().ToUpperInvariant()}.tmp");
        var other = Touch($"other.json.{Guid32()}.tmp");

        var failed = AppDataCleaner.DeleteOwnedContent(_dir, new LoggingService());

        Assert.False(failed);
        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(cacheLeftover));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(settingsLeftover), "delete my data never touches the settings file or its siblings");
        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(upper));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Delete_my_data_skips_a_directory_that_looks_like_a_temp_sibling()
    {
        var lookalike = Path.Combine(_dir, $"history.jsonl.{Guid32()}.tmp");
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(lookalike, "inner.txt"), "x");

        AppDataCleaner.DeleteOwnedContent(_dir, new LoggingService());

        Assert.True(File.Exists(Path.Combine(lookalike, "inner.txt")));
    }

    [Fact]
    public void The_startup_sweep_removes_only_old_temp_siblings_including_those_of_the_settings_file()
    {
        var oldOwned = Touch($"history.jsonl.{Guid32()}.tmp");
        var oldSettings = Touch($"settings.json.{Guid32()}.tmp");
        var fresh = Touch($"history.jsonl.{Guid32()}.tmp");
        var settings = Touch("settings.json", "keep");
        var history = Touch("history.jsonl", "keep");
        var stranger = Touch($"notes.txt.{Guid32()}.tmp");
        File.SetLastWriteTimeUtc(oldOwned, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(oldSettings, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(stranger, DateTime.UtcNow.AddDays(-2));

        AppDataCleaner.DeleteStaleTempSiblings(_dir, TimeSpan.FromDays(1));

        Assert.False(File.Exists(oldOwned));
        Assert.False(File.Exists(oldSettings));
        Assert.True(File.Exists(fresh), "a temp file younger than the age limit may still be being written");
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(history));
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void The_startup_sweep_does_not_look_into_subdirectories_and_survives_a_missing_directory()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "cache")).FullName;
        var nested = Path.Combine(sub, $"history.jsonl.{Guid32()}.tmp");
        File.WriteAllText(nested, "x");
        File.SetLastWriteTimeUtc(nested, DateTime.UtcNow.AddDays(-2));

        AppDataCleaner.DeleteStaleTempSiblings(_dir, TimeSpan.FromDays(1));
        AppDataCleaner.DeleteStaleTempSiblings(Path.Combine(_dir, "missing"), TimeSpan.FromDays(1));

        Assert.True(File.Exists(nested));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
