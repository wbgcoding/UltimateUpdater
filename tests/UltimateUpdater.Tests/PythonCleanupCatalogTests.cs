using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The two catalogue rows themselves: safe/on by default for the pip leftovers, red/off/elevated for the
/// older-versions row, the same shape <see cref="DriverCleanupCatalogTests"/> already proves for its own
/// row. Building the catalogue is pure data; the real interpreter/registry read only happens behind
/// <see cref="PythonCleanupDiscoveryTests"/>'s own fakes.
/// </summary>
public class PythonCleanupCatalogTests
{
    [Fact]
    public void The_pip_leftovers_row_is_on_by_default_and_safe()
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == "pip-install-leftovers");

        Assert.Equal(CleanupKind.PipInstallLeftovers, target.Kind);
        Assert.True(target.DefaultEnabled);
        Assert.False(target.NeedsElevation);
        Assert.Equal(CleanupRiskLevel.Safe, target.RiskLevel);
    }

    [Fact]
    public void The_old_python_versions_row_is_off_by_default_and_needs_elevation()
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == "old-python-versions");

        Assert.Equal(CleanupKind.OldPythonVersions, target.Kind);
        Assert.False(target.DefaultEnabled);
        Assert.True(target.NeedsElevation);
        Assert.Equal(CleanupRiskLevel.Risky, target.RiskLevel);
    }

    [Fact]
    public void Only_the_old_python_versions_kind_target_resolves_as_an_elevated_target()
    {
        var resolved = CleanupWorker.ResolveElevatedTargets(
            ["old-python-versions", "old-python-versions", "not-a-real-id", "pip-install-leftovers"]);

        Assert.Single(resolved);
        Assert.Equal("old-python-versions", resolved[0].Id);
    }

    [Fact]
    public void Both_new_rows_resolve_to_a_known_dialog_group()
    {
        var pip = CleanupCatalog.Build().Single(t => t.Id == "pip-install-leftovers");
        var oldVersions = CleanupCatalog.Build().Single(t => t.Id == "old-python-versions");

        Assert.Equal(CleanupGroupKey.Caches, CleanupGroups.For(pip));
        Assert.Equal(CleanupGroupKey.Drivers, CleanupGroups.For(oldVersions));
    }

    /// <summary>The "none found" line binds to a computed property; it only appears if the end of the
    /// scan tells the binding to read it again.</summary>
    [Fact]
    public void The_none_found_line_is_raised_when_the_scan_ends()
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == "old-python-versions");
        var item = new CleanupItemViewModel(target, () => { });
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.IsScanning = false;

        Assert.Contains(nameof(CleanupItemViewModel.ShowsNoOldPythonVersionsFound), raised);
        Assert.True(item.ShowsNoOldPythonVersionsFound);

        raised.Clear();
        item.IsWaiting = true;

        Assert.Contains(nameof(CleanupItemViewModel.ShowsNoOldPythonVersionsFound), raised);
        Assert.False(item.ShowsNoOldPythonVersionsFound);
    }

    [Fact]
    public void A_language_switch_refreshes_each_older_version_row()
    {
        var target = CleanupCatalog.Build().Single(t => t.Id == "old-python-versions");
        var item = new CleanupItemViewModel(target, () => { });
        item.SetOldPythonVersionCandidates([new PythonVersionCandidateDto { PythonExe = @"C:\Python312\python.exe", DisplayLabel = "Python 3.12" }]);
        var raised = new List<string?>();
        item.OldPythonVersionCandidates[0].PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.RefreshLocalizedText();

        Assert.Contains(nameof(PythonVersionCandidateViewModel.DetailText), raised);
    }
}
