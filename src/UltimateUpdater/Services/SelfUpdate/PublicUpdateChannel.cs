using System.Net.Http;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// The channel that ships. Dormant until the repository is public: with no credentials the API
/// returns 404 for a private repo, which surfaces as "no update available" rather than an error.
/// </summary>
public sealed class PublicUpdateChannel : IUpdateChannel
{
    private readonly LoggingService _logging;

    public PublicUpdateChannel(LoggingService logging)
    {
        _logging = logging;
    }

    public bool IsAvailable => true;

    public async Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        if (!SelfUpdateService.IsAllowedUrl(url))
        {
            // The URL comes out of the release JSON: logging it would put its query values and any line
            // breaks on disk.
            _logging.Warn("Self-update: refusing a request to a host that is not on the allowlist.");
            return null;
        }

        using var request = CreateRequest(HttpMethod.Get, url, AcceptFor(url));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.IsSuccessStatusCode
            ? await UpdateMetadata.ReadLimitedAsync(response, _logging, cancellationToken)
            : null;
    }

    public async Task<bool> DownloadAsync(HttpClient client, string url, string targetPath, long maxBytes, CancellationToken cancellationToken)
    {
        // The artifact URL comes out of the release JSON and can point anywhere. Checking it only on
        // the metadata call left the download itself free to go to any host, over plain HTTP.
        if (!SelfUpdateService.IsAllowedUrl(url))
        {
            _logging.Warn("Self-update: refusing a download from a host that is not on the allowlist.");
            return false;
        }

        return await UpdateDownload.ExecuteAsync(client, CreateRequest(HttpMethod.Get, url, "application/octet-stream"), targetPath, maxBytes, _logging, cancellationToken);
    }

    /// <summary>An asset fetched through the API URL answers with its JSON metadata unless the binary is
    /// asked for, which would fail the check closed on a perfectly good file.</summary>
    private static string AcceptFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Contains("/releases/assets/", StringComparison.Ordinal)
            ? "application/octet-stream"
            : "application/vnd.github+json";

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string accept)
    {
        var request = new HttpRequestMessage(method, url);
        // GitHub's API rejects requests without a user agent
        request.Headers.UserAgent.ParseAdd("UltimateUpdater");
        request.Headers.Accept.ParseAdd(accept);
        return request;
    }
}
