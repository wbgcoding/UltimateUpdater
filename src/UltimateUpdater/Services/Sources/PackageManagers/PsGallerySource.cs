using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>PowerShell modules from the PSGallery. Driven through one PowerShell call that emits JSON.</summary>
public sealed class PsGallerySource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    /// <summary>
    /// The PSGallery OData feed, asked for exactly one package's current release. `Id eq '..'` rather
    /// than the shorter `FindPackagesById()`, which matches by prefix: asking it for "PSReadLine" also
    /// returns PSReadlineHistory, PSReadlineHelper and Theme.PSReadLine (verified live), and
    /// a foreign package's version number would have become this module's "available" one.
    /// </summary>
    private const string LatestVersionUrlFormat =
        "https://www.powershellgallery.com/api/v2/Packages()?$filter=Id%20eq%20'{0}'%20and%20IsLatestVersion&$select=Version";

    // Only modules in the user's own scope: an AllUsers module cannot be updated by the unelevated
    // in-process batch, so listing it would only ever produce an access-denied failure.
    // Listing is ALL this asks PowerShell for. Resolving the newest version via `Find-Module` per module
    // would drag in PackageManagement's NuGet provider, when that provider has to be bootstrapped and the
    // download fails, the error is raised past -ErrorAction SilentlyContinue, $ErrorActionPreference='Stop'
    // aborts the whole script, and the source reports nothing but "exited with code 1". The gallery's own
    // web feed answers the same question over plain HTTP instead, with no provider and no bootstrap in the
    // way: the same shape DotnetToolSource already uses for NuGet.
    private const string ScanScript =
        "$ErrorActionPreference='Stop'; " +
        "Get-InstalledModule | Where-Object { $_.InstalledLocation -and " +
        "  $_.InstalledLocation.StartsWith($env:USERPROFILE, [System.StringComparison]::OrdinalIgnoreCase) } | " +
        "ForEach-Object { [pscustomobject]@{ Name=$_.Name; Installed=$_.Version.ToString() } } | " +
        "ConvertTo-Json -Compress";

    public override string Id => "psgallery";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "powershell";

    protected override TimeSpan ScanTimeout => TimeSpan.FromMinutes(5);

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(
            await RunAsync(["-NoProfile", "-NonInteractive", "-Command", ScanScript], cancellationToken));

        return await CheckAgainstGalleryAsync(PackageManagerParsers.ParsePsGalleryInstalledJson(result.StandardOutput), cancellationToken);
    }

    /// <summary>
    /// One gallery lookup per installed module, fired together rather than one after another, the same
    /// shape DotnetToolSource uses, for the same reason. Split out so a test can hand in the installed list
    /// and a fake client. Every lookup failing is a failed source (see <see cref="RegistryFetch.ThrowIfAllFailed"/>).
    /// </summary>
    internal async Task<IReadOnlyList<UpdateItem>> CheckAgainstGalleryAsync(
        IReadOnlyList<OutdatedPackage> installedModules, CancellationToken cancellationToken)
    {
        var checks = await BoundedFanOut.SelectAsync(installedModules, async (installed, token) =>
        {
            var (latest, fetch) = await TryGetLatestAsync(installed.Id, token);
            var row = PackageManagerParsers.IsNewer(installed.Installed, latest)
                ? CreateItem(installed.Id, installed.Name, installed.Installed, latest)
                : null;
            return (Row: row, Fetch: fetch);
        }, cancellationToken);

        RegistryFetch.ThrowIfAllFailed(Id, checks.Select(check => check.Fetch));

        return checks.Where(check => check.Row is not null).Select(check => check.Row!).ToList();
    }

    private async Task<(string? Latest, RegistryFetch? Fetch)> TryGetLatestAsync(string moduleName, CancellationToken cancellationToken)
    {
        // A module name comes from the local machine, but it still ends up inside a URL, and the same
        // guard every other source applies to a package id applies here.
        if (!ProcessRunner.IsSafePositionalArgument(moduleName))
            return (null, null);

        var url = string.Format(LatestVersionUrlFormat, Uri.EscapeDataString(moduleName));

        // A slow request is a failed outcome carrying a TimeoutException, never a cancellation: rethrowing
        // that would abort the whole provider and read as "checked, nothing found".
        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(httpClient, url, Logging, Id, moduleName, cancellationToken);

        return (fetch.Body is null ? null : PackageManagerParsers.ParseODataLatestVersion(fetch.Body), fetch);
    }

    /// <summary>
    /// PSGallery's `/api/v2/Packages(Id='..',Version='..')` is the same NuGet OData v2 feed shape as
    /// Chocolatey's community feed (verified against a live response: same Published/PackageSize
    /// element names). reusing that parser rather than duplicating it.
    /// </summary>
    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || !ProcessRunner.IsSafePositionalArgument(item.Id))
            return null;

        var url = $"https://www.powershellgallery.com/api/v2/Packages(Id='{Uri.EscapeDataString(item.Id)}',Version='{Uri.EscapeDataString(item.AvailableVersion)}')";
        var enrichment = await RegistryEnrichment.FetchAsync(
            httpClient, url, PackageMetadataParsers.ParseChocolateyODataMeta, Logging, Id, item.Id, cancellationToken);

        // The gallery page needs no lookup (id/version are already known) so it is attached even when
        // the OData call above failed or carried no ReleaseNotes text of its own.
        var pageUrl = $"https://www.powershellgallery.com/packages/{Uri.EscapeDataString(item.Id)}/{Uri.EscapeDataString(item.AvailableVersion)}";
        return (enrichment ?? default) with { ChangelogUrl = enrichment?.ChangelogUrl ?? pageUrl };
    }

    // The module name reaches PowerShell as an environment variable, never interpolated into the
    // script text, so a hostile package name cannot become executable code.
    private const string InstallScript =
        "$ErrorActionPreference='Stop'; Update-Module -Name $env:WU_MODULE_NAME -Force";

    private const string UninstallScript =
        "$ErrorActionPreference='Stop'; Uninstall-Module -Name $env:WU_MODULE_NAME -AllVersions -Force";

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) =>
        ["-NoProfile", "-NonInteractive", "-Command", InstallScript];

    public override Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken) =>
        RunModuleScriptAsync(item, InstallScript, "install", progress, cancellationToken);

    public override bool CanUninstall(UpdateItem item) => true;

    public override Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken) =>
        RunModuleScriptAsync(item, UninstallScript, "uninstall", progress, cancellationToken);

    private async Task RunModuleScriptAsync(
        UpdateItem item, string script, string action, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunAsync(
            Executable,
            ["-NoProfile", "-NonInteractive", "-Command", script],
            InstallTimeout,
            cancellationToken,
            new Dictionary<string, string> { ["WU_MODULE_NAME"] = item.Id });

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: {action} timed out for '{item.Id}'.");

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{Id}: {action} failed for '{item.Id}' (exit {result.ExitCode}).");

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }
}
