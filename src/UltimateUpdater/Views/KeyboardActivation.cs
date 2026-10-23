using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace UltimateUpdater.Views;

/// <summary>
/// Makes a MouseBinding-only click target (a sortable column header, the source pill's click-to-toggle)
/// keyboard-operable: Enter/Space runs the same Command the mouse click already binds, read straight off
/// the element's own InputBindings: no second command wiring to keep in sync. These
/// elements are a Border/StackPanel, not a real ButtonBase, so WPF gives them no keyboard handling for
/// free the way a Button gets Enter/Space from ButtonBase; Focusable="True" plus this handler is what a
/// FrameworkElement needs to close that gap. Requires Focusable="True" on the same element: this handler
/// only ever runs once that is already true, but does not set it itself.
/// </summary>
internal static class KeyboardActivation
{
    public static void ActivateOnEnterOrSpace(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space)
            return;

        // Ctrl+Enter and friends are the window's KeyBindings (install selected), not this element's click.
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        // A real ButtonBase fires Click once per press (on release), never once per OS key-repeat tick;
        // without this check, holding Enter/Space re-executed the bound command dozens of times a second
        // (re-sorting the table, or re-toggling a source, on every repeat).
        if (e.IsRepeat)
            return;

        if (sender is not UIElement element)
            return;

        // LeftClick specifically: a RightClick/MiddleClick/MouseWheel binding on the same element must
        // not fire from Enter/Space just because it happens to be the first MouseBinding found.
        var binding = element.InputBindings.OfType<MouseBinding>()
            .FirstOrDefault(b => b.MouseAction == MouseAction.LeftClick);
        if (binding?.Command?.CanExecute(binding.CommandParameter) != true)
            return;

        binding.Command.Execute(binding.CommandParameter);
        e.Handled = true;
    }
}
