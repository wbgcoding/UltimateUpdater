using System.Collections.Concurrent;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>A run must start exactly once however often it is asked while a scan unwinds, and a scan that
/// was superseded by a newer one must not write its numbers or its "finished" state over the newer one.</summary>
public class CleanupRunScanRaceTests
{
    /// <summary>Scans block on a gate and ignore cancellation, the way a slow directory walk does; the first
    /// scan of each target answers 111 bytes, any later one 222. Cleaning never touches the disk.</summary>
    private sealed class GatedService(LoggingService logging) : CleanupService(logging)
    {
        public readonly TaskCompletionSource FirstScanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LaterScanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _scans = new();
        public int CleanCalls;

        public override async Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            var number = _scans.AddOrUpdate(target.Id, 1, (_, n) => n + 1);
            if (number == 1)
            {
                await FirstScanGate.Task;
                return new CleanupScanResult(111, 1, 0);
            }

            await LaterScanGate.Task;
            return new CleanupScanResult(222, 2, 0);
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
            IReadOnlyCollection<string>? confirmedEntryPaths = null)
        {
            Interlocked.Increment(ref CleanCalls);
            return Task.FromResult(new CleanupOutcome(0, 0, 0));
        }
    }

    private static CleanupViewModel Create(GatedService service)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-cleanup-race-").FullName, "settings.json"));
        var history = new CleanupHistoryService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-cleanup-race-history-").FullName, "history.json"));
        var viewModel = new CleanupViewModel(service, logging, settings, history: history);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        return viewModel;
    }

    [Fact]
    public async Task Asking_for_a_run_twice_while_a_scan_unwinds_starts_one_run()
    {
        var service = new GatedService(new LoggingService());
        var viewModel = Create(service);

        // Only a plain directory target stays selected: no restore point, no elevation, and the fake
        // service never deletes anything anyway.
        var only = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories && !i.Target.NeedsElevation);
        foreach (var item in viewModel.Items)
            item.IsSelected = ReferenceEquals(item, only);

        var scan = viewModel.StartScanAsync();
        var first = viewModel.RunCleanupAsync();
        var second = viewModel.RunCleanupAsync();

        // The scan finishes only now, so both calls were waiting on it when they were made.
        service.FirstScanGate.SetResult();
        await Task.WhenAll(scan, first, second).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, service.CleanCalls);
    }

    [Fact]
    public async Task A_superseded_scan_keeps_its_numbers_and_its_finished_state_to_itself()
    {
        var service = new GatedService(new LoggingService());
        var viewModel = Create(service);
        var row = viewModel.Items.First(i => !i.Target.NeedsElevation);

        var older = viewModel.StartScanAsync();
        var newer = viewModel.StartScanAsync();

        // The older scan ends first and answers 111 for everything it measured.
        service.FirstScanGate.SetResult();
        await older.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(viewModel.IsScanning, "the older scan's end switched the newer scan's spinner off");
        Assert.NotEqual(111, row.Bytes);

        service.LaterScanGate.SetResult();
        await newer.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(viewModel.IsScanning);
        Assert.Equal(222, row.Bytes);
    }
}
