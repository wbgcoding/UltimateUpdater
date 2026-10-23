using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>The cache file can be large, so its read must happen on a pool thread, never in the constructor
/// of a view model that is being built on the UI thread.</summary>
public class EnrichmentCacheLoadTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wu-enrichment-load-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private string WriteSampleFile()
    {
        var path = Path.Combine(_folder, "enrichment-cache.json");
        var writer = new EnrichmentCache(new LoggingService(), path);
        writer.Set("winget:a:1", new UpdateEnrichment(DateTimeOffset.UtcNow, 7));
        writer.Save();
        return path;
    }

    [Fact]
    public async Task Constructing_reads_nothing_and_the_load_runs_on_another_thread()
    {
        var path = WriteSampleFile();
        var callerThread = Environment.CurrentManagedThreadId;
        var readThreads = new List<int>();
        var cache = new EnrichmentCache(
            new LoggingService(), path,
            beforeRead: () => { lock (readThreads) readThreads.Add(Environment.CurrentManagedThreadId); });

        lock (readThreads)
            Assert.Empty(readThreads);

        await cache.LoadAsync();

        lock (readThreads)
        {
            var read = Assert.Single(readThreads);
            Assert.NotEqual(callerThread, read);
        }

        Assert.True(cache.TryGet("winget:a:1", out _));
    }

    [Fact]
    public void A_lookup_before_the_load_was_asked_for_still_sees_the_file()
    {
        var path = WriteSampleFile();

        var cache = new EnrichmentCache(new LoggingService(), path);

        Assert.True(cache.TryGet("winget:a:1", out _));
    }

    /// <summary>A save that ran before the read finished would replace the file with a nearly empty one.</summary>
    [Fact]
    public void A_set_and_save_right_after_construction_keep_the_entries_already_on_disk()
    {
        var path = WriteSampleFile();

        var cache = new EnrichmentCache(new LoggingService(), path);
        cache.Set("winget:b:2", new UpdateEnrichment(DateTimeOffset.UtcNow, 9));
        cache.Save();

        var reread = new EnrichmentCache(new LoggingService(), path);
        Assert.True(reread.TryGet("winget:a:1", out _));
        Assert.True(reread.TryGet("winget:b:2", out _));
    }
}
