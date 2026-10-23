using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The catalogue row itself: default off, red risk, needs elevation, same shape as the registry-cleanup
/// rows it sits beside. Never touches SetupAPI/CfgMgr32 or the driver store: building the catalogue is pure
/// data, the real machine read only happens behind <see cref="IDriverInventoryProvider"/>, which
/// <see cref="DriverCleanupDiscoveryTests"/> exercises with fakes instead.
/// </summary>
public class DriverCleanupCatalogTests
{
    [Fact]
    public void The_driver_cleanup_row_is_off_by_default_and_needs_elevation()
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == "driver-cleanup");

        Assert.Equal(CleanupKind.DriverCleanup, target.Kind);
        Assert.False(target.DefaultEnabled);
        Assert.True(target.NeedsElevation);
        Assert.Equal(CleanupRiskLevel.Risky, target.RiskLevel);
    }

    [Fact]
    public void Only_a_driver_cleanup_kind_target_lands_in_the_drivers_group()
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == "driver-cleanup");

        Assert.Equal(CleanupGroupKey.Drivers, CleanupGroups.For(target));
    }

    /// <summary>The driver and old Python rows carry no byte total: their own candidate lists say whether
    /// anything was found, so the generic "nothing to clean" size cell never shows beside a filled list.</summary>
    [Theory]
    [InlineData("driver-cleanup")]
    [InlineData("old-python-versions")]
    public void Candidate_rows_never_show_the_generic_nothing_found_cell(string id)
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == id);
        var item = new CleanupItemViewModel(target, () => { }) { IsScanning = false, IsWaiting = false };

        Assert.False(item.ShowsNothingFound);
    }

    [Fact]
    public void An_ordinary_row_without_bytes_still_shows_nothing_found()
    {
        var target = CleanupCatalog.Build().First(t => t.Kind == CleanupKind.Directories);
        var item = new CleanupItemViewModel(target, () => { }) { IsScanning = false, IsWaiting = false };

        Assert.True(item.ShowsNothingFound);
    }

    /// <summary>Mirrors <see cref="CleanupWorker.ResolveElevatedTargets"/>'s own membership rule: an id the
    /// pipe never actually names resolves to nothing, whatever a hostile payload repeats.</summary>
    [Fact]
    public void ResolveElevatedTargets_only_ever_returns_the_real_catalogue_entry()
    {
        var resolved = CleanupWorker.ResolveElevatedTargets(["driver-cleanup", "driver-cleanup", "not-a-real-id"]);

        Assert.Single(resolved);
        Assert.Equal("driver-cleanup", resolved[0].Id);
    }

    /// <summary>The driver row stays tickable without admin rights (the run asks for them, like every other
    /// admin row); when the prompt is declined the in-process clean must skip every ticked driver, never
    /// call pnputil unelevated.</summary>
    [Fact]
    public async Task An_unelevated_driver_clean_skips_every_ticked_driver()
    {
        if (ElevationService.IsElevated)
            return; // the elevated path really removes drivers; this checks the unelevated guard only
        var target = CleanupCatalog.Build().Single(t => t.Id == "driver-cleanup");
        var service = new CleanupService(new LoggingService());

        var outcome = await service.CleanAsync(target, progress: null, CancellationToken.None,
            selectedDriverDeviceInstanceIds: [@"USB\VID_0000&PID_0000\GONE"], selectedDriverPackageNames: ["oem999.inf"]);

        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(2, outcome.SkippedFiles);
    }

    [Fact]
    public void Driver_candidates_found_without_admin_rights_can_still_be_ticked()
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == "driver-cleanup");
        var row = new CleanupItemViewModel(target, () => { }, _ => { });

        row.SetDriverCandidates(
            [new DriverDeviceCandidateDto { InstanceId = "X", DisplayName = "Gone device" }],
            [new DriverPackageCandidateDto { PackageName = "oem1.inf", Provider = "Vendor" }]);
        row.DriverDeviceCandidates[0].IsSelected = true;
        row.DriverPackageCandidates[0].IsSelected = true;

        Assert.Equal(["X"], row.SelectedDriverDeviceInstanceIds);
        Assert.Equal(["oem1.inf"], row.SelectedDriverPackageNames);
    }
}
