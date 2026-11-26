using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UltimateUpdater.Views.Uninstall;

/// <summary>The uninstaller window. Unowned and independent like the cleanup window (see MainViewModel.ShowUninstaller); deliberately no DismissOnDeactivate, so working in the main window leaves it open.</summary>
public partial class UninstallerWindow
{
    private readonly UninstallerViewModel _viewModel;

    public UninstallerWindow(UninstallerViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        _viewModel = viewModel;

        void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => viewModel.RefreshText();
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;

        Loaded += async (_, _) =>
        {
            try
            {
                if (viewModel.AllRows.Count == 0 && !viewModel.IsLoading)
                    await viewModel.OpenAsync();
            }
            catch (Exception ex)
            {
                // OpenAsync already catches everything it can; this is the second line of defence, since an exception in an async void continuation takes the whole app down.
                viewModel.Logging.Error("Uninstaller: the first list load faulted unexpectedly.", ex);
            }
        };

        DialogKeyboard.CloseOnEscape(this);
    }

    /// <summary>
    /// A left click anywhere on a row (the box, the name, the empty space) toggles its tick; Shift+click ticks the range
    /// from the last click. Left unhandled, so the list still highlights the row for Space and Ctrl/Shift selection.
    /// </summary>
    private void OnRowListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list || e.OriginalSource is not DependencyObject source)
            return;

        var item = ItemsControl.ContainerFromElement(list, source) as ListBoxItem;
        if (item?.DataContext is not UninstallRowViewModel row)
            return;

        _viewModel.ClickRow(row, list.Items.OfType<UninstallRowViewModel>().ToList(), (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    /// <summary>Space ticks or unticks the highlighted rows together (one row: like its own checkbox).</summary>
    private void OnRowListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not System.Windows.Controls.ListBox list)
            return;

        _viewModel.ToggleRows(list.SelectedItems.OfType<UninstallRowViewModel>().ToList());
        e.Handled = true;
    }
}
