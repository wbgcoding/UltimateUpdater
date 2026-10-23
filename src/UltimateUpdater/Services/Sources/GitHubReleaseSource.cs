using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Update detection for software that did NOT arrive through any package manager: a setup.exe
/// downloaded straight from a GitHub project's Releases page. Generalizes the same GitHub
/// releases lookup <see cref="SelfUpdatingToolSource"/> uses per named tool, but applies it to ANY
/// installed program whose Add/Remove Programs entry happens to name a GitHub repository.
/// Deliberately Tier C, not Tier A: an unknown third-party installer has no agreed silent-install
/// switch (Inno's /VERYSILENT, NSIS's /S, InstallShield's /s, or nothing at all) and guessing one wrong
/// means either a failed run or, worse, a foreign installer wizard popping up mid-batch: this app's
/// own convention (winget --silent, NVIDIA -s -n, …) is to never do that. Detection and the version
/// comparison are exactly as real as any Tier A source; only the action differs (open the release page,
/// same as Steam's real-status/hand-off precedent), matching the existing SourceTier.C contract
/// (IUpdateSource.InstallAsync throws for it) rather than introducing a new pattern.
/// No uninstall for the same reason there is no install: an unknown third-party program has no agreed
/// silent-uninstall switch either, and the Registry-`UninstallString` fallback that would be needed here
/// is out of scope for this app on principle.
/// </summary>
public sealed class GitHubReleaseSource(
    LoggingService logging, HttpClient httpClient, IRegistryView registry, IReadOnlyCollection<string>? ownedRepos = null)
    : IUpdateSource
{

    /// <summary>Repos a dedicated <see cref="SelfUpdatingToolSource"/> already owns, never duplicate
    /// that row.</summary>
    private readonly HashSet<string> _ownedRepos = new(ownedRepos ?? [], StringComparer.OrdinalIgnoreCase);

    public string Id => "github-release";

    public UpdateCategory Category => UpdateCategory.Software;

    public SourceTier Tier => SourceTier.C;

    /// <summary>Enumerating Add/Remove Programs is a handful of local registry reads; cheap enough to
    /// just do it, rather than duplicating the walk here only to answer "any at all".</summary>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// A ceiling for the whole source, not just per request. Candidates are looked up one after another
    /// on purpose: an unauthenticated GitHub allows sixty requests an hour, and firing them together
    /// only burns that budget faster, but a machine with a couple of dozen qualifying programs and a slow
    /// answer for each would otherwise keep the scan running for many minutes. Whatever is found by the
    /// deadline is reported; the rest waits for the next scan.
    /// </summary>
    private static readonly TimeSpan SourceDeadline = ExternalCallTimeouts.ScanDeadline;

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SourceDeadline);

        // One outcome per lookup actually made: when every one of them failed (offline, rate limited),
        // the source has checked nothing and must say so instead of reporting a clean result.
        var fetches = new List<RegistryFetch?>();

        foreach (var candidate in FindCandidates())
        {
            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logging.Info("github-release: stopped at the source deadline; the remaining candidates wait for the next scan.");
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            UpdateItem? row;
            try
            {
                (row, var fetch) = await ResolveAsync(candidate, deadline.Token);
                fetches.Add(fetch);
            }
            // Only a REAL cancellation may end the source. The source deadline also arrives as an
            // OperationCanceledException; it ends this candidate only, and the loop stops at the top.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                logging.Warn($"github-release: lookup for {candidate.OwnerRepo} failed: {ex.Message}");
                row = null;
            }

            if (row is not null)
                yield return row;
        }

        RegistryFetch.ThrowIfAllFailed(Id, fetches);
    }

    private readonly record struct Candidate(string DisplayName, string InstalledVersion, string OwnerRepo);

    /// <summary>
    /// Every Add/Remove Programs entry whose about/help URL names exactly one GitHub repository. A repo
    /// can appear more than once across the four roots (per-machine vs per-user installs of the same
    /// app do not happen together in practice, and if they somehow did, the later one simply overwrites
    /// the dictionary entry for the same key, not worth guarding against for a detect-only source).
    /// </summary>
    private List<Candidate> FindCandidates()
    {
        var seen = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            foreach (var subKey in registry.SubKeyNames(root))
            {
                var entry = $@"{root}\{subKey}";

                var displayName = registry.Value(entry, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;

                var version = registry.Value(entry, "DisplayVersion");
                if (string.IsNullOrWhiteSpace(version))
                    continue;

                var repo = GitHubReleaseParsers.TryExtractOwnerRepo(registry.Value(entry, "URLInfoAbout"))
                           ?? GitHubReleaseParsers.TryExtractOwnerRepo(registry.Value(entry, "HelpLink"));
                if (repo is null || _ownedRepos.Contains(repo))
                    continue;

                // `_ownedRepos` only names the self-updating runtimes (deno/bun/uv). A program installed by
                // winget/scoop/chocolatey whose OWN uninstall entry names a GitHub repo (mise-en-place does)
                // is already tracked by that manager; a second "manual update" row from here could carry a
                // DIFFERENT available version. So every candidate gets the same ownership check
                // `SelfUpdatingToolSource` applies to itself.
                var installPath = registry.Value(entry, "InstallLocation") is { Length: > 0 } location
                    ? location
                    : RegistryCleanupSafety.ExtractFilePath(
                        registry.Value(entry, "DisplayIcon") ?? registry.Value(entry, "UninstallString"), File.Exists);

                if (installPath is not null && PackageManagerOwnership.IsManagedElsewhere(installPath))
                    continue;

                seen[repo] = new Candidate(displayName.Trim(), version.Trim(), repo);
            }
        }

        return [.. seen.Values];
    }

    /// <summary>The row, if the release is newer, and how the lookup ended: a repository GitHub does not
    /// know or that has no release (404) is an answer; a refusal, an outage or no connection is a failure.</summary>
    private async Task<(UpdateItem? Row, RegistryFetch Fetch)> ResolveAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(
            httpClient, GitHubReleases.LatestUrl(candidate.OwnerRepo), logging, Id, candidate.OwnerRepo,
            cancellationToken, GitHubReleases.Headers, timeout: ExternalCallTimeouts.Default);

        return (fetch.Body is null ? null : ToRow(candidate, fetch.Body), fetch);
    }

    private UpdateItem? ToRow(Candidate candidate, string json)
    {
        var release = GitHubReleaseParsers.ParseLatestRelease(json);

        // A release tag is not a version (Bun tags "bun-v1.3.14", for example): the same defensive
        // parse every other GitHub-backed source in this app uses, not a second weaker one.
        if (release.Version is null || PackageManagerParsers.FirstVersionToken(release.Version) is not { } available)
            return null;

        // The SAME parse on the installed side; comparing it raw instead would let a registry
        // DisplayVersion like "v1.2.3" or "22.9 build 5" fail to parse as a Version, IsNewer would fall
        // back to string inequality, and the row would claim an update to the version already installed,
        // for good. No readable installed version means no row at all, rather than a guess.
        if (PackageManagerParsers.FirstVersionToken(candidate.InstalledVersion) is not { } installed)
            return null;

        if (!PackageManagerParsers.IsNewer(installed, available))
            return null;

        var meta = new Dictionary<string, string>();
        if (release.Body is { Length: > 0 } body)
            meta["changelogBody"] = body;
        // Read from a remote response, so checked on the way in like every other source's links.
        var releasePage = UrlSafety.SafeHttpUrl(release.HtmlUrl);
        if (releasePage is not null)
            meta["changelogUrl"] = releasePage;

        return new UpdateItem
        {
            Id = $"github:{candidate.OwnerRepo}",
            SourceId = Id,
            Category = Category,
            Name = candidate.DisplayName,
            InstalledVersion = candidate.InstalledVersion,
            AvailableVersion = available,
            IsCritical = SecurityRelevance.IsSecurityRelevant(candidate.OwnerRepo, candidate.DisplayName),
            Tier = SourceTier.C,
            PublishDate = release.PublishDate,
            DownloadSize = GitHubReleaseParsers.TryFindUnambiguousWindowsInstaller(release.Assets)?.Size,
            OpenTarget = releasePage ?? $"https://github.com/{candidate.OwnerRepo}/releases/latest",
            Meta = meta,
        };
    }

    public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Id} can only be opened; see this source's class remarks for why.");
}
