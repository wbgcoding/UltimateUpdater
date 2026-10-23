namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>F5 and the tray's scan entry execute the command even while the button is disabled; a request
    /// made during a running scan must leave that scan alone instead of cancelling and restarting it.</summary>
    [Fact]
    public void A_scan_request_during_a_running_scan_does_not_cancel_it()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new GatedSource();
            var viewModel = Create([source]);

            var first = viewModel.ScanCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsScanning);

            var ignored = viewModel.ScanCommand.ExecuteAsync(null);
            Pump(ignored);

            Assert.True(viewModel.IsScanning);

            source.Release();
            Pump(first);

            Assert.Equal(1, source.ScanCount);
            Assert.Single(viewModel.Updates);
            Assert.False(viewModel.IsScanning);
        });
    }
}
