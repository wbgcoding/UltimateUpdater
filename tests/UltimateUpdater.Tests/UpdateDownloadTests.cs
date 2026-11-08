using System.IO;
using System.Net;
using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

/// <summary>The streaming artifact download: a dropped connection is a failed download, and a failed one leaves no file.</summary>
public sealed class UpdateDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "uu-download-" + Guid.NewGuid().ToString("N"));

    public UpdateDownloadTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private async Task<bool> RunAsync(int sent, long? advertised, long maxBytes, string target)
    {
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            var content = new StreamContent(new MemoryStream(new byte[sent]));
            content.Headers.ContentLength = advertised;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        return await UpdateDownload.ExecuteAsync(
            client,
            new HttpRequestMessage(HttpMethod.Get, "https://github.com/x/y/releases/download/v1/a.exe"),
            target,
            maxBytes,
            new LoggingService(),
            CancellationToken.None);
    }

    [Fact]
    public async Task A_stream_that_ends_before_the_advertised_length_is_a_failed_download_with_no_file()
    {
        var target = Path.Combine(_folder, "a.bin");

        Assert.False(await RunAsync(sent: 60, advertised: 100, maxBytes: 1000, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_complete_download_keeps_its_file()
    {
        var target = Path.Combine(_folder, "a.bin");

        Assert.True(await RunAsync(sent: 100, advertised: 100, maxBytes: 1000, target));
        Assert.Equal(100, new FileInfo(target).Length);
    }

    [Fact]
    public async Task A_download_without_an_advertised_length_is_accepted()
    {
        var target = Path.Combine(_folder, "a.bin");

        Assert.True(await RunAsync(sent: 40, advertised: null, maxBytes: 1000, target));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task A_download_over_the_limit_mid_stream_leaves_no_file()
    {
        var target = Path.Combine(_folder, "a.bin");

        Assert.False(await RunAsync(sent: 500, advertised: null, maxBytes: 100, target));
        Assert.False(File.Exists(target));
    }
}
