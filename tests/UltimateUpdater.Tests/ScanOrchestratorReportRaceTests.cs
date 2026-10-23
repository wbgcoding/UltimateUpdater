using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>A provider report from a superseded run must never land on top of the newer run's state.</summary>
public class ScanOrchestratorReportRaceTests
{
    private static ScanOrchestrator NewOrchestrator() =>
        new([], new LoggingService(), isOnline: () => true);

    [Fact]
    public async Task A_new_run_cannot_start_between_the_generation_check_and_the_state_write()
    {
        var orchestrator = NewOrchestrator();
        await orchestrator.RunAsync(CancellationToken.None);
        var oldGeneration = orchestrator.GenerationForTest;

        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        orchestrator.ReportSeamForTest = stage =>
        {
            if (stage == "locked")
            {
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(15));
            }
        };

        var stale = Task.Run(() => orchestrator.Report(
            new ProviderStatus { SourceId = "winget", State = ProviderScanState.Error }, oldGeneration));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            // The next run, started while the old report sits between its check and its write.
            // RunAsync takes the lock before its first await, so it runs on its own thread here.
            var next = Task.Run(() => orchestrator.RunAsync(CancellationToken.None));
            await Task.Delay(400);
            Assert.False(next.IsCompleted, "the generation bump got in between the check and the state write");

            release.Set();
            await stale.WaitAsync(TimeSpan.FromSeconds(10));
            await next.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
        }

        // The new run's own report is the last word, and nothing older can overwrite it.
        orchestrator.ReportSeamForTest = null;
        orchestrator.Report(
            new ProviderStatus { SourceId = "winget", State = ProviderScanState.Done }, orchestrator.GenerationForTest);
        orchestrator.Report(
            new ProviderStatus { SourceId = "winget", State = ProviderScanState.Error }, oldGeneration);

        Assert.Equal(ProviderScanState.Done, orchestrator.LastKnownStateForTest("winget"));
    }

    [Fact]
    public async Task A_run_that_starts_after_the_lock_is_released_suppresses_the_status_event()
    {
        var orchestrator = NewOrchestrator();
        await orchestrator.RunAsync(CancellationToken.None);
        var oldGeneration = orchestrator.GenerationForTest;

        var raised = 0;
        orchestrator.ProviderStatusChanged += _ => raised++;
        orchestrator.ReportSeamForTest = stage =>
        {
            if (stage == "released")
                orchestrator.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        };

        orchestrator.Report(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Done }, oldGeneration);

        Assert.Equal(0, raised);
    }
}
