using System.Text.RegularExpressions;
using UltimateUpdater.Models;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The row-level logic Enter/Space/Entf drive
/// (<see cref="UpdateRowViewModel.ExecuteDefaultAction"/>, <see cref="UpdateRowViewModel.ToggleSelectCommand"/>)
/// is a pure ViewModel decision and testable here without any window or message pump. The actual key
/// routing (MainWindow.xaml's <c>PreviewKeyDown</c> handlers, Tab/arrow focus order, the Steam child row
/// becoming reachable at all) has no WPF-side automation-peer harness to assert against and is covered by
/// manual VM passes instead.
///
/// The two XAML-source checks below (an accessibility audit) fill part of that gap the same
/// way XamlBindingTests/LocalizationCompletenessTests do, regex over the raw markup, no live window
/// needed, for the two things a harness-less test CAN still prove: every icon-only button carries a
/// ToolTip and an AutomationProperties.Name, and every element wired only through a MouseBinding declares
/// itself Focusable. The audit found 12 real gaps (two missing ToolTips on MainWindow's self-update
/// dismiss buttons; ten Focusable-less MouseBinding targets, MainWindow's/HistoryWindow's sortable
/// column headers and MainWindow's source pill), all fixed the same way: the missing ToolTip reuses the
/// AutomationProperties.Name text already there, and every MouseBinding target gained
/// <c>Focusable="True"</c> plus a KeyDown handler that runs the SAME Command+CommandParameter the
/// MouseBinding already carries (<see cref="UltimateUpdater.Views.KeyboardActivation"/>), no second,
/// divergeable command wiring.
/// </summary>
public class KeyboardOperabilityTests
{
    private sealed class RecordingRowActions : IRowActions
    {
        public int InstallCalls { get; private set; }

        public int UninstallCalls { get; private set; }

        public int StartCalls { get; private set; }

        public bool AllowStart { get; set; }

        public bool CanStart(UpdateItem item) => AllowStart;

        public bool CanUninstall(UpdateItem item) => true;

        public void Start(UpdateRowViewModel row) => StartCalls++;

        public void Uninstall(UpdateRowViewModel row) => UninstallCalls++;

        public void InstallSingle(UpdateRowViewModel row) => InstallCalls++;
    }

    private static UpdateItem InstallableItem() => new()
    {
        Id = "Contoso.App",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Contoso App",
        Tier = SourceTier.A,
    };

    private static UpdateItem NonInstallableItem() => new()
    {
        Id = "steam-child",
        SourceId = "steam",
        Category = UpdateCategory.Games,
        Name = "A Steam game",
        Tier = SourceTier.C,
    };

    [Fact]
    public void Enter_on_an_idle_installable_row_installs_it()
    {
        var actions = new RecordingRowActions();
        var row = new UpdateRowViewModel(InstallableItem(), actions: actions);

        row.ExecuteDefaultAction();

        Assert.Equal(1, actions.InstallCalls);
        Assert.Equal(0, actions.UninstallCalls);
        Assert.Equal(0, actions.StartCalls);
    }

    /// <summary>A failed removal is retried as a removal, not an install, same rule <see
    /// cref="UpdateRowViewModel.Retry"/> already enforces; Enter has to reach the same branch.</summary>
    [Fact]
    public void Enter_on_a_failed_removal_retries_the_removal()
    {
        var actions = new RecordingRowActions();
        var row = new UpdateRowViewModel(InstallableItem(), actions: actions)
        {
            InstallState = RowInstallState.Failed,
            WasUninstalled = true,
        };

        row.ExecuteDefaultAction();

        Assert.Equal(1, actions.UninstallCalls);
        Assert.Equal(0, actions.InstallCalls);
    }

    [Fact]
    public void Enter_on_an_ignored_row_shows_it_again()
    {
        UpdateRowViewModel? ignored = null;
        var row = new UpdateRowViewModel(InstallableItem(), onIgnore: r => ignored = r)
        {
            IsIgnored = true,
        };

        row.ExecuteDefaultAction();

        Assert.Same(row, ignored);
    }

    /// <summary>A Steam child game has no action button of its own at all, Enter has to fall through to
    /// Start, the same command its context menu offers.</summary>
    [Fact]
    public void Enter_on_a_startable_row_with_no_action_button_starts_it()
    {
        var actions = new RecordingRowActions { AllowStart = true };
        var row = new UpdateRowViewModel(NonInstallableItem(), actions: actions);

        row.ExecuteDefaultAction();

        Assert.Equal(1, actions.StartCalls);
        Assert.Equal(0, actions.InstallCalls);
    }

