using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The registry catalogue merges 5 real scanners into 2 user-visible rows. Pure and structural,
/// no registry access: <see cref="CleanupServiceTests"/> covers the real, cross-category scan and clean.
/// </summary>
public class RegistryCleanupCatalogTests
{
    [Fact]
    public void The_catalogue_has_exactly_two_rows()
    {
        var targets = RegistryCleanupCatalog.Build();

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal(CleanupKind.RegistryCleanup, t.Kind));
        Assert.All(targets, t => Assert.True(t.NeedsElevation));
        Assert.All(targets, t => Assert.False(t.DefaultEnabled));
    }

    [Fact]
    public void Orphaned_references_covers_app_paths_shared_dlls_activex_com_and_autostart()
    {
        var target = RegistryCleanupCatalog.Build().Single(t => t.Id == "registry-orphaned-references");

        Assert.Equal(
            [RegistryCleanupCategory.AppPaths, RegistryCleanupCategory.SharedDlls,
             RegistryCleanupCategory.ActiveXCom, RegistryCleanupCategory.Autostart],
            target.RegistryCategories);
    }

    [Fact]
    public void Orphaned_uninstall_entries_covers_exactly_the_uninstall_entries_category()
    {
        var target = RegistryCleanupCatalog.Build().Single(t => t.Id == "registry-orphaned-uninstall");

        Assert.Equal([RegistryCleanupCategory.UninstallEntries], target.RegistryCategories);
    }

    /// <summary>Every one of the 5 real scanners (this fix's audit target) is still reachable from exactly
    /// one of the 2 merged rows, none dropped, none duplicated.</summary>
    [Fact]
    public void Every_category_belongs_to_exactly_one_row()
    {
        var targets = RegistryCleanupCatalog.Build();
        var allCategories = targets.SelectMany(t => t.RegistryCategories).ToList();

        Assert.Equal(Enum.GetValues<RegistryCleanupCategory>().Length, allCategories.Count);
        Assert.Equal(allCategories.Count, allCategories.Distinct().Count());
        foreach (var category in Enum.GetValues<RegistryCleanupCategory>())
            Assert.Contains(category, allCategories);
    }
}
