using System.Globalization;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>
/// A percentage (0;100) to a pixel width, given the track width as the converter parameter. Used to
/// draw a progress fill by hand; a WPF-UI ProgressBar measured against a star-sized or infinite width
/// on a table row sends WPF's layout into an infinite measure recursion, so the bar is two Borders and
/// this converter instead.
/// </summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0d;
        var track = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var t)
            ? t
            : 0d;

        percent = Math.Clamp(percent, 0d, 100d);
        return percent / 100d * track;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
