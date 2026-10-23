using System.Globalization;
using System.Windows;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// A cleanup category found nothing on the measured machines moves out of the default view
/// (<c>CleanupTarget.DefaultVisible = false</c>) rather than being deleted from the catalogue. Nothing here
/// is flipped to false yet, that needs a real two-machine measurement, but the mechanism itself, and the
/// promise that a hidden category is display-only and never excluded from scanning or selection, is covered
/// on its own.
/// </summary>
public class CleanupCategoryVisibilityTests
{
    private static CleanupTarget Target(bool defaultVisible = true, bool defaultEnabled = true) => new(
        "test-target", "SettingsDataFolder", "SettingsDataFolder", CleanupKind.Directories,
        defaultEnabled, CleanupRiskLevel.Safe, "SettingsDataFolder")
    {
        DefaultVisible = defaultVisible,
    };

    private static CleanupItemViewModel Item(bool defaultVisible = true, bool defaultEnabled = true) =>
        new(Target(defaultVisible, defaultEnabled), onSelectionChanged: () => { });

    [Fact]
    public void A_target_defaults_to_visible()
    {
        Assert.True(Target().DefaultVisible);
    }

    [Fact]
    public void Nothing_in_the_real_catalogue_is_hidden_yet()
    {
        // Point 4 of the item: nothing is removed or hidden in this round, only the mechanism is built.
        // The two-machine measurement that would justify flipping a real target is still outstanding.
        Assert.All(CleanupCatalog.Build(), target => Assert.True(target.DefaultVisible));
    }

    [Fact]
    public void A_hidden_category_stays_selected_and_countable()
    {
        var item = Item(defaultVisible: false, defaultEnabled: true);

        Assert.False(item.Target.DefaultVisible);
        // The one thing that decides whether a run and its totals actually include this row: unaffected
        // by DefaultVisible, which only ever touches what the resting list shows.
        Assert.True(item.IsSelected);
    }

    [Theory]
    [InlineData(true, false, Visibility.Visible)]
    [InlineData(false, false, Visibility.Collapsed)]
    [InlineData(false, true, Visibility.Visible)]
    [InlineData(true, true, Visibility.Visible)]
    public void Card_visibility_converter_combines_category_visibility_and_show_all(
        bool defaultVisible, bool showAllCategories, Visibility expected)
    {
        var converter = new CleanupCardVisibilityConverter();

        var result = converter.Convert(
            [defaultVisible, showAllCategories], typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Card_visibility_converter_fails_open_on_a_malformed_input()
    {
        var converter = new CleanupCardVisibilityConverter();

        var result = converter.Convert([true], typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void Group_stays_visible_while_at_least_one_member_passes_both_filters()
    {
        var converter = new CleanupGroupVisibilityConverter();
        var items = new[] { Item(defaultVisible: true), Item(defaultVisible: false) };

        var result = converter.Convert(
            [items, false], typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void Group_collapses_when_every_member_is_hidden_and_show_all_is_off()
    {
        var converter = new CleanupGroupVisibilityConverter();
        var items = new[] { Item(defaultVisible: false), Item(defaultVisible: false) };

        var result = converter.Convert(
            [items, false], typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void Group_reappears_once_show_all_categories_is_switched_on()
    {
        var converter = new CleanupGroupVisibilityConverter();
        var items = new[] { Item(defaultVisible: false), Item(defaultVisible: false) };

        var result = converter.Convert(
            [items, true], typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    /// <summary>The catalogue is built in the background after the dialog opens, so the expander's count
    /// and visibility must be re-read once it lands, or they stay at the empty list's zero for good.</summary>
    [Fact]
    public void The_hidden_category_count_is_raised_once_the_catalogue_arrives()
    {
        var logging = new LoggingService();
        var folder = Directory.CreateTempSubdirectory("wu-cleanup-hidden-").FullName;
        var settings = new SettingsService(logging, Path.Combine(folder, "settings.json"));
        var history = new CleanupHistoryService(logging, Path.Combine(folder, "history.json"));
        var viewModel = new CleanupViewModel(
            new CleanupService(logging), logging, settings, history: history,
            catalogBuilder: _ => [Target(defaultVisible: false)]);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        WpfHarness.WaitForCleanupCatalog(viewModel);

        Assert.Contains(nameof(CleanupViewModel.HiddenCategoriesCount), raised);
        Assert.Contains(nameof(CleanupViewModel.HasHiddenCategories), raised);
        Assert.Contains(nameof(CleanupViewModel.ShowAllCategoriesText), raised);
        Assert.Equal(1, viewModel.HiddenCategoriesCount);
    }
}