    /// <summary>Nothing applies (already installed, nothing to start), Enter is silently a no-op, not an
    /// exception.</summary>
    [Fact]
    public void Enter_with_no_applicable_action_does_nothing()
    {
        var row = new UpdateRowViewModel(NonInstallableItem());

        var exception = Record.Exception(row.ExecuteDefaultAction);

        Assert.Null(exception);
    }

    [Fact]
    public void Space_toggles_selection_on_an_installable_row()
    {
        var row = new UpdateRowViewModel(InstallableItem());
        Assert.False(row.IsSelected);

        row.ToggleSelectCommand.Execute(null);
        Assert.True(row.IsSelected);

        row.ToggleSelectCommand.Execute(null);
        Assert.False(row.IsSelected);
    }

    /// <summary>A row with no checkbox to act on (nothing shown for it in the table) ignores Space rather
    /// than silently flipping a selection state the user can never see.</summary>
    [Fact]
    public void Space_does_nothing_on_a_row_with_no_checkbox()
    {
        var row = new UpdateRowViewModel(NonInstallableItem());

        row.ToggleSelectCommand.Execute(null);

        Assert.False(row.IsSelected);
    }

    // ===== XAML-source accessibility checks, see the class doc comment. =====

    private static readonly string ViewsDirectory = FindViewsDirectory();

