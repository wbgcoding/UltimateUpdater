using System.Windows.Input;

namespace UltimateUpdater.Views;

/// <summary>
/// The one static table every keyboard shortcut this app defines comes from, beyond a row's own local
/// ones (Alt+Up/Down reorder, Space/Enter/Delete on a focused row, both already documented at their own
/// handlers in MainWindow.xaml.cs). MainWindow's own tooltips (<see cref="ShortcutTooltipConverter"/>) and
/// the F1 overview window (<see cref="ShortcutsWindow"/>) both read these SAME entries rather than each
/// keeping a separately hand-typed copy of a gesture, which is what keeps a tooltip's mention and the
/// overview list from ever drifting out of step with what the actual key binding fires.
///
/// Gesture text is plain key names (Ctrl, Enter, F1, a raw comma) rather than each language's own word
/// for a modifier key: a keyboard shortcut names a physical key combination, not prose, and this app
/// localizes no other raw key name anywhere else either.
/// </summary>
public static class KeyboardShortcuts
{
    /// <summary>LabelKey is the SAME localization key the matching control's own label or placeholder
    /// already binds through, so a lookup by that key can never name the wrong action.</summary>
    public sealed record Entry(string LabelKey, string Gesture, Key Key, ModifierKeys Modifiers);

    public static readonly Entry[] All =
    [
        new("ScanButton", "F5", Key.F5, ModifierKeys.None),
        new("SelectAllButton", "Ctrl+A", Key.A, ModifierKeys.Control),
        new("InstallSelectedButton", "Ctrl+Enter", Key.Enter, ModifierKeys.Control),
        new("SearchPlaceholder", "Ctrl+F", Key.F, ModifierKeys.Control),
        new("CleanupTitle", "Ctrl+L", Key.L, ModifierKeys.Control),
        new("HistoryButton", "Ctrl+H", Key.H, ModifierKeys.Control),
        new("SettingsButton", "Ctrl+,", Key.OemComma, ModifierKeys.Control),
        new("ShortcutsTitle", "F1", Key.F1, ModifierKeys.None),
    ];
}
