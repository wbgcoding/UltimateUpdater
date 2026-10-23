using System.Net.Http;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Volta, the pinned Node/npm/yarn/pnpm toolchain manager. Volta has NO built-in
/// "outdated" concept at all (confirmed against its own docs and, live on this dev box, its
/// <c>list --help</c>), so unlike every other <c>CliUpdateSource</c> here, the "is a newer version
/// available" answer has to come from outside Volta entirely (Node's own release index for the node
/// runtime, the npm registry for its package-manager tools), same shape as <see cref="FlutterSource"/>.
/// Scoped to ONLY the "(default)" entry per tool kind: the version Volta actually resolves outside a
/// pinned project, and the one clear "current version" concept Volta has. A non-default installed
/// version is Volta deliberately keeping an older toolchain around for some other project; this is not
/// "an update available" in the sense every other row here means, and is left alone (the reasoning
/// that rules out nvm-windows/juliaup/pyenv-win outright; Volta's "(default)" marker is what makes
/// THIS tool different from those).
/// Every comparison here is a REAL fetched version against a REAL read installed version: the standing
/// rule: never show an update that would repeat forever
/// because the installed version could not actually be read. If the installed side
/// cannot be parsed from `volta list`, that tool is silently skipped rather than guessed.
/// </summary>
public sealed class VoltaSource(LoggingService logging, HttpClient httpClient) : IUpdateSource
{
    private const string NodeReleaseIndexUrl = "https://nodejs.org/dist/index.json";
    private static readonly TimeSpan FetchTimeout = ExternalCallTimeouts.Default;

    public string Id => "volta";

    public UpdateCategory Category => UpdateCategory.DeveloperTools;

