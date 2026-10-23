using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// A group whose every member is hidden by <c>CleanupTarget.DefaultVisible</c> (and "show all categories"
/// is off) would be an empty card shell with a header and nothing under it. Two inputs, in that order:
/// <c>Items</c> (the group's own fixed member list), <c>ShowAllCategories</c> (the window's own toggle,
/// reached the same AncestorType=Window way <see cref="CleanupCardVisibilityConverter"/> already does).
/// The card shows only when at least one member is either one of the default-visible categories or the
/// toggle is open.
/// </summary>
public sealed class CleanupGroupVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [IEnumerable items, bool showAllCategories])
            return Visibility.Visible;

        foreach (var entry in items)
        {
            if (entry is CleanupItemViewModel item && (item.Target.DefaultVisible || showAllCategories))
                return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
