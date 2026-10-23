using System.Globalization;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>Negates a bool for bindings like IsEnabled="{Binding IsBusy, Converter=...}".</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}
