using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>True → Visible, false → Hidden (never Collapsed); for a control whose reserved space must
/// stay in the layout even while it is not shown, so sibling content does not shift.</summary>
public sealed class BoolToHiddenVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, Visibility.Visible);
}
