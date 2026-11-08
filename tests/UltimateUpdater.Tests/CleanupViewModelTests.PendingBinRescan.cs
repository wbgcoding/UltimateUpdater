using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    /// <summary>Holds the recycle bin's first measurement until released and records every age it was
    /// asked to apply.</summary>
    private sealed class HeldBinScanService(LoggingService logging) : CleanupService(logging)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _binScans;

        public TaskCompletionSource FirstBinScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> BinAges { get; } = [];

        public void Release() => _release.TrySetResult();

        public override async Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            if (target.Kind != CleanupKind.RecycleBin)
                return new CleanupScanResult(0, 0, 0);

            bool first;
            lock (BinAges)
            {
                BinAges.Add(recycleBinMinAgeDays);
                first = _binScans++ == 0;
            }

            if (first)
            {
                FirstBinScanStarted.TrySetResult();
                await _release.Task;
            }

            return new CleanupScanResult(0, 0, 0);
        }
    }

    [Fact]
    public async Task A_bin_age_changed_during_the_scan_is_applied_by_a_rescan_when_it_ends()
    {
        var logging = new LoggingService();
        var service = new HeldBinScanService(logging);
        var settings = CreateSettings();
        var viewModel = new CleanupViewModel(service, logging, settings);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        var bin = viewModel.Items.First(i => i.Target.Kind == CleanupKind.RecycleBin);

        var scan = viewModel.StartScanAsync();
        await service.FirstBinScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(viewModel.IsScanning);

        bin.RecycleBinMinAgeDays = 45;
        lock (service.BinAges)
            Assert.Single(service.BinAges);

        service.Release();
        await scan.WaitAsync(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 300; i++)
        {
            lock (service.BinAges)
            {
                if (service.BinAges.Count >= 2 && !bin.IsScanning)
                    break;
            }

            await Task.Delay(100);
        }

        lock (service.BinAges)
            Assert.Equal(45, service.BinAges.Last());

        Assert.False(bin.IsScanning);
        Assert.False(viewModel.IsScanning);
    }
}
