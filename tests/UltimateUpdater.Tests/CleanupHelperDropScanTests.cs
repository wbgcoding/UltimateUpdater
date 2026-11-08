using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>An open scan whose helper never delivers must not leave the admin rows showing their old
/// figure as if it were just measured.</summary>
[Collection("UiLanguage")]
public class CleanupHelperDropScanTests
{
    private sealed class QuietCleanupService(LoggingService logging) : CleanupService(logging)
    {
        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
            Task.FromResult(new CleanupScanResult(0, 0, 0));
    }

    [Fact]
    public async Task Admin_rows_the_helper_never_measured_do_not_keep_their_old_figure_in_an_open_scan()
    {
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var host = new ElevatedHost(logging);
        host.SetRunningForTest(true);

        // Reported as running, but the job never reaches it.
        host.EnsureStartedOverrideForTest = _ => Task.FromResult(false);
        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-helper-drop-").FullName, "settings.json"));
        var viewModel = new CleanupViewModel(new QuietCleanupService(logging), logging, settings, elevated: host);
        WpfHarness.WaitForCleanupCatalog(viewModel);

        var admin = viewModel.Items.Where(i => i.Target.NeedsElevation).ToList();
        Assert.NotEmpty(admin);
        foreach (var row in admin)
            row.Bytes = 5_000;

        await viewModel.StartOpenScanAsync();

        Assert.All(admin, row =>
        {
            Assert.Equal(0, row.Bytes);
            Assert.False(row.IsScanning);
            Assert.True(row.ShowsElevationHint);
        });
    }
}
