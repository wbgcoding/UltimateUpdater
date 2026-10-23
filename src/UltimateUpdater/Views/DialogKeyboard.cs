using System.Windows;
using System.Windows.Input;

namespace UltimateUpdater.Views;

/// <summary>
/// WPF has no built-in "Escape just closes this window" gesture, only <c>Button.IsCancel</c>, which
/// invokes THAT button's own Click handler (wrong for a confirmation: Escape must dismiss without
/// answering, exactly like the title-bar X, never fire the same code path as clicking a real button).
/// This wires a neutral Close() instead, safe on every dialog including ones that ask a question,
/// Escape conventionally means "never mind", the same as closing via the title bar already does here.
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
    /// A drop-in replacement for <see cref="Window.ShowDialog"/> on this app's three real modal windows
    /// (<see cref="UltimateUpdater.Services.Dialogs"/>, the install preflight window, the first-run source
    /// picker), same signature and the same return value, so any of their call sites can switch to it with
    /// a one-line change. WPF's own ShowDialog already disables the owner for as long as the dialog is up,
    /// which is what keeps Tab from ever reaching a control behind it, but a disabled element cannot hold
    /// keyboard focus, so whatever had it in the owner loses it the moment the dialog opens and is never
    /// handed it back on its own once the owner is re-enabled. This remembers that element and restores it
    /// after the dialog closes, so the opener is exactly as usable as it was before the dialog interrupted
    /// it. Wired into all three call sites (Services/Dialogs.cs, ViewModels/MainViewModel.Install.cs,
    /// App.xaml.cs).
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
