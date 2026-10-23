using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// Every real catalogue entry has to land in exactly one dialog group, or it silently
/// falls out of the list a user actually sees. Same "a new entry must be reviewed" discipline
/// <c>CleanupSafetyTests</c> already applies to the deletion gate.
/// </summary>
public class CleanupGroupsTests
{
    [Fact]
    public void Every_catalogue_target_has_a_group()
    {
        foreach (var target in CleanupCatalog.Build())
        {
            Assert.True(
                CleanupGroups.For(target) is not null,
                $"'{target.Id}' has no group mapping in CleanupGroups, add one before it ships.");
        }
    }

    [Fact]
    public void Every_registry_category_lands_in_the_registry_group()
    {
        foreach (var target in CleanupCatalog.Build().Where(t => t.Kind == CleanupKind.RegistryCleanup))
            Assert.Equal(CleanupGroupKey.Registry, CleanupGroups.For(target));
    }

    /// <summary>
    /// After a run each section reports what IT freed, so the result can be read where
    /// the work was chosen instead of only as one figure in the ring.
    /// </summary>
    [Fact]
    public void A_group_reports_the_sum_of_what_its_own_targets_freed()
    {
        var first = Item("user-temp");
        var second = Item("windows-temp");
        var group = new CleanupGroupViewModel(CleanupGroupKey.TempFiles, [first, second]);

        Assert.Equal(0, group.FreedBytes);

        first.FreedBytes = 1024 * 1024;
        second.FreedBytes = 3 * 1024 * 1024;

        Assert.Equal(4 * 1024 * 1024, group.FreedBytes);
        Assert.Contains("4", group.FreedText);
    }

    private static CleanupItemViewModel Item(string id) =>
        new(new CleanupTarget(
            id, "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x"), () => { });

