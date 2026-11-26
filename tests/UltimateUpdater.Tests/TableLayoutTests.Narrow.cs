using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>The narrow-table rule: Publisher, then Date, give up their lane while the Name column would get too little. Derived from the table width only, never persisted, never in the chooser.</summary>
public partial class TableLayoutTests
{
    private static MainViewModel BuildMainViewModelWithRows(out SettingsService settings)
    {
        settings = CreateSettings();
        var viewModel = BuildMainViewModel(settings);
        viewModel.Updates.Add(FullRow("Vendor.App"));
        return viewModel;
    }

    [Fact]
    public void An_unmeasured_table_collapses_nothing()
    {
        var viewModel = BuildMainViewModelWithRows(out _);

        Assert.False(viewModel.IsMainColumnCollapsed("publisher"));
        Assert.False(viewModel.IsMainColumnCollapsed("date"));
    }

    [Theory]
    [InlineData(1100, false, false)] // wide: everything shows
    [InlineData(924, false, false)]  // Name gets exactly 180
    [InlineData(923, true, false)]   // one px less: Publisher goes
    [InlineData(804, true, false)]   // Name 180 without Publisher: Date stays
    [InlineData(803, true, true)]    // under 180: Date goes too
    [InlineData(600, true, true)]
    public void Publisher_then_date_collapse_as_the_name_column_runs_short(double width, bool publisherGone, bool dateGone)
    {
        var viewModel = BuildMainViewModelWithRows(out _);

        viewModel.SetMainTableWidth(width);

        Assert.Equal(publisherGone, viewModel.IsMainColumnCollapsed("publisher"));
        Assert.Equal(dateGone, viewModel.IsMainColumnCollapsed("date"));
        Assert.False(viewModel.IsMainColumnCollapsed("version"));
        Assert.False(viewModel.IsMainColumnCollapsed("size"));
    }

    [Fact]
    public void The_chooser_keeps_the_users_choice_while_the_table_is_narrow()
    {
        var viewModel = BuildMainViewModelWithRows(out _);

        viewModel.SetMainTableWidth(700);

        Assert.True(viewModel.IsMainColumnCollapsed("publisher"));
        Assert.False(viewModel.IsMainColumnHidden("publisher"));
        Assert.False(viewModel.IsMainColumnHidden("date"));
    }

    [Fact]
    public void Widening_the_window_brings_the_columns_back()
    {
        var viewModel = BuildMainViewModelWithRows(out _);

        viewModel.SetMainTableWidth(700);
        Assert.True(viewModel.IsMainColumnCollapsed("publisher"));
        Assert.True(viewModel.IsMainColumnCollapsed("date"));

        viewModel.SetMainTableWidth(1280);
        Assert.False(viewModel.IsMainColumnCollapsed("publisher"));
        Assert.False(viewModel.IsMainColumnCollapsed("date"));
    }

    [Fact]
    public void A_column_the_user_already_hid_does_not_count_against_the_name_width()
    {
        var viewModel = BuildMainViewModelWithRows(out _);
        viewModel.SetMainColumnHidden("publisher", true);

        // Without Publisher the Name column gets 810 - 624 = 186: enough, so Date stays.
        viewModel.SetMainTableWidth(810);

        Assert.False(viewModel.IsMainColumnCollapsed("date"));
        Assert.True(viewModel.IsMainColumnHidden("publisher"));
    }

    [Fact]
    public void The_narrow_collapse_is_not_saved_with_the_layout()
    {
        var viewModel = BuildMainViewModelWithRows(out var settings);

        viewModel.SetMainTableWidth(700);
        viewModel.SaveMainTableLayout();

        var state = settings.Current.Ui.Tables["main"];
        Assert.DoesNotContain("publisher", state.HiddenColumns);
        Assert.DoesNotContain("date", state.HiddenColumns);
    }

    [Fact]
    public void A_sub_pixel_width_change_is_ignored()
    {
        var viewModel = BuildMainViewModelWithRows(out _);

        viewModel.SetMainTableWidth(924.2);
        Assert.False(viewModel.IsMainColumnCollapsed("publisher"));

        // 923.9 would collapse Publisher, but a 0.3 px move is layout noise and never re-evaluates.
        viewModel.SetMainTableWidth(923.9);
        Assert.False(viewModel.IsMainColumnCollapsed("publisher"));
    }
}
