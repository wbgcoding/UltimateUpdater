using System.Net.Http;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_winget_offer_waits_while_the_window_is_not_shown_and_comes_with_the_next_scan()
    {
        WpfHarness.OnUiThread(() =>
        {
            // winget reported absent, so the offer is due; the dialog layer is suppressed in tests and
            // answers "no", which is enough to tell that the offer was started.
            var bootstrap = new WingetBootstrapService(new HttpClient(), new LoggingService(), () => false);
            var windowShown = false;
            var viewModel = Create([new InstantSource()], wingetBootstrap: bootstrap, canShowWingetOffer: () => windowShown);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            Pump(Task.Delay(300));
            Assert.True(bootstrap.NeedsOffer, "a hidden window must not spend the offer");

            windowShown = true;
            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            Pump(WaitUntilAsync(() => !bootstrap.NeedsOffer, TimeSpan.FromSeconds(5)));

            Assert.False(bootstrap.NeedsOffer);
        });
    }
}
