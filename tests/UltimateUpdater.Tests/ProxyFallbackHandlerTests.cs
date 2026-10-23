using System.Net;
using System.Net.Http;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Services/Sources/SourceRegistry.cs's ProxyFallbackHandler: the retry decision only, driven against
/// stub inner handlers rather than a real proxy (there is no fake corporate network to test against
/// here). A 407 or an outright connection failure to the first handler retries exactly once through the
/// second; anything else is final, whichever handler it came from.
/// </summary>
public class ProxyFallbackHandlerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw exception;
        }
    }

    /// <summary>Stands in for whatever <see cref="System.Net.Http.HttpClient.DefaultProxy"/> reports on
    /// the real machine, which this test run must not depend on. <paramref name="hasRealProxy"/> true
    /// mirrors a genuine corporate proxy (GetProxy names a different address, not bypassed);
    /// false mirrors what Windows reports with no proxy configured at all (GetProxy echoes the
    /// destination back, IsBypassed true), see SourceRegistry.ProxyFallbackHandler.HasRealProxyFor.</summary>
    private sealed class FakeSystemProxy(bool hasRealProxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) =>
            hasRealProxy ? new Uri("http://proxy.example.invalid:8080") : destination;

        public bool IsBypassed(Uri host) => !hasRealProxy;
    }

    /// <summary>Swaps the process-wide <see cref="System.Net.Http.HttpClient.DefaultProxy"/> for the
    /// duration of one test and restores it afterward, the property HasRealProxyFor reads is static, so
    /// a test cannot pass it in any other way.</summary>
    private static async Task WithFakeProxy(bool hasRealProxy, Func<Task> body)
    {
        var original = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new FakeSystemProxy(hasRealProxy);
        try
        {
            await body();
        }
        finally
        {
            HttpClient.DefaultProxy = original;
        }
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://example.invalid/package");

    [Fact]
    public async Task A_407_response_retries_once_through_the_direct_handler() => await WithFakeProxy(hasRealProxy: true, async () =>
    {
        var proxy = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired));
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct));

        var response = await invoker.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(1, direct.CallCount);
    });

    [Fact]
    public async Task A_connection_failure_reaching_the_proxy_retries_once_through_the_direct_handler() => await WithFakeProxy(hasRealProxy: true, async () =>
    {
        var proxy = new ThrowingHandler(new HttpRequestException("could not connect to proxy"));
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct));

        var response = await invoker.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(1, direct.CallCount);
    });

    [Fact]
    public async Task An_ordinary_404_is_final_and_never_retries() => await WithFakeProxy(hasRealProxy: true, async () =>
    {
        var proxy = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct));

        var response = await invoker.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(0, direct.CallCount);
    });

    [Fact]
    public async Task A_second_failure_on_the_direct_retry_is_final_and_names_the_proxy() => await WithFakeProxy(hasRealProxy: true, async () =>
    {
        var proxy = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired));
        var direct = new ThrowingHandler(new HttpRequestException("connection refused"));
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct));

        var exception = await Assert.ThrowsAsync<ProxyConnectionFailedException>(
            () => invoker.SendAsync(Request(), CancellationToken.None));

        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(1, direct.CallCount);
        Assert.NotNull(exception.ProxyAddress);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    });

    /// <summary>With no proxy actually configured, a connection failure on the first
    /// (and only) attempt must propagate unchanged - no direct retry, and never wrapped as
    /// <see cref="ProxyConnectionFailedException"/>, which would wrongly blame a proxy that does not
    /// exist.</summary>
    [Fact]
    public async Task A_connection_failure_with_no_proxy_configured_never_retries_and_is_not_blamed_on_a_proxy() => await WithFakeProxy(hasRealProxy: false, async () =>
    {
        var original = new HttpRequestException("connection refused");
        var proxy = new ThrowingHandler(original);
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(Request(), CancellationToken.None));

        Assert.Same(original, thrown);
        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(0, direct.CallCount);
    });

    /// <summary>The 407 case is theoretical with no proxy configured (only a real proxy sends 407), but
    /// the same gate covers it too: no retry, the 407 itself is returned to the caller.</summary>
    [Fact]
    public async Task A_407_with_no_proxy_configured_is_returned_as_is_and_never_retries() => await WithFakeProxy(hasRealProxy: false, async () =>
    {
        var proxy = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired));
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct));

        var response = await invoker.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(0, direct.CallCount);
    });

    /// <summary>Never answers: waits until the token it was handed is cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_proxy_that_never_answers_falls_back_to_the_direct_handler() => await WithFakeProxy(hasRealProxy: true, async () =>
    {
        var proxy = new HangingHandler();
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("direct") });
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct, TimeSpan.FromMilliseconds(100)));

        var response = await invoker.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("direct", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(1, direct.CallCount);
    });

    [Fact]
    public async Task The_callers_own_cancellation_during_the_proxy_attempt_is_not_retried() => await WithFakeProxy(hasRealProxy: true, async () =>
    {
        var proxy = new HangingHandler();
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct, TimeSpan.FromSeconds(30)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(Request(), cancellation.Token));

        Assert.Equal(1, proxy.CallCount);
        Assert.Equal(0, direct.CallCount);
    });

    [Fact]
    public async Task Without_a_real_proxy_a_hanging_handler_gets_no_internal_timeout_and_no_direct_call() => await WithFakeProxy(hasRealProxy: false, async () =>
    {
        var proxy = new HangingHandler();
        var direct = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(new SourceRegistry.ProxyFallbackHandler(proxy, direct, TimeSpan.FromMilliseconds(50)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(Request(), cancellation.Token));

        Assert.Equal(0, direct.CallCount);
    });
}
