using System.Net.Http;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The size ceiling applies to the STREAM, not to the Content-Length header, exercised here with
/// Content-Length deliberately cleared to simulate a chunked response.
/// </summary>
public class AndroidSdkManifestReadTests
{
    private static HttpResponseMessage ChunkedResponse(string body)
    {
        var response = new HttpResponseMessage { Content = new StringContent(body) };
        response.Content.Headers.ContentLength = null;
        return response;
    }

    [Fact]
    public async Task An_ordinary_small_body_is_read_normally()
    {
        using var response = ChunkedResponse("<manifest/>");

        var result = await AndroidSdkSource.ReadLimitedAsync(response, CancellationToken.None);

        Assert.Equal("<manifest/>", result);
    }

    [Fact]
    public async Task A_body_over_the_limit_with_no_content_length_header_returns_null_not_buffered_whole()
    {
        using var response = ChunkedResponse(new string('x', 40 * 1024 * 1024));

        var result = await AndroidSdkSource.ReadLimitedAsync(response, CancellationToken.None);

        Assert.Null(result);
    }
}
