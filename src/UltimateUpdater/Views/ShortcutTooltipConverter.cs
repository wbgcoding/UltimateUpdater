using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace UltimateUpdater.Views;

/// <summary>
/// Appends a control's own keyboard shortcut onto its already-localized label for a ToolTip, read from
/// <see cref="KeyboardShortcuts.All"/> by the SAME key the label itself came from (the converter
/// parameter) rather than a second, separately hand-typed copy of the gesture. A key with no matching
/// entry passes the label through unchanged, so a control that has not been given a shortcut yet never
/// shows a broken tooltip.
/// </summary>
public sealed class ShortcutTooltipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = value as string ?? string.Empty;
        var gesture = KeyboardShortcuts.All.FirstOrDefault(entry => entry.LabelKey == parameter as string)?.Gesture;
        return gesture is null ? label : $"{label} ({gesture})";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
