#if DEBUG
using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Review;

/// <summary>Update channel that never reaches the network: the review build must not contact anything.</summary>
internal sealed class OfflineUpdateChannel : IUpdateChannel
{
    public bool IsAvailable => false;

    public Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<bool> DownloadAsync(
        HttpClient client, string url, string targetPath, long maxBytes, CancellationToken cancellationToken,
        IProgress<(long Read, long? Total)>? progress = null) => Task.FromResult(false);
}

/// <summary>A source that finds nothing, for the "everything is up to date" state of a real scan.</summary>
internal sealed class EmptyReviewSource(string id) : IUpdateSource
{
    public string Id => id;

    public UpdateCategory Category => UpdateCategory.Software;

    public SourceTier Tier => SourceTier.A;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async IAsyncEnumerable<UpdateItem> ScanAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The review build installs nothing.");
}

/// <summary>A source whose scan never ends on its own, so the scanning state can be shot and then stopped.</summary>
internal sealed class BlockingReviewSource(string id) : IUpdateSource
{
    public string Id => id;

    public UpdateCategory Category => UpdateCategory.Software;

    public SourceTier Tier => SourceTier.A;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async IAsyncEnumerable<UpdateItem> ScanAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The review build installs nothing.");
}

/// <summary>Measures and cleans nothing: every target answers at once with a fixed, varied size.</summary>
internal sealed class ReviewCleanupService(LoggingService logging, TimeSpan cleanDelay) : CleanupService(logging)
{
    public override Task<CleanupScanResult> ScanAsync(
        CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
    {
        var seed = Math.Abs(target.Id.Aggregate(7, (hash, c) => hash * 31 + c)) % 9;
        return Task.FromResult(seed % 3 == 0
            ? new CleanupScanResult(0, 0, 0)
            : new CleanupScanResult((seed + 1) * 61L * 1024 * 1024, 40 * (seed + 1), 3 + seed));
    }

    public override Task<CleanupOutcome> CleanAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
        PauseGate? pause = null, Models.Settings.RegistryCleanupSettings? registrySettings = null,
        IReadOnlyCollection<string>? selectedLeftoverPaths = null,
        IReadOnlyList<RegistryFinding>? registryFindings = null,
        IReadOnlyCollection<string>? excludedEntryPaths = null,
        bool recycleInsteadOfDelete = false,
        int recycleBinMinAgeDays = 0,
        IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
        IReadOnlyCollection<string>? selectedDriverPackageNames = null,
        IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
        IReadOnlyCollection<string>? confirmedEntryPaths = null) =>
        FakeCleanAsync(target, cancellationToken);

    private async Task<CleanupOutcome> FakeCleanAsync(CleanupTarget target, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(cleanDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new CleanupOutcome(0, 0, 0, WasStopped: true);
        }

        var scanned = await ScanAsync(target, cancellationToken);
        return new CleanupOutcome(scanned.Bytes, scanned.Files, 0);
    }
}

/// <summary>An uninstaller that lists a fixed inventory and removes nothing.</summary>
internal sealed class ReviewUninstallerBackend(IReadOnlyList<UninstallListing> listings) : IUninstallerBackend
{
    public Task<IReadOnlyList<UninstallListing>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(listings);

    public UninstallRunner CreateRunner(Func<InstalledProgramEntry, UninstallPlan> plan) =>
        throw new NotSupportedException("The review build uninstalls nothing.");

    public Task<IReadOnlyList<UninstallLeftoverGroup>> FindLeftoversAsync(
        IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UninstallLeftoverGroup>>([]);

    public Task<UninstallLeftoverResult> RemoveLeftoversAsync(
        IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken) =>
        Task.FromResult(new UninstallLeftoverResult(0, 0, 0));

    public UninstallBackupMode BackupMode => UninstallBackupMode.Off;

    public Task<UninstallRestorePointResult> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new UninstallRestorePointResult(UninstallRestorePointOutcome.Created));

    public void RecordRemoved(InstalledProgramEntry entry)
    {
    }

    /// <summary>A flat tile in a colour taken from the name, for every program but the ones named "Legacy", so the shots show both the icon and the symbol fallback. Built from raw pixels: safe off the UI thread.</summary>
    public System.Windows.Media.Imaging.BitmapSource? ResolveIcon(InstalledProgramEntry entry)
    {
        if (entry.Name.StartsWith("Legacy", StringComparison.OrdinalIgnoreCase))
            return null;

        const int size = 32;
        var hash = entry.Name.Aggregate(17, (acc, ch) => acc * 31 + ch);
        var red = (byte)(60 + (hash & 0x7F));
        var green = (byte)(60 + ((hash >> 7) & 0x7F));
        var blue = (byte)(60 + ((hash >> 14) & 0x7F));
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Rounded corners: leave the pixels outside a radius-6 corner transparent.
                var dx = Math.Max(0, Math.Max(6 - x, x - (size - 7)));
                var dy = Math.Max(0, Math.Max(6 - y, y - (size - 7)));
                if (dx * dx + dy * dy > 36)
                    continue;

                var at = (y * size + x) * 4;
                pixels[at] = blue;
                pixels[at + 1] = green;
                pixels[at + 2] = red;
                pixels[at + 3] = 255;
            }
        }

        var icon = System.Windows.Media.Imaging.BitmapSource.Create(
            size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, size * 4);
        icon.Freeze();
        return icon;
    }
}
#endif
