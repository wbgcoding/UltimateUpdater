using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// The streaming packument read: it must find the few fields the enrichment uses wherever they sit,
/// fail closed on a bad body like a full parse would, keep every size limit, and never hold the document.
/// </summary>
public class NpmPackumentReaderTests
{
    private const long Cap = 64L * 1024 * 1024;

    /// <summary>A stream that serves lazily produced parts in reads of at most <c>maxRead</c> bytes and
    /// records how much was asked for and delivered at once.</summary>
    private sealed class PartsStream(IEnumerable<byte[]> parts, int maxRead = 4096) : Stream
    {
        private readonly IEnumerator<byte[]> _parts = parts.GetEnumerator();
        private byte[] _current = [];
        private int _offset;

        public long TotalRead { get; private set; }
        public int LargestRequest { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<int>(Fill(buffer.Span));
        }

        private int Fill(Span<byte> destination)
        {
            LargestRequest = Math.Max(LargestRequest, destination.Length);
            while (_offset >= _current.Length)
            {
                if (!_parts.MoveNext())
                    return 0;
                _current = _parts.Current;
                _offset = 0;
            }

            var count = Math.Min(Math.Min(destination.Length, maxRead), _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(destination);
            _offset += count;
            TotalRead += count;
            return count;
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static PartsStream StreamOf(string json, int maxRead = 4096) => new([Utf8(json)], maxRead);

    private static Task<NpmPackumentFacts?> ReadAsync(
        Stream stream, string version = "2.0.0", long cap = Cap, Action? onExceeded = null, CancellationToken token = default) =>
        NpmPackumentReader.ReadAsync(stream, version, cap, token, onExceeded);

    [Fact]
    public async Task Fields_are_found_in_any_order_after_large_irrelevant_sections()
    {
        var filler = new string('x', 300_000);
        var json = $$"""
            {
              "readme": "{{filler}}",
              "versions": {
                "1.0.0": { "description": "{{filler}}", "dist": { "unpackedSize": 1, "tarball": "https://r.example/1.tgz" } },
                "2.0.0": { "scripts": { "a": "b" }, "dist": { "shasum": "abc", "tarball": "https://r.example/2.tgz", "unpackedSize": 4096 } },
                "3.0.0": { "dist": { "unpackedSize": 3 } }
              },
              "maintainers": [ { "name": "a", "email": "b" }, [ 1, [ 2 ] ], null ],
              "repository": { "type": "git", "url": "git+https://github.com/x/y.git" },
              "time": { "created": "2020-01-01T00:00:00Z", "1.0.0": "2021-01-01T00:00:00Z", "2.0.0": "2024-04-10T13:40:00.000Z" },
              "homepage": "https://example.org/home"
            }
            """;

        var facts = await ReadAsync(StreamOf(json, 5000));

        Assert.NotNull(facts);
        Assert.Equal("2024-04-10T13:40:00.000Z", facts!.PublishedText);
        Assert.Equal(4096, facts.UnpackedSize);
        Assert.Equal("https://r.example/2.tgz", facts.Tarball);
        Assert.Equal("https://example.org/home", facts.Homepage);
        Assert.Equal("git+https://github.com/x/y.git", facts.RepositoryUrl);
        Assert.True(facts.PeakBufferBytes < 1024 * 1024);
    }

    [Fact]
    public async Task Only_fields_at_the_right_depth_count()
    {
        // decoys: the same names nested where they mean something else, and a version key spelled with escapes
        const string json = """
            {
              "versions": {
                "1.0.0": {
                  "homepage": "https://decoy.example/home",
                  "repository": { "url": "https://decoy.example/repo" },
                  "time": { "2.0.0": "1999-01-01T00:00:00Z" },
                  "other": { "dist": { "unpackedSize": 7, "tarball": "https://decoy.example/t.tgz" } }
                },
                "2.0.0": { "dist": { "nested": { "unpackedSize": 9 }, "unpackedSize": 11 } }
              },
              "dist-tags": { "latest": "2.0.0", "time": "no" }
            }
            """;

        var facts = await ReadAsync(StreamOf(json));

        Assert.NotNull(facts);
        Assert.Null(facts!.PublishedText);
        Assert.Equal(11, facts.UnpackedSize);
        Assert.Null(facts.Tarball);
        Assert.Null(facts.Homepage);
        Assert.Null(facts.RepositoryUrl);
    }

    [Fact]
    public async Task A_field_of_the_wrong_type_counts_as_absent()
    {
        const string json = """
            {
              "time": { "2.0.0": 12345 },
              "homepage": 42,
              "repository": { "url": { "nested": "object" } },
              "versions": { "2.0.0": { "dist": { "unpackedSize": "big", "tarball": null } } }
            }
            """;

        var facts = await ReadAsync(StreamOf(json));

        Assert.NotNull(facts);
        Assert.Null(facts!.PublishedText);
        Assert.Null(facts.UnpackedSize);
        Assert.Null(facts.Tarball);
        Assert.Null(facts.Homepage);
        Assert.Null(facts.RepositoryUrl);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public async Task A_non_positive_or_fractional_size_is_ignored(string size)
    {
        var json = "{\"versions\":{\"2.0.0\":{\"dist\":{\"unpackedSize\":" + size + "}}}}";

        var facts = await ReadAsync(StreamOf(json));

        Assert.NotNull(facts);
        Assert.Null(facts!.UnpackedSize);
    }

    [Fact]
    public async Task A_missing_version_still_reports_the_project_links()
    {
        const string json = """
            { "time": { "1.0.0": "2021-01-01T00:00:00Z" }, "homepage": "https://example.org",
              "versions": { "1.0.0": { "dist": { "unpackedSize": 5 } } } }
            """;

        var facts = await ReadAsync(StreamOf(json));

        Assert.NotNull(facts);
        Assert.Null(facts!.PublishedText);
        Assert.Null(facts.UnpackedSize);
        Assert.Null(facts.Tarball);
        Assert.Equal("https://example.org", facts.Homepage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("{ \"time\": { \"2.0.0\": \"2024-01-01T00:00:00Z\" }")]
    [InlineData("{ \"time\": { \"2.0.0\": \"2024-01-01T00:00:00Z\" } } trailing")]
    [InlineData("{ \"time\": { \"2.0.0\": \"2024-01-01T00:00:00Z\" } } { }")]
    [InlineData("{ \"homepage\": \"https://x.example\", \"versions\": { \"a\": [ 1, 2 }")]
    [InlineData("{ \"time\": { \"2.0.0\": \"2024-01-01T00:00:00Z\", } }")]
    [InlineData("{ \"time\": { \"2.0.0\": \"unterminated")]
    public async Task Malformed_or_truncated_json_fails_closed(string json)
    {
        Assert.Null(await ReadAsync(StreamOf(json, 7)));
    }

    [Fact]
    public async Task A_valid_prefix_followed_by_garbage_after_the_wanted_fields_still_fails()
    {
        // the wanted fields come first; a full parse would still reject the document
        var json = "{\"time\":{\"2.0.0\":\"2024-01-01T00:00:00Z\"},\"readme\":\"" + new string('x', 200_000) + "\",\"broken\":}";

        Assert.Null(await ReadAsync(StreamOf(json, 8192)));
    }

    [Fact]
    public async Task A_payload_over_the_cap_is_refused_and_reported()
    {
        var exceeded = 0;
        var json = "{\"readme\":\"" + new string('x', 200_000) + "\"}";

        var facts = await ReadAsync(StreamOf(json), cap: 100_000, onExceeded: () => exceeded++);

        Assert.Null(facts);
        Assert.Equal(1, exceeded);
    }

    [Fact]
    public async Task A_payload_exactly_at_the_cap_is_accepted()
    {
        var json = "{\"homepage\":\"https://x.example\"}";

        var facts = await ReadAsync(StreamOf(json), cap: Utf8(json).Length);

        Assert.Equal("https://x.example", facts?.Homepage);
    }

    [Fact]
    public async Task A_single_token_beyond_the_ceiling_is_refused()
    {
        var chunk = Utf8(new string('x', 1024 * 1024));
        IEnumerable<byte[]> Parts()
        {
            yield return Utf8("{\"readme\":\"");
            for (var i = 0; i < NpmPackumentReader.MaxTokenBytes / (1024 * 1024) + 2; i++)
                yield return chunk;
            yield return Utf8("\"}");
        }

        Assert.Null(await ReadAsync(new PartsStream(Parts(), 64 * 1024)));
    }

    [Fact]
    public async Task A_cancelled_read_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAsync(StreamOf("{}"), token: cancellation.Token));
    }

    [Fact]
    public async Task A_twenty_megabyte_packument_streams_without_being_held()
    {
        const int versionCount = 20_000;
        var filler = new string('f', 1000);

        IEnumerable<byte[]> Parts()
        {
            yield return Utf8("{\"name\":\"big\",\"readme\":\"" + new string('r', 200_000) + "\",\"versions\":{");
            for (var i = 0; i < versionCount; i++)
            {
                var version = $"0.0.{i}";
                var target = i == versionCount / 2 ? "\"unpackedSize\":123456," : string.Empty;
                yield return Utf8(
                    $"\"{version}\":{{\"name\":\"big\",\"description\":\"{filler}\",\"dist\":{{{target}\"tarball\":\"https://r.example/{version}.tgz\"}}}},");
            }

            yield return Utf8("\"9.9.9\":{\"dist\":{\"unpackedSize\":5}}},\"time\":{");
            for (var i = 0; i < versionCount; i++)
                yield return Utf8($"\"0.0.{i}\":\"2020-01-01T00:00:00.000Z\",");
            yield return Utf8("\"9.9.9\":\"2025-05-05T05:05:05.000Z\"},\"homepage\":\"https://example.org/big\"}");
        }

        var stream = new PartsStream(Parts(), maxRead: 16 * 1024);
        var middle = $"0.0.{versionCount / 2}";

        var facts = await ReadAsync(stream, middle);

        Assert.NotNull(facts);
        Assert.True(stream.TotalRead > 20L * 1024 * 1024, $"payload was only {stream.TotalRead} bytes");
        Assert.Equal(123456, facts!.UnpackedSize);
        Assert.Equal($"https://r.example/{middle}.tgz", facts.Tarball);
        Assert.Equal("2020-01-01T00:00:00.000Z", facts.PublishedText);
        Assert.Equal("https://example.org/big", facts.Homepage);

        // the whole payload was read, but never held: one buffer, only grown for the 200 KB readme token
        Assert.True(facts.PeakBufferBytes <= 1024 * 1024, $"peak buffer was {facts.PeakBufferBytes} bytes");
        Assert.True(stream.LargestRequest <= 1024 * 1024, $"one read asked for {stream.LargestRequest} bytes");
    }

    [Fact]
    public async Task The_last_version_in_a_huge_document_is_found()
    {
        IEnumerable<byte[]> Parts()
        {
            yield return Utf8("{\"versions\":{");
            for (var i = 0; i < 5_000; i++)
                yield return Utf8($"\"1.0.{i}\":{{\"dist\":{{\"unpackedSize\":{i + 1}}}}},");
            yield return Utf8("\"2.0.0\":{\"dist\":{\"tarball\":\"https://r.example/last.tgz\"}}}}");
        }

        var facts = await ReadAsync(new PartsStream(Parts(), 100));

        Assert.Equal("https://r.example/last.tgz", facts?.Tarball);
        Assert.Null(facts?.UnpackedSize);
    }

    [Fact]
    public async Task The_enrichment_reads_a_chunked_packument_end_to_end()
    {
        const string json = """
            {
              "homepage": "https://example.org/pkg",
              "time": { "2.0.0": "2024-04-10T13:40:00.000Z" },
              "versions": { "2.0.0": { "dist": { "unpackedSize": 2048 } } }
            }
            """;
        using var client = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(StreamOf(json, 11)),
        }));