    private static int LineOf(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    /// <summary>An icon-only button has no visible label, so a ToolTip and an AutomationProperties.Name are the
    /// only way a sighted-but-unfamiliar user or a screen reader ever learns what it does. Every icon-only
    /// ui:Button elsewhere in this codebase already pairs the two (MainWindow's toolbar buttons,
    /// CleanupHistoryWindow's per-row delete, SettingsWindow's excluded-path release button, ...), a button with
    /// Content="..." text is exempt, the visible label already does the job. A ContextMenu never reaches this
    /// check: every menu action in this codebase is a MenuItem, never a ui:Button, and MenuItem is
    /// keyboard-operable by the OS once the menu is open regardless.</summary>
    [Fact]
    public void Icon_only_buttons_have_a_tooltip_and_an_automation_name()
    {
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ViewsDirectory, "*.xaml"))
        {
            var xaml = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Each <ui:Button> block is carved out by a plain index scan, not one combined regex: a
            // regex alternation of "self-closing OR paired" back-and-forth-backtracks on a GREEDY
            // "[^>]*" in a way that, for a self-closing button immediately followed by another
            // <ui:Button>, silently swallows the next button's whole opening tag as if it were still
            // this one's attributes, found by this test itself misreporting only 1 of 2 known gaps
            // until the extraction was rewritten this way. "(?!\.)" excludes property-element tags
            // (<ui:Button.Icon>, <ui:Button.Style>, ...) that \b alone would still match, since a
            // literal '.' counts as a word boundary too, a real occurrence otherwise gets treated as
            // its own bogus "button" that swallows everything up to the NEXT real </ui:Button>, missing
            // the real button's own ToolTip/AutomationProperties.Name entirely.
            foreach (Match open in Regex.Matches(xaml, @"<ui:Button(?!\.)\b"))
            {
                var tagEnd = xaml.IndexOf('>', open.Index);
                if (tagEnd < 0)
                    continue;

                string block;
                if (xaml[tagEnd - 1] == '/')
                {
                    block = xaml[open.Index..(tagEnd + 1)];
                }
                else
                {
                    var closeTag = xaml.IndexOf("</ui:Button>", tagEnd, StringComparison.Ordinal);
                    if (closeTag < 0)
                        continue;
                    block = xaml[open.Index..(closeTag + "</ui:Button>".Length)];
                }

                // A Content attribute is the normal case; the Scan button (MainWindow.xaml) instead
                // swaps its Content through a <Setter Property="Content"> inside <ui:Button.Style> so
                // the label can flip on IsScanning, still visible text, just not an attribute.
                var hasVisibleText = Regex.IsMatch(block, @"\bContent\s*=|Setter\s+Property=""Content""");
                var hasIcon = Regex.IsMatch(block, @"\bIcon\s*=|<ui:SymbolIcon\b|<Path\b|<Image\b");
                if (hasVisibleText || !hasIcon)
                    continue;

                var hasToolTip = Regex.IsMatch(block, @"\bToolTip\s*=");
                var hasAutomationName = Regex.IsMatch(block, @"AutomationProperties\.Name\s*=");
                if (hasToolTip && hasAutomationName)
                    continue;

                var missing = string.Join(" and ",
                    new[] { hasToolTip ? null : "ToolTip", hasAutomationName ? null : "AutomationProperties.Name" }
                        .Where(m => m is not null));
                violations.Add($"{fileName}:{LineOf(xaml, open.Index)}: icon-only ui:Button missing {missing}");
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>A Border/StackPanel takes no keyboard focus and fires no Enter/Space handler on its own,
    /// unlike a real Button/ToggleButton/MenuItem, which get both for free from ButtonBase. Wiring one up
    /// with only a <c>&lt;MouseBinding MouseAction="LeftClick"&gt;</c> (the sortable column-header labels,
    /// the source pill's click-to-toggle) therefore makes that action mouse-only unless the element also
    /// opts into focus (<c>Focusable="True"</c>) and something routes a key to the same command, the
    /// queued-install row card two elements down in MainWindow.xaml does exactly that (Focusable="True"
    /// plus a KeyDown handler) and is the model to follow.
    ///
    /// The keyboard/accessibility audit found 10 such elements with no
    /// keyboard path at all: HistoryWindow.xaml's When/Name/Source/Version/Result column headers,
    /// MainWindow.xaml's Name/Date/Version/Size column headers, and MainWindow.xaml's source-pill toggle
    /// (SourcePillTemplate), all fixed the same way (see the class doc comment).</summary>
    [Fact]
    public void Mouse_only_click_targets_are_reachable_by_keyboard()
    {
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ViewsDirectory, "*.xaml"))
        {
            var xaml = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            foreach (Match binding in Regex.Matches(xaml, @"<MouseBinding\s+MouseAction=""LeftClick"""))
            {
                var beforeText = xaml[..binding.Index];
                // "<StackPanel" alone also matches the property-element "<StackPanel.InputBindings>"
                // that sits BETWEEN the real container's opening tag and this very MouseBinding, that
                // false match made containerStart land on ".InputBindings>" itself (no Focusable
                // attribute to find, ever) instead of the container that actually needs one. "(?!\.)"
                // excludes it, same trick XamlBindingTests/the icon-only-button check above use for the
                // same property-element shape.
                var containerStart = Regex.Matches(beforeText, @"<(?:StackPanel|Border)(?!\.)\b")
                    .Select(m => m.Index)
                    .DefaultIfEmpty(-1)
                    .Max();
                if (containerStart < 0)
                    continue;

                var containerEnd = xaml.IndexOf('>', containerStart);
                var containerTag = xaml[containerStart..(containerEnd + 1)];
                var isFocusable = Regex.IsMatch(containerTag, @"Focusable\s*=\s*""True""");
                // Focusable alone is only half the fix: an element that can be TABBED to but has no
                // KeyDown/PreviewKeyDown handler is still keyboard-inert once it has focus, Enter/Space
                // does nothing, exactly the gap this whole test exists to catch. Both must be present.
                var hasKeyHandler = Regex.IsMatch(containerTag, @"(?:Preview)?KeyDown\s*=");
                if (isFocusable && hasKeyHandler)
                    continue;

                var missing = string.Join(" and ",
                    new[] { isFocusable ? null : "Focusable=\"True\"", hasKeyHandler ? null : "a KeyDown handler" }
                        .Where(m => m is not null));
                violations.Add(
                    $"{fileName}:{LineOf(xaml, containerStart)}: click-only target (MouseBinding " +
                    $"MouseAction=\"LeftClick\") is missing {missing}");
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>
    /// The row's own action-column buttons (Install/Open/Retry/Details/Unignore, each visible only in its
    /// own row state, never two at once) mirror entries in the shared row context menu
    /// (<c>RowActionsMenu</c>, MainWindow.xaml) by long-standing design; this guards that the set of
    /// commands doing so never grows beyond the ones already accounted for, and specifically that
    /// retrying interactively, which used to ALSO live as a second button in ErrorDetailWindow.xaml
    /// (a different file entirely, and not gated by row state at all), does not come back as a
    /// same-window duplicate either.
    /// </summary>
    [Fact]
    public void No_row_command_is_offered_by_both_the_context_menu_and_an_inline_button()
    {
        var mainWindowPath = Path.Combine(ViewsDirectory, "MainWindow.xaml");
        var xaml = File.ReadAllText(mainWindowPath);

        var menuStart = xaml.IndexOf("<ContextMenu x:Key=\"RowActionsMenu\"", StringComparison.Ordinal);
        var menuEnd = xaml.IndexOf("</ContextMenu>", menuStart, StringComparison.Ordinal);
        Assert.True(menuStart >= 0 && menuEnd > menuStart, "RowActionsMenu was not found in MainWindow.xaml");

        var menuBlock = xaml[menuStart..menuEnd];
        var outsideMenu = xaml[..menuStart] + xaml[(menuEnd + "</ContextMenu>".Length)..];

        var menuCommands = RowCommandBindings(menuBlock, @"<MenuItem\b(?!\.)");
        var inlineCommands = RowCommandBindings(outsideMenu, @"<ui:Button\b(?!\.)");

        // Each one is the row's own state-gated action-column button, not an extra way to reach the same
        // thing at the same time; "open" and "un-ignore" are the two the item itself calls out.
        var accountedFor = new HashSet<string>
        {
            "OpenCommand", "IgnoreCommand", "RetryCommand", "ShowErrorDetailCommand",
        };
        var offeredTwice = menuCommands.Intersect(inlineCommands).Except(accountedFor).ToList();

        Assert.True(offeredTwice.Count == 0,
            $"offered both in the row context menu and as an inline button: {string.Join(", ", offeredTwice)}");
    }

    /// <summary>
    /// MouseBinding LeftClick is not the only way this codebase wires a click, and the two checks above
    /// only ever look for that one shape. A raw MouseLeftButtonDown/MouseDown handler is the other one, and
    /// every element that carries one is checked by hand here rather than by a second generic regex,
    /// because "is there a keyboard equivalent" is a judgement call a pattern cannot make: window-chrome
    /// dragging (CleanupWindow's two custom title bars) has none by design, the same as dragging any
    /// ordinary window; the install queue's row card is a real exception with a real, already-tested
    /// keyboard path (Alt+Up/Down, <c>MainWindow.xaml.cs</c>'s <c>OnQueueRowKeyDown</c>),
    /// MouseLeftButtonDown there only starts the OPTIONAL drag gesture, the same reorder is fully reachable
    /// without ever touching it. Both exemptions are named here explicitly, so a THIRD occurrence added
    /// later without a keyboard path of its own fails this test instead of going unnoticed the way the ten
    /// original gaps (see the class doc comment) did.
    /// </summary>
    [Fact]
    public void Every_mouse_down_only_handler_is_a_known_and_justified_exception()
    {
        var known = new HashSet<(string File, string Handler)>
        {
            ("CleanupWindow.xaml", "OnHeaderDrag"),
            ("MainWindow.xaml", "OnQueueRowMouseDown"),
        };

        var found = new HashSet<(string File, string Handler)>();

        foreach (var file in Directory.EnumerateFiles(ViewsDirectory, "*.xaml"))
        {
            var fileName = Path.GetFileName(file);
            foreach (Match m in Regex.Matches(
                File.ReadAllText(file), @"(?:Preview)?Mouse(?:Left|Right)?ButtonDown\s*=\s*""(\w+)"""))
            {
                found.Add((fileName, m.Groups[1].Value));
            }
        }

        var unaccountedFor = found.Except(known).ToList();
        Assert.True(unaccountedFor.Count == 0,
            "New mouse-down-only handler(s) with no recorded keyboard equivalent: " +
            string.Join(", ", unaccountedFor.Select(f => $"{f.File}:{f.Handler}")) +
            ", give it one (or a documented reason it needs none) and add it to the allowlist above.");

        var stale = known.Except(found).ToList();
        Assert.True(stale.Count == 0,
            "Allowlisted handler(s) no longer found, remove from the list above: " +
            string.Join(", ", stale.Select(f => $"{f.File}:{f.Handler}")));
    }

    /// <summary>The one cross-file duplicate the item found: a second "retry interactively" button in the
    /// error window, beside the row's own context menu entry. The window still names where the action is,
    /// through the pointer text gated on the same CanRetryInteractive flag the button used to use.</summary>
    [Fact]
    public void The_error_window_no_longer_has_its_own_retry_interactively_button()
    {
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory, "ErrorDetailWindow.xaml"));

        Assert.DoesNotContain("RetryInteractiveCommand", xaml);
        Assert.Contains("ErrorDetailRetryInMenu", xaml);
    }

    /// <summary>Every <c>Command="{Binding XyzCommand}"</c> on an element matching <paramref
    /// name="openingTag"/>, found anywhere in <paramref name="xaml"/>.</summary>
    private static HashSet<string> RowCommandBindings(string xaml, string openingTag)
    {
        var commands = new HashSet<string>();

        foreach (Match open in Regex.Matches(xaml, openingTag))
        {
            var tagEnd = xaml.IndexOf('>', open.Index);
            if (tagEnd < 0)
                continue;

            var tag = xaml[open.Index..(tagEnd + 1)];
            var command = Regex.Match(tag, @"\bCommand\s*=\s*""\{Binding\s+(\w+)\}""");
            if (command.Success)
                commands.Add(command.Groups[1].Value);
        }

        return commands;
    }

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

        throw new DirectoryNotFoundException("Could not locate the Views directory from the test output path.");
    }
}
