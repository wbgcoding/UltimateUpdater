using UltimateUpdater.Models;
using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    private static readonly string[] FilterChipNames =
        ["FilterCriticalChip", "FilterIgnoredChip", "FilterManualChip", "FilterFailuresChip"];

    [Theory]
    [InlineData("de", 860)]
    [InlineData("de", 1160)]
    [InlineData("de", 1400)]
    [InlineData("en", 860)]
    [InlineData("en", 1160)]
    [InlineData("en", 1400)]
    [InlineData("pt", 860)]
    [InlineData("pt", 1160)]
    [InlineData("pt", 1400)]
    [InlineData("zh-Hans", 860)]
    [InlineData("zh-Hans", 1160)]
    [InlineData("pl", 1160)]
    [InlineData("tr", 1160)]
    public void Filter_chips_share_one_row_with_the_search_box_and_drop_their_text_only_when_needed(
        string language, int windowWidth)
    {
        RenderResult? result = null;
        var off = new List<string>();
        var visibleLabels = 0;
        var shownChips = 0;
        double panelWidth = 0;
        double searchHeight = 0, chipHeight = 0;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            // the failure chip is the widest case: it only exists while it has something to show
            viewModel.Updates[0].InstallState = RowInstallState.NeedsManualWork;
            Assert.True(viewModel.ShowPersistentFailureFilter);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-chips-{language}-{windowWidth}", windowWidth, 700);

            var search = FindByName<FrameworkElement>(content, "SearchBox")!;
            var panel = FindByName<WrapPanel>(content, "ToolbarWrapPanel")!;
            panelWidth = panel.ActualWidth;
            searchHeight = search.ActualHeight;
            var searchMid = search.TranslatePoint(default, panel).Y + search.ActualHeight / 2;

            foreach (var name in FilterChipNames)
            {
                var chip = FindByName<FrameworkElement>(content, name)!;
                shownChips++;
                chipHeight = chip.ActualHeight;
                var mid = chip.TranslatePoint(default, panel).Y + chip.ActualHeight / 2;
                if (chip.ActualWidth <= 0 || Math.Abs(mid - searchMid) > 8)
                    off.Add($"{name} (width {chip.ActualWidth:0}, mid {mid:0.#} vs search {searchMid:0.#})");

                var label = (TextBlock)((Panel)((ContentControl)chip).Content).Children[1];
                if (label.Visibility == Visibility.Visible)
                    visibleLabels++;
            }
        });

        AssertRendered(result);
        Assert.True(off.Count == 0, $"[{language} {windowWidth}, panel {panelWidth:0}] chips off the search row: {string.Join("; ", off)}");
        Assert.Equal(4, shownChips);
        Assert.InRange(chipHeight, searchHeight - 3, searchHeight + 3);
        if (windowWidth >= 1400)
            Assert.Equal(4, visibleLabels);
        if (windowWidth <= 860)
            Assert.Equal(0, visibleLabels);
        // whatever the width, all four labels or none: a mix would look broken
        Assert.True(visibleLabels is 0 or 4, $"[{language} {windowWidth}, panel {panelWidth:0}] {visibleLabels} labels shown");
    }
}
