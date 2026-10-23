using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>
/// A cleanup target card is visible when it is one of the default-visible categories OR the "show all
/// categories" toggle is open. Two bool inputs, in that order: <c>Target.DefaultVisible</c>,
/// <c>ShowAllCategories</c>. A MultiBinding rather than a Style MultiDataTrigger: a Style trigger's Setter
/// can never win over an already-set local value.
/// </summary>
public sealed class CleanupCardVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [bool defaultVisible, bool showAllCategories])
            return Visibility.Visible;

        return defaultVisible || showAllCategories ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
