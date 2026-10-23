using System.Windows;
using Wpf.Ui.Controls;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

public partial class HistoryWindow : FluentWindow
{
    private readonly LoggingService _logging;

    public HistoryWindow(HistoryViewModel viewModel, LoggingService logging)
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        _logging = logging;
        DialogKeyboard.CloseOnEscape(this);
        DialogSizing.FitHeightToContent(this);

        // clicking away from a dialog that only shows things closes it
        DismissOnDeactivate.Attach(this);

        Closing += (_, _) => viewModel.SaveTableLayout();
    }

    /// <summary>How far around the entry's own timestamp a log excerpt is allowed to reach; wide enough
    /// to catch the "inst" lines a real attempt writes (start, result), tight enough that it cannot pull
    /// in an unrelated install from just before or after this one.</summary>
    private static readonly TimeSpan LogExcerptWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Opens the row's raw error text in the same colourised, copy-button window a failed
    /// install row already uses (MainViewModel.ShowErrorDetail); reused as-is rather than re-plumbed
    /// through a view model, since the row view model stays free of view types either way.
    /// </summary>
    private void OnShowErrorDetail(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryRowViewModel row
            || string.IsNullOrWhiteSpace(row.Error))
        {
            return;
        }

        var (detail, sourceKey) = ResolveDetail(row);
        var versionChange = row.VersionChangeText;
        var detailWindow = new ErrorDetailWindow(
            new ErrorDetailViewModel(
                row.Name, row.SourceDisplay, versionChange, detail, _logging, detailSourceKey: sourceKey))
        {
            Owner = this,
        };
        detailWindow.Show();
    }

    /// <summary>
    /// In order: the entry's own stored output, then a log excerpt around when it ran (category "inst"),
    /// then the short sentence that was always shown before this field existed; an entry from before Detail existed
    /// still shows something better than nothing whenever the day's log has not been rotated away yet.
    /// </summary>
    private (string Detail, string? SourceKey) ResolveDetail(HistoryRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.Detail))
            return (row.Detail, null);

        var excerpt = _logging.ReadAround(row.Timestamp, LogExcerptWindow, "inst");
        if (excerpt.Count > 0)
            return (string.Join(Environment.NewLine, excerpt), "ErrorDetailSourceLog");

        return (row.Error!, "ErrorDetailSourceShort");
    }

    /// <summary>Enter/Space on a sortable column header; see <see cref="KeyboardActivation"/>.</summary>
    private void OnClickTargetKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        KeyboardActivation.ActivateOnEnterOrSpace(sender, e);
}
