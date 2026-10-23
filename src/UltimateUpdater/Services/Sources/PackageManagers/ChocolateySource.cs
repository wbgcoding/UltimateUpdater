using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>Chocolatey. Machine-wide, so its installs run in the elevated worker.</summary>
public sealed class ChocolateySource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "chocolatey";

    public override UpdateCategory Category => UpdateCategory.Software;

    protected override string Executable => "choco";

    /// <summary>
    /// `choco outdated` carries no date or size, so both are read afterwards from the Chocolatey
    /// community feed's OData entry for the exact version; read-only, no key. Fails closed.
    /// </summary>
    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || !ProcessRunner.IsSafePositionalArgument(item.Id))
            return null;

        var url = $"https://community.chocolatey.org/api/v2/Packages(Id='{Uri.EscapeDataString(item.Id)}',Version='{Uri.EscapeDataString(item.AvailableVersion)}')";
        var enrichment = await RegistryEnrichment.FetchAsync(
            httpClient, url, PackageMetadataParsers.ParseChocolateyODataMeta, Logging, Id, item.Id, cancellationToken);

        // The package page needs no lookup (id/version are already known) so it is attached even when
        // the OData call above failed or carried no ReleaseNotes text of its own.
        var pageUrl = $"https://community.chocolatey.org/packages/{Uri.EscapeDataString(item.Id)}/{Uri.EscapeDataString(item.AvailableVersion)}";
        return (enrichment ?? default) with { ChangelogUrl = enrichment?.ChangelogUrl ?? pageUrl };
    }

    /// <summary>`choco outdated` documents 2 as "outdated packages found"; its success case, not a failure.</summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => exitCode is 0 or 2;

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        // `choco outdated` exits 2 when it finds outdated packages, so that code says nothing about
        // success, but anything else does and must not be swallowed (see EnsureScanCompleted).
        var result = EnsureScanCompleted(await RunAsync(["outdated", "--limit-output"], cancellationToken));

        return PackageManagerParsers.ParseChocolatey(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    /// <summary>The re-check: one exact, local-only lookup, no network.</summary>
    protected override IReadOnlyList<string>? VersionCheckArguments(string id) => ListArguments(id, _isVersion1);

    /// <summary>
    /// Chocolatey 1.x `list` asks the remote feed unless told `--local-only`, and its re-check would then
    /// report the feed's version as the installed one; 2.x lists local packages only and has removed that
    /// option, so it is added for 1.x alone.
    /// </summary>
    internal static IReadOnlyList<string> ListArguments(string id, bool version1) =>
        version1 ? ["list", id, "--exact", "--limit-output", "--local-only"] : ["list", id, "--exact", "--limit-output"];

    private volatile bool _isVersion1;
    private volatile bool _versionKnown;

    public override async Task<string?> GetInstalledVersionAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (!_versionKnown && await ReadMajorVersionAsync(cancellationToken) is { } major)
        {
            _isVersion1 = major < 2;
            _versionKnown = true;
        }

        return await base.GetInstalledVersionAsync(item, cancellationToken);
    }

    /// <summary>`choco --version` prints the bare version ("1.4.0", "2.2.2"); null when it cannot be read,
    /// which keeps the 2.x form.</summary>
    private async Task<int?> ReadMajorVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(Executable, ["--version"], ExternalCallTimeouts.Default, cancellationToken);
            return result.TimedOut || result.ExitCode != 0 ? null : ParseMajorVersion(result.StandardOutput);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            Logging.Warn($"{Id}: could not read the Chocolatey version: {ex.Message}");
            return null;
        }
    }

    internal static int? ParseMajorVersion(string stdout) =>
        PackageManagerParsers.FirstVersionToken(stdout) is { } version
        && int.TryParse(version.Split('.')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            ? major
            : null;

    protected override string? ParseInstalledVersion(string stdout, string id) =>
        PackageManagerParsers.ParseChocolateyExactVersion(stdout, id);

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => UpgradeArguments(item.Id);

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        ["uninstall", item.Id, "-y", "--limit-output"];

    /// <summary>
    /// Chocolatey installs silently unless it is told otherwise, and that silence is what stops the
    /// packaged installer from opening the application it just updated; --not-silent must therefore
    /// never appear here. -y and --accept-license answer the two dialogs it would otherwise wait on,
    /// and --no-progress keeps the download bar out of the captured output.
    /// </summary>
    internal static IReadOnlyList<string> UpgradeArguments(string packageId) =>
        ["upgrade", packageId, "-y", "--accept-license", "--limit-output", "--no-progress"];
}
