using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>True → Collapsed, false → Visible; the opposite of the stock BooleanToVisibilityConverter,
/// for a panel that must disappear entirely (not merely hide) while some other flag is true.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !Equals(value, Visibility.Visible);
}
