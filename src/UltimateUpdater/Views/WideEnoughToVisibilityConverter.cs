using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>
/// Shows an element only while its container is at least ConverterParameter pixels wide. The install strip
/// carries the current item, a progress bar, the batch figures and two buttons, and at the window's own
/// 860px minimum they do not all fit, the secondary figures give way rather than being clipped.
/// Bind it to the ActualWidth of a container whose width does NOT depend on this element (the strip is the
/// DockPanel's filling child, so its width comes from the docked buttons beside it), or the collapse would
/// free space, the element would reappear and layout would oscillate.
/// </summary>
public sealed class WideEnoughToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d && double.IsFinite(d) ? d : 0d;
        var minimum = parameter is string s
                      && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m)
            ? m
            : 0d;

        // Before the first layout pass everything measures 0 wide; that must not hide the element for good.
        return width <= 0d || width >= minimum ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
