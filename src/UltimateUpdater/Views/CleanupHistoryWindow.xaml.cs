using System.Windows;
using Wpf.Ui.Controls;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>Earlier cleanup runs, pulled out of the cleanup dialog's own target list.
/// Shares the cleanup dialog's own CleanupViewModel instance rather than owning one: the history it
/// shows already lives there.</summary>
public partial class CleanupHistoryWindow : FluentWindow
{
    private readonly CleanupViewModel _viewModel;

    public CleanupHistoryWindow(CleanupViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        _viewModel = viewModel;
        DialogKeyboard.CloseOnEscape(this);
        DialogSizing.FitHeightToContent(this);

        // clicking away from a dialog that only shows things closes it
        DismissOnDeactivate.Attach(this);
    }

    /// <summary>The one destructive action in this window, so it is the one that asks; same
    /// split as the cleanup dialog's own OnClean (a Window owns the dialog, the view model owns the action).</summary>
    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;

        var confirmed = Dialogs.Confirm(
            loc["CleanupHistoryClearAllConfirmHeadline"],
            loc["CleanupHistoryClearAllConfirmDetail"],
            confirmText: loc["CleanupHistoryClearAll"],
            destructive: true);

        if (confirmed)
            _viewModel.ClearHistoryCommand.Execute(null);
    }
}
