using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>Every pill that reached a final state counts as finished, absent tools included, or the
    /// progress bar stalls below its total although the scan is over.</summary>
    [Fact]
    public void Scan_progress_reaches_its_total_when_every_source_has_ended()
    {
        WpfHarness.OnUiThread(() =>
        {
            var failing = new OneShotSource("failing");
            failing.ReportErrorNextScan();
            var viewModel = Create([new OneShotSource("healthy"), failing, new AbsentToolSource()]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Contains(viewModel.ProviderStatuses, p => p.State == ProviderScanState.NotInstalled);
            Assert.Equal(viewModel.ScanProgressTotal, viewModel.ScanProgressDone);
        });
    }

    /// <summary>A source stopped from its pill delivered a partial result, so the rows an earlier scan found
    /// for it must stay: nothing was proven gone.</summary>
    [Fact]
    public void Rows_of_a_source_stopped_by_its_pill_survive_the_stale_row_sweep()
    {
        WpfHarness.OnUiThread(() =>
        {
            MainViewModel? viewModel = null;
            var source = new StopsItselfOnSecondScanSource(() => viewModel!.CancelSourceScanCommand.Execute("flaky"));
            viewModel = Create([source]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            Assert.Single(viewModel.Updates);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Single(viewModel.Updates);
        });
    }

    /// <summary>A source that fails outright has not re-checked anything, so the rows an earlier live scan
    /// found for it stay, the same as a cached row and as a single-source rescan keeps them.</summary>
    [Fact]
    public void Rows_from_an_earlier_live_scan_survive_a_scan_where_their_source_failed()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new OneShotSource("flaky", Item("flaky-tool", "Flaky Tool", "flaky"));
            var viewModel = Create([source]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            Assert.Single(viewModel.Updates);

            source.ReportErrorNextScan();
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Single(viewModel.Updates);
        });
    }

    private sealed class StopsItselfOnSecondScanSource(Action stopSelf) : IUpdateSource
    {
        private int _scans;

        public string Id => "flaky";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref _scans) == 1)
            {
                yield return Item("flaky-tool", "Flaky Tool", "flaky");
                yield break;
            }

            stopSelf();
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
