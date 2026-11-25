using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UltimateUpdater.Views.Uninstall;

/// <summary>The uninstaller window. Unowned and independent like the cleanup window (see MainViewModel.ShowUninstaller); deliberately no DismissOnDeactivate, so working in the main window leaves it open.</summary>
public partial class UninstallerWindow
{
    private readonly UninstallerViewModel _viewModel;

    /// <summary>The row of the last plain checkbox click, where a Shift+click range starts.</summary>
    private UninstallRowViewModel? _anchor;

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
                    await viewModel.LoadAsync();
            }
            catch (Exception ex)
            {
                // LoadAsync already catches everything it can; this is the second line of defence, since an exception in an async void continuation takes the whole app down.
                viewModel.Logging.Error("Uninstaller: the first list load faulted unexpectedly.", ex);
            }
        };

        DialogKeyboard.CloseOnEscape(this);
    }

    /// <summary>
    /// Shift+click on a row's checkbox ticks the range from the last clicked row (Risky rows excepted, they are only
    /// ever ticked by their own click); a plain click remembers its row as the next range's start and ticks normally.
    /// </summary>
    private void OnRowCheckBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UninstallRowViewModel row })
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _anchor is not null)
        {
            var section = _viewModel.Sections.FirstOrDefault(s => s.Rows.Contains(row) && s.Rows.Contains(_anchor));
            if (section is not null)
            {
                _viewModel.SelectRange(section.Rows, _anchor, row);
                e.Handled = true;
                return;
            }
        }

        _anchor = row;
    }

    /// <summary>Space ticks or unticks the highlighted rows together (one row: like its own checkbox).</summary>
    private void OnRowListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not System.Windows.Controls.ListBox list || e.OriginalSource is System.Windows.Controls.CheckBox)
            return;

        _viewModel.ToggleRows(list.SelectedItems.OfType<UninstallRowViewModel>().ToList());
        e.Handled = true;
    }
}
