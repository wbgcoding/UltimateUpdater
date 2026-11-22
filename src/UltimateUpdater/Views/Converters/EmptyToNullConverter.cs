using System.Globalization;
using System.Windows.Data;

namespace UltimateUpdater.Views.Converters;

/// <summary>Turns an empty or blank string into null, so a ToolTip bound to text that is sometimes empty
/// shows nothing instead of an empty box.</summary>
public sealed class EmptyToNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && string.IsNullOrWhiteSpace(text) ? null : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
