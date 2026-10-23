using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>A scan that ended cleanly while every enabled source failed has checked nothing, so the
    /// headline must not claim the machine is up to date.</summary>
    [Fact]
    public void When_every_source_failed_the_headline_does_not_say_up_to_date()
    {
        WpfHarness.OnUiThread(() =>
        {
            var first = new OneShotSource("first");
            var second = new OneShotSource("second");
            first.ReportErrorNextScan();
            second.ReportErrorNextScan();
            var viewModel = Create([first, second]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Equal(viewModel.Loc["HeadlineNoSourceChecked"], viewModel.HeadlineText);
            Assert.NotEqual(viewModel.Loc["HeadlineUpToDate"], viewModel.HeadlineText);
        });
    }

    [Fact]
    public void When_one_source_succeeded_the_headline_still_says_up_to_date()
    {
        WpfHarness.OnUiThread(() =>
        {
            var failing = new OneShotSource("failing");
            failing.ReportErrorNextScan();
            var healthy = new OneShotSource("healthy");
            var viewModel = Create([failing, healthy]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Equal(viewModel.Loc["HeadlineUpToDate"], viewModel.HeadlineText);
        });
    }

    /// <summary>Absent tools sit in the pill list as NotInstalled; they did not check anything either, so
    /// one failed source plus several absent ones still means nothing was checked.</summary>
    [Fact]
    public void Absent_tools_do_not_hide_that_every_present_source_failed()
    {
        WpfHarness.OnUiThread(() =>
        {
            var failing = new OneShotSource("failing");
            failing.ReportErrorNextScan();
            var viewModel = Create([failing, new AbsentToolSource(), new AbsentToolSource()]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Contains(viewModel.ProviderStatuses, p => p.State == ProviderScanState.NotInstalled);
            Assert.Equal(viewModel.Loc["HeadlineNoSourceChecked"], viewModel.HeadlineText);
        });
    }
}
