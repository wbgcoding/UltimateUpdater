using System.Windows.Input;
using UltimateUpdater.Views;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="KeyboardShortcuts.All"/> is the one table MainWindow's own InputBindings, its tooltips and
/// the F1 overview window all read; these tests hold that promise to real, constructed WPF objects rather
/// than to the table's own good intentions.
/// </summary>
[Collection("wpf")]
public class KeyboardShortcutsTests
{
    [Fact]
    public void No_two_shortcuts_share_a_gesture()
    {
        var duplicates = KeyboardShortcuts.All
            .GroupBy(entry => (entry.Key, entry.Modifiers))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Modifiers}+{group.Key.Key} is claimed by {string.Join(", ", group.Select(e => e.LabelKey))}")
            .ToList();

        Assert.True(duplicates.Count == 0, string.Join("\n", duplicates));
    }

    /// <summary>Every entry actually fires from MainWindow: a real KeyBinding with the same Key/Modifiers
    /// exists on the constructed window, bound to a non-null command (either the view model's own command
    /// the entry names, or one of the window's two local RoutedCommands for the two shortcuts with no view
    /// model command of their own).</summary>
    [Fact]
    public void Every_shortcut_has_a_real_key_binding_on_the_main_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = WindowLayoutTests.CreateMainViewModel("de");
            var window = new MainWindow(viewModel, theme, settings);
            ((System.Windows.FrameworkElement)window.Content).UpdateLayout();

            var keyBindings = window.InputBindings.OfType<KeyBinding>().ToList();
            var violations = new List<string>();

            foreach (var entry in KeyboardShortcuts.All)
            {
                var matching = keyBindings.Where(kb => kb.Key == entry.Key && kb.Modifiers == entry.Modifiers).ToList();

                if (matching.Count == 0)
                {
                    violations.Add($"{entry.LabelKey}: no KeyBinding for {entry.Modifiers}+{entry.Key} on MainWindow.");
                    continue;
                }

                if (matching.Count > 1)
                {
                    violations.Add($"{entry.LabelKey}: {matching.Count} KeyBindings claim {entry.Modifiers}+{entry.Key}.");
                    continue;
                }

                if (matching[0].Command is null)
                    violations.Add($"{entry.LabelKey}: its KeyBinding carries no Command at all.");
            }

            Assert.True(violations.Count == 0, string.Join("\n", violations));

            // Deliberately never Close()d: MainWindow's own Closing handler treats a real close as the
            // app actually quitting (App.ExitApplication) once nothing is installing and close-to-tray is
            // off, which would tear down the one shared harness Application every later test in the run
            // depends on. AccessibilityWalkTests' own MainWindow case leaves it unclosed for the same
            // reason; every other window type here closes safely and keeps doing so.
        });
    }

    /// <summary>Every shortcut with a matching control in MainWindow.xaml (all but the F1 overview itself,
    /// which has no separate triggering control to name it on) mentions its gesture in that control's own
    /// ToolTip, through the shared <see cref="ShortcutTooltipConverter"/> reading the SAME table rather
    /// than a separately hand-typed copy of the gesture text.</summary>
    [Fact]
    public void Every_control_bound_shortcut_is_named_in_its_own_tooltip()
    {
        var xaml = File.ReadAllText(Path.Combine(FindViewsDirectory(), "MainWindow.xaml"));
        var violations = new List<string>();

        foreach (var entry in KeyboardShortcuts.All)
        {
            if (entry.LabelKey == "ShortcutsTitle")
                continue;

            if (!xaml.Contains($"ConverterParameter={entry.LabelKey}"))
                violations.Add($"{entry.LabelKey}: no ToolTip in MainWindow.xaml reads its shortcut through ConverterParameter={entry.LabelKey}.");
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void ShortcutTooltipConverter_appends_the_gesture_to_a_known_key()
    {
        var converter = new ShortcutTooltipConverter();
        var result = converter.Convert("Cleanup", typeof(string), "CleanupTitle", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("Cleanup (Ctrl+L)", result);
    }

    [Fact]
    public void ShortcutTooltipConverter_passes_the_label_through_for_an_unknown_key()
    {
        var converter = new ShortcutTooltipConverter();
        var result = converter.Convert("Something", typeof(string), "NotAShortcut", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("Something", result);
    }

    /// <summary>Same search-upward idiom <see cref="AccessibilityWalkTests"/> already uses to locate the
    /// Views folder regardless of the test runner's working directory; kept local rather than shared since
    /// that one is private to its own class.</summary>
    private static string FindViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/UltimateUpdater/Views from " + AppContext.BaseDirectory);
    }
}
