using System.Windows.Input;

namespace UltimateUpdater.Views.Behaviors;

/// <summary>
/// The one static table every keyboard shortcut comes from, beyond a row's own local ones (Alt+Up/Down reorder,
/// Space/Enter/Delete on a focused row, documented at their handlers in MainWindow.xaml.cs). The tooltips
/// (<see cref="ShortcutTooltipConverter"/>) and the F1 overview (<see cref="ShortcutsWindow"/>) read these same
/// entries, so they cannot drift from what the key binding fires.
///
/// Gesture text is plain key names (Ctrl, Enter, F1, a raw comma) rather than each language's word for a
/// modifier: a shortcut names a physical key combination, not prose.
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
        new("UninstallerTitle", "Ctrl+U", Key.U, ModifierKeys.Control),
        new("HistoryButton", "Ctrl+H", Key.H, ModifierKeys.Control),
        new("SettingsButton", "Ctrl+,", Key.OemComma, ModifierKeys.Control),
        new("ShortcutsTitle", "F1", Key.F1, ModifierKeys.None),
    ];
}
