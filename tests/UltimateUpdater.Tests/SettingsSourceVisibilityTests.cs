using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The settings window's own source list hides a source whose tool is not on this machine
/// (<c>ScanOrchestrator.IsKnownNotInstalled</c>, injected here as a plain predicate), with the same counted
/// "show all" escape the cleanup dialog's own category toggle uses. Real wiring of the live predicate at the
/// window's own call site is outside this batch's file scope (see the item's deviation note); this covers
/// the mechanism the predicate feeds.
/// </summary>
public class SettingsSourceVisibilityTests
{
    private static SettingsViewModel Create(Func<string, bool>? isNotInstalled = null)
    {
        var logging = new LoggingService();
        // Isolated settings file: the parameterless SettingsService constructor reads/writes the real,
        // shared settings.json this machine actually uses, which a source-enabled assertion must never
        // depend on.
        var settingsPath = Path.Combine(Directory.CreateTempSubdirectory("wu-source-visibility-").FullName, "settings.json");
        return new SettingsViewModel(
            new SettingsService(logging, settingsPath), logging, new ThemeService(), new AutostartService(logging),
            ["winget", "npm", "scoop"], isSourceNotInstalled: isNotInstalled);
    }

    [Fact]
    public void No_predicate_hides_nothing()
    {
        var viewModel = Create();

        Assert.False(viewModel.HasHiddenSources);
        Assert.Equal(0, viewModel.HiddenSourceCount);
        Assert.Equal(3, viewModel.SourceToggleView.Cast<object>().Count());
    }

    [Fact]
    public void A_source_with_no_tool_on_this_machine_is_hidden_by_default()
    {
        var viewModel = Create(id => id == "scoop");

        Assert.True(viewModel.HasHiddenSources);
        Assert.Equal(1, viewModel.HiddenSourceCount);
        Assert.DoesNotContain(
            viewModel.SourceToggleView.Cast<SettingsViewModel.SourceToggle>(), t => t.Id == "scoop");
    }

    [Fact]
    public void Show_all_sources_reveals_the_hidden_one_again()
    {
        var viewModel = Create(id => id == "scoop");
        viewModel.ShowAllSources = true;

        Assert.Contains(
            viewModel.SourceToggleView.Cast<SettingsViewModel.SourceToggle>(), t => t.Id == "scoop");
    }

    [Fact]
    public void A_hidden_source_is_still_a_real_toggle_the_user_can_switch_back_on()
    {
        var viewModel = Create(id => id == "scoop");
        var hidden = viewModel.SourceToggles.Single(t => t.Id == "scoop");

        Assert.True(hidden.IsNotInstalled);
        Assert.True(hidden.IsEnabled);

        hidden.IsEnabled = false;
        Assert.False(hidden.IsEnabled);
    }
}
