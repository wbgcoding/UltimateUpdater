using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wu-atomic-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WriteAllText_writes_utf8_without_a_byte_order_mark_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "state.json");

        AtomicFile.WriteAllText(path, "{\"name\":\"Käse\"}");

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("{\"name\":\"Käse\"}", Encoding.UTF8.GetString(bytes));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void WriteAllText_replaces_an_existing_file()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void A_failing_writer_keeps_the_old_file_removes_the_temp_and_rethrows()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "old");

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, stream =>
        {
            stream.WriteByte(1);
            throw new InvalidOperationException("encoder failed");
        }));

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Write_creates_the_file_when_the_target_does_not_exist_yet()
    {
        var path = Path.Combine(_dir, "fresh.bin");

        AtomicFile.Write(path, stream => stream.Write([1, 2, 3]));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    /// <summary>A fixed temp name made two writers fight over one file; each now has its own.</summary>
    [Fact]
    public void Parallel_writers_each_leave_a_complete_file_and_no_temp_files()
    {
        var path = Path.Combine(_dir, "state.json");
        var payloads = Enumerable.Range(0, 16).Select(i => new string((char)('a' + i), 50_000)).ToList();

        Parallel.ForEach(payloads, new ParallelOptions { MaxDegreeOfParallelism = 8 }, payload =>
        {
            for (var round = 0; round < 5; round++)
            {
                try
                {
                    AtomicFile.WriteAllText(path, payload);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Windows can refuse a replace while another thread's move is in flight; the file
                    // must still be whole afterwards, which is what is asserted below.
                }
            }
        });

        Assert.Contains(File.ReadAllText(path), payloads);
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void TryReadAllText_returns_the_text_within_the_cap()
    {
        var path = Path.Combine(_dir, "in.json");
        File.WriteAllText(path, "hello");

        Assert.Equal("hello", AtomicFile.TryReadAllText(path, maxBytes: 5));
    }

    [Fact]
    public void TryReadAllText_returns_null_for_a_file_over_the_cap()
    {
        var path = Path.Combine(_dir, "big.json");
        File.WriteAllText(path, "hello!");

        Assert.Null(AtomicFile.TryReadAllText(path, maxBytes: 5));
    }

    [Fact]
    public void TryReadAllText_returns_null_for_a_missing_file()
    {
        Assert.Null(AtomicFile.TryReadAllText(Path.Combine(_dir, "nope.json"), maxBytes: 100));
    }

    [Fact]
    public void TryReadAllText_drops_a_leading_byte_order_mark()
    {
        var path = Path.Combine(_dir, "bom.json");
        File.WriteAllText(path, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("{}", AtomicFile.TryReadAllText(path, maxBytes: 100));
    }

    [Fact]
    public void The_scan_cache_treats_a_file_over_its_cap_as_no_cache()
    {
        var path = Path.Combine(_dir, "scan.json");
        File.WriteAllText(path, new string(' ', ScanResultCache.MaxFileBytes + 1));

        var loaded = new ScanResultCache(new LoggingService(), path).TryLoad();

        Assert.Null(loaded);
    }

    [Fact]
    public void The_enrichment_cache_ignores_a_file_over_its_cap()
    {
        var path = Path.Combine(_dir, "enrichment.json");
        var entry = "{\"winget:a:1\":{\"fetchedAtUnix\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}}";

        // The same valid entry loads when small, so an empty cache after padding proves the size check.
        File.WriteAllText(path, entry);
        Assert.True(new EnrichmentCache(new LoggingService(), path).TryGet("winget:a:1", out _));

        File.WriteAllText(path, entry + new string(' ', EnrichmentCache.MaxFileBytes));
        Assert.False(new EnrichmentCache(new LoggingService(), path).TryGet("winget:a:1", out _));
    }
}
