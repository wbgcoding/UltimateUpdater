using System.Globalization;
using System.Windows;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// The converter takes a values ARRAY (IMultiValueConverter) instead of a single value, so it can be
/// bound alongside MainViewModel.SelectionVersion, the second value is never read, it exists only so WPF
/// has a reason to re-run this converter when a row inside the group changes state, the same pattern
/// GroupSelectionConverter already uses. That re-evaluation timing itself is a live WPF binding behaviour,
/// not something a plain unit test can observe; this covers the converter's own logic only.
/// </summary>
public class GroupInstallableToVisibilityConverterTests
{
    private readonly GroupInstallableToVisibilityConverter _converter = new();

    [Fact]
    public void No_values_is_hidden() =>
        Assert.Equal(Visibility.Hidden, _converter.Convert([], typeof(Visibility), null, CultureInfo.InvariantCulture));

    [Fact]
    public void A_non_group_first_value_is_hidden() =>
        Assert.Equal(Visibility.Hidden, _converter.Convert(["not a group", 0], typeof(Visibility), null, CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_is_not_supported() =>
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack(null, [typeof(object)], null, CultureInfo.InvariantCulture));
}
