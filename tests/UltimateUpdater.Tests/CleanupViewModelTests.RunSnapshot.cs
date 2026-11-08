using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    /// <summary>Records what each clean was handed and deletes nothing.</summary>
    private sealed class RecordingCleanupService(LoggingService logging) : CleanupService(logging)
    {
        public List<string[]> PythonPathsPerCall { get; } = [];

        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
            Task.FromResult(new CleanupScanResult(0, 0, 0));

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
            lock (PythonPathsPerCall)
            {
                if (target.Kind == CleanupKind.OldPythonVersions)
                    PythonPathsPerCall.Add([.. selectedPythonVersionPaths ?? []]);
            }

            return Task.FromResult(new CleanupOutcome(0, 0, 0));
        }
    }

    [Fact]
    public async Task A_candidate_ticked_after_the_run_started_is_not_cleaned()
    {
        var logging = new LoggingService();
        var service = new RecordingCleanupService(logging);
        var settings = CreateSettings();
        settings.Current.Install.CreateRestorePoint = true;
        var viewModel = new CleanupViewModel(service, logging, settings);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.OldPythonVersions);
        item.IsScanning = false;
        item.SetOldPythonVersionCandidates([PythonCandidate("Py38", OneGb), PythonCandidate("Py39", 2 * OneGb)]);
        SelectOnly(viewModel, item);
        item.OldPythonVersionCandidates[0].IsSelected = true;

        var timestamp = Path.Combine(AppPaths.DataDirectory, "restore-point.json");
        if (File.Exists(timestamp))
            File.Delete(timestamp);

        // The restore point is made after the run was confirmed and before the Python row starts: the
        // window in which the detail lists used to be ticked unnoticed.
        var lockedDuringRun = false;
        viewModel.CreateRestorePointOverride = _ =>
        {
            lockedDuringRun = !viewModel.CanEditDetails;
            item.OldPythonVersionCandidates[1].IsSelected = true;
            return Task.FromResult(false);
        };

        await viewModel.RunCleanupAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(lockedDuringRun);
        Assert.True(viewModel.CanEditDetails);
        var call = Assert.Single(service.PythonPathsPerCall);
        Assert.Equal([item.OldPythonVersionCandidates[0].PythonExe], call);
    }
}
