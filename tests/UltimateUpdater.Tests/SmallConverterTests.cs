using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UltimateUpdater.Models;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>Value converters behind the table: they decide checkbox state, visibility and label casing.</summary>
[Collection("UiLanguage")]
public class SmallConverterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static UpdateRowViewModel Row(string id, SourceTier tier = SourceTier.A, bool selected = false) =>
        new(new UpdateItem
        {
            Id = id, Name = id, SourceId = "winget", Category = UpdateCategory.Software,
            InstalledVersion = "1.0", AvailableVersion = "2.0", Tier = tier,
        })
        { IsSelected = selected };

    /// <summary>The one real group of a view over the given rows (a CollectionViewGroup cannot be built by hand).</summary>
    private static CollectionViewGroup GroupOf(params UpdateRowViewModel[] rows)
    {
        var view = new ListCollectionView(rows.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription { Converter = new ConstantKey() });
        return (CollectionViewGroup)view.Groups!.Single();
    }

    private sealed class ConstantKey : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => "all";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    // ---- GroupSelectionConverter

    private static object? Select(params object[] values) =>
        new GroupSelectionConverter().Convert(values, typeof(bool?), null, Invariant);

    [Fact]
    public void A_group_with_every_installable_row_ticked_is_checked() =>
        Assert.Equal(true, Select(GroupOf(Row("a", selected: true), Row("b", selected: true)), 0));

    [Fact]
    public void A_group_with_none_ticked_is_unchecked() =>
        Assert.Equal(false, Select(GroupOf(Row("a"), Row("b")), 0));

    [Fact]
    public void A_group_with_some_ticked_is_indeterminate_which_is_null() =>
        Assert.Null(Select(GroupOf(Row("a", selected: true), Row("b")), 0));

    [Fact]
    public void Rows_that_cannot_install_do_not_count_toward_the_tri_state()
    {
        // the manual-only row is never ticked and must not keep the box from reading "all ticked"
        var group = GroupOf(Row("a", selected: true), Row("b", selected: true), Row("manual", SourceTier.C));

        Assert.Equal(true, Select(group, 0));
    }

    [Fact]
    public void A_group_of_only_manual_rows_is_unchecked_not_indeterminate() =>
        Assert.Equal(false, Select(GroupOf(Row("m1", SourceTier.C), Row("m2", SourceTier.C)), 0));

    [Fact]
    public void An_empty_or_foreign_value_list_is_unchecked()
    {
        Assert.Equal(false, Select());
        Assert.Equal(false, Select("not a group", 0));
    }

    [Fact]
    public void The_group_checkbox_cannot_be_converted_back() =>
        Assert.Throws<NotSupportedException>(() =>
            new GroupSelectionConverter().ConvertBack(true, [typeof(object)], null, Invariant));

    // ---- boolean and visibility converters

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBoolean_negates_in_both_directions(bool input, bool expected)
    {
        var converter = new InverseBooleanConverter();

        Assert.Equal(expected, converter.Convert(input, typeof(bool), null, Invariant));
        Assert.Equal(expected, converter.ConvertBack(input, typeof(bool), null, Invariant));
    }

    [Fact]
    public void InverseBoolean_treats_a_non_bool_as_false()
    {
        // "not a bool" is not "true negated": an unset binding must not enable a control
        Assert.Equal(false, new InverseBooleanConverter().Convert(null, typeof(bool), null, Invariant));
        Assert.Equal(false, new InverseBooleanConverter().Convert("true", typeof(bool), null, Invariant));
    }

    [Theory]
    [InlineData(true, Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    [InlineData(null, Visibility.Visible)]
    public void InverseBoolToVisibility_collapses_only_for_true(bool? input, Visibility expected) =>
        Assert.Equal(expected, new InverseBoolToVisibilityConverter().Convert(input, typeof(Visibility), null, Invariant));

    [Theory]
    [InlineData(Visibility.Visible, false)]
    [InlineData(Visibility.Collapsed, true)]
    [InlineData(Visibility.Hidden, true)]
    public void InverseBoolToVisibility_converts_back_to_the_opposite_flag(Visibility input, bool expected) =>
        Assert.Equal(expected, new InverseBoolToVisibilityConverter().ConvertBack(input, typeof(bool), null, Invariant));

    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Hidden)]
    [InlineData(null, Visibility.Hidden)]
    public void BoolToHidden_keeps_the_space_by_hiding_instead_of_collapsing(bool? input, Visibility expected) =>
        Assert.Equal(expected, new BoolToHiddenVisibilityConverter().Convert(input, typeof(Visibility), null, Invariant));

    [Fact]
    public void BoolToHidden_converts_back_to_true_only_for_visible()
    {
        var converter = new BoolToHiddenVisibilityConverter();

        Assert.Equal(true, converter.ConvertBack(Visibility.Visible, typeof(bool), null, Invariant));
        Assert.Equal(false, converter.ConvertBack(Visibility.Hidden, typeof(bool), null, Invariant));
        Assert.Equal(false, converter.ConvertBack(Visibility.Collapsed, typeof(bool), null, Invariant));
    }

    // ---- UpperCaseConverter

    [Fact]
    public void UpperCase_follows_the_ui_culture_so_turkish_keeps_its_dotted_capital_i()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            Assert.Equal("İŞLEM", new UpperCaseConverter().Convert("işlem", typeof(string), null, Invariant));

            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal("ILEM", new UpperCaseConverter().Convert("ilem", typeof(string), null, Invariant));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void UpperCase_of_a_missing_value_is_empty_and_cannot_be_converted_back()
    {
        Assert.Equal(string.Empty, new UpperCaseConverter().Convert(null, typeof(string), null, Invariant));
        Assert.Equal(string.Empty, new UpperCaseConverter().Convert(42, typeof(string), null, Invariant));
        Assert.Throws<NotSupportedException>(() =>
            new UpperCaseConverter().ConvertBack("X", typeof(string), null, Invariant));
    }
}