        var enrichment = await NpmEnrichment.FetchAsync(
            client, new LoggingService(), "npm", "pkg", "2.0.0", "pkg", CancellationToken.None);

        Assert.NotNull(enrichment);
        Assert.Equal(2048, enrichment!.Value.DownloadSize);
        Assert.Equal("https://example.org/pkg", enrichment.Value.ProjectUrl);
        Assert.Equal(4, enrichment.Value.PublishDate!.Value.Month);
    }

    [Fact]
    public async Task The_enrichment_gives_nothing_for_a_malformed_or_oversized_body()
    {
        using var broken = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"time\": "),
        }));
        Assert.Null(await NpmEnrichment.FetchAsync(
            broken, new LoggingService(), "npm", "pkg", "2.0.0", "pkg", CancellationToken.None));

        using var notFound = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await NpmEnrichment.FetchAsync(
            notFound, new LoggingService(), "npm", "pkg", "2.0.0", "pkg", CancellationToken.None));
    }

    [Fact]
    public void The_string_parser_and_the_stream_reader_agree()
    {
        const string json = """
            {
              "homepage": "javascript:alert(1)",
              "repository": { "url": "git+https://github.com/x/y.git" },
              "time": { "2.0.0": "2024-04-10T13:40:00.000Z" },
              "versions": { "2.0.0": { "dist": { "unpackedSize": 10, "tarball": "https://r.example/2.tgz" } } }
            }
            """;

        var (enrichment, tarball) = PackageMetadataParsers.ParseNpmPackumentAndTarball(json, "2.0.0");

        Assert.Equal("https://github.com/x/y", enrichment.ProjectUrl);
        Assert.Equal(10, enrichment.DownloadSize);
        Assert.Equal("https://r.example/2.tgz", tarball);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
