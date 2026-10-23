using System.Windows;
using Wpf.Ui.Controls;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// Manages the ignore list: every ignored package/version, with a remove button per row and a
/// remove-all behind a confirmation. Opened from the settings sources card; non-modal, same idiom as
/// every other owned dialog in this app.
/// </summary>
public partial class IgnoreListWindow : FluentWindow
{
    public IgnoreListWindow(IgnoreListViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = Services.ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        DialogKeyboard.CloseOnEscape(this);
        DialogSizing.FitHeightToContent(this);

        DismissOnDeactivate.Attach(this);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
