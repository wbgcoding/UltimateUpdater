using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_download_percentage_rises_and_ends_at_one_hundred()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var shown = new List<double>();
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(viewModel.SelfUpdatePercent))
                    shown.Add(viewModel.SelfUpdatePercent);
            };

            foreach (var read in new long[] { 10, 40, 30, 80, 100 })
                viewModel.ShowSelfUpdateProgress(new SelfUpdateProgress(SelfUpdatePhase.Downloading, read, 100));

            Assert.Equal(shown.Order().ToArray(), shown.ToArray());
            Assert.Equal(100, viewModel.SelfUpdatePercent);
            Assert.True(viewModel.SelfUpdateShowsProgress);
            Assert.True(viewModel.SelfUpdateCanCancel);
            Assert.False(viewModel.SelfUpdateProgressIndeterminate);
        });
    }

    [Fact]
    public void Verifying_has_no_percentage_and_can_no_longer_be_cancelled()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.ShowSelfUpdateProgress(new SelfUpdateProgress(SelfUpdatePhase.Downloading, 50, 100));
            viewModel.ShowSelfUpdateProgress(new SelfUpdateProgress(SelfUpdatePhase.Verifying));

            Assert.True(viewModel.SelfUpdateProgressIndeterminate);
            Assert.False(viewModel.SelfUpdateCanCancel);
            Assert.Equal(viewModel.Loc["SelfUpdatePhaseVerifying"], viewModel.SelfUpdatePhaseText);
            Assert.Equal(string.Empty, viewModel.SelfUpdateDetail);
        });
    }

    [Fact]
    public void An_unknown_total_shows_an_indeterminate_bar()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.ShowSelfUpdateProgress(new SelfUpdateProgress(SelfUpdatePhase.Downloading, 5000, null));

            Assert.True(viewModel.SelfUpdateProgressIndeterminate);
            Assert.True(viewModel.SelfUpdateShowsProgress);
        });
    }

    [Fact]
    public void Cancelling_during_the_download_ends_the_update_quietly_and_clears_the_progress()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.OfferSelfUpdateForTest(OfferedUpdate());
            viewModel.SelfUpdateDownloadForTest = async (_, token) =>
            {
                viewModel.ShowSelfUpdateProgress(new SelfUpdateProgress(SelfUpdatePhase.Downloading, 10, 100));
                viewModel.CancelSelfUpdateCommand.Execute(null);
                await Task.Delay(Timeout.Infinite, token);
                return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "x.exe");
            };

            Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));

            Assert.False(viewModel.SelfUpdateInProgress);
            Assert.False(viewModel.SelfUpdateShowsProgress);
            Assert.False(viewModel.SelfUpdateCanCancel);
        });
    }
}