    public SourceTier Tier => SourceTier.A;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProcessRunner.Exists("volta"));

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("volta", ["list", "all", "--format", "plain"], ExternalCallTimeouts.Long, cancellationToken);

        foreach (var item in await CheckToolsAsync(ReadDefaultTools(result), cancellationToken))
            yield return item;
    }

    /// <summary>
    /// The feed half of the scan, split out so a test can hand in the installed tools and a fake client.
    /// Fail closed both ways: no answer from the feed, or an installed version that could not be
    /// read/compared, both mean "say nothing". never a repeated guess. But when EVERY feed lookup could not
    /// be answered at all, that is a failed source (see <see cref="RegistryFetch.ThrowIfAllFailed"/>).
    /// </summary>
    internal async Task<IReadOnlyList<UpdateItem>> CheckToolsAsync(
        IReadOnlyList<VoltaDefaultTool> tools, CancellationToken cancellationToken)
    {
        var rows = new List<UpdateItem>();
        var fetches = new List<RegistryFetch>();

        foreach (var tool in tools)
        {
            var (latest, fetch) = await FetchLatestAsync(tool, cancellationToken);
            fetches.Add(fetch);

            if (latest is null || !PackageManagerParsers.IsNewer(tool.Version, latest))
                continue;

            rows.Add(new UpdateItem
            {
                Id = $"volta-{tool.Name}",
                SourceId = Id,
                Category = Category,
                Name = $"{tool.Name} (Volta)",
                InstalledVersion = tool.Version,
                AvailableVersion = latest,
                IsCritical = false,
                Meta = new Dictionary<string, string> { ["voltaName"] = tool.Name, ["voltaKind"] = tool.Kind },
            });
        }

        RegistryFetch.ThrowIfAllFailed(Id, fetches.Select(fetch => (RegistryFetch?)fetch));

        return rows;
    }

    /// <summary>
    /// A `volta list` that ran is an answer or a failed scan: a timeout or a non-zero exit means Volta is
    /// broken, which must show as a failed source. Output with no default tool in it is a valid, empty answer.
    /// </summary>
    internal static IReadOnlyList<VoltaDefaultTool> ReadDefaultTools(ProcessResult result)
    {
        if (result.TimedOut)
            throw new TimeoutException("volta: listing the installed tools timed out.");

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"volta: 'volta list' exited with code {result.ExitCode}.");

        return VoltaParsers.ParseDefaultTools(result.StandardOutput);
    }

    /// <summary>
    /// Neither Volta itself nor the feeds compared against during the scan (§ScanAsync) carry a
    /// date/size, each is read again here, off the scan's critical path, from the SAME feeds: the node
    /// runtime's release date and archive size from Node's own index, a package-manager tool's date and
    /// size from the npm registry packument (shared with <see cref="NpmSource"/>/<see cref="PnpmSource"/>
    /// via <see cref="NpmEnrichment"/>).
    /// </summary>
    public Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || !item.Meta.TryGetValue("voltaKind", out var kind))
            return Task.FromResult<UpdateEnrichment?>(null);

        return kind == "runtime"
            ? EnrichRuntimeAsync(item, cancellationToken)
            : item.Meta.TryGetValue("voltaName", out var name)
                ? NpmEnrichment.FetchAsync(httpClient, logging, Id, name, item.AvailableVersion, item.Id, cancellationToken)
                : Task.FromResult<UpdateEnrichment?>(null);
    }

    private async Task<UpdateEnrichment?> EnrichRuntimeAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var json = await RegistryEnrichment.FetchRawAsync(httpClient, NodeReleaseIndexUrl, logging, Id, item.Id, cancellationToken);
        var date = json is null ? null : VoltaParsers.ParseNodeReleaseDate(json, item.AvailableVersion!);

        var archiveUrl = $"https://nodejs.org/dist/v{item.AvailableVersion}/node-v{item.AvailableVersion}-win-x64.zip";
        var size = await RegistryEnrichment.HeadContentLengthAsync(archiveUrl, logging, Id, item.Id, cancellationToken);

        return date is null && size is null ? null : new UpdateEnrichment(date, size);
    }

    private async Task<(string? Latest, RegistryFetch Fetch)> FetchLatestAsync(VoltaDefaultTool tool, CancellationToken cancellationToken)
    {
        if (tool.Kind == "runtime")
        {
            var feed = await RegistryEnrichment.FetchRawOutcomeAsync(
                httpClient, NodeReleaseIndexUrl, logging, Id, tool.Name, cancellationToken, timeout: FetchTimeout);

            return (feed.Body is null ? null : VoltaParsers.ParseLatestNodeVersion(feed.Body, tool.Version.Split('.')[0]), feed);
        }

        // package-manager: npm, yarn, pnpm: all real npm-registry packages. Only the manifest the `latest`
        // tag points at: the full packument of npm itself is far past the registry size cap.
        var registryUrl = $"https://registry.npmjs.org/{Uri.EscapeDataString(tool.Name)}/latest";
        var manifest = await RegistryEnrichment.FetchRawOutcomeAsync(
            httpClient, registryUrl, logging, Id, tool.Name, cancellationToken, timeout: FetchTimeout);

        return (manifest.Body is null ? null : PackageMetadataParsers.ParseNpmManifestVersion(manifest.Body), manifest);
    }

    /// <summary>Re-pins the default to the exact version this scan verified, never a floating `@latest`
    /// tag, so what gets installed is provably the version the row actually offered.</summary>
    public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!item.Meta.TryGetValue("voltaName", out var name) || string.IsNullOrWhiteSpace(item.AvailableVersion))
            throw new InvalidOperationException($"{Id}: missing install target for '{item.Name}'.");

        // "voltaName" comes straight out of `volta list`'s own output (VoltaParsers.ParseDefaultTools'
        // name group is a bare \S+): an argument array stops shell injection but not a value starting
        // with '-' being read as a flag by volta itself, same class of check every CliUpdateSource-based
        // source gets for free from its base class's GuardPackageId.
        if (!ProcessRunner.IsSafePositionalArgument(name))
            throw new InvalidOperationException($"{Id}: refusing to install '{name}': that is not a plain tool name.");

        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunStreamingAsync(
            "volta",
            ["install", $"{name}@{item.AvailableVersion}"],
            TimeSpan.FromMinutes(10),
        segment => progress.Report(new InstallProgress { PercentComplete = 50, StatusText = $"{item.Name}: {segment.Trim()}" }),
            cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: installing '{item.Name}' timed out.");

        if (result.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: installing '{item.Name}' failed. {detail}");
        }

        progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>`volta uninstall` only removes packages: for the runtimes node, npm and yarn it answers
    /// "not supported", so the menu entry is not offered for them at all.</summary>
    public bool CanUninstall(UpdateItem item) =>
        item.Meta.TryGetValue("voltaName", out var name)
        && !name.Equals("node", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("npm", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("yarn", StringComparison.OrdinalIgnoreCase);

    /// <summary>`volta uninstall` takes the tool's own name, never the row's `volta-` prefixed id.</summary>
    public async Task UninstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!item.Meta.TryGetValue("voltaName", out var name))
            throw new InvalidOperationException($"{Id}: missing uninstall target for '{item.Name}'.");

        // Same untrusted-source reasoning as InstallAsync above.
        if (!ProcessRunner.IsSafePositionalArgument(name))
            throw new InvalidOperationException($"{Id}: refusing to remove '{name}': that is not a plain tool name.");

        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunAsync("volta", ["uninstall", name], ExternalCallTimeouts.ScanDeadline, cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: removing '{item.Name}' timed out.");

        if (result.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: removing '{item.Name}' failed. {detail}");
        }

        progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }
}
