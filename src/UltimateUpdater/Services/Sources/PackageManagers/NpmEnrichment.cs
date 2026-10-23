using System.Net.Http;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// npm registry enrichment shared by <see cref="NpmSource"/>, <see cref="PnpmSource"/> and Volta's
/// package-manager rows (<see cref="VoltaSource"/>), pnpm's global packages and Volta's yarn/pnpm/npm
/// tools ARE npm registry packages, same lookup either way. `dist.unpackedSize` is optional; when the
/// registry omits it, the real download size comes from a HEAD on `dist.tarball` instead, one GET for
/// both facts, never two. No <c>ChangelogUrl</c> here: the npm registry exposes no
/// changelog-specific field distinct from <see cref="PackageMetadataParsers.ParseNpmPackument"/>'s own
/// <c>ProjectUrl</c> (homepage/repo), which already drives the row's separate info-link button, a
/// changelog button pointing at that exact same URL would just be a second button for one destination.
/// </summary>
internal static class NpmEnrichment
{
    /// <summary>
    /// Fully percent-encoded rather than a hand-rolled <c>Replace("/", "%2f")</c>: a manual
    /// substitution only ever covers the one character someone thought of, and anything it misses is
    /// pasted into the URL raw. The registry serves the fully encoded form, verified live against
    /// <c>https://registry.npmjs.org/%40babel%2Fcore</c> (200).
    /// </summary>
    internal static string PackumentUrl(string packageName) =>
        $"https://registry.npmjs.org/{Uri.EscapeDataString(packageName)}";

    /// <summary>
    /// The publish date lives only in the full packument's "time" map, and for a popular package that
    /// document is far past the registry default cap (npm about 26 MB, next about 31 MB, typescript about
    /// 16 MB), so every such row lost its enrichment. This one read gets its own, larger ceiling and the
    /// time to download it. The body is scanned as a stream and never held whole, so the ceiling only bounds
    /// download time and work, not memory.
    /// </summary>
    private const long MaxPackumentBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan PackumentTimeout = TimeSpan.FromMinutes(2);

    public static async Task<UpdateEnrichment?> FetchAsync(
        HttpClient httpClient, LoggingService logging, string sourceId, string packageName, string? version,
        string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version) || !ProcessRunner.IsSafePositionalArgument(packageName))
            return null;

        var url = PackumentUrl(packageName);
        var facts = await RegistryEnrichment.FetchStreamedAsync(
            httpClient, url, logging, sourceId, itemId, cancellationToken,
            async (stream, cap, onExceeded, token) =>
            {
                var scanned = await NpmPackumentReader.ReadAsync(stream, version, cap, token, onExceeded);
                if (scanned is null)
                    logging.Warn($"{sourceId}: the npm packument for '{itemId}' could not be read.");
                return scanned;
            },
            timeout: PackumentTimeout, maxContentBytes: MaxPackumentBytes);
        if (facts is null)
            return null;

        var (enrichment, tarballUrl) = PackageMetadataParsers.FromNpmPackument(facts);

        if (enrichment.DownloadSize is null && tarballUrl is not null)
        {
            var size = await RegistryEnrichment.HeadContentLengthAsync(tarballUrl, logging, sourceId, itemId, cancellationToken);
            if (size is not null)
                enrichment = enrichment with { DownloadSize = size };
        }

        return enrichment.PublishDate is null && enrichment.DownloadSize is null &&
               enrichment.ProjectUrl is null && enrichment.AvailableVersion is null
            ? null
            : enrichment;
    }
}
