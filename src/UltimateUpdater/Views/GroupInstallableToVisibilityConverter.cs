using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// Visible only when a category group contains at least one installable row. The "Open in a store" and
/// "Open in a launcher" groups are manual-open rows with nothing to tick, so their group-level
/// select-all checkbox is hidden, as there is nothing for it to select. Hidden, not
/// collapsed, so the header keeps the same height and the label stays in the same column as every other
/// category header rather than shifting left into the vacated checkbox space.
/// A <see cref="CollectionViewGroup"/> raises nothing when a row inside it changes state, so the last
/// installable row in a group being queued would otherwise leave this checkbox visibly stuck. It has the same
/// root cause as <see cref="GroupSelectionConverter"/> and the same fix, a MultiBinding over the group
/// AND MainViewModel.SelectionVersion so WPF has a reason to re-run this converter.
/// </summary>
public sealed class GroupInstallableToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var installable = values.Length > 0 && values[0] is CollectionViewGroup group
                          && group.Items.OfType<UpdateRowViewModel>().Any(row => row.CanInstall);

        return installable ? Visibility.Visible : Visibility.Hidden;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
