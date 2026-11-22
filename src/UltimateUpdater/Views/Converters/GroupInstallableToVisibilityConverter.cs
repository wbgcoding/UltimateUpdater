using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UltimateUpdater.Views.Converters;

/// <summary>
/// Visible only when a category group contains at least one installable row. The "Open in a store" and
/// "Open in a launcher" groups are manual-open rows with nothing to tick, so their select-all checkbox is hidden.
/// Hidden, not collapsed, so the header keeps its height and the label stays in the same column as every other
/// category header.
/// A <see cref="CollectionViewGroup"/> raises nothing when a row inside it changes state, so this has the same
/// cause and fix as <see cref="GroupSelectionConverter"/>: a MultiBinding over the group AND
/// MainViewModel.SelectionVersion, giving WPF a reason to re-run the converter.
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
