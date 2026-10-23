using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// VS Code extensions via the `code` CLI. Like pipx, `--list-extensions` reports every installed
/// extension rather than only outdated ones, so these rows are upgrade candidates with no known target
/// version, the install itself always asks for the latest (--force).
/// </summary>
public sealed class VsCodeExtensionSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "vscode-extension";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "code";

    /// <summary>
    /// `--list-extensions --show-versions` never says what the newest marketplace version is (like
    /// pipx, every installed extension is an upgrade CANDIDATE), the Marketplace's own gallery API
    /// answers that, plus when it last updated. Read-only, unauthenticated. No <c>ChangelogUrl</c> here:
    /// the Marketplace "properties" bag has no changelog-specific key, only source/getstarted/
    /// learn links, the same ones <see cref="PackageMetadataParsers.ParseVsMarketplaceExtension"/> already
    /// surfaces as <c>ProjectUrl</c> for the row's separate info-link button, so a changelog button here
    /// would just duplicate it.
    /// </summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (!TrySplitExtensionId(item.Id, out var publisher, out var extension))
            return Task.FromResult<UpdateEnrichment?>(null);

        return RegistryEnrichment.FetchAsync(
            httpClient, MarketplaceUrl(publisher, extension), PackageMetadataParsers.ParseVsMarketplaceExtension,
            Logging, Id, item.Id, cancellationToken);
    }

    /// <summary>
    /// `--list-extensions` says nothing about what is newest, so listing it directly would report every
    /// installed extension as a pending update whether or not it was actually behind, the same phantom-row
    /// defect this project keeps out of the table elsewhere (bun's release tag, winget's stale entries).
    /// The Marketplace is asked during the SCAN instead, in parallel, and only an extension that is
    /// genuinely behind gets a row.
    /// Fails closed: no answer from the Marketplace means no known newer version, so no row, never an
    /// assumed one.
    /// </summary>
    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["--list-extensions", "--show-versions"], cancellationToken));
        return await CheckAgainstMarketplaceAsync(PackageManagerParsers.ParseVsCodeExtensions(result.StandardOutput), cancellationToken);
    }

    /// <summary>The Marketplace half of the scan, split out so a test can hand in the installed list and a
    /// fake client. Every lookup failing is a failed source (see <see cref="RegistryFetch.ThrowIfAllFailed"/>).</summary>
    internal async Task<IReadOnlyList<UpdateItem>> CheckAgainstMarketplaceAsync(
        IReadOnlyList<OutdatedPackage> installed, CancellationToken cancellationToken)
    {
        var checks = await BoundedFanOut.SelectAsync(installed, async (extension, token) =>
        {
            var (latest, fetch) = await LatestOnMarketplaceAsync(extension.Id, token);
            var row = PackageManagerParsers.IsNewer(extension.Installed, latest)
                ? CreateItem(extension.Id, extension.Name, extension.Installed, latest)
                : null;
            return (Row: row, Fetch: fetch);
        }, cancellationToken);

        RegistryFetch.ThrowIfAllFailed(Id, checks.Select(check => check.Fetch));

        return checks.Where(check => check.Row is not null).Select(check => check.Row!).ToList();
    }

    /// <summary>The one place the Marketplace address is spelled out, scan and enrichment share it.</summary>
    private static string MarketplaceUrl(string publisher, string extension) =>
        "https://marketplace.visualstudio.com/_apis/public/gallery/publishers/"
        + $"{Uri.EscapeDataString(publisher)}/extensions/{Uri.EscapeDataString(extension)}?api-version=3.0-preview.1";

    private async Task<(string? Latest, RegistryFetch? Fetch)> LatestOnMarketplaceAsync(string extensionId, CancellationToken cancellationToken)
    {
        if (!TrySplitExtensionId(extensionId, out var publisher, out var extension))
            return (null, null);

        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(
            httpClient, MarketplaceUrl(publisher, extension), Logging, Id, extensionId, cancellationToken);

        return (fetch.Body is null ? null : PackageMetadataParsers.ParseVsMarketplaceExtension(fetch.Body).AvailableVersion, fetch);
    }

    // --force re-installs at the latest marketplace version; without it a same-version reinstall is a no-op
    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) =>
        ["--install-extension", item.Id, "--force"];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        ["--uninstall-extension", item.Id];

    /// <summary>The id is "publisher.name", split on the FIRST dot, both halves a plain Marketplace-safe token.</summary>
    private static bool TrySplitExtensionId(string id, out string publisher, out string extension)
    {
        publisher = string.Empty;
        extension = string.Empty;

        var dot = id.IndexOf('.');
        if (dot <= 0 || dot >= id.Length - 1)
            return false;

        var publisherPart = id[..dot];
        var extensionPart = id[(dot + 1)..];
        if (!IsPlainToken(publisherPart) || !IsPlainToken(extensionPart))
            return false;

        publisher = publisherPart;
        extension = extensionPart;
        return true;
    }

    private static bool IsPlainToken(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
}
