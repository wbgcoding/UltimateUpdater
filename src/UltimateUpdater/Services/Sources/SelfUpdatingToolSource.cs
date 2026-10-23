using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// A tool that updates ITSELF with one command, and whose newest version is announced on GitHub. One row per
/// tool: what is installed, what the newest release is, and a single command to move between them.
/// The rule that decides whether such a source shows anything at all: **a tool that another
/// manager already updates gets no row here.** Git for Windows is the example: it registers an uninstall
/// entry and is therefore offered by `winget upgrade`; a second row would be the same update twice. Git is
/// not implemented for that reason; the tools below can also arrive through a package manager, so each one
/// checks where its executable actually resolved from (<see cref="PackageManagerOwnership"/>) and stays quiet
/// when winget, scoop or chocolatey owns it. Installed by its own script into the user's profile; which is
/// how these tools usually arrive; nothing else can update it, and the row is the only way.
/// Nothing here is downloaded or run by this app: the tool updates itself with its own signed mechanism, so
/// there is no installer for us to verify (SECURITY.md's supply-chain rule stays with the vendor's tool).
/// No uninstall: the row IS the tool itself, not a package it manages; there is no
/// separate "remove this update" action distinct from removing the tool's own install, which this app
/// did not place and has no safe, tool-agnostic way to reverse.
/// </summary>
public abstract class SelfUpdatingToolSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    /// <summary>"owner/repo"; where the newest released version is announced.</summary>
    protected abstract string GitHubRepo { get; }

    /// <summary>Public wrapper so <see cref="SourceRegistry"/> can tell <see cref="GitHubReleaseSource"/>
    /// which repos already have a dedicated source, without widening <see cref="GitHubRepo"/> itself.</summary>
    public string CoversGitHubRepo => GitHubRepo;

    /// <summary>What the row is called. Not localized: these are product names.</summary>
    protected abstract string DisplayName { get; }

    /// <summary>The command that performs the update.</summary>
    protected abstract IReadOnlyList<string> UpgradeArguments { get; }

    /// <summary>How to ask the tool what it is. Nearly all of them answer <c>--version</c>.</summary>
    protected virtual IReadOnlyList<string> VersionArguments => ["--version"];

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    /// <summary>
    /// Resolved once per process: an executable does not move while the app is running, and the check reads
    /// the file system.
    /// </summary>
    private bool? _managedElsewhere;

    private bool ManagedElsewhere =>
        _managedElsewhere ??= PackageManagerOwnership.IsCommandManagedElsewhere(Executable);

    /// <summary>
    /// Present AND ours to update. A tool inside winget's package store is available in every sense except
    /// the one that matters here, and reporting it available would put a source in the settings and the
    /// first-run picker that can never produce a row.
    /// </summary>
    public override Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProcessRunner.Exists(Executable) && !ManagedElsewhere);

    /// <summary>
    /// A `--version` call's exit code carries no information here: several of these print their version to
    /// stderr, and the real check is whether a version could be READ (an unreadable one already produces no
    /// row). So only the timeout decides for this source.
    /// </summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => true;

    /// <summary>
    /// The version token of a finished `--version` call. Some of these print their version to stderr, so
    /// both streams are considered. A tool that ran but printed no readable version is a broken tool: a
    /// failed source, not a machine with nothing to update.
    /// </summary>
    internal string ReadInstalledVersion(ProcessResult version) =>
        PackageManagerParsers.FirstVersionToken(version.StandardOutput)
        ?? PackageManagerParsers.FirstVersionToken(version.StandardError)
        ?? throw new InvalidOperationException(
            $"{Id}: could not read the installed version from '{Executable} {string.Join(' ', VersionArguments)}'.");

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        if (ManagedElsewhere)
        {
            Logging.Info($"{Id}: skipped; {Executable} is installed through a package manager that already updates it.");
            return [];
        }

        var installed = ReadInstalledVersion(EnsureScanCompleted(await RunAsync(VersionArguments, cancellationToken)));

        return await CheckLatestAsync(installed, cancellationToken);
    }

    /// <summary>
    /// The GitHub half of the scan, split out so a test can hand in the installed version and a fake
    /// client. A repository GitHub does not know (404) is a valid, empty answer; a lookup that could not be
    /// answered (rate limit, outage, no connection, timeout) is a failed source.
    /// </summary>
    internal async Task<IReadOnlyList<UpdateItem>> CheckLatestAsync(string installed, CancellationToken cancellationToken)
    {
        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(
            httpClient, GitHubReleases.LatestUrl(GitHubRepo), Logging, Id, Id, cancellationToken, GitHubReleases.Headers);

        RegistryFetch.ThrowIfAllFailed(Id, [fetch]);

        // Fail closed: no answer from GitHub means we do not know of a newer version, not that there is one.
        if (fetch.Body is null || PackageMetadataParsers.ParseGitHubLatestRelease(fetch.Body) is not { AvailableVersion: { } tag } enrichment)
            return [];

        // A release TAG is not a version. Bun tags its releases "bun-v1.3.14", and comparing that against an
        // installed "1.3.14" parses as neither; the comparison fell through to string inequality and offered
        // an update to the version already installed. The number in
        // the tag is the version; anything else in it is the project's own habit.
        if (PackageManagerParsers.FirstVersionToken(tag) is not { } available)
        {
            Logging.Info($"{Id}: the newest release is tagged '{tag}', which carries no version; ignoring it.");
            return [];
        }

        if (!PackageManagerParsers.IsNewer(installed, available))
            return [];

        return
        [
            new UpdateItem
            {
                Id = Id,
                SourceId = Id,
                Category = Category,
                Name = DisplayName,
                InstalledVersion = installed,
                AvailableVersion = available,
                IsCritical = SecurityRelevance.IsSecurityRelevant(Id, DisplayName),
                PublishDate = enrichment.PublishDate,
                DownloadSize = enrichment.DownloadSize,
                Meta = enrichment.ProjectUrl is { } url
                    ? new Dictionary<string, string> { ["projectUrl"] = url }
                    : new Dictionary<string, string>(),
            },
        ];
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => UpgradeArguments;
}
