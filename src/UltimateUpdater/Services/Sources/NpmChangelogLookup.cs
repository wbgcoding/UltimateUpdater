using System.IO;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The npm half: on click only, `npm view &lt;package&gt; homepage repository --json` names the
/// package's repository and homepage; never called during a scan or the registry-based background
/// enrichment (<c>NpmEnrichment</c> deliberately never touches the changelog fields, see its own doc
/// comment, precisely so a changelog button pointing at the same URL its info-link already uses does not
/// duplicate that button). Two real captured shapes back the parser (`npm view react homepage repository
/// --json` and `npm view left-pad homepage repository --json`): a package's "repository"
/// field can be a plain JSON string or an object with a "url", and that url can be a plain
/// "git+https://..." (react) or an ssh form, "git+ssh://git@github.com/..." (left-pad) that
/// <see cref="GitHubReleaseParsers.TryExtractOwnerRepo"/> does not accept. The ssh/shorthand cases are
/// deliberately left unrecognised, this project's "never guess a URL" rule, and fall back to the
/// homepage instead of a hand-rolled second recogniser.
/// </summary>
public static class NpmChangelogLookup
{
    private static readonly TimeSpan LookupTimeout = ExternalCallTimeouts.Default;

    public readonly record struct NpmChangelogSource(string? RepositoryUrl, string? Homepage);

    /// <summary>
    /// Runs `npm view &lt;package&gt; homepage repository --json`. Never throws: a missing/unpublished
    /// package, a timeout, or npm itself being unavailable all degrade to an empty result, the same
    /// discipline as every other on-click enrichment fetch in this project.
    /// </summary>
    public static async Task<NpmChangelogSource> LookupAsync(
        string packageName, LoggingService logging, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(packageName))
            return default;

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                "npm", ["view", packageName, "homepage", "repository", "--json"], LookupTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            logging.Warn($"npm: changelog lookup for '{packageName}' failed: {ex.Message}");
            return default;
        }

        // An unpublished/removed package makes `npm view` exit non-zero with an error object on stdout
        // that ParseView would already read as "nothing found" (neither expected key is present); the
        // exit code is still checked first so a genuine tool failure is never silently folded into that
        // same "found nothing" answer.
        if (result.TimedOut || result.ExitCode != 0)
            return default;

        return ParseView(result.StandardOutput);
    }

    /// <summary>Pure parse of the `--json` output above. Malformed or unexpected JSON returns an empty result, never throws.</summary>
    public static NpmChangelogSource ParseView(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
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

            var homepage = root.TryGetProperty("homepage", out var homepageElement)
                && homepageElement.ValueKind == JsonValueKind.String
                ? homepageElement.GetString()
                : null;

            string? repositoryUrl = null;
            if (root.TryGetProperty("repository", out var repository))
            {
                if (repository.ValueKind == JsonValueKind.String)
                    repositoryUrl = repository.GetString();
                else if (repository.ValueKind == JsonValueKind.Object
                    && repository.TryGetProperty("url", out var urlElement)
                    && urlElement.ValueKind == JsonValueKind.String)
                    repositoryUrl = urlElement.GetString();
            }

            return new NpmChangelogSource(
                string.IsNullOrWhiteSpace(repositoryUrl) ? null : repositoryUrl,
                string.IsNullOrWhiteSpace(homepage) ? null : homepage);
        }
    }

    /// <summary>
    /// The repository field's real-world "git+" prefix (npm's own packument convention) stripped, then
    /// handed to <see cref="GitHubReleaseParsers.TryExtractOwnerRepo"/> and turned into that repo's
    /// release-page URL, the same shape <c>ChangelogUrlParsers.TryParseGitHubReleaseUrl</c> recognises so
    /// the existing release-link enrichment fetches the real text. An ssh-form or shorthand repository is
    /// a GitHub location but not a GitHub URL that parser accepts, and null is returned rather than
    /// guessed at (caller falls back to the homepage).
    /// </summary>
    public static string? TryBuildGitHubReleasesUrl(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return null;

        var trimmed = repositoryUrl.Trim();
        if (trimmed.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[4..];

        return GitHubReleaseParsers.TryExtractOwnerRepo(trimmed) is { } ownerRepo
            ? $"https://github.com/{ownerRepo}/releases/latest"
            : null;
    }
}
