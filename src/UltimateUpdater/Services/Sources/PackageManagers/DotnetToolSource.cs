using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// .NET global tools. `dotnet tool list` reports what is installed but not what is available, so the
/// latest stable version is read from the public NuGet flat-container index (read-only, no key).
/// </summary>
public sealed class DotnetToolSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    private const string FlatContainerFormat = "https://api.nuget.org/v3-flatcontainer/{0}/index.json";

    public override string Id => "dotnet-tool";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "dotnet";

    /// <summary>
    /// `dotnet tool list` reports neither a publish date nor a size. The date comes from the NuGet
    /// registration leaf for the exact version; the registration leaf carries no size, so that comes from
    /// a HEAD on the flat-container.nupkg itself (the SAME container <see cref="TryGetLatestStableAsync"/>
    /// already reads the version index from); its Content-Length is the exact download size. Both
    /// read-only, no key, and independent of each other: either can fail without losing the other.
    /// </summary>
    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || !ProcessRunner.IsSafePositionalArgument(item.Id))
            return null;

        var id = Uri.EscapeDataString(item.Id.ToLowerInvariant());
        var version = Uri.EscapeDataString(item.AvailableVersion.ToLowerInvariant());

        var registrationUrl = $"https://api.nuget.org/v3/registration5-semver1/{id}/{version}.json";
        var json = await RegistryEnrichment.FetchRawAsync(httpClient, registrationUrl, Logging, Id, item.Id, cancellationToken);
        var enrichment = json is null ? default : PackageMetadataParsers.ParseNuGetPublished(json);

        var nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";
        var size = await RegistryEnrichment.HeadContentLengthAsync(nupkgUrl, Logging, Id, item.Id, cancellationToken);
        if (size is not null)
            enrichment = enrichment with { DownloadSize = size };

        // The package page needs no lookup because id and version are already known, so it is attached even when
        // the registration call above failed or carried no releaseNotes text of its own.
        var pageUrl = $"https://www.nuget.org/packages/{Uri.EscapeDataString(item.Id)}/{Uri.EscapeDataString(item.AvailableVersion)}";
        return enrichment with { ChangelogUrl = enrichment.ChangelogUrl ?? pageUrl };
    }

    /// <summary>
    /// `dotnet` on PATH is not enough: a machine with only the runtime has the host but no SDK, and then
    /// every SDK command exits with a hostfxr code. The probe is the SDK-resolving `--version`; a probe
    /// that merely times out or fails some other way counts as present, so a real problem still surfaces
    /// in the scan instead of hiding the source.
    /// </summary>
    public override async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!ProcessRunner.Exists(Executable))
            return false;

        var probe = await ProcessRunner.RunAsync(Executable, ["--version"], PresenceProbeTimeout, cancellationToken);
        return probe.TimedOut || !IsSdkMissing(probe);
    }

    private static readonly TimeSpan PresenceProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The host could not find an SDK. hostfxr reports it as 0x8000809B (SdkResolverResolveFailure) or
    /// 0x80008091 (LibHostSdkFindFailure); its message carries the same fact, matched case-insensitively
    /// in case a host version reports another code.
    /// </summary>
    internal static bool IsSdkMissing(ProcessResult result)
    {
        if (result.ExitCode is unchecked((int)0x8000809B) or unchecked((int)0x80008091))
            return true;

        return result.ExitCode != 0
            && (ContainsSdkMissingText(result.StandardError) || ContainsSdkMissingText(result.StandardOutput));
    }

    private static bool ContainsSdkMissingText(string? text) =>
        text is not null
        && (text.Contains("find any installed .NET SDK", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No .NET SDKs were found", StringComparison.OrdinalIgnoreCase));

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken) =>
        await ScanFromResultAsync(await RunAsync(["tool", "list", "--global"], cancellationToken), cancellationToken);

    /// <summary>The scan after `dotnet tool list` ran, split out so a test can hand in the tool's result.</summary>
    internal async Task<IReadOnlyList<UpdateItem>> ScanFromResultAsync(ProcessResult listed, CancellationToken cancellationToken)
    {
        // The probe in IsAvailableAsync normally catches this first; an SDK removed mid-session lands here.
        if (IsSdkMissing(listed))
        {
            Logging.Info($"{Id}: the .NET SDK is not installed, nothing to check.");
            return [];
        }

        var result = EnsureScanCompleted(listed);

        // One NuGet lookup per installed tool: fire them together rather than one after another, so a
        // machine with a dozen global tools is not a dozen sequential round-trips.
        return await CheckAgainstNuGetAsync(PackageManagerParsers.ParseDotnetToolList(result.StandardOutput), cancellationToken);
    }

    /// <summary>The NuGet half of the scan, split out so a test can hand in the installed list and a fake
    /// client. Every lookup failing is a failed source (see <see cref="RegistryFetch.ThrowIfAllFailed"/>).</summary>
    internal async Task<IReadOnlyList<UpdateItem>> CheckAgainstNuGetAsync(
        IReadOnlyList<OutdatedPackage> installedTools, CancellationToken cancellationToken)
    {
        var checks = await BoundedFanOut.SelectAsync(installedTools, async (installed, token) =>
        {
            var (latest, fetch) = await TryGetLatestStableAsync(installed.Id, token);
            var row = PackageManagerParsers.IsNewer(installed.Installed, latest)
                ? CreateItem(installed.Id, installed.Name, installed.Installed, latest)
                : null;
            return (Row: row, Fetch: fetch);
        }, cancellationToken);

        RegistryFetch.ThrowIfAllFailed(Id, checks.Select(check => check.Fetch));

        return checks.Where(check => check.Row is not null).Select(check => check.Row!).ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) =>
        ["tool", "update", "--global", item.Id];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        ["tool", "uninstall", "--global", item.Id];

    private async Task<(string? Latest, RegistryFetch? Fetch)> TryGetLatestStableAsync(string packageId, CancellationToken cancellationToken)
    {
        var url = string.Format(FlatContainerFormat, Uri.EscapeDataString(packageId.ToLowerInvariant()));

        // A slow request surfaces as a failed outcome carrying a TimeoutException, never as a cancellation:
        // rethrowing that would abort the whole provider and read as "checked, nothing found".
        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(httpClient, url, Logging, Id, packageId, cancellationToken);

        return (fetch.Body is null ? null : PackageManagerParsers.ParseNuGetLatestStable(fetch.Body), fetch);
    }
}
