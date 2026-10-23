using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// pipx-managed Python CLI tools. `pipx list` has no outdated mode, so every installed package is
/// listed by the CLI with no notion of "outdated", so the scan asks PyPI for each package's newest version
/// and only a package genuinely behind becomes a row.
/// </summary>
public sealed class PipxSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "pipx";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "pipx";

    /// <summary>
    /// pipx wraps PyPI, so PyPI knows everything `pipx list` cannot: the newest version, when it shipped
    /// and its download size. Unlike pip's enrichment (which looks up an already-known target version),
    /// there is no version to plug into the URL here (`pipx list` never reports one), so this hits the
    /// version-less PyPI endpoint and reads `info.version` as the target itself.
    /// </summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(item.Id))
            return Task.FromResult<UpdateEnrichment?>(null);

        var url = $"{PipSource.PyPiBaseUrl}{Uri.EscapeDataString(item.Id)}/json";
        return RegistryEnrichment.FetchAsync(
            httpClient, url, PackageMetadataParsers.ParsePyPiLatest, Logging, Id, item.Id, cancellationToken);
    }

    /// <summary>
    /// `pipx list` reports every installed package with no notion of "outdated" (unlike `pip
    /// list --outdated`, which pip itself filters), so PyPI is asked during the SCAN (as
    /// VsCodeExtensionSource does with its marketplace) and only a package genuinely behind gets a row. Bounded concurrency, like every other per-package lookup in
    /// this app (<see cref="BoundedFanOut"/>).
    /// </summary>
    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["list", "--json"], cancellationToken));
        return await CheckAgainstPyPiAsync(PackageManagerParsers.ParsePipxJson(result.StandardOutput), cancellationToken);
    }

    /// <summary>The PyPI half of the scan, split out so a test can hand in the installed list and a fake
    /// client. Every lookup failing is a failed source (see <see cref="RegistryFetch.ThrowIfAllFailed"/>).</summary>
    internal async Task<IReadOnlyList<UpdateItem>> CheckAgainstPyPiAsync(
        IReadOnlyList<OutdatedPackage> installed, CancellationToken cancellationToken)
    {
        var checks = await BoundedFanOut.SelectAsync(installed, async (package, token) =>
        {
            var (latest, fetch) = await PipSource.LatestOnPyPiAsync(httpClient, Logging, Id, package.Id, token);
            var row = Pep440.IsNewer(package.Installed, latest)
                ? CreateItem(package.Id, package.Name, package.Installed, latest)
                : null;
            return (Row: row, Fetch: fetch);
        }, cancellationToken);

        RegistryFetch.ThrowIfAllFailed(Id, checks.Select(check => check.Fetch));

        return checks.Where(check => check.Row is not null).Select(check => check.Row!).ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["upgrade", item.Id];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["uninstall", item.Id];
}
