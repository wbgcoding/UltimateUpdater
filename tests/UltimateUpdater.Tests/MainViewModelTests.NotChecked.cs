using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Before_any_scan_the_headline_and_empty_state_say_nothing_was_checked()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();

            Assert.Equal(viewModel.Loc["HeadlineNotChecked"], viewModel.HeadlineText);
            Assert.Equal(viewModel.Loc["EmptyStateNotChecked"], viewModel.EmptyStateText);
            Assert.True(viewModel.ShowNotCheckedPrompt);
        });
    }

    [Fact]
    public void After_a_clean_scan_with_nothing_found_the_headline_says_up_to_date()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Equal(viewModel.Loc["HeadlineUpToDate"], viewModel.HeadlineText);
            Assert.Equal(viewModel.Loc["EmptyState"], viewModel.EmptyStateText);
            Assert.False(viewModel.ShowNotCheckedPrompt);
        });
    }

    [Fact]
    public void A_saved_result_from_the_last_session_counts_as_checked()
    {
        WpfHarness.OnUiThread(() => { });

        var cachePath = ScanResultCache.EffectivePath;
        new ScanResultCache(new LoggingService()).Save([]);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var source = new GatedSource();
                var viewModel = Create([source]);

                var scan = viewModel.ScanCommand.ExecuteAsync(null);
                Pump(WaitUntilAsync(() => viewModel.HasCachedResult, TimeSpan.FromSeconds(5)));

                Assert.True(viewModel.HasScanResult);

                source.Release();
                Pump(scan);
            });
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    [Fact]
    public void A_cancelled_first_scan_does_not_claim_everything_is_up_to_date()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new GatedSource();
            var viewModel = Create([source]);

            var scan = viewModel.ScanCommand.ExecuteAsync(null);
            viewModel.CancelScanCommand.Execute(null);
            source.Release();
            Pump(scan);

            Assert.False(viewModel.IsScanning);
            Assert.Equal(viewModel.Loc["HeadlineNotChecked"], viewModel.HeadlineText);
        });
    }
}
