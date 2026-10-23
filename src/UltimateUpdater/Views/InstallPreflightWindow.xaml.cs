using System.Windows.Input;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// Shown only when <see cref="InstallPreflightViewModel.Lines"/> is non-empty, right before a batch is
/// queued (see <see cref="MainViewModel.Install"/>). Always opened via <c>ShowDialog</c>: a start
/// button sets <see cref="System.Windows.Window.DialogResult"/> exactly like every other themed dialog in
/// this app. With blocked updates "close and continue" is the default instead of the plain Start.
/// </summary>
public partial class InstallPreflightWindow
{
    public InstallPreflightWindow(InstallPreflightViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = Services.ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        DialogKeyboard.CloseOnEscape(this);
        DialogSizing.FitHeightToContent(this);
        var defaultButton = viewModel.HasBlockers ? CloseProgramsButton : StartButton;
        defaultButton.IsDefault = true;
        FocusManager.SetFocusedElement(this, defaultButton);

        viewModel.RequestClose += OnRequestClose;
        Closed += (_, _) => viewModel.RequestClose -= OnRequestClose;
    }

    private void OnRequestClose()
    {
        DialogResult = ((InstallPreflightViewModel)DataContext).Started;
        Close();
    }
}
