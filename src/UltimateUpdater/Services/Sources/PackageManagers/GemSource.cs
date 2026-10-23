using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>RubyGems. `gem` writes into Ruby's own gem directory; setups vary, so it runs in-process.</summary>
public sealed class GemSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "gem";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "gem";

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["outdated"], cancellationToken));

        return PackageManagerParsers.ParseGemOutdated(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["update", item.Id];

    /// <summary>
    /// `--all` and `--executables` both avoid an interactive prompt: without `--all` gem asks which
    /// installed version to remove whenever more than one is present, and without `--executables` it asks
    /// whether to also remove the gem's bundled executables; either question hangs the process until
    /// `ProcessRunner`'s timeout, since nothing is there to answer it.
    /// </summary>
    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        ["uninstall", item.Id, "--all", "--executables"];

    /// <summary>
    /// `gem` gives no date or size itself. `rubygems.org/api/v1/versions/{gem}.json` has the publish
    /// date; it reports no size, so `rubygems.org/downloads/{gem}-{version}.gem`: the gem's own,
    /// well-known download URL; is HEAD'd separately for that (both verified live).
    /// </summary>
    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || !ProcessRunner.IsSafePositionalArgument(item.Id))
            return null;

        // The gem's own page needs no lookup: it is attached below regardless of whether the version
        // API call succeeds, same reasoning as Chocolatey's community page.
        var changelogUrl = $"https://rubygems.org/gems/{Uri.EscapeDataString(item.Id)}";

        try
        {
            var listUrl = $"https://rubygems.org/api/v1/versions/{Uri.EscapeDataString(item.Id)}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new UpdateEnrichment(null, null, ChangelogUrl: changelogUrl);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var publishDate = PackageMetadataParsers.ParseRubyGemsVersionDate(json, item.AvailableVersion);

            var downloadUrl = $"https://rubygems.org/downloads/{Uri.EscapeDataString(item.Id)}-{Uri.EscapeDataString(item.AvailableVersion)}.gem";
            var size = await RegistryEnrichment.HeadContentLengthAsync(
                downloadUrl, Logging, Id, item.Id, cancellationToken);

            return new UpdateEnrichment(publishDate, size, ChangelogUrl: changelogUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            Logging.Warn($"{Id}: enrichment for '{item.Id}' failed: {ex.Message}");
            return new UpdateEnrichment(null, null, ChangelogUrl: changelogUrl);
        }
    }
}
