using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>An int greater than zero → Visible, else Collapsed. The "hide sources without a tool"
/// toggle only makes sense to show at all once there is at least one such source.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
