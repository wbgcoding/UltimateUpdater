using System.Net;
using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public class PublicUpdateChannelTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]"),
                RequestMessage = request,
            });
        }
    }

    [Fact]
    public async Task A_refused_url_is_not_written_to_the_log_and_sends_nothing()
    {
        var lines = new List<string>();
        var handler = new RecordingHandler();
        var channel = new PublicUpdateChannel(new LoggingService(lines.Add));

        var body = await channel.GetStringAsync(
            new HttpClient(handler), "https://evil.example/x?sig=SECRET", CancellationToken.None);

        Assert.Null(body);
        Assert.Empty(handler.Requests);
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, line => line.Contains("SECRET") || line.Contains("evil.example"));
    }

    [Fact]
    public async Task A_download_asks_for_the_binary()
    {
        var handler = new RecordingHandler();
        var channel = new PublicUpdateChannel(new LoggingService(_ => { }));
        var target = Path.Combine(Path.GetTempPath(), $"uu-channel-{Guid.NewGuid():N}.bin");

        try
        {
            await channel.DownloadAsync(
                new HttpClient(handler), "https://github.com/o/r/releases/download/v1/a.exe", target, 1024, CancellationToken.None);
        }
        finally
        {
            File.Delete(target);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal("application/octet-stream", request.Headers.Accept.ToString());
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/releases", "application/vnd.github+json")]
    [InlineData("https://api.github.com/repos/o/r/releases/assets/1", "application/octet-stream")]
    public async Task The_metadata_call_picks_its_accept_header_by_target(string url, string expectedAccept)
    {
        var handler = new RecordingHandler();
        var channel = new PublicUpdateChannel(new LoggingService(_ => { }));

        await channel.GetStringAsync(new HttpClient(handler), url, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectedAccept, request.Headers.Accept.ToString());
    }
}
