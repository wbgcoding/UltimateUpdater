using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>A scan whose lookups were all cache hits changes nothing, so saving must not serialise the whole
/// cache again. The file's presence is the probe: a save that does nothing leaves a deleted file deleted.</summary>
public class EnrichmentCacheSaveTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wu-enrichment-save-").FullName;
    private string Path_ => Path.Combine(_folder, "enrichment-cache.json");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static readonly UpdateEnrichment Sample = new(DateTimeOffset.UtcNow, 1234);

    [Fact]
    public void Saving_after_only_reads_writes_nothing()
    {
        var first = new EnrichmentCache(new LoggingService(), Path_);
        first.Set("winget:a:1", Sample);
        first.Save();
        Assert.True(File.Exists(Path_));

        var second = new EnrichmentCache(new LoggingService(), Path_);
        Assert.True(second.TryGet("winget:a:1", out _));
        File.Delete(Path_);

        second.Save();

        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void Saving_after_a_change_writes_once_and_is_then_clean_again()
    {
        var cache = new EnrichmentCache(new LoggingService(), Path_);
        cache.Set("winget:a:1", Sample);

        cache.Save();
        Assert.True(File.Exists(Path_));

        File.Delete(Path_);
        cache.Save();
        Assert.False(File.Exists(Path_));

        cache.Set("winget:b:2", Sample);
        cache.Save();
        Assert.True(File.Exists(Path_));
    }
}
