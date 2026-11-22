using System.Windows;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Views.History;

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

    /// <summary>How far around the entry's timestamp a log excerpt may reach: wide enough to catch the "inst" lines
    /// of a real attempt, tight enough not to pull in an unrelated install.</summary>
    private static readonly TimeSpan LogExcerptWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Opens the row's raw error text in the same colourised window a failed install row uses
    /// (MainViewModel.ShowErrorDetail); the row view model stays free of view types.
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
    /// In order: the entry's stored output, then a log excerpt around when it ran (category "inst"), then the short
    /// summary sentence. An entry without stored detail still shows something while the day's log exists.
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
