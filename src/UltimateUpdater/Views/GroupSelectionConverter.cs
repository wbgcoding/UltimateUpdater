using System.Globalization;
using System.Windows.Data;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// The tri-state for a category group's own checkbox: every installable row in the group ticked (true),
/// none (false), some (null → indeterminate).
/// A <see cref="CollectionViewGroup"/> raises nothing when a row inside it is selected, so the binding is a
/// MultiBinding over the group AND MainViewModel.SelectionVersion; a counter that changes on every
/// selection change. The second value is not read; it exists so WPF re-evaluates this converter, which is
/// what makes "select all" visibly tick the category boxes too.
/// </summary>
public sealed class GroupSelectionConverter : IMultiValueConverter
{
    /// <summary>Returns bool?; null is the indeterminate box, which is a value here, not a missing one.</summary>
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not CollectionViewGroup group)
            return false;

        var installable = group.Items.OfType<UpdateRowViewModel>().Where(row => row.CanInstall).ToList();
        if (installable.Count == 0)
            return false;

        var selected = installable.Count(row => row.IsSelected);
        return selected == 0 ? false : selected == installable.Count ? true : null;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
