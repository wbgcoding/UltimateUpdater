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

        Assert.Null(await source.GetRepositoryIndexAsync(CancellationToken.None));
        Assert.NotNull(await source.GetRepositoryIndexAsync(CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Too_many_requests_is_asked_again_on_the_next_call()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var source = SourceWith(handler);

        Assert.Null(await source.GetRepositoryIndexAsync(CancellationToken.None));
        Assert.NotNull(await source.GetRepositoryIndexAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_definite_not_found_is_final()
    {
        var handler = new SequenceHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);
        var source = SourceWith(handler);

        Assert.Null(await source.GetRepositoryIndexAsync(CancellationToken.None));
        Assert.Null(await source.GetRepositoryIndexAsync(CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_manifest_in_hand_is_not_fetched_twice()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var source = SourceWith(handler);

        var first = await source.GetRepositoryIndexAsync(CancellationToken.None);
        var second = await source.GetRepositoryIndexAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Same(first, second);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A tray session outlives Google releases: the manifest describes only the newest ones, so a
    /// copy older than the maximum age is fetched again.</summary>
    [Fact]
    public async Task A_manifest_older_than_six_hours_is_fetched_again()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero));
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient(handler), clock);

        await source.GetRepositoryIndexAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromHours(5);
        await source.GetRepositoryIndexAsync(CancellationToken.None);
        Assert.Equal(1, handler.Calls);

        clock.Now += TimeSpan.FromHours(2);
        await source.GetRepositoryIndexAsync(CancellationToken.None);
        Assert.Equal(2, handler.Calls);
    }
}
