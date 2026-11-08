using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The old Delivery Optimization row listed a folder current Windows no longer uses, so it always read
/// "nothing found" while the real cache (owned by the running service) stayed. The row is gone; Disk
/// Cleanup's own Delivery Optimization handler covers the real cache and its row says so.
/// </summary>
public class DeliveryOptimizationRowTests
{
    [Fact]
    public void No_catalog_row_lists_a_delivery_optimization_folder()
    {
        var targets = CleanupCatalog.Build();

        Assert.DoesNotContain(targets, t => t.Id == "delivery-optimization");
        Assert.DoesNotContain(targets, t => t.Directories.Any(d =>
            d.Contains("DeliveryOptimization", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void The_disk_cleanup_row_names_the_delivery_optimization_files()
    {
        var disk = CleanupCatalog.Build().Single(t => t.Id == "disk-cleanup");

        var text = LocalizationService.Instance[disk.DescriptionKey];
        Assert.True(
            text.Contains("Delivery Optimization", StringComparison.Ordinal) || text.Contains("Übermittlungsoptimierung", StringComparison.Ordinal),
            text);
    }
}
