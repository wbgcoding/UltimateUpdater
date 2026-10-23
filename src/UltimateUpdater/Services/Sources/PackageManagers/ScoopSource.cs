using System.IO;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>Scoop. Per-user by design, so nothing here needs admin.</summary>
public sealed class ScoopSource(LoggingService logging) : CliUpdateSource(logging)
{
    public override string Id => "scoop";

    public override UpdateCategory Category => UpdateCategory.Software;

    protected override string Executable => "scoop";

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        // refresh the bucket manifests first, otherwise `status` compares against stale data
        var refresh = await RunAsync(["update"], cancellationToken);
        if (refresh.TimedOut || refresh.ExitCode != 0)
            Logging.Warn($"{Id}: bucket refresh did not complete cleanly; results may be stale.");

        var result = EnsureScanCompleted(await RunAsync(["status"], cancellationToken));

        return PackageManagerParsers.ParseScoopStatus(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["update", item.Id];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["uninstall", item.Id];

    /// <summary>
    /// `scoop status` carries no size. `scoop cat {id}` prints the app's own manifest, plain JSON,
    /// resolved from whichever bucket it actually came from by scoop itself, so there is no bucket
    /// guessing here, and its download URL is HEAD'd for size. No date: a bucket's git-commit time is
    /// not a real publish date (verified against the manifest schema; the manifest itself carries none
    /// either).
    /// </summary>
    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(item.Id))
            return null;

        try
        {
            var result = await RunAsync(["cat", item.Id], cancellationToken);
            if (result.TimedOut || result.ExitCode != 0)
                return null;

            var preferArm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64;
            var url = PackageMetadataParsers.ParseScoopManifestDownloadUrl(result.StandardOutput, preferArm64);
            var homepage = PackageMetadataParsers.ParseScoopManifestHomepage(result.StandardOutput);
            if (url is null && homepage is null)
                return null;

            var size = url is null
                ? null
                : await RegistryEnrichment.HeadContentLengthAsync(url, Logging, Id, item.Id, cancellationToken);
            return size is null && homepage is null ? null : new UpdateEnrichment(null, size, ChangelogUrl: homepage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Logging.Warn($"{Id}: enrichment for '{item.Id}' failed: {ex.Message}");
            return null;
        }
    }
}
