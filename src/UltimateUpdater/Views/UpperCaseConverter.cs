using System.Globalization;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>Display-only capitals for the small category labels above each block of rows. The binding
/// keeps the real name (screen readers and the collapsed-state key read that, not this text), XAML has no
/// text-transform of its own, and the UI culture, not the binding's default en-US, decides the casing so Turkish keeps its dotted I.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(CultureInfo.CurrentUICulture) ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
