using System.IO;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// Services/ScanResultCache.cs: the last finished scan persisted so the table has something to show
/// before the first scan of a session finishes. Every test uses its own temporary file (the explicit-path
/// constructor is a testing seam, mirroring SettingsService's) so none of this touches the shared
/// AppPaths.DataDirectory the rest of the suite writes to.
/// </summary>
public class ScanResultCacheTests
{
    private static string TempCachePath() => Path.Combine(Path.GetTempPath(), $"scan-result-cache-{Guid.NewGuid()}.json");

    private static UpdateItem MakeItem(string id = "pkg", string sourceId = "winget") => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.Software,
        Name = "Test Package",
        InstalledVersion = "1.0.0",
        AvailableVersion = "1.1.0",
        IsCritical = true,
        Tier = SourceTier.A,
        DownloadSize = 12345,
        PublishDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Meta = new Dictionary<string, string> { ["publisher"] = "Test Publisher" },
    };

    [Fact]
    public void A_saved_scan_round_trips_with_the_same_items_and_timestamp()
    {
        var path = TempCachePath();
        try
        {
            var cache = new ScanResultCache(new LoggingService(), path);
            var saved = new[] { MakeItem() };

            cache.Save(saved);
            var loaded = cache.TryLoad();

            Assert.NotNull(loaded);
            Assert.Single(loaded.Value.Items);
            var item = loaded.Value.Items[0];
            Assert.Equal("pkg", item.Id);
            Assert.Equal("winget", item.SourceId);
            Assert.Equal("1.0.0", item.InstalledVersion);
            Assert.Equal("1.1.0", item.AvailableVersion);
            Assert.True(item.IsCritical);
            Assert.Equal(12345, item.DownloadSize);
            Assert.Equal("Test Publisher", item.Meta["publisher"]);
            Assert.True(DateTimeOffset.UtcNow - loaded.Value.SavedAt < TimeSpan.FromMinutes(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_cache_older_than_seven_days_is_discarded_and_the_file_is_removed()
    {
        var path = TempCachePath();
        try
        {
            var tooOld = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeSeconds();
            File.WriteAllText(path,
                "{\"appVersion\":\"" + ScanResultCache.CurrentAppVersion + "\",\"savedAtUnix\":" + tooOld
                + ",\"items\":[{\"id\":\"pkg\",\"sourceId\":\"winget\",\"category\":0,\"name\":\"Test\",\"tier\":0,\"children\":[],\"meta\":{}}]}");

            var cache = new ScanResultCache(new LoggingService(), path);
            var loaded = cache.TryLoad();

            Assert.Null(loaded);
            Assert.False(File.Exists(path), "a stale cache file should be removed rather than re-checked every load");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_cache_from_a_different_app_version_is_discarded()
    {
        var path = TempCachePath();
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.WriteAllText(path,
                "{\"appVersion\":\"0.0.1-not-this-build\",\"savedAtUnix\":" + now
                + ",\"items\":[{\"id\":\"pkg\",\"sourceId\":\"winget\",\"category\":0,\"name\":\"Test\",\"tier\":0,\"children\":[],\"meta\":{}}]}");

            var cache = new ScanResultCache(new LoggingService(), path);
            var loaded = cache.TryLoad();

            Assert.Null(loaded);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_corrupt_cache_file_is_discarded_without_throwing()
    {
        var path = TempCachePath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json at all");

            var cache = new ScanResultCache(new LoggingService(), path);
            var loaded = cache.TryLoad();

            Assert.Null(loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_file_means_no_result_and_no_throw()
    {
        var path = TempCachePath();
        var cache = new ScanResultCache(new LoggingService(), path);

        Assert.Null(cache.TryLoad());
    }

    [Fact]
    public void Saving_never_leaves_a_temporary_file_behind()
    {
        var path = TempCachePath();
        try
        {
            var cache = new ScanResultCache(new LoggingService(), path);
            cache.Save([MakeItem()]);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FreshCacheJson(string itemsJson) =>
        $$"""{"appVersion":"{{ScanResultCache.CurrentAppVersion}}","savedAtUnix":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"items":{{itemsJson}}}""";

    /// <summary>A hand-edited or damaged file can carry a literal null where a collection belongs. That used to
    /// throw a NullReferenceException, outside every catch, straight out of startup.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("""[{"id":"a","sourceId":"winget","name":"A","children":null}]""")]
    [InlineData("""[{"id":"a","sourceId":"winget","name":"A","meta":null}]""")]
    [InlineData("""[{"id":"a","sourceId":"winget","name":"A","children":[{"id":"b","sourceId":"winget","name":"B","children":null}]}]""")]
    [InlineData("""[{"id":null,"sourceId":"winget","name":"A"}]""")]
    public void A_cache_with_a_null_where_a_required_value_belongs_loads_as_nothing_and_keeps_the_file(string itemsJson)
    {
        var path = TempCachePath();
        try
        {
            File.WriteAllText(path, FreshCacheJson(itemsJson));
            var cache = new ScanResultCache(new LoggingService(), path);

            var loaded = cache.TryLoad();

            Assert.Null(loaded);
            Assert.True(File.Exists(path), "a structurally bad cache is left for the next save to replace");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_well_formed_cache_with_empty_children_and_meta_still_loads()
    {
        var path = TempCachePath();
        try
        {
            File.WriteAllText(path, FreshCacheJson("""[{"id":"a","sourceId":"winget","name":"A","children":[],"meta":{}}]"""));
            var cache = new ScanResultCache(new LoggingService(), path);

            var loaded = cache.TryLoad();

            Assert.NotNull(loaded);
            Assert.Single(loaded.Value.Items);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
