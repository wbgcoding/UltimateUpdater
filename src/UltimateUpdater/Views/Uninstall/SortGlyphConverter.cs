using System.Globalization;
using System.Windows.Data;

namespace UltimateUpdater.Views.Uninstall;

/// <summary>The sort direction mark after a column header's text: an arrow on the sorted column, nothing on the others. Values: the sorted column, descending; parameter: this header's column.</summary>
public sealed class SortGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] as string != parameter as string)
            return string.Empty;

        return values[1] is true ? " ▼" : " ▲";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
