using UltimateUpdater.Services.Cleanup;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// After "continue without" the component cleanup still ran unelevated: DISM ended at once with exit
/// code 740 and the row read "done, 0 B". A target only an administrator can run is skipped and
/// flagged instead; targets that free part of their contents without admin still run.
/// </summary>
public class AdminOnlyCleanupSkipTests
{
    [Theory]
    [InlineData(CleanupKind.SystemRepair)]
    [InlineData(CleanupKind.Defragmentation)]
    [InlineData(CleanupKind.DriverCleanup)]
    [InlineData(CleanupKind.DiskCleanup)]
    public void An_admin_only_kind_is_skipped_unelevated_and_runs_elevated(CleanupKind kind)
    {
        Assert.True(CleanupService.SkipsWithoutAdmin(kind, processElevated: false));
        Assert.False(CleanupService.SkipsWithoutAdmin(kind, processElevated: true));
    }

    [Theory]
    [InlineData(CleanupKind.Directories)]
    [InlineData(CleanupKind.AppCacheSweep)]
    [InlineData(CleanupKind.RecycleBin)]
    [InlineData(CleanupKind.RegistryCleanup)]
    [InlineData(CleanupKind.OldPythonVersions)]
    public void A_kind_that_frees_part_without_admin_still_runs_unelevated(CleanupKind kind) =>
        Assert.False(CleanupService.SkipsWithoutAdmin(kind, processElevated: false));

    [Theory]
    [InlineData("windows-update-cache")]
    [InlineData("delivery-optimization")]
    public void A_folder_only_an_administrator_can_empty_is_skipped_unelevated(string id)
    {
        var target = CleanupCatalog.Build().First(t => t.Id == id);

        Assert.True(CleanupService.SkipsWithoutAdmin(target, processElevated: false));
        Assert.False(CleanupService.SkipsWithoutAdmin(target, processElevated: true));
    }

    [Theory]
    [InlineData("user-temp")]
    [InlineData("windows-temp")]
    public void A_folder_that_frees_part_without_admin_still_runs_unelevated(string id)
    {
        var target = CleanupCatalog.Build().First(t => t.Id == id);

        Assert.False(CleanupService.SkipsWithoutAdmin(target, processElevated: false));
    }

    [Fact]
    public void The_skip_outcome_frees_nothing_and_says_admin_is_needed()
    {
        var outcome = CleanupOutcome.AdminRequired;
        Assert.True(outcome.NeedsAdmin);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.Equal(0, outcome.SkippedFiles);
    }
}
