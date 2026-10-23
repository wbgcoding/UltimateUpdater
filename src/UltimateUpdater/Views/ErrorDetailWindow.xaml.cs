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
    /// One segment, one inline. An exit code becomes a small capsule (background + rounded corners)
    /// instead of just coloured bold text: fatal (red) and retryable (amber) sitting next to each other
    /// as plain coloured text gives no explanation of what the colour means, while a capsule reads as
    /// "this is a marked code" on its own, without needing a legend the window has no room for.
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
    /// Same Border+TextBlock capsule shape the table's own critical badge already uses (MainWindow.xaml)
    ///, kept visually consistent rather than inventing a second "marked" look. Tabular figures
    /// so an exit code's digits line up the same way every other number field in the app now does.
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

        // BaselineAlignment keeps the capsule sitting on the surrounding prose's text baseline instead
        // of its own box's bottom edge, otherwise every capsule floated a few pixels low.
        return new InlineUIContainer(capsule) { BaselineAlignment = BaselineAlignment.Center };
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
