using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A real visual-tree walk over every window this app owns, rather than the source-level regex audit in
/// <see cref="KeyboardOperabilityTests"/> (which only catches the two shapes it was written for). Each
/// window is built with realistic content through the same <see cref="WpfHarness"/> the render snapshots
/// use, laid out once, then inspected for three separate accessibility properties: every interactive
/// control has a name a screen reader can announce, no window sets a TabIndex that would fight its own
/// visual order, and a modal dialog gives focus back to whatever owned it once it closes.
///
/// The accessible-name rule is the one the item pre-decides rather than leaves to judgement: a control's
/// own visible label (its Content, or a descendant TextBlock/Run with real text) already answers for it;
/// an icon-only control needs its ToolTip or an explicit AutomationProperties.Name instead. WaitSpinner and
/// MeasuringDots are Focusable="False" and carry no command at all, so they were never in scope of that
/// rule; see <see cref="Wait_indicators_carry_a_stable_automation_id"/> for what they gained instead.
/// </summary>
[Collection("wpf")]
public class AccessibilityWalkTests
{
    [Fact]
    public void Every_interactive_control_has_an_accessible_name()
    {
        var violations = new List<string>();

        WpfHarness.OnUiThread(() =>
        {
            foreach (var (name, window) in BuildWindows())
            {
                var root = ArrangeContent(window);
                WalkForNames(name, root, violations);
            }
        });

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>
    /// Neither control is Focusable, and neither carries a command, so they were never in scope for the
    /// "interactive control needs a name" rule above; the text beside them (a row's own name, the cleanup
    /// category label) already says what is happening, and repeating that on the spinner itself would be a
    /// screen reader reading the same sentence twice. What they gained instead is a stable
    /// AutomationProperties.AutomationId, so automation tooling can still find "the spinner" or "the
    /// measuring dots" inside a row without it being mistaken for silence.
    /// </summary>
    [Fact]
    public void Wait_indicators_carry_a_stable_automation_id()
    {
        WpfHarness.OnUiThread(() =>
        {
            var spinner = new WaitSpinner();
            var dots = new MeasuringDots();

            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(spinner)));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(dots)));
        });
    }

    /// <summary>Every window's focusable elements must already agree with their own visual order: a
    /// TabIndex override is the one thing that can make Tab skip around unpredictably, and nothing in this
    /// codebase needs one. This fails the moment one is added without the matching visual reordering, which
    /// is exactly the disagreement between tab order and visual order this guards against.</summary>
    [Fact]
    public void No_window_overrides_tab_order_away_from_visual_order()
    {
        var viewsDirectory = FindViewsDirectory();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(viewsDirectory, "*.xaml"))
        {
            if (File.ReadAllText(file).Contains("TabIndex", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "TabIndex found in: " + string.Join(", ", offenders) +
            ", an explicit TabIndex must be checked by hand against that window's visual order " +
            "before this test is updated to allow it.");
    }

    /// <summary>
    /// This app has two real modal windows: <see cref="MessageDialogWindow"/> and
    /// <see cref="InstallPreflightWindow"/>, each opened today through a
    /// plain <see cref="Window.ShowDialog"/> call. WPF disables the owner for the length of a modal
    /// ShowDialog, which already keeps focus from leaving the dialog, but it does NOT restore the owner's
    /// previously focused control once the dialog closes, a real gap, not a hypothetical one, reproduced
    /// here with a real Window.Show()/real keyboard focus rather than the detached-content rendering the
    /// layout snapshots use, because focus restoration is exactly the part that needs a live
    /// PresentationSource to mean anything. <see cref="DialogKeyboard.ShowModal"/> is the fix, exercised
    /// directly here; it is a same-signature replacement for ShowDialog, ready for each of the call
    /// sites to switch to.
    /// </summary>
    [Theory]
    [InlineData("message")]
    [InlineData("preflight")]
    public void Closing_a_modal_dialog_returns_focus_to_the_opener(string kind)
    {
        WpfHarness.OnUiThread(() =>
        {
            var owner = new Window { Width = 200, Height = 200, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
            var ownerButton = new Button { Content = "opener" };
            owner.Content = ownerButton;
            owner.Show();
            ownerButton.Focus();
            Assert.True(ownerButton.IsKeyboardFocused);

            Window dialog = kind switch
            {
                "message" => new MessageDialogWindow(new MessageDialogViewModel(
                    DialogKind.Info, "Title", "Headline", "Body", "OK")),
                _ => new InstallPreflightWindow(
                    new InstallPreflightViewModel([], new LoggingService(), true)),
            };

            dialog.Owner = owner;
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(dialog.Close));

            DialogKeyboard.ShowModal(dialog);

            Assert.True(ownerButton.IsKeyboardFocused,
                $"{dialog.GetType().Name} did not return focus to the control that had it before the dialog opened.");

            owner.Close();
        });
    }

    /// <summary>A binding whose path does not exist on its DataContext fails silently: a Loc[...] lookup
    /// inside an item template, where the row's view model has no Loc, left the registry backup button
    /// with neither a tooltip nor a screen reader name. Every window is built and laid out with a listener
    /// on WPF's binding trace, and any path error fails the test.</summary>
    [Fact]
    public void No_window_has_a_binding_path_error()
    {
        var listener = new BindingErrorListener();
        var source = PresentationTraceSources.DataBindingSource;

        WpfHarness.OnUiThread(() =>
        {
            // Without a debugger attached WPF writes no binding trace at all until Refresh is called.
            PresentationTraceSources.Refresh();
            source.Listeners.Add(listener);
            var previous = source.Switch.Level;
            source.Switch.Level = SourceLevels.Error;
            try
            {
                // Positive control: a path that cannot exist must reach the listener, or a green run
                // below would only mean the trace was off.
                listener.Window = "control";
                var probe = new TextBlock { DataContext = new object() };
                probe.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty"));
                probe.Measure(new Size(100, 100));
                Assert.Single(listener.Errors);
                listener.Errors.Clear();

                foreach (var (name, window) in BuildWindows())
                {
                    listener.Window = name;
                    ArrangeContent(window);
                }
            }
            finally
            {
                source.Switch.Level = previous;
                source.Listeners.Remove(listener);
            }
        });

        Assert.True(listener.Errors.Count == 0, string.Join(Environment.NewLine, listener.Errors.Distinct()));
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public string Window { get; set; } = string.Empty;

        public List<string> Errors { get; } = [];

        private string _prefix = string.Empty;

        // The trace source writes its "System.Windows.Data Error: n :" header through Write and the
        // message itself through WriteLine; the switch is set to Error, so every line is an error.
        public override void Write(string? message) => _prefix += message;

        public override void WriteLine(string? message)
        {
            Errors.Add($"{Window}: {_prefix}{message}");
            _prefix = string.Empty;
        }
    }

    /// <summary>The ignore list's rows are plain view-model objects: without a name on the container a
    /// screen reader announced each row as its .NET type name, and the remove button only as the package
    /// name, never as what pressing it does.</summary>
    [Fact]
    public void Ignore_list_rows_and_their_remove_buttons_announce_what_they_are()
    {
        WpfHarness.OnUiThread(() =>
        {
            var settings = new SettingsService(new LoggingService(), TempSettingsPath());
            settings.Current.Excludes.Add("winget:Discord.Discord@1.0.9248");
            var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["winget:Discord.Discord"] = "Discord" };
            var window = new IgnoreListWindow(new IgnoreListViewModel(settings, names));
            var root = ArrangeContent(window);

            var list = Descendants<ItemsControl>(root).Single(items => items.Items.Count == 1);
            var row = (UIElement)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Equal("Discord", AutomationProperties.GetName(row));

            var remove = Descendants<Button>(row).Single();
            Assert.Equal(LocalizationService.Instance["UnignoreButton"], AutomationProperties.GetName(remove));
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                yield return match;

            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json");

    /// <summary>Detaches the window's content and lays it out at a generous size, the same idea as
    /// <see cref="WpfHarness.RenderWindow"/> minus the PNG: nothing here needs a picture, only real visual
    /// children to walk.</summary>
    private static FrameworkElement ArrangeContent(Window window)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;

        var host = new Border { Width = 1200, Height = 1000, Child = root, DataContext = window.DataContext };
        for (var pass = 0; pass < 2; pass++)
        {
            host.Measure(new Size(1200, 1000));
            host.Arrange(new Rect(0, 0, 1200, 1000));
            host.UpdateLayout();
        }

        return root;
    }

    private static void WalkForNames(
        string windowName, DependencyObject node, List<string> violations, bool ancestorCollapsed = false)
    {
        var collapsedHere = ancestorCollapsed
            || (node is UIElement { Visibility: Visibility.Collapsed });

        if (!collapsedHere && node is UIElement element && IsInteractive(element) && !HasAccessibleName(element))
        {
            violations.Add($"{windowName}: {DescribePath(element)} has no accessible name " +
                "(no AutomationProperties.Name, no ToolTip, no visible text)");
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            WalkForNames(windowName, VisualTreeHelper.GetChild(node, i), violations, collapsedHere);
    }

    /// <summary>
    /// This app's own controls only. WPF-UI's title bar buttons and every plain WPF ScrollBar/RepeatButton/
    /// Thumb come from a library control template, never declared in any file this batch owns, and WPF-UI
    /// already names its own chrome through its own localization, not through AutomationProperties.Name or
    /// a literal ToolTip string, which is what this walk can actually see. Nothing in Views/*.xaml declares
    /// a bare ScrollBar, RepeatButton or Thumb directly (every one on screen belongs to a ListBox/TextBox/
    /// DataGrid's own default template), so excluding the type is exact, not a guess.
    /// </summary>
    private static bool IsInteractive(UIElement element) =>
        element is ButtonBase or TextBoxBase or RangeBase or ComboBox
        && element.GetType().Name is not ("TitleBarButton" or "ScrollBar" or "RepeatButton" or "Thumb");

    /// <summary>The three ways a control in this codebase is allowed to be named, in order: an explicit
    /// name wins outright; failing that, a real ToolTip string; failing that, the control's own visible
    /// text, either as its Content or as a descendant TextBlock/Run/AccessText.</summary>
    private static bool HasAccessibleName(UIElement element)
    {
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
            return true;

        if (ToolTipService.GetToolTip(element) is string { Length: > 0 } toolTip && !string.IsNullOrWhiteSpace(toolTip))
            return true;

        if (element is ContentControl { Content: string { Length: > 0 } content } && !string.IsNullOrWhiteSpace(content))
            return true;

        return HasVisibleTextDescendant(element);
    }

    private static bool HasVisibleTextDescendant(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock { Text.Length: > 0 } textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
            case AccessText { Text.Length: > 0 } accessText when !string.IsNullOrWhiteSpace(accessText.Text):
                return true;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (HasVisibleTextDescendant(VisualTreeHelper.GetChild(node, i)))
                return true;
        }

        return false;
    }

    private static string DescribePath(UIElement element)
    {
        var name = (element as FrameworkElement)?.Name;
        return string.IsNullOrEmpty(name) ? element.GetType().Name : $"{element.GetType().Name} '{name}'";
    }

    /// <summary>Every window type this app has, each with the minimum realistic content its own render
    /// snapshot test already builds it with (see WindowLayoutTests.cs), so this walk sees the same shapes
    /// the app actually shows rather than an empty shell.</summary>
    internal static IEnumerable<(string Name, Window Window)> BuildWindows()
    {
        LocalizationService.Instance.Apply("de");

        var logging = new LoggingService();
        var settings = new SettingsService(logging, TempSettingsPath());
        var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();

        var (mainViewModel, mainTheme, mainSettings) = WindowLayoutTests.CreateMainViewModel("de");

        // SelectAllTooltip/SelectionSummary start out as string.Empty until the first
        // RefreshSelectionState pass; a real scan always runs one before the window is shown, a bare
        // construction here does not, so it is called by hand (its own doc comment names this exact case).
        mainViewModel.RefreshSelectionState();

        yield return ("MainWindow", new MainWindow(mainViewModel, mainTheme, mainSettings));

        yield return ("SettingsWindow", new SettingsWindow(new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), sourceIds)));

        yield return ("HistoryWindow", new HistoryWindow(
            new HistoryViewModel(new HistoryService(logging)), logging));

        var cleanupSettings = new SettingsService(logging, TempSettingsPath());
        var cleanupViewModel = new CleanupViewModel(new CleanupService(logging), logging, cleanupSettings);
        WpfHarness.WaitForCleanupCatalog(cleanupViewModel);
        yield return ("CleanupWindow", new CleanupWindow(cleanupViewModel) { DisplayPercent = 0 });
        yield return ("CleanupHistoryWindow", new CleanupHistoryWindow(cleanupViewModel));

        yield return ("ErrorDetailWindow", new ErrorDetailWindow(new ErrorDetailViewModel(
            "OpenJDK 17", "winget", "17.0.8 → 17.0.9", "install failed", logging)));

        yield return ("ChangelogWindow", new ChangelogWindow(new ChangelogViewModel(
            "Contoso App", "1.2.3", "Fixed things.", "https://example.invalid/releases", logging)));

        yield return ("IgnoreListWindow", new IgnoreListWindow(new IgnoreListViewModel(settings)));

        yield return ("InstallPreflightWindow", new InstallPreflightWindow(
            new InstallPreflightViewModel([], logging, true)));

        yield return ("MessageDialogWindow", new MessageDialogWindow(new MessageDialogViewModel(
            DialogKind.Question, "Title", "Headline", "Body text", "Yes", "No", details: "stack trace")));
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
