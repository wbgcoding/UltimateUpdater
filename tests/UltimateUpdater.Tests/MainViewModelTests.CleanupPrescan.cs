using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    private sealed record PrescanRig(
        MainViewModel ViewModel, CleanupPrescanTests.RecordingCleanupService Service, Func<DateTime> Now, Action<TimeSpan> Advance);

    /// <summary>A main view model whose cleanup measuring is a recording stand-in, so nothing here reads the
    /// machine it runs on. The prescan is allowed by default, as in the real app.</summary>
    private static PrescanRig CreatePrescanRig(
        bool settingOn = true, bool onAc = true, bool allowed = true)
    {
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-vm-prescan-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";
        settings.Current.CleanupPrescanAtStartup = settingOn;

        var sources = new List<IUpdateSource> { new EmptySource() };
        var history = new HistoryService(logging);
        var service = new CleanupPrescanTests.RecordingCleanupService(logging);
        var now = new DateTime(2026, 3, 4, 12, 0, 0);

        var viewModel = new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources)
        {
            CleanupPrescanAllowed = allowed,
            OnAcPowerOverride = () => onAc,
            CleanupServiceOverride = () => service,
        };

        viewModel.CleanupClock = () => now;
        return new PrescanRig(viewModel, service, () => now, span => now += span);
    }

    /// <summary>A source with nothing to report: the scan ends cleanly at once.</summary>
    private sealed class EmptySource : IUpdateSource
    {
        public string Id => "prescan-empty-source";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static void ScanOnce(MainViewModel viewModel)
    {
        Pump(viewModel.ScanCommand.ExecuteAsync(null));
        if (viewModel.CleanupPrescanTask is { } task)
            Pump(task);
    }

    [Fact]
    public void The_first_scan_starts_a_cleanup_prescan_when_it_is_wanted()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig();

            ScanOnce(rig.ViewModel);

            Assert.NotNull(rig.ViewModel.CleanupPrescanTask);
            Assert.NotEmpty(rig.Service.Scanned);
            Assert.NotNull(rig.ViewModel.EnsureCleanupViewModel().PrescanFinishedAt);
        });
    }

    [Fact]
    public void No_prescan_runs_with_the_setting_off()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig(settingOn: false);

            ScanOnce(rig.ViewModel);

            Assert.Null(rig.ViewModel.CleanupPrescanTask);
            Assert.Empty(rig.Service.Scanned);
        });
    }

    [Fact]
    public void No_prescan_runs_on_battery()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig(onAc: false);

            ScanOnce(rig.ViewModel);

            Assert.Null(rig.ViewModel.CleanupPrescanTask);
            Assert.Empty(rig.Service.Scanned);
        });
    }

    [Fact]
    public void No_prescan_runs_in_a_session_that_is_not_attended()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig(allowed: false);

            ScanOnce(rig.ViewModel);

            Assert.Null(rig.ViewModel.CleanupPrescanTask);
            Assert.Empty(rig.Service.Scanned);
        });
    }

    [Fact]
    public void A_prescan_runs_once_per_session_not_after_every_scan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig();
            ScanOnce(rig.ViewModel);
            var first = rig.ViewModel.CleanupPrescanTask;
            var measured = rig.Service.Scanned.Count;

            ScanOnce(rig.ViewModel);

            Assert.Same(first, rig.ViewModel.CleanupPrescanTask);
            Assert.Equal(measured, rig.Service.Scanned.Count);
        });
    }

    [Fact]
    public void A_dialog_opened_within_half_an_hour_reuses_the_prescanned_view_model()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig();
            ScanOnce(rig.ViewModel);
            var prescanned = rig.ViewModel.EnsureCleanupViewModel();

            rig.Advance(TimeSpan.FromMinutes(29));

            Assert.Same(prescanned, rig.ViewModel.EnsureCleanupViewModel());
        });
    }

    [Fact]
    public void A_dialog_opened_after_half_an_hour_starts_over_with_a_fresh_view_model()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig();
            ScanOnce(rig.ViewModel);
            var prescanned = rig.ViewModel.EnsureCleanupViewModel();

            rig.Advance(TimeSpan.FromMinutes(31));
            var reopened = rig.ViewModel.EnsureCleanupViewModel();

            Assert.NotSame(prescanned, reopened);
            Assert.Null(reopened.PrescanFinishedAt);
        });
    }
}
