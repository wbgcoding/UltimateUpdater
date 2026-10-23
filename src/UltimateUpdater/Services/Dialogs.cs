using System.Windows;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Services;

/// <summary>Which of a three-way dialog's buttons the user picked: see <see cref="Dialogs.ConfirmThreeWay"/>.</summary>
public enum ThreeWayChoice
{
    Primary,
    Secondary,
    Tertiary,

    /// <summary>Closed via the title bar/Escape rather than any button. Treated the same as the safest
    /// (most cautious) of the three answers by every caller, never as a green light.</summary>
    Dismissed,
}

/// <summary>
/// The app's own message boxes. Every notice, warning and confirmation goes through here so it matches
/// the app's look instead of the grey system MessageBox. Always shown on the UI thread,
/// owned by the main window; if anything about that fails it falls back to the system box so a message is
/// never silently lost.
/// </summary>
public static class Dialogs
{
    /// <summary>
    /// Set by the render/test harness. A modal dialog on a thread with no user in front of it waits for a
    /// click that can never come, which hung the whole test run for ever, and the crash handler routes
    /// through here, so ANY exception during a render turned into a permanent hang. Never set in the real
    /// app: the user always gets their dialogs.
    /// </summary>
    public static bool SuppressInteractiveDialogs { get; set; }

    private static string L(string key) => LocalizationService.Instance[key];

    private static string Title => L("AppTitle");

    /// <summary>Test-only: sees the headline of every info notice before it is shown.</summary>
    internal static Action<string>? InfoObserverForTests { get; set; }

    /// <summary>A plain acknowledgement.</summary>
    public static void Info(string headline, string body = "")
    {
        InfoObserverForTests?.Invoke(headline);
        Show(new MessageDialogViewModel(DialogKind.Info, Title, headline, body, L("DialogOk")));
    }

    /// <summary>Test-only: sees the headline of every warning before it is shown (the dialog itself is
    /// suppressed headlessly), so a test can tell which message a code path chose.</summary>
    internal static Action<string>? WarnObserverForTests { get; set; }

    public static void Warn(string headline, string body = "")
    {
        WarnObserverForTests?.Invoke(headline);
        Show(new MessageDialogViewModel(DialogKind.Warning, Title, headline, body, L("DialogOk")));
    }

    /// <summary>An error, optionally with the raw diagnostic text folded in.</summary>
    public static void Error(string headline, string body = "", string? details = null) =>
        Show(new MessageDialogViewModel(DialogKind.Error, Title, headline, body, L("DialogOk"), details: details));

    public static void Success(string headline, string body = "") =>
        Show(new MessageDialogViewModel(DialogKind.Success, Title, headline, body, L("DialogOk")));

    /// <summary>
    /// A yes/no question. Returns true only on the primary button. A destructive action paints that
    /// button red and shows the caution icon.
    /// </summary>
    public static bool Confirm(
        string headline,
        string body,
        string? confirmText = null,
        string? cancelText = null,
        bool destructive = false)
    {
        var vm = new MessageDialogViewModel(
            destructive ? DialogKind.Warning : DialogKind.Question,
            Title,
            headline,
            body,
            confirmText ?? L("DialogYes"),
            cancelText ?? L("DialogCancel"),
            destructive: destructive);

        return Show(vm) == true;
    }

    /// <summary>
    /// A three-way question (the blocker dialog: close-and-continue / skip / try-anyway):
    /// a minimal extension of <see cref="Confirm"/>'s shape (see <see cref="ThreeWayChoice"/>) rather than a
    /// second dialog system. Headless callers get <see cref="ThreeWayChoice.Dismissed"/>, same fail-closed
    /// contract as every other dialog here: a caller that treats Dismissed as "do the safe/no-op thing"
    /// never blocks a test run.
    /// </summary>
    public static ThreeWayChoice ConfirmThreeWay(
        string headline, string body, string primaryText, string secondaryText, string tertiaryText)
    {
        if (SuppressInteractiveDialogs)
            return ThreeWayChoice.Dismissed;

        var vm = new MessageDialogViewModel(
            DialogKind.Warning, Title, headline, body, primaryText, secondaryText, tertiaryText: tertiaryText);

        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is not null && !dispatcher.CheckAccess()
            ? dispatcher.Invoke(() => ShowThreeWayCore(vm))
            : ShowThreeWayCore(vm);
    }

    private static ThreeWayChoice ShowThreeWayCore(MessageDialogViewModel viewModel)
    {
        try
        {
            var window = new Views.MessageDialogWindow(viewModel);

            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow;

            if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, window))
                window.Owner = owner;

            Views.DialogKeyboard.ShowModal(window);
            return window.Choice;
        }
        catch
        {
            // The app may already be tearing down. Unlike ShowCore there is no system-box fallback (it cannot
            // offer three choices), so answer Dismissed, which every caller treats as the safe/no-op choice.
            return ThreeWayChoice.Dismissed;
        }
    }

    private static bool? Show(MessageDialogViewModel viewModel)
    {
        // Headless (tests/render harness): nothing may block waiting for a click.
        if (SuppressInteractiveDialogs)
            return null;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => ShowCore(viewModel));

        return ShowCore(viewModel);
    }

    private static bool? ShowCore(MessageDialogViewModel viewModel)
    {
        try
        {
            var window = new Views.MessageDialogWindow(viewModel);

            // The ACTIVE window, not the main one: a dialog owned by the wrong window leaves its real
            // parent with an empty OwnedWindows, so the "click away closes me" rule fired the moment this
            // dialog appeared: the cleanup window vanished and then deleted with no UI at all, and the
            // settings window disappeared while its own confirmation was still open.
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow;

            if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, window))
                window.Owner = owner;

            return Views.DialogKeyboard.ShowModal(window);
        }
        catch
        {
            // A themed dialog can fail while the app is already tearing down (no window, no dispatcher).
            // A message must still reach the user, so fall back to the system box rather than vanish.
            var text = viewModel.HasBody ? $"{viewModel.Headline}\n\n{viewModel.Body}" : viewModel.Headline;
            try
            {
                // True only on an explicit OK: with OKCancel, Close/Escape returns Cancel, so a question
                // fails closed.
                return MessageBox.Show(text, viewModel.Title, FallbackButtons(viewModel)) == MessageBoxResult.OK;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The system box's buttons when the themed dialog failed. A question keeps a way to say no: with OK
    /// alone, a destructive confirmation would come back confirmed whatever the user did, even on Close.
    /// </summary>
    internal static MessageBoxButton FallbackButtons(MessageDialogViewModel viewModel) =>
        viewModel.HasSecondary ? MessageBoxButton.OKCancel : MessageBoxButton.OK;
}
