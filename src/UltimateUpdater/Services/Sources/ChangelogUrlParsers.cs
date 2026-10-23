namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Pure recognition of "this link is a GitHub/GitLab release PAGE"; deliberately narrower than
/// <see cref="GitHubReleaseParsers.TryExtractOwnerRepo"/>, which accepts a bare repository root. A bare
/// root is a project link, not a release; only the two specific release-page shapes below are eligible
/// for the click-triggered changelog re-fetch. Only https, an exact host match and no subdomains; this is the
/// gate before any network call, so it fails closed on anything it does not recognise.
/// </summary>
public static class ChangelogUrlParsers
{
    public readonly record struct GitHubReleasePage(string Owner, string Repo, string? Tag);

    public readonly record struct GitLabReleasePage(string OwnerAndRepo, string Tag);

    /// <summary>
    /// <c>github.com/&lt;owner&gt;/&lt;repo&gt;/releases/tag/&lt;tag&gt;</c> or
    /// <c>github.com/&lt;owner&gt;/&lt;repo&gt;/releases/latest</c> (Tag null for the latter).
    /// </summary>
    public static GitHubReleasePage? TryParseGitHubReleaseUrl(string? url)
    {
        if (!TryCreateHttpsUri(url, "github.com", out var uri))
            return null;

        // AbsolutePath is still percent-encoded and the API URL builders encode again, so each segment is
        // decoded here: a "v1.2.3%2Bbuild" tag would otherwise reach the API as "%252B" and 404.
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();

        if (segments.Length == 4
            && string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[3], "latest", StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubReleasePage(segments[0], segments[1], null);
        }

        if (segments.Length == 5
            && string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[3], "tag", StringComparison.OrdinalIgnoreCase))
        {
            return new GitHubReleasePage(segments[0], segments[1], segments[4]);
        }

        return null;
    }

    /// <summary><c>gitlab.com/&lt;group[/subgroups]&gt;/&lt;project&gt;/-/releases/&lt;tag&gt;</c>.</summary>
    public static GitLabReleasePage? TryParseGitLabReleaseUrl(string? url)
    {
        if (!TryCreateHttpsUri(url, "gitlab.com", out var uri))
            return null;

        // AbsolutePath is still percent-encoded and the API URL builders encode again, so each segment is
        // decoded here: a "v1.2.3%2Bbuild" tag would otherwise reach the API as "%252B" and 404.
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();

        // Subgroups make the project path any length, so the "-" marker is located rather than assumed
        // at a fixed position; the namespace needs at least a group and a project before it.
        var marker = Array.IndexOf(segments, "-");
        if (marker >= 2
            && segments.Length == marker + 3
            && string.Equals(segments[marker + 1], "releases", StringComparison.OrdinalIgnoreCase))
        {
            return new GitLabReleasePage(string.Join('/', segments[..marker]), segments[marker + 2]);
        }

        return null;
    }

    /// <summary>
    /// https only (rejects http, javascript:, and everything else) and an exact, case-insensitive host
    /// match with no subdomain allowance; a lookalike host like "github.com.evil.com" fails here since
    /// Uri.Host for that string is the whole lookalike, not "github.com".
    /// </summary>
    private static bool TryCreateHttpsUri(string? url, string requiredHost, out Uri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!string.Equals(parsed.Host, requiredHost, StringComparison.OrdinalIgnoreCase))
            return false;

        uri = parsed;
        return true;
    }
}
