using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Components of the Google Cloud CLI. Its own listing reports the installed and the newest version of every
/// component AND the download size in bytes, so these rows carry a real size without any extra lookup.
/// There is no per-component update command, `gcloud components update` moves every installed component (and
/// the CLI itself) forward at once, so the row's id is deliberately not an argument, the same shape as rustup
/// and the Android SDK. Verified against real output on a clean VM.
/// </summary>
public sealed class GcloudComponentSource(LoggingService logging) : CliUpdateSource(logging)
{
    public override string Id => "gcloud";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "gcloud";

    /// <summary>No per-component release notes exist, the SDK's combined release notes page is attached
    /// instead, a constant with no lookup involved.</summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
        Task.FromResult<UpdateEnrichment?>(
            new UpdateEnrichment(null, null, ChangelogUrl: "https://cloud.google.com/sdk/docs/release-notes"));

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(
            await RunAsync(["components", "list", "--format=json", "--quiet"], cancellationToken));

        return PackageManagerParsers.ParseGcloudComponents(result.StandardOutput)
            .Select(component => new UpdateItem
            {
                Id = component.Id,
                SourceId = Id,
                Category = Category,
                Name = component.Name,
                InstalledVersion = component.Installed,
                AvailableVersion = component.Available,
                IsCritical = SecurityRelevance.IsSecurityRelevant(component.Id, component.Name),
                DownloadSize = component.Size,
            })
            .ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) =>
        ["components", "update", "--quiet"];

    /// <summary>Unlike the blanket `update`, `remove` names one component, `--quiet` skips its own confirmation.</summary>
    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        ["components", "remove", item.Id, "--quiet"];
}
