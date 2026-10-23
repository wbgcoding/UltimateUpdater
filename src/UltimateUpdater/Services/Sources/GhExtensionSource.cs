using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>GitHub CLI extensions. `gh extension list` itself flags which ones have an update pending.</summary>
public sealed class GhExtensionSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "gh-extension";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "gh";

    /// <summary>
    /// `gh extension list` marks an update as pending but never says what version it upgrades to, the
    /// GitHub REST API for the extension's own repo does. Read-only, unauthenticated (rate-limited but
    /// that only means occasional 403s, which fail closed like any other lookup).
    /// </summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (!TrySplitOwnerRepo(item.Id, out var owner, out var repo))
            return Task.FromResult<UpdateEnrichment?>(null);

        return RegistryEnrichment.FetchAsync(
            httpClient, GitHubReleases.LatestUrl($"{owner}/{repo}"), PackageMetadataParsers.ParseGitHubLatestRelease,
            Logging, Id, item.Id, cancellationToken, GitHubReleases.Headers);
    }

    /// <summary>
    /// `gh extension list` exits 1 when NO extensions are installed, which is a perfectly ordinary answer
    /// and parses to an empty list. Anything above that is a broken gh and fails the source.
    /// </summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => exitCode is 0 or 1;

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["extension", "list"], cancellationToken));

        return PackageManagerParsers.ParseGhExtensionList(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["extension", "upgrade", item.Id];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["extension", "remove", item.Id];

    /// <summary>The id is "owner/repo", exactly one separator, both halves a plain GitHub-safe token.</summary>
    private static bool TrySplitOwnerRepo(string id, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        var parts = id.Split('/');
        if (parts.Length != 2 || !IsPlainToken(parts[0]) || !IsPlainToken(parts[1]))
            return false;

        owner = parts[0];
        repo = parts[1];
        return true;
    }

    private static bool IsPlainToken(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
}
