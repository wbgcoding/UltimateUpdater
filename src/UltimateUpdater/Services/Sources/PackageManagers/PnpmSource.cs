using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// Global packages via pnpm, instead of npm. `pnpm outdated -g --format json` prints an object keyed by
/// package name with `current`/`latest` string fields, the shape <see cref="PackageManagerParsers.ParseNpmJson"/>
/// already reads for npm, so that parser is reused as-is. The global packages of pnpm ARE npm registry packages,
/// so the same registry enrichment npm uses applies unchanged too.
/// Exit code contract (see <see cref="BuildScanResult"/>): 0 is a clean answer; 1 is an answer only when
/// stdout is an outdated object without an `error` member (pnpm exits 1 for outdated packages, and for
/// every error); the refusal to run global commands before `pnpm setup` is a machine with no global
/// packages to check, so zero rows; anything else fails the scan.
/// </summary>
public sealed class PnpmSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "pnpm";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "pnpm";

    /// <summary>pnpm's global packages are npm registry packages: same lookup npm's own row uses.</summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
        NpmEnrichment.FetchAsync(httpClient, Logging, Id, item.Id, item.AvailableVersion, item.Id, cancellationToken);

    /// <summary>Only 0 is accepted outright; exit 1 is decided by what stdout holds, see <see cref="BuildScanResult"/>.</summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => exitCode == 0;

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken) =>
        BuildScanResult(await RunAsync(["outdated", "-g", "--format", "json"], cancellationToken));

    /// <summary>pnpm refuses global commands until its global bin directory exists and is on PATH.</summary>
    private static readonly string[] GlobalBinRefusals = ["ERR_PNPM_NO_GLOBAL_BIN_DIR", "ERR_PNPM_GLOBAL_BIN_DIR_NOT_IN_PATH"];

    /// <summary>
    /// Split out of <see cref="ScanCoreAsync"/> and left internal so a test can feed a
    /// <see cref="ProcessResult"/> straight in. A timeout fails the scan. The global-bin-directory refusal
    /// (pnpm without `pnpm setup`) means there are no global packages to check: zero rows. Exit 0 is parsed.
    /// Exit 1 is parsed only when stdout is a JSON object without a top level `error` member, because pnpm
    /// reports its errors with exit 1 too; any other exit 1, and every other code, fails the scan.
    /// </summary>
    internal IReadOnlyList<UpdateItem> BuildScanResult(ProcessResult result)
    {
        if (result.TimedOut)
            EnsureScanCompleted(result);

        if (GlobalBinRefusals.Any(code =>
                result.StandardOutput.Contains(code, StringComparison.Ordinal)
                || result.StandardError.Contains(code, StringComparison.Ordinal)))
        {
            Logging.Info("pnpm: global bin directory not set up; no global packages to check");
            return [];
        }

        if (result.ExitCode != 0 && !(result.ExitCode == 1 && IsOutdatedObject(result.StandardOutput)))
            EnsureScanCompleted(result);

        return PackageManagerParsers.ParseNpmJson(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    private static bool IsOutdatedObject(string stdout)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(stdout);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && !document.RootElement.TryGetProperty("error", out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => UpgradeArguments(item.Id, item.AvailableVersion);

    /// <summary>
    /// `pnpm update -g` stays inside the range recorded for the global package, while the scan offers
    /// `latest`; adding the exact version applies a new major too. A version that is not a plain release
    /// number keeps the in-range update.
    /// </summary>
    internal static IReadOnlyList<string> UpgradeArguments(string packageId, string? version) =>
        IsPlainVersion(version) ? ["add", "-g", $"{packageId}@{version}"] : ["update", "-g", packageId];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["uninstall", "-g", item.Id];
}