    private static CleanupItemViewModel Item(string id, CleanupKind kind) =>
        new(new CleanupTarget(
            id, "x", "y", kind, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x"), () => { });

    /// <summary>
    /// Systemreparatur (DISM + sfc) can never report a size, summing two unknown-size targets always
    /// lands on exactly zero, and printing "0,0 B" would look like a measurement rather than "not
    /// measurable". A mixed group (Windows-Wartung: disk-cleanup next to real byte counts)
    /// must keep showing the known sum.
    /// </summary>
    [Fact]
    public void A_group_of_only_unknown_size_targets_shows_no_size()
    {
        var group = new CleanupGroupViewModel(
            CleanupGroupKey.SystemRepair,
            [Item("system-repair-dism", CleanupKind.SystemRepair), Item("system-repair-sfc", CleanupKind.SystemRepair)]);

        Assert.False(group.ShowsSize);
    }

    /// <summary>The two component store targets sit under Windows maintenance, beside the other
    /// Windows-owned housekeeping; system repair keeps exactly DISM and sfc.</summary>
    [Fact]
    public void The_component_store_targets_sit_under_windows_maintenance_and_repair_keeps_dism_and_sfc()
    {
        var byId = CleanupCatalog.Build().ToDictionary(t => t.Id);

        Assert.Equal(CleanupGroupKey.WindowsMaintenance, CleanupGroups.For(byId["component-cleanup"]));
        Assert.Equal(CleanupGroupKey.WindowsMaintenance, CleanupGroups.For(byId["component-resetbase"]));
        Assert.Equal(CleanupGroupKey.SystemRepair, CleanupGroups.For(byId["system-repair-dism"]));
        Assert.Equal(CleanupGroupKey.SystemRepair, CleanupGroups.For(byId["system-repair-sfc"]));
    }

    /// <summary>Only the component cleanup card carries the analysis block.</summary>
    [Fact]
    public void Only_the_component_cleanup_card_carries_the_component_store_analysis()
    {
        var byId = CleanupCatalog.Build().ToDictionary(t => t.Id);

        Assert.True(new CleanupItemViewModel(byId["component-cleanup"], () => { }).IsComponentCleanup);
        Assert.False(new CleanupItemViewModel(byId["component-resetbase"], () => { }).IsComponentCleanup);
        Assert.False(new CleanupItemViewModel(byId["system-repair-dism"], () => { }).IsComponentCleanup);
    }

    [Fact]
    public void A_mixed_group_still_shows_the_known_size()
    {
        var group = new CleanupGroupViewModel(
            CleanupGroupKey.WindowsMaintenance,
            [Item("disk-cleanup", CleanupKind.DiskCleanup), Item("windows-update-cache", CleanupKind.Directories)]);

        Assert.True(group.ShowsSize);
    }

    /// <summary>A single leftover registry entry reads "1 entry", not the grammatically wrong
    /// "1 entries", the plural resource key was only ever exercised with counts &gt; 1 before.</summary>
    [Fact]
    public void A_single_registry_entry_uses_the_singular_wording()
    {
        var item = Item("registry-leftover", CleanupKind.RegistryCleanup);
        item.Files = 1;
        Assert.Equal(string.Format(LocalizationService.Instance["CleanupRegistryEntryCountOne"], 1), item.SizeText);

        item.Files = 2;
        Assert.Equal(string.Format(LocalizationService.Instance["CleanupRegistryEntryCount"], 2), item.SizeText);
    }

    /// <summary>Same singular/plural distinction one level up, where the group sums its rows' <see
    /// cref="CleanupItemViewModel.Files"/> for the registry-only size text.</summary>
    [Fact]
    public void A_registry_group_summing_to_one_entry_uses_the_singular_wording()
    {
        var only = Item("registry-leftover", CleanupKind.RegistryCleanup);
        only.Files = 1;
        var group = new CleanupGroupViewModel(CleanupGroupKey.Registry, [only]);

        Assert.Equal(string.Format(LocalizationService.Instance["CleanupRegistryEntryCountOne"], 1), group.SizeText);
    }

    /// <summary>"1 of 1 area selected", not "1 of 1 areas selected", the plural agrees with the VISIBLE
    /// total, which can itself be exactly 1 once the risk-level filter narrows the group down.</summary>
    [Fact]
    public void A_group_with_exactly_one_visible_area_uses_the_singular_wording()
    {
        var only = Item("user-temp");
        var group = new CleanupGroupViewModel(CleanupGroupKey.TempFiles, [only]);

        Assert.Equal(
            string.Format(LocalizationService.Instance["CleanupSelectedCountOne"], group.SelectedCount, 1),
            group.SelectedCountText);
    }

    /// <summary>A ticked drive optimisation row is outside the group toggle, so it must stay outside the
    /// header count as well: otherwise the header reads more areas selected than it lists.</summary>
    [Fact]
    public void A_ticked_drive_optimisation_row_is_not_counted_in_the_group_header()
    {
        var temp = Item("user-temp");
        var defrag = Item("defrag-C", CleanupKind.Defragmentation);
        temp.IsSelected = true;
        defrag.IsSelected = true;
        var group = new CleanupGroupViewModel(CleanupGroupKey.WindowsMaintenance, [temp, defrag]);

        Assert.Equal(1, group.SelectedCount);
        Assert.Equal(
            string.Format(LocalizationService.Instance["CleanupSelectedCountOne"], 1, 1),
            group.SelectedCountText);
        Assert.Equal(true, group.IsSelected);
    }

    /// <summary>Zero items selected among several visible ones must show the header checkbox UNCHECKED,
    /// not indeterminate, the tri-state switch only ever compared <c>SelectedCount</c> against the full
    /// <c>VisibleCount</c>, with no explicit zero case, so "none selected" fell through to the same
    /// <c>null</c> (indeterminate) result as "some selected".</summary>
    [Fact]
    public void A_group_with_nothing_selected_shows_unchecked_not_indeterminate()
    {
        var first = Item("user-temp");
        var second = Item("windows-temp");
        first.IsSelected = false;
        second.IsSelected = false;
        var group = new CleanupGroupViewModel(CleanupGroupKey.TempFiles, [first, second]);

        Assert.Equal(false, group.IsSelected);
    }

    /// <summary>Every group actually named in the dialog order has at least one real catalogue entry,
    /// an empty group would be a header over nothing.</summary>
    [Fact]
    public void Every_group_in_the_display_order_has_at_least_one_member()
    {
        var byKey = CleanupCatalog.Build()
            .Select(CleanupGroups.For)
            .Where(key => key is not null)
            .Select(key => key!.Value)
            .ToHashSet();

        foreach (var key in CleanupGroups.Order)
            Assert.Contains(key, byKey);
    }
}
