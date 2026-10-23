using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>Composer's global packages (PHP), read as JSON so the columns are locale-independent.</summary>
public sealed class ComposerSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "composer";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "composer";

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(
            await RunAsync(["global", "outdated", "--format=json", "--no-interaction"], cancellationToken));

        return PackageManagerParsers.ParseComposerJson(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available,
                p.BeyondConstraint ? new Dictionary<string, string> { [BeyondConstraintMetaKey] = "true" } : null))
            .ToList();
    }

    /// <summary>Marks a row whose offered version lies outside the package's current constraint.</summary>
    internal const string BeyondConstraintMetaKey = "composerBeyondConstraint";

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) =>
        UpgradeArguments(item.Id, item.AvailableVersion, item.Meta.ContainsKey(BeyondConstraintMetaKey));

    /// <summary>
    /// `composer global update` respects the caret constraint in the global composer.json, so a new major
    /// the scan offers would never be applied. For those rows the constraint is moved to the offered
    /// version with `global require`, which installs it; `--update-with-dependencies` lets the package's
    /// own dependencies move along, as a new major usually needs. Every other row keeps the plain update,
    /// which leaves composer.json untouched.
    /// </summary>
    internal static IReadOnlyList<string> UpgradeArguments(string packageId, string? version, bool beyondConstraint) =>
        beyondConstraint && TildeConstraint(version) is { } constraint
            ? ["global", "require", $"{packageId}:{constraint}", "--update-with-dependencies", "--no-interaction"]
            : ["global", "update", packageId, "--no-interaction"];

    /// <summary>
    /// "~MAJOR.MINOR" for a plain release number: the same range a caret gives from MAJOR 1 on. A caret
    /// itself cannot be used, composer is a .bat on Windows and cmd.exe strips an unquoted '^' as its
    /// escape character, which would turn the range into an exact pin.
    /// </summary>
    internal static string? TildeConstraint(string? version)
    {
        if (!IsPlainVersion(version))
            return null;

        // Build metadata after '+' does not change which release this is; anything else past the digits and
        // dots ("-beta1", "RC1") makes it a prerelease. A tilde range would not resolve to that on a project
        // with stable minimum stability, so those keep the plain update.
        var core = version!.TrimStart('v', 'V').Split('+')[0];
        if (!core.All(c => char.IsAsciiDigit(c) || c == '.'))
            return null;

        var parts = core.Split('.');
        if (parts[0].Length == 0)
            return null;

        var minor = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "0";
        return $"~{parts[0]}.{minor}";
    }

    /// <summary>Mirrors <see cref="InstallArguments"/>'s exact shape; `--no-interaction` for the same reason.</summary>
    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        ["global", "remove", item.Id, "--no-interaction"];

    /// <summary>
    /// `composer global outdated` gives no date or size. `repo.packagist.org/p2/{vendor}/{pkg}.json`
    /// has both the publish time and the distributable's own URL, HEAD'd for size (verified live).
    /// Composer package ids are already "vendor/pkg", exactly what the URL needs.
    /// </summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var parts = item.Id.Split('/');
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || parts.Length != 2 ||
            parts[0].Length == 0 || parts[1].Length == 0)
        {
            return Task.FromResult<UpdateEnrichment?>(null);
        }

        return FetchAsync(item, parts[0], parts[1], cancellationToken);
    }

    private async Task<UpdateEnrichment?> FetchAsync(UpdateItem item, string vendor, string package, CancellationToken cancellationToken)
    {
        // The package's Packagist page needs no lookup; attached below regardless of whether the p2
        // call succeeds, same reasoning as Chocolatey's community page.
        var changelogUrl = $"https://packagist.org/packages/{Uri.EscapeDataString(vendor)}/{Uri.EscapeDataString(package)}";

        try
        {
            var url = $"https://repo.packagist.org/p2/{Uri.EscapeDataString(vendor)}/{Uri.EscapeDataString(package)}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new UpdateEnrichment(null, null, ChangelogUrl: changelogUrl);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var (publishDate, downloadUrl) = PackageMetadataParsers.ParsePackagistVersion(json, item.Id, item.AvailableVersion!);

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
