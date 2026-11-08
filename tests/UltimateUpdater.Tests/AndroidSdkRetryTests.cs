using System.Net;
using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>A manifest answer that was only a transient failure must be asked for again.</summary>
public class AndroidSdkRetryTests
{
    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statuses[Math.Min(Calls, statuses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<manifest/>") });
        }
    }

    private static AndroidSdkSource SourceWith(SequenceHandler handler) =>
        new(new LoggingService(), new HttpClient(handler));

    [Fact]
    public async Task A_server_error_is_asked_again_on_the_next_call()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var source = SourceWith(handler);

        Assert.Null(await source.GetRepositoryManifestAsync(CancellationToken.None));
        Assert.Equal("<manifest/>", await source.GetRepositoryManifestAsync(CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Too_many_requests_is_asked_again_on_the_next_call()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var source = SourceWith(handler);

        Assert.Null(await source.GetRepositoryManifestAsync(CancellationToken.None));
        Assert.NotNull(await source.GetRepositoryManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_definite_not_found_is_final()
    {
        var handler = new SequenceHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);
        var source = SourceWith(handler);

        Assert.Null(await source.GetRepositoryManifestAsync(CancellationToken.None));
        Assert.Null(await source.GetRepositoryManifestAsync(CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_manifest_in_hand_is_not_fetched_twice()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var source = SourceWith(handler);

        await source.GetRepositoryManifestAsync(CancellationToken.None);
        await source.GetRepositoryManifestAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }
}
