using System.Collections.Concurrent;
using System.Globalization;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The quiet measuring pass at startup: what it reaches, what it must never ask for, and what a dialog
/// opened afterwards does with its result.
/// </summary>
[Collection("UiLanguage")]
public class CleanupPrescanTests
{
    /// <summary>Records which targets were measured in-process and answers at once.</summary>
    internal sealed class RecordingCleanupService(LoggingService logging) : CleanupService(logging)
    {
        public ConcurrentQueue<string> Scanned { get; } = new();

        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            Scanned.Enqueue(target.Id);
            return Task.FromResult(new CleanupScanResult(0, 0, 0));
        }
    }

    private sealed record Rig(
        CleanupViewModel ViewModel, RecordingCleanupService Service, ElevatedHost Host, Func<int> StartRequests);

    private static Rig CreateRig(bool helperRunning, Func<DateTime>? clock = null)
    {
        // An elevated test runner would skip every elevation path and prove nothing.
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var service = new RecordingCleanupService(logging);
        var host = new ElevatedHost(logging);
        host.SetRunningForTest(helperRunning);
        var requests = 0;

        // Every way the helper could be started or reached goes through here: counting it is how a test sees
        // an elevation request. It answers "no", so a request ends the run without a real helper.
        host.EnsureStartedOverrideForTest = _ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(false);
        };

        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-prescan-").FullName, "settings.json"));
        var viewModel = new CleanupViewModel(service, logging, settings, elevated: host)
        {
            Clock = clock ?? (() => DateTime.Now),
        };
        WpfHarness.WaitForCleanupCatalog(viewModel);
        return new Rig(viewModel, service, host, () => Volatile.Read(ref requests));
    }

    private static List<string> AdminIds(CleanupViewModel viewModel) =>
        viewModel.Items.Where(i => i.Target.NeedsElevation).Select(i => i.Target.Id).ToList();

    [Fact]
    public async Task A_prescan_measures_user_targets_and_leaves_admin_targets_alone_without_a_helper()
    {
        var rig = CreateRig(helperRunning: false);
        var admin = AdminIds(rig.ViewModel);
        Assert.NotEmpty(admin);

        await rig.ViewModel.PrescanAsync();

        var scanned = rig.Service.Scanned.ToList();
        Assert.NotEmpty(scanned);
        Assert.Empty(scanned.Intersect(admin));
        Assert.NotNull(rig.ViewModel.PrescanFinishedAt);
        Assert.All(rig.ViewModel.Items, i => Assert.False(i.IsScanning));
    }

    [Fact]
    public async Task A_prescan_never_asks_for_elevation()
    {
        var rig = CreateRig(helperRunning: false);

        await rig.ViewModel.PrescanAsync();

        Assert.Equal(0, rig.StartRequests());
        Assert.False(rig.Host.IsRunning);
        Assert.False(rig.Host.WasDeclined);
    }

    [Fact]
    public async Task A_prescan_hands_the_admin_targets_to_a_helper_that_is_already_running()
    {
        var rig = CreateRig(helperRunning: true);
        var admin = AdminIds(rig.ViewModel);

        await rig.ViewModel.PrescanAsync();

        // They went to the helper (the stand-in is reached once for the whole admin batch, and once more by
        // the quiet component store analysis), not in-process.
        Assert.Equal(2, rig.StartRequests());
        Assert.Empty(rig.Service.Scanned.Intersect(admin));
        Assert.NotNull(rig.ViewModel.PrescanFinishedAt);
    }

    [Fact]
    public async Task Opening_on_a_prescan_measures_only_what_it_left_out()
    {
        var rig = CreateRig(helperRunning: false);
        var admin = AdminIds(rig.ViewModel);
        await rig.ViewModel.PrescanAsync();
        var afterPrescan = rig.Service.Scanned.ToList();

        await rig.ViewModel.StartOpenScanAsync();

        var onOpen = rig.Service.Scanned.Skip(afterPrescan.Count).ToList();
        Assert.Equal(admin.OrderBy(x => x), onOpen.OrderBy(x => x));
        Assert.NotNull(rig.ViewModel.PrescanFinishedAt);
    }

    [Fact]
    public async Task Opening_without_a_prescan_measures_everything()
    {
        var rig = CreateRig(helperRunning: false);

        await rig.ViewModel.StartOpenScanAsync();

        Assert.Equal(rig.ViewModel.Items.Count, rig.Service.Scanned.Distinct().Count());
        Assert.Null(rig.ViewModel.PrescanFinishedAt);
    }

    [Fact]
    public async Task The_stamp_names_the_measurement_end_and_goes_with_a_rescan()
    {
        var measuredAt = new DateTime(2026, 3, 4, 14, 5, 0);
        var rig = CreateRig(helperRunning: false, clock: () => measuredAt);
        LocalizationService.Instance.Apply("en");

        await rig.ViewModel.PrescanAsync();

        var expected = string.Format(
            LocalizationService.Instance["CleanupMeasuredAt"], measuredAt.ToString("t", CultureInfo.CurrentUICulture));
        Assert.Equal(expected, rig.ViewModel.MeasuredAtText);
        Assert.True(rig.ViewModel.HasMeasuredAt);

        await rig.ViewModel.RescanCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, rig.ViewModel.MeasuredAtText);
        Assert.False(rig.ViewModel.HasMeasuredAt);
        Assert.Null(rig.ViewModel.PrescanFinishedAt);
    }
}
