using System.Globalization;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <summary>One asset attached to a GitHub release.</summary>
public readonly record struct GitHubReleaseAsset(string Name, string DownloadUrl, long? Size);

/// <summary>
/// A repository's latest release, as much as this source needs: the version (tag with a leading "v"
/// trimmed, GitHub's own convention), when it was published, the release's own page (both the changelog
/// link and the fallback "open" target), the changelog text itself, and every asset attached; the
/// source decides what to do with the asset list, this only reports it.
/// </summary>
public readonly record struct GitHubRelease(
    string? Version,
    DateTimeOffset? PublishDate,
    string? HtmlUrl,
    string? Body,
    IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>
/// Pure parsing for GitHub release data. <see cref="ParseLatestRelease"/> is the one reader of the "latest
/// release" JSON: the generic "any installed program whose uninstall entry points at GitHub" source uses
/// its asset list to decide whether a Windows installer can be identified unambiguously, and
/// <see cref="PackageMetadataParsers.ParseGitHubLatestRelease"/> maps the same result to the smaller
/// enrichment shape the self-updating tool sources need.
/// </summary>
public static class GitHubReleaseParsers
{
    /// <summary>
    /// An uninstall entry's about/help URL, if it names exactly one GitHub owner and repository;
    /// "github.com/owner/repo" and nothing else in the path. Deliberately strict: a URL with more path
    /// segments (an issue, a wiki page, a specific release) or fewer (a bare user profile) is NOT a
    /// product repository and is skipped rather than guessed at, per this project's "no fabricated
    /// values" rule extended to "no fabricated repositories".
    /// </summary>
    public static string? TryExtractOwnerRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            return null;

        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        if (owner.Length == 0 || repo.Length == 0)
            return null;

        return $"{owner}/{repo}";
    }

    /// <summary>Parses the raw JSON body of GitHub's "latest release" endpoint. Never throws; malformed or unexpected shapes return default.</summary>
    public static GitHubRelease ParseLatestRelease(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return default;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return default;

            string? version = null;
            if (root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String)
            {
                var raw = tag.GetString();
                version = !string.IsNullOrWhiteSpace(raw) && raw.Length > 1 && (raw[0] == 'v' || raw[0] == 'V')
                    ? raw[1..]
                    : raw;
            }

            // Invariant culture + AssumeUniversal: GitHub's timestamp is ISO-8601 UTC ("Z"-suffixed), and
            // without this a machine whose current culture defaults to a non-Gregorian calendar (Thai,
            // Hijri, ...) parses it into the wrong era/year. Same pattern as PackageMetadataParsers.ParseDate.
            DateTimeOffset? published = null;
            if (root.TryGetProperty("published_at", out var publishedAt) &&
                publishedAt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    publishedAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                published = parsedDate;
            }

            string? htmlUrl = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
                ? html.GetString()
                : null;

            string? body = root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : null;

            var assets = new List<GitHubReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object)
                        continue;

                    var name = asset.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString()
                        : null;
                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                        ? urlEl.GetString()
                        : null;
                    long? size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number &&
                                 sizeEl.TryGetInt64(out var bytes)
                        ? bytes
                        : null;

                    if (name is not null && downloadUrl is not null)
                        assets.Add(new GitHubReleaseAsset(name, downloadUrl, size));
                }
            }

            return new GitHubRelease(version, published, htmlUrl, body, assets);
        }
    }

    /// <summary>
    /// The one asset that is unambiguously "the Windows installer for this release"; a single `.exe` or
    /// `.msi` whose name does not also look like a Linux/macOS build (no other asset shares its
    /// extension). Two or more candidates means picking one would be a guess; this returns null rather
    /// than guess, same rule as everywhere else in this project.
    /// </summary>
    public static GitHubReleaseAsset? TryFindUnambiguousWindowsInstaller(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        var candidates = assets
            .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .Where(a => !LooksNonWindows(a.Name))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool LooksNonWindows(string assetName)
    {
        var lower = assetName.ToLowerInvariant();
        return lower.Contains("linux") || lower.Contains("macos") || lower.Contains("darwin") || lower.Contains("osx");
    }
}

/// <summary>
/// Pure parsing for GitLab's release API; just the one field this app needs, mirroring
/// <see cref="GitHubReleaseParsers.ParseLatestRelease"/>'s never-throws discipline for a much smaller shape.
/// </summary>
public static class GitLabReleaseParsers
{
    /// <summary>The release notes body (markdown). Never throws; malformed or unexpected shapes return null.</summary>
    public static string? ParseReleaseDescription(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                ? description.GetString()
                : null;
        }
    }
}
