using System.Windows;
using System.Windows.Input;

namespace UltimateUpdater.Views;

/// <summary>
/// Closes an owned dialog when the user clicks the main window BESIDE it.
/// Deactivated is the wrong event to listen for: it fires for any reason the dialog loses focus,
/// including switching to another application entirely, and alt-tabbing away and coming back to find
/// the settings gone is not "clicking away from it". Listening on the OWNER's mouse input says exactly
/// what was meant: the click has to land in this app's own window.
/// This only works because these dialogs are shown non-modally (see MainViewModel.ShowOwned): a modal
/// dialog disables its owner, so there is no click to hear.
/// Deliberately NOT applied to anything that asks a question: losing a confirmation by clicking beside it
/// would answer it by accident. Only windows that just show something get this.
/// </summary>
public static class DismissOnDeactivate
{
    /// <summary>
    /// Wires the window so a click in its owner closes it. Safe to call once per window.
    /// </summary>
    /// <param name="canDismiss">
    /// Optional veto, asked at click time: the cleanup dialog says no while it is deleting, because a
    /// stray click must not abort a deletion half way through.
    /// </param>
    public static void Attach(Window window, Func<bool>? canDismiss = null)
    {
        // Headless (tests, the render harness): those windows are never shown and have no owner to listen
        // to, and closing them mid-render produced blank snapshots.
        if (Services.Dialogs.SuppressInteractiveDialogs)
            return;

        // The owner is assigned before Show(), but Attach runs in the constructor: so wait for Loaded,
        // by which time it is set.
        window.Loaded += OnLoaded;

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            window.Loaded -= OnLoaded;

            if (window.Owner is not { } owner)
                return;

            owner.PreviewMouseDown += OnOwnerMouseDown;
            window.Closed += (_, _) => owner.PreviewMouseDown -= OnOwnerMouseDown;
        }

        void OnOwnerMouseDown(object? sender, MouseButtonEventArgs e)
        {
            // A window that is going away, or that opened a child of its own (a confirmation, a file
            // dialog), must not take that child with it.
            if (!window.IsLoaded || !window.IsVisible || window.OwnedWindows.Count > 0)
                return;

            if (canDismiss?.Invoke() == false)
                return;

            window.Close();
        }
    }
}
