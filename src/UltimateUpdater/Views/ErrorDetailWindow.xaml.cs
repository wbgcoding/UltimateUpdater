using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Wpf.Ui.Controls;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// Shows the raw output behind a failed install or uninstall. Opened on deliberate request from a
/// row's "Details" affordance; read-only and non-modal (closes on a click in the owner, see
/// <see cref="DismissOnDeactivate"/>), so nothing here can change what happened.
/// </summary>
public partial class ErrorDetailWindow : FluentWindow
{
    public ErrorDetailWindow(ErrorDetailViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        DialogKeyboard.CloseOnEscape(this);
        DialogSizing.FitHeightToContent(this);

        // clicking away from a dialog that only shows things closes it
        DismissOnDeactivate.Attach(this);

        OutputBox.Document = BuildDocument(viewModel.Detail);
    }

    /// <summary>
    /// Detail never changes after construction (ErrorDetailViewModel has no setter for it), so the
    /// FlowDocument is built exactly once here rather than through a converter or a change handler.
    /// </summary>
    private static FlowDocument BuildDocument(string detail)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };

        foreach (var segment in ErrorDetailFormatter.Format(detail))
            paragraph.Inlines.Add(BuildInline(segment));

        return new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }

    /// <summary>
    /// One segment, one inline. An exit code becomes a small capsule rather than coloured bold text: fatal (red) and
    /// retryable (amber) side by side as plain text explain nothing, while a capsule reads as a marked code without
    /// a legend the window has no room for.
    /// </summary>
    private static Inline BuildInline(ErrorDetailFormatter.Segment segment)
    {
        switch (segment.Kind)
        {
            case ErrorDetailFormatter.SegmentKind.RungLabel:
            {
                var run = new Run(segment.Text) { FontWeight = FontWeights.SemiBold };
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                return run;
            }
            case ErrorDetailFormatter.SegmentKind.ToolName:
                return new Run(segment.Text) { FontWeight = FontWeights.SemiBold };
            case ErrorDetailFormatter.SegmentKind.ExitCodeFatal:
                return ExitCodeCapsule(segment.Text, "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush");
            case ErrorDetailFormatter.SegmentKind.ExitCodeRetryable:
                return ExitCodeCapsule(segment.Text, "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush");
            default:
                return new Run(segment.Text);
        }
    }

    /// <summary>
    /// The same Border+TextBlock capsule shape the table's critical badge uses (MainWindow.xaml), for a consistent
    /// "marked" look. Tabular figures, so exit-code digits line up like every other number in the app.
    /// </summary>
    private static InlineUIContainer ExitCodeCapsule(string text, string backgroundKey, string foregroundKey)
    {
        var label = new System.Windows.Controls.TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
        };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foregroundKey);

        var capsule = new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(4),
            Child = label,
        };
        capsule.SetResourceReference(Border.BackgroundProperty, backgroundKey);

        // BaselineAlignment keeps the capsule on the surrounding prose's text baseline instead of its own box's
        // bottom edge.
        return new InlineUIContainer(capsule) { BaselineAlignment = BaselineAlignment.Center };
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>The click starts the row's confirmed reinstall flow (the bound command); this window has nothing
    /// left to show once it does, as the progress is in the main window.</summary>
    private void OnReinstallClicked(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
}
