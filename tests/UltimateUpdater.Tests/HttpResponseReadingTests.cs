using System.Net.Http;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Shared by <c>AndroidSdkSource</c> and <c>RegistryEnrichment</c>: both had the identical
/// chunked-stream-cap loop before this consolidation; <see cref="AndroidSdkManifestReadTests"/>
/// already covers the plain forward, this covers the <c>onExceeded</c> callback the other caller
/// needs for its own logging.
/// </summary>
public class HttpResponseReadingTests
{
    private static HttpResponseMessage ChunkedResponse(string body)
    {
        var response = new HttpResponseMessage { Content = new StringContent(body) };
        response.Content.Headers.ContentLength = null;
        return response;
    }

    [Fact]
    public async Task A_body_within_the_cap_is_read_in_full()
    {
        using var response = ChunkedResponse("hello");

        var result = await HttpResponseReading.ReadLimitedAsync(response, maxBytes: 100, CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task A_body_over_the_cap_returns_null_and_invokes_onExceeded_exactly_once()
    {
        using var response = ChunkedResponse(new string('x', 200 * 1024));
        var exceededCount = 0;

        var result = await HttpResponseReading.ReadLimitedAsync(
            response, maxBytes: 64 * 1024, CancellationToken.None, onExceeded: () => exceededCount++);

        Assert.Null(result);
        Assert.Equal(1, exceededCount);
    }

    [Fact]
    public async Task A_body_over_the_cap_with_no_onExceeded_callback_just_returns_null()
    {
        using var response = ChunkedResponse(new string('x', 200 * 1024));

        var result = await HttpResponseReading.ReadLimitedAsync(response, maxBytes: 64 * 1024, CancellationToken.None);

        Assert.Null(result);
    }
}
