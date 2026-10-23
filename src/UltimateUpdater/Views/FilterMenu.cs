using System.Windows;
// ContextMenu/MenuItem collide with System.Windows.Forms (referenced for the tray icon, see
// GlobalUsings.cs); fully qualified here rather than adding more global aliases for a single file.
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace UltimateUpdater.Views;

/// <summary>
/// One checkable entry of the filter menu: a stable id, a label read live (so a language switch needs no
/// special handling), the current state and the setter.
/// </summary>
public sealed record FilterEntry(string Id, Func<string> Label, Func<bool> IsOn, Action<bool> Set);

/// <summary>
/// The table's filter menu: the table's switches, folded into one checkbox menu
/// built exactly like the column chooser (<see cref="ColumnChooser"/>), so both open, toggle shut and
/// follow the keyboard the same way.
/// </summary>
public static class FilterMenu
{
    /// <summary>Opens the menu anchored on <paramref name="anchor"/>; a second call while it is open closes
    /// it again (see <see cref="ColumnChooser.ShowMenu"/>).</summary>
    public static ContextMenu? ShowFor(FrameworkElement anchor, IReadOnlyList<FilterEntry> entries) =>
        ColumnChooser.ShowMenu(anchor, () => Build(entries));

    internal static ContextMenu Build(IReadOnlyList<FilterEntry> entries)
    {
        var menu = new ContextMenu();

        foreach (var entry in entries)
        {
            var item = new MenuItem
            {
                Header = entry.Label(),
                IsCheckable = true,
                IsChecked = entry.IsOn(),
            };
            // WPF flips IsChecked BEFORE raising Click, so it already carries the new, intended state.
            item.Click += (_, _) => entry.Set(item.IsChecked);
            menu.Items.Add(item);
        }

        return menu;
    }
}
