using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Python command-line tools installed with `uv tool`. uv's own listing says what is installed and nothing
/// about what is newest, so PyPI is asked once per tool; in parallel, and only tools that are genuinely
/// behind become rows. Guessing instead would put every installed tool on the table as an "update", which is
/// the phantom-update behaviour this project keeps out of the table.
/// This is separate from <see cref="UvSelfSource"/>: that one is uv updating ITSELF and steps aside when a
/// package manager owns uv, while this one manages other packages and is useful either way.
/// </summary>
public sealed class UvToolSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "uv-tool";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "uv";

    /// <summary>Date and size for the version being offered, from the same PyPI release pip already reads.</summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || !ProcessRunner.IsSafePositionalArgument(item.Id))
            return Task.FromResult<UpdateEnrichment?>(null);

        var url = $"{PipSource.PyPiBaseUrl}{Uri.EscapeDataString(item.Id)}/{Uri.EscapeDataString(item.AvailableVersion)}/json";
        return RegistryEnrichment.FetchAsync(
            httpClient, url, PackageMetadataParsers.ParsePyPiRelease, Logging, Id, item.Id, cancellationToken);
    }

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["tool", "list"], cancellationToken));

        return await CheckAgainstPyPiAsync(PackageManagerParsers.ParseUvToolList(result.StandardOutput), cancellationToken);
    }

    /// <summary>The PyPI half of the scan, split out so a test can hand in the installed list and a fake
    /// client. Together rather than one after another: a machine with a dozen tools is otherwise a dozen
    /// sequential round-trips (the same reasoning as DotnetToolSource). Every lookup failing is a failed
    /// source (see <see cref="RegistryFetch.ThrowIfAllFailed"/>).</summary>
    internal async Task<IReadOnlyList<UpdateItem>> CheckAgainstPyPiAsync(
        IReadOnlyList<OutdatedPackage> installed, CancellationToken cancellationToken)
    {
        var checks = await BoundedFanOut.SelectAsync(installed, async (tool, token) =>
        {
            var (latest, fetch) = await PipSource.LatestOnPyPiAsync(httpClient, Logging, Id, tool.Id, token);
            var row = Pep440.IsNewer(tool.Installed, latest)
                ? CreateItem(tool.Id, tool.Name, tool.Installed, latest)
                : null;
            return (Row: row, Fetch: fetch);
        }, cancellationToken);

        RegistryFetch.ThrowIfAllFailed(Id, checks.Select(check => check.Fetch));

        return checks.Where(check => check.Row is not null).Select(check => check.Row!).ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["tool", "upgrade", item.Id];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["tool", "uninstall", item.Id];
}
