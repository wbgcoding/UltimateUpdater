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

    /// <summary>Zero only; exit 1 is judged by <see cref="ScanFromResult"/> because gh uses it for errors too.</summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => exitCode == 0;

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken) =>
        ScanFromResult(await RunAsync(["extension", "list"], cancellationToken));

    private const string NoExtensionsMarker = "no installed extensions found";

    /// <summary>
    /// `gh extension list` exits 1 when NO extensions are installed (empty stdout, that sentence on stderr),
    /// which is an ordinary answer. gh also uses 1 for every other error, so exit 1 counts only with empty
    /// stdout and either an empty stderr or that sentence; anything else fails the source with stderr's first line.
    /// </summary>
    internal IReadOnlyList<UpdateItem> ScanFromResult(ProcessResult raw)
    {
        var emptyAnswer = raw is { TimedOut: false, ExitCode: 1 }
            && string.IsNullOrWhiteSpace(raw.StandardOutput)
            && (string.IsNullOrWhiteSpace(raw.StandardError)
                || raw.StandardError.Contains(NoExtensionsMarker, StringComparison.OrdinalIgnoreCase));
        if (emptyAnswer)
            return [];

        if (raw is { TimedOut: false, ExitCode: 1 })
        {
            var first = raw.StandardError.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0)
                ?? raw.StandardOutput.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0)
                ?? string.Empty;
            var suffix = first.Length > 0 ? $" {LoggingService.Redact(first.Length <= 300 ? first : first[..300])}" : string.Empty;
            throw new InvalidOperationException($"{Id}: scan failed; '{Executable}' exited with code 1.{suffix}");
        }

        var result = EnsureScanCompleted(raw);

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
