using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using UltimateUpdater.Models;

namespace UltimateUpdater.Views.Main;

/// <summary>The results table: group headers, row templates, the manual-open block and the column headers. Binds to the window's MainViewModel through the inherited DataContext.</summary>
public partial class UpdatesTable
{
    public UpdatesTable()
    {
        InitializeComponent();
    }

    /// <summary>The language-independent identity of a group; see <see cref="UpdateRowViewModel.CategoryKey"/>. The view groups by the translated CategoryDisplay text, so the first row's key answers for the whole group.</summary>
    private static string? StableGroupKey(CollectionViewGroup group) =>
        group.Items.Count > 0 && group.Items[0] is UpdateRowViewModel row ? row.CategoryKey : group.Name as string;

    /// <summary>Applies the remembered collapse state to a group header as it is (re)built.</summary>
    private void OnGroupExpanderLoaded(object sender, RoutedEventArgs e)
    {
        // The un-prefixed Expander in XAML is the standard WPF control, not WPF-UI's own.
        if (sender is System.Windows.Controls.Expander { DataContext: CollectionViewGroup group } expander
            && DataContext is MainViewModel viewModel)
        {
            expander.IsExpanded = viewModel.IsGroupExpanded(StableGroupKey(group));
        }
    }

    /// <summary>A row's menu opened: a row that could map to an installed program has it looked up now (once per scan), so "Uninstall" appears only when it fits.</summary>
    private void RowActionsMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ContextMenu { DataContext: UpdateRowViewModel row })
            row.OnContextMenuOpened();
    }

    /// <summary>Remembers a group's collapse state so the next rebuild keeps it.</summary>
    private void OnGroupExpanderToggled(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Expander { DataContext: CollectionViewGroup group } expander
            && DataContext is MainViewModel viewModel)
        {
            viewModel.SetGroupExpanded(StableGroupKey(group), expander.IsExpanded);
        }
    }

    /// <summary>The main table's column chooser, built from MainViewModel's static column list by the shared builder in Views/ColumnChooser.cs.</summary>
    private void OnColumnsButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || DataContext is not MainViewModel viewModel)
            return;

        ColumnChooser.ShowFor(
            anchor,
            MainViewModel.MainTableColumns,
            viewModel.IsMainColumnHidden,
            viewModel.SetMainColumnHidden,
            viewModel.ResetMainColumns);
    }

    /// <summary>Double-clicking a row runs its Open action where it has one: store and launcher rows exist only to be opened and carry no checkbox.</summary>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UpdateRowViewModel { CanOpen: true } row })
            row.OpenCommand.Execute(null);
    }

    /// <summary>Row-level keyboard shortcuts: Space toggles the checkbox, Enter runs the primary action, Entf ignores. Fires only when the row itself has focus, not its buttons or checkbox, which handle Space and Enter themselves. Attached once at the ListBox, since PreviewKeyDown tunnels to whichever element has focus. Arrow and Tab movement needs no code.</summary>
    private void OnUpdatesListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not FrameworkElement { DataContext: UpdateRowViewModel row } focused)
            return;

        if (focused is not (System.Windows.Controls.ListBoxItem or System.Windows.Controls.Border))
            return;

        // Chorded keys belong to the window's KeyBindings; handling Ctrl+Enter here would run the row's own action instead of "install selected".
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Space:
                row.ToggleSelectCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                row.ExecuteDefaultAction();
                e.Handled = true;
                break;
            case Key.Delete:
                if (row.IgnoreCommand.CanExecute(null))
                {
                    row.IgnoreCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>Reachable from inside the table (CommandBinding beside <c>UpdatesListBox</c>): forwards to the SelectAllCommand the window-level Ctrl+A binding runs, so the selection logic exists once.</summary>
    private void OnUpdatesListSelectAllExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SelectAllCommand.Execute(null);

        e.Handled = true;
    }

    private void OnUpdatesListSelectAllCanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
        e.Handled = true;
    }


    /// <summary>The sortable column headers share the window's click-target key handling (see <see cref="MainWindow.HandleClickTargetKeyDown"/>).</summary>
    private void OnClickTargetKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        MainWindow.HandleClickTargetKeyDown(sender, e);
}
