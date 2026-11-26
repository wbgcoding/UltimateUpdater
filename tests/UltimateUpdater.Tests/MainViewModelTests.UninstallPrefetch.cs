using static UltimateUpdater.Tests.UninstallerViewModelTests;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    private static (MainViewModel ViewModel, FakeBackend Reader) CreatePrefetchRig(bool allowed = true, bool mock = false)
    {
        var rig = CreatePrescanRig(settingOn: false, allowed: false, mock: mock);
        var reader = new FakeBackend(Listing("A"));
        rig.ViewModel.UninstallPrefetchAllowed = allowed;
        rig.ViewModel.UninstallerBackendOverride = () => reader;
        return (rig.ViewModel, reader);
    }

    [Fact]
    public void The_first_scan_starts_one_background_read_of_the_uninstaller_listing()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, reader) = CreatePrefetchRig();

            ScanOnce(viewModel);
            Pump(viewModel.UninstallPrefetch!.Current!);
            ScanOnce(viewModel);

            Assert.Equal(1, reader.ListCalls);
            Assert.Single(reader.Saved);
        });
    }

    [Fact]
    public void No_background_read_runs_in_a_session_that_is_not_attended()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, reader) = CreatePrefetchRig(allowed: false);

            ScanOnce(viewModel);

            Assert.Null(viewModel.UninstallPrefetch);
            Assert.Equal(0, reader.ListCalls);
        });
    }

    [Fact]
    public void No_background_read_runs_with_mock_sources()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, reader) = CreatePrefetchRig(mock: true);

            ScanOnce(viewModel);

            Assert.Null(viewModel.UninstallPrefetch);
            Assert.Equal(0, reader.ListCalls);
        });
    }

    [Fact]
    public void The_background_read_waits_while_work_is_running_and_starts_when_a_later_scan_ends_idle()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, reader) = CreatePrefetchRig();
            viewModel.IsCleanupRunning = true;

            ScanOnce(viewModel);
            Assert.Null(viewModel.UninstallPrefetch);

            viewModel.IsCleanupRunning = false;
            ScanOnce(viewModel);

            Assert.NotNull(viewModel.UninstallPrefetch);
            Pump(viewModel.UninstallPrefetch.Current!);
            Assert.Equal(1, reader.ListCalls);
        });
    }

    [Fact]
    public void Leaving_the_app_cancels_a_background_read_still_going()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, reader) = CreatePrefetchRig();
            var gate = new TaskCompletionSource();
            reader.ListGate = gate.Task;

            ScanOnce(viewModel);
            viewModel.CancelUninstallPrefetch();

            Assert.True(SpinWait.SpinUntil(() => viewModel.UninstallPrefetch!.Current!.IsCompleted, TimeSpan.FromSeconds(10)));
            Assert.True(viewModel.UninstallPrefetch!.Current!.IsCanceled);
            Assert.Empty(reader.Saved);
        });
    }
}
