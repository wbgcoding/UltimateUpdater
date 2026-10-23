using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Wpf.Ui.Controls;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

public partial class MessageDialogWindow
{
    /// <summary>
    /// Which button actually closed the dialog, <c>DialogResult</c> alone is only true/false/null and
    /// cannot carry three distinct answers, so <see cref="Dialogs.ConfirmThreeWay"/> reads this instead.
    /// Defaults to Dismissed: closing via the title bar or Escape sets neither button's handler.
    /// </summary>
    public ThreeWayChoice Choice { get; private set; } = ThreeWayChoice.Dismissed;

    public MessageDialogWindow(MessageDialogViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        DialogKeyboard.CloseOnEscape(this);
        DialogSizing.FitHeightToContent(this);

        ApplyKind(viewModel.Kind);

        // Enter must never fire a destructive action by default, only a safe primary gets to be
        // the window's default button (same reasoning that kept CleanupWindow's Clean button and
        // CleanupHistoryWindow's ClearAll out of IsDefault entirely).
        if (viewModel.PrimaryIsDestructive)
        {
            PrimaryButton.Appearance = ControlAppearance.Danger;
            // A FOCUSED button clicks on Enter/Space even without IsDefault, so the destructive one must not
            // start with keyboard focus either: it goes to the safe choice instead.
            FocusManager.SetFocusedElement(this, viewModel.HasSecondary ? SecondaryButton : null);
        }
        else
        {
            PrimaryButton.IsDefault = true;
        }
    }

    /// <summary>
    /// Icon glyph and colour by dialog kind. The colour is set as a DYNAMIC resource reference rather than
    /// a resolved brush: a resolved one keeps the colour of the theme that was current when the dialog was
    /// built, and the system theme can change while it is open.
    /// </summary>
    private void ApplyKind(DialogKind kind)
    {
        var (symbol, brushKey) = kind switch
        {
            DialogKind.Warning => (SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
            DialogKind.Error => (SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
            DialogKind.Question => (SymbolRegular.QuestionCircle24, "AccentTextFillColorPrimaryBrush"),
            DialogKind.Success => (SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
            _ => (SymbolRegular.Info24, "AccentTextFillColorPrimaryBrush"),
        };

        KindIcon.Symbol = symbol;
        KindIcon.SetResourceReference(ForegroundProperty, brushKey);

        // Decorative, not named: the Headline right beside it already carries the same information out
        // loud, and giving the icon its own AutomationProperties.Name would just repeat it. The id is for
        // automation tooling to find the right icon by kind, never spoken by a screen reader.
        AutomationProperties.SetAutomationId(KindIcon, $"DialogKindIcon-{kind}");
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Choice = ThreeWayChoice.Primary;
        DialogResult = true;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        Choice = ThreeWayChoice.Secondary;
        DialogResult = false;
        Close();
    }

    private void OnTertiary(object sender, RoutedEventArgs e)
    {
        Choice = ThreeWayChoice.Tertiary;
        // DialogResult merely has to be non-null for ShowDialog() to return, the real answer is Choice.
        DialogResult = false;
        Close();
    }
}
