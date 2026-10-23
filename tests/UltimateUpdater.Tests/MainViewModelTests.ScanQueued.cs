using System.Reflection;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>A search started while the previous scan still holds the gate (right after Stop) shows the
    /// scanning state at once, instead of looking dead until the old scan has unwound.</summary>
    [Fact]
    public void A_scan_waiting_for_the_gate_already_shows_the_scanning_state()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new InstantSource()]);
            var gate = (SemaphoreSlim)typeof(MainViewModel)
                .GetField("_scanGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel)!;
            gate.Wait();

            Task scan;
            try
            {
                scan = viewModel.ScanCommand.ExecuteAsync(null);
                Pump(WaitUntilAsync(() => viewModel.IsScanning, TimeSpan.FromSeconds(5)));

                Assert.True(viewModel.IsScanning);
                Assert.Equal(viewModel.Loc["HeadlineScanning"], viewModel.HeadlineText);
                Assert.False(scan.IsCompleted);
            }
            finally
            {
                gate.Release();
            }

            Pump(scan);
            Assert.False(viewModel.IsScanning);
        });
    }

    /// <summary>A second request made while the first still waits for the gate is ignored at once and must not
    /// clear the scanning state the first one still needs.</summary>
    [Fact]
    public void An_ignored_scan_request_leaves_the_scanning_state_to_the_waiting_one()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new InstantSource()]);
            var gate = (SemaphoreSlim)typeof(MainViewModel)
                .GetField("_scanGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel)!;
            gate.Wait();

            Task first, second;
            try
            {
                first = viewModel.ScanCommand.ExecuteAsync(null);
                second = viewModel.ScanCommand.ExecuteAsync(null);
                Pump(WaitUntilAsync(() => second.IsCompleted, TimeSpan.FromSeconds(5)));

                Assert.False(first.IsCompleted);
                Assert.True(viewModel.IsScanning, "the first request is still waiting for the gate");
            }
            finally
            {
                gate.Release();
            }

            Pump(first);
            Assert.False(viewModel.IsScanning);
        });
    }

    /// <summary>Stop also retires a request that is still waiting for the gate: the window leaves the
    /// scanning state at once and no scan starts later.</summary>
    [Fact]
    public void Stop_retires_a_scan_request_that_is_still_waiting_for_the_gate()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new GatedSource();
            var viewModel = Create([source]);
            var gate = (SemaphoreSlim)typeof(MainViewModel)
                .GetField("_scanGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel)!;
            gate.Wait();

            Task scan;
            try
            {
                scan = viewModel.ScanCommand.ExecuteAsync(null);
                Pump(WaitUntilAsync(() => viewModel.IsScanning, TimeSpan.FromSeconds(5)));

                viewModel.CancelScanCommand.Execute(null);

                Assert.False(viewModel.IsScanning);
            }
            finally
            {
                gate.Release();
            }

            Pump(scan);
            Assert.Equal(0, source.ScanCount);
            Assert.False(viewModel.IsScanning);
        });
    }
}
