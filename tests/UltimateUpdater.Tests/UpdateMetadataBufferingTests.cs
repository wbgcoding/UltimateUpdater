using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

/// <summary>The metadata size limit only protects anything if the body is not buffered whole before it is
/// applied: HttpClient does exactly that unless the call asks for the headers only.</summary>
public class UpdateMetadataBufferingTests
{
    /// <summary>A body with no declared length that keeps producing zeros and counts what was pulled out of it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesServed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Stops far past anything the limit allows, so a buffering client terminates the test anyway.
            if (BytesServed > 256L * 1024 * 1024)
                return 0;

            Array.Clear(buffer, offset, count);
            BytesServed += count;
            return count;
        }
    }

    private sealed class EndlessHandler(EndlessStream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
                RequestMessage = request,
            });
    }

    [Fact]
    public async Task The_public_channel_stops_reading_an_oversized_metadata_body_at_the_limit()
    {
        var body = new EndlessStream();
        using var client = new HttpClient(new EndlessHandler(body));

        var result = await new PublicUpdateChannel(new LoggingService()).GetStringAsync(
            client, "https://api.github.com/repos/wbgcoding/UltimateUpdater/releases/latest", CancellationToken.None);

        Assert.Null(result);
        Assert.True(body.BytesServed < 16L * 1024 * 1024, $"{body.BytesServed} bytes were pulled before the limit applied");
    }

    /// <summary>Checked in the source so no channel can slip in a call that buffers the body: every SendAsync in the self-update folder must ask for the headers only.</summary>
    [Fact]
    public void Every_self_update_request_reads_the_headers_first()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UltimateUpdater.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        var folder = Path.Combine(root.FullName, "src", "UltimateUpdater", "Services", "SelfUpdate");
        var calls = Directory.EnumerateFiles(folder, "*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\.SendAsync\([^;]*;").Select(match => (file, text: match.Value)))
            .ToList();

        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Contains("HttpCompletionOption.ResponseHeadersRead", call.text));
    }
}
