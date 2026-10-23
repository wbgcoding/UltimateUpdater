using System.Windows;
using Wpf.Ui.Controls;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// Shows a source's real changelog text and/or release-page link. Opened on deliberate
/// request from a row's info icon; read-only and non-modal, same idiom as ErrorDetailWindow.
/// </summary>
public partial class ChangelogWindow : FluentWindow
{
    public ChangelogWindow(ChangelogViewModel viewModel)
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
