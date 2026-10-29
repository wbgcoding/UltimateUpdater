using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The freed figure of a cleanup run climbs while a target works, from throttled progress, and still ends
/// on exactly the sum of the finished targets' outcomes.
/// </summary>
[Collection("UiLanguage")]
public class CleanupLiveFreedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-live-freed-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class ListProgress : IProgress<CleanupProgress>
    {
        public List<CleanupProgress> Reports { get; } = [];

        public void Report(CleanupProgress value) => Reports.Add(value);
    }

    private CleanupTarget TempFolderWithFiles(int count, int bytesEach)
    {
        var folder = Path.Combine(_root, "files");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(folder, $"f{i}.tmp"), new byte[bytesEach]);

        return new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [folder],
        };
    }

    [Fact]
    public async Task A_file_pass_reports_what_it_has_freed_before_it_ends()
    {
        var target = TempFolderWithFiles(20, 1000);
        var progress = new ListProgress();
        var service = new CleanupService(new LoggingService()) { ProgressInterval = TimeSpan.Zero };

        var outcome = await service.CleanAsync(target, progress, CancellationToken.None);

        Assert.Equal(20_000, outcome.FreedBytes);
        Assert.NotEmpty(progress.Reports);
        var figures = progress.Reports.Select(r => r.FreedBytes).ToList();
        Assert.Equal(figures.OrderBy(x => x), figures);
        Assert.Contains(figures, f => f > 0 && f < outcome.FreedBytes);
    }

    [Fact]
    public async Task A_file_pass_reports_on_a_clock_not_per_file()
    {
        var target = TempFolderWithFiles(200, 10);
        var progress = new ListProgress();
        var service = new CleanupService(new LoggingService()) { ProgressInterval = TimeSpan.FromHours(1) };

        var outcome = await service.CleanAsync(target, progress, CancellationToken.None);

        Assert.Equal(2_000, outcome.FreedBytes);
        Assert.Empty(progress.Reports);
    }

    /// <summary>Reports bytes freed, then waits at the door until the test lets the target finish.</summary>
    private sealed class SlowCleanService(
        LoggingService logging, long liveBytes, long finalBytes, Task release, bool fail = false)
        : CleanupService(logging)
    {
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
            Task.FromResult(new CleanupScanResult(0, 0, 0));

        public override async Task<CleanupOutcome> CleanAsync(
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
            progress?.Report(new CleanupProgress("a.tmp", 40, liveBytes));
            Reported.TrySetResult();
            await release.WaitAsync(cancellationToken);
            if (fail)
                throw new InvalidOperationException("boom");

            return new CleanupOutcome(finalBytes, 1, 0);
        }
    }

    private static (CleanupViewModel ViewModel, CleanupItemViewModel Item) CreateRun(SlowCleanService service)
    {
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;
        var logging = new LoggingService();
        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-live-freed-s-").FullName, "settings.json"));
        var history = new CleanupHistoryService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-live-freed-h-").FullName, "history.json"));
        var viewModel = new CleanupViewModel(service, logging, settings, history: history);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        viewModel.ComponentStoreResult = new ComponentStoreAnalysis(1_000_000_000, null, null, null);

        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories && !i.Target.NeedsElevation);
        foreach (var other in viewModel.Items)
            other.IsSelected = ReferenceEquals(other, item);

        return (viewModel, item);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    [Fact]
    public async Task The_total_climbs_with_progress_before_the_target_ends_and_lands_on_the_outcome()
    {
        var release = new TaskCompletionSource();
        var service = new SlowCleanService(new LoggingService(), 1_000, 1_500, release.Task);
        var (viewModel, item) = CreateRun(service);
        await viewModel.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var run = viewModel.RunCleanupAsync();
        await service.Reported.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntil(() => viewModel.FreedBytes == 1_000);

        // The target is still working, and the figure already shows what is gone.
        Assert.True(item.IsCleaning);
        Assert.Equal(1_000, viewModel.FreedBytes);
        Assert.Equal(CleanupViewModel.FormatFreed(1_000), viewModel.FreedText);

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1_500, viewModel.FreedBytes);
        Assert.Equal(1_500, item.FreedBytes);
    }

    [Fact]
    public async Task A_target_that_fails_after_progress_adds_nothing_as_before()
    {
        var release = new TaskCompletionSource();
        var service = new SlowCleanService(new LoggingService(), 1_000, 0, release.Task, fail: true);
        var (viewModel, _) = CreateRun(service);
        await viewModel.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var run = viewModel.RunCleanupAsync();
        await service.Reported.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntil(() => viewModel.FreedBytes == 1_000);
        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, viewModel.FreedBytes);
    }

    [Fact]
    public async Task The_live_figure_does_not_step_back_when_a_failed_target_takes_its_part_out()
    {
        var release = new TaskCompletionSource();
        var service = new SlowCleanService(new LoggingService(), 1_000, 0, release.Task, fail: true);
        var (viewModel, _) = CreateRun(service);
        await viewModel.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var run = viewModel.RunCleanupAsync();
        await service.Reported.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntil(() => viewModel.FreedBytes == 1_000);
        var shown = viewModel.LiveFreedText;
        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, viewModel.FreedBytes);
        Assert.Equal(shown, viewModel.LiveFreedText);
    }

    [Fact]
    public async Task A_stopped_target_keeps_what_its_progress_confirmed()
    {
        var release = new TaskCompletionSource();
        var service = new SlowCleanService(new LoggingService(), 1_000, 0, release.Task);
        var (viewModel, _) = CreateRun(service);
        await viewModel.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var run = viewModel.RunCleanupAsync();
        await service.Reported.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntil(() => viewModel.FreedBytes == 1_000);
        viewModel.StopCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1_000, viewModel.FreedBytes);
    }
}
