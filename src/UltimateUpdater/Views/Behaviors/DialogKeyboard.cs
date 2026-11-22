using System.Windows;
using System.Windows.Input;

namespace UltimateUpdater.Views.Behaviors;

/// <summary>
/// WPF has no built-in "Escape just closes this window" gesture, only <c>Button.IsCancel</c>, which runs THAT
/// button's Click handler: wrong for a confirmation, where Escape must dismiss without answering, like the
/// title-bar X. This wires a neutral Close() instead, safe on every dialog.
/// </summary>
public static class DialogKeyboard
{
    /// <summary>Wires the window so Escape closes it. Safe to call once per window. Deliberately the
    /// BUBBLING KeyDown, not PreviewKeyDown: a tunnelling handler on the window fires before a descendant
    /// ever sees the key, which would close the whole window out from under an open ComboBox/Popup
    /// instead of letting it close just its own dropdown first (ComboBox already marks Escape Handled
    /// when it does that, so it never reaches here), bubbling respects that nested precedence for free.</summary>
    public static void CloseOnEscape(Window window)
    {
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            window.Close();
            e.Handled = true;
        };
    }

    /// <summary>
    /// A drop-in replacement for <see cref="Window.ShowDialog"/> on this app's real modal windows
    /// (<see cref="Dialogs"/> and the install preflight window), same signature and result.
    /// ShowDialog disables the owner, and a disabled element cannot hold keyboard focus, so whatever had it in the
    /// owner loses it and is never handed it back. This remembers that element and restores it after the dialog
    /// closes.
    /// </summary>
    public static bool? ShowModal(Window dialog)
    {
        var previouslyFocused = dialog.Owner is not null ? Keyboard.FocusedElement : null;

        var result = dialog.ShowDialog();

        // A control that was removed, hidden or disabled while the dialog was up simply declines the
        // focus request; Focus() reports that with its own return value rather than throwing.
        if (previouslyFocused is IInputElement target)
            Keyboard.Focus(target);

        return result;
    }
}
