using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Security;

namespace UltimateUpdater.Tests;

/// <summary>
/// Proves the documented promise (SECURITY.md, docs/DESIGN.md § outbound traffic) that only the HOST of
/// an outbound request is ever written to the log, never the full URL, which could carry a package
/// name, a query string, or (for the dev update channel) a token. A prior review only read the source
/// and asserted this by inspection; this drives a real request through the real handler and reads back
/// what actually landed in the log.
/// </summary>
public class TrafficLoggingHandlerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") });
    }

    [Fact]
    public async Task Only_the_host_is_logged_never_the_path_query_or_a_token()
    {
        var lines = new List<string>();
        var logging = new LoggingService(lines.Add);
        var handler = new TrafficLoggingHandler(new StubHandler()) { Logging = logging };
        using var client = new HttpClient(handler);

        // A query string is exactly where a lookup's real subject (and, for an authenticated request, a token)
        // would leak if the handler ever logged more than the bare host.
        await client.GetAsync("https://example.com/packages/super-secret-app-name?token=abc123secret");

        var logged = Assert.Single(lines);
        Assert.Contains("example.com", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-app-name", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("token", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123secret", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("/packages", logged, StringComparison.Ordinal);
    }

    /// <summary>A handler with no <see cref="TrafficLoggingHandler.Logging"/> attached yet (the real
    /// startup ordering: the handler is built at static-field-init time, before a LoggingService exists)
    /// must not throw, the request still has to go through.</summary>
    [Fact]
    public async Task A_request_before_logging_is_attached_still_succeeds()
    {
        var handler = new TrafficLoggingHandler(new StubHandler());
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.com/anything");

        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>Issues a real 302 with a <c>Location</c> header, one entry from <see cref="Hops"/> per
    /// call, never resolves the redirect itself. Every request it actually RECEIVES is recorded, so a
    /// test can prove a rejected hop was never sent, not just that the final answer was rejected (the
    /// bug the real fix closed: the old code checked the FINAL url only after the inner handler had
    /// already auto-followed the redirect and sent the clear-text request for real).</summary>
    private sealed class RedirectingHandler : HttpMessageHandler
    {
        public required IReadOnlyList<(string Target, string? RedirectTo)> Hops { get; init; }
        public List<string> ReceivedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            ReceivedUrls.Add(url);
            var hop = Hops.First(h => h.Target == url);

            if (hop.RedirectTo is null)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") });

            var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(hop.RedirectTo);
            return Task.FromResult(redirect);
        }
    }

    [Fact]
    public async Task A_redirect_that_would_leave_HTTPS_is_refused_before_the_downgraded_hop_is_ever_sent()
    {
        var stub = new RedirectingHandler
        {
            Hops = [("https://registry.example/package", "http://attacker.example/payload")],
        };
        var handler = new TrafficLoggingHandler(stub);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://registry.example/package"));

        // The proof: only the ORIGINAL https request was ever sent, the downgraded http hop the
        // redirect pointed at was rejected before TrafficLoggingHandler followed it, never after.
        Assert.Equal(["https://registry.example/package"], stub.ReceivedUrls);
    }

    /// <summary>A request that was deliberately HTTP from the start (the local Ollama API on loopback)
    /// must stay untouched, only a DOWNGRADE away from HTTPS is refused.</summary>
    [Fact]
    public async Task A_request_that_started_HTTP_is_left_alone()
    {
        var stub = new RedirectingHandler { Hops = [("http://127.0.0.1:11434/api/tags", null)] };
        var handler = new TrafficLoggingHandler(stub);
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("http://127.0.0.1:11434/api/tags");

        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>A legitimate same-scheme redirect chain (HTTPS staying HTTPS) must still work, disabling
    /// the inner handler's automatic redirect-following must not break ordinary redirects, only reject
    /// ones that downgrade scheme.</summary>
    [Fact]
    public async Task A_same_scheme_redirect_chain_still_succeeds()
    {
        var stub = new RedirectingHandler
        {
            Hops =
            [
                ("https://registry.example/latest", "https://cdn.example/latest-v2"),
                ("https://cdn.example/latest-v2", null),
            ],
        };
        var handler = new TrafficLoggingHandler(stub);
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://registry.example/latest");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(["https://registry.example/latest", "https://cdn.example/latest-v2"], stub.ReceivedUrls);
    }
}
