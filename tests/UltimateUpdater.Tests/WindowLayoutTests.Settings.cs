using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The portable-mode note in the About card (AppPaths.IsPortable): every test process runs from a plain
    /// build output folder with no installer marker file, so this is the one mode actually renderable here;
    /// an installed copy hiding the line again is a host-only check (no installer marker to flip inside a
    /// test).
    /// </summary>
    [Fact]
    public void About_card_states_the_portable_mode_under_the_version()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            ((Expander)window.FindName("AboutExpander")!).IsExpanded = true;

            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();

            var note = window.FindName("AboutPortableText") as TextBlock;
            Assert.NotNull(note);

            // scroll the note (and the card around it) fully into view before the snapshot
            note!.BringIntoView();
            var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll")!;
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            scroll.UpdateLayout();
            note.BringIntoView();
            scroll.UpdateLayout();

            result = WpfHarness.RenderWindow(window, "settings-about-portable-de", (int)window.Width, (int)window.Height + 140);

            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("AboutPortableRow")!).Visibility);
            Assert.Equal(LocalizationService.Instance["AboutPortableShort"], note.Text);
            Assert.True(note.ActualHeight > 0, "the portable note was never laid out");
        });

        AssertRendered(result);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Settings_window_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            settings.Current.Ui.Language = language;
            LocalizationService.Instance.Apply(language);

            // the real source set, so the snapshot shows the sources group at its real height
            var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
            var window = new SettingsWindow(viewModel);
            // the window's OWN size, not a copy of it: hardcoding the numbers here meant the snapshot
            // kept showing 740 px after the window was made taller, and the overflow it was supposed
            // to prove was gone stayed in the picture
            result = WpfHarness.RenderWindow(window, $"settings-{language}", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>The sources card is collapsed by default in every other snapshot, so the
    /// FilterChip layout (App.xaml) is otherwise never captured. Opened by name before rendering,
    /// FindName works right after InitializeComponent, no Loaded needed.</summary>
    [Fact]
    public void Settings_window_with_sources_expanded_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
            var window = new SettingsWindow(viewModel);
            ((Expander)window.FindName("SourcesExpander")!).IsExpanded = true;
            result = WpfHarness.RenderWindow(window, "settings-sources-expanded-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>The About and network-targets cards now collapse the same way the Sources
    /// card already did - not built into the visual tree at all while collapsed (invariant: a collapsed
    /// <see cref="Expander"/> never realizes its content). Checks both start collapsed (their body text is
    /// unreachable) and that expanding one actually reveals its content.</summary>
    [Fact]
    public void Settings_window_about_and_network_targets_start_collapsed()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
            var window = new SettingsWindow(viewModel);
            _ = WpfHarness.RenderWindow(window, "settings-about-collapsed-de", (int)window.Width, (int)window.Height);

            var aboutExpander = (Expander)window.FindName("AboutExpander")!;
            var networkExpander = (Expander)window.FindName("NetworkTargetsExpander")!;
            Assert.False(aboutExpander.IsExpanded);
            Assert.False(networkExpander.IsExpanded);

            // Collapsed: the repository button deep inside the About body, and every network-target
            // entry, are not realized at all.
            Assert.Null(FindFirstDescendant<Wpf.Ui.Controls.Button>(aboutExpander));
            Assert.Null(FindFirstDescendant<ItemsControl>(networkExpander));

            window.Close();

            // A fresh window, expanded before its own render pass - same "opened by name before
            // rendering" pattern the Sources card's own snapshot above already relies on.
            var expandedWindow = new SettingsWindow(viewModel);
            ((Expander)expandedWindow.FindName("AboutExpander")!).IsExpanded = true;
            ((Expander)expandedWindow.FindName("NetworkTargetsExpander")!).IsExpanded = true;
            _ = WpfHarness.RenderWindow(expandedWindow, "settings-about-expanded-touch-de", (int)expandedWindow.Width, (int)expandedWindow.Height);

            var expandedAbout = (Expander)expandedWindow.FindName("AboutExpander")!;
            var expandedNetwork = (Expander)expandedWindow.FindName("NetworkTargetsExpander")!;
            Assert.NotNull(FindFirstDescendant<Wpf.Ui.Controls.Button>(expandedAbout));
            var networkItems = FindFirstDescendant<ItemsControl>(expandedNetwork);
            Assert.NotNull(networkItems);
            Assert.True(networkItems!.Items.Count > 0, "the network-targets catalog should not be empty");

            expandedWindow.Close();
        });
    }

    /// <summary>Same "opened by name before rendering" pattern as the Sources card's own
    /// snapshot above - both new cards expanded so their content is actually captured somewhere at
    /// least once.</summary>
    [Fact]
    public void Settings_window_with_about_and_network_targets_expanded_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
            var window = new SettingsWindow(viewModel);
            ((Expander)window.FindName("AboutExpander")!).IsExpanded = true;
            ((Expander)window.FindName("NetworkTargetsExpander")!).IsExpanded = true;
            result = WpfHarness.RenderWindow(window, "settings-about-network-expanded-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The log card and the retention dropdown sit below the fold, so every other settings snapshot would
    /// show the window without them and prove nothing about how they look.
    /// </summary>
    [Fact]
    public void Settings_window_scrolled_to_the_log_card_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);

            // Laid out once by hand first: until the content has been measured and arranged the scroll
            // viewer has no scrollable height to scroll by, and the snapshot came out at the top of the
            // page. The offset survives the harness's own passes, which re-arrange the same objects.
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();

            var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll")!;
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * 0.62);
            scroll.UpdateLayout();

            result = WpfHarness.RenderWindow(window, "settings-log-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>The About card is the last one in the list, below the fold in every other settings
    /// snapshot. Scrolled to the bottom in both languages to prove the wrapped disclaimer text and
    /// the repository link button render correctly at the window's own width. This fix: the card is
    /// collapsed by default now, so it is expanded first - a collapsed Expander never realizes its
    /// content (this file's invariant elsewhere), which would otherwise render nothing to look at.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Settings_window_scrolled_to_the_about_card_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply(language);

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            ((Expander)window.FindName("AboutExpander")!).IsExpanded = true;

            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();

            var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll")!;
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            scroll.UpdateLayout();

            result = WpfHarness.RenderWindow(window, $"settings-about-{language}", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>22.08.2026: the "open log folder" button used to sit in its own row below the path
    /// text; this proves it now sits IN the card's own grid, vertically centred on the
    /// two-line title+path block, the same shape the retention row below it already uses, and that
    /// the path itself is set up to ellipsise rather than push the button.</summary>
    [Fact]
    public void Settings_log_folder_button_sits_beside_the_path_not_below_it()
    {
        Exception? failure = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();

            try
            {
                var pathText = FindByName<TextBlock>(root, "LogFolderPathText")!;
                var panel = FindByName<StackPanel>(root, "LogFolderInfoPanel")!;
                var button = FindByName<Button>(root, "OpenLogFolderButton")!;
                Assert.Equal(TextTrimming.CharacterEllipsis, pathText.TextTrimming);
                Assert.Equal(TextWrapping.NoWrap, pathText.TextWrapping);

                // beside, not below: the button's vertical centre matches the title+path block's
                // centre (Grid.Column="2" of the same row), proving it no longer sits in a
                // StackPanel underneath it.
                var panelMidY = panel.TransformToAncestor(root).Transform(new Point(0, 0)).Y + panel.ActualHeight / 2;
                var buttonMidY = button.TransformToAncestor(root).Transform(new Point(0, 0)).Y + button.ActualHeight / 2;
                Assert.True(Math.Abs(buttonMidY - panelMidY) < 1,
                    $"button (Y={buttonMidY}) is not centred on the title+path block (Y={panelMidY}).");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null) throw failure;
    }

    /// <summary>The light theme is a separate set of brushes, and a resource that only
    /// resolves in one of them shows up as invisible text or a missing background on the merged
    /// cards, the other Settings snapshots are all dark. SettingsWindow never attaches itself to a
    /// ThemeService the way MainWindow does (see <see cref="ApplyTheme"/>'s own doc comment: only the
    /// window that self-attaches actually swaps the shared resources), so the theme is applied through a
    /// throwaway attached window BEFORE construction, the same trick <see cref="ApplyTheme"/> uses.</summary>
    [Fact]
    public void Settings_window_in_the_light_theme_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "settings-light-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>Explicit dark counterpart to the light snapshot above, applied the same way rather than
    /// relying on whatever theme an earlier test left behind.</summary>
    [Fact]
    public void Settings_window_in_the_dark_theme_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "settings-dark-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>The six settings dropdowns switched from a fixed 120px Width to a 120px
    /// MinWidth (both the local value and the shared CardCombo style itself - a local MinWidth alone
    /// would still have lost to the style's own Width Setter, which is what a first pass at this test
    /// missed: it kept passing even with the Style still pinning every box to exactly 120px, because
    /// none of today's option text is actually wider than that on its own). A ComboBox's closed box
    /// only ever measures its SELECTED item (this file's style comment above), so this selects the
    /// widest German option on each combo BEFORE rendering - only then does a fixed Width actually
    /// clip anything, and only then does the assertion mean something.</summary>
    [Fact]
    public void Settings_window_combo_boxes_fit_their_widest_german_option()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
            var window = new SettingsWindow(viewModel);

            // The combos' own FontFamily/FontStyle/FontWeight/FontStretch, taken from a live element,
            // rather than guessing at what the CardCombo style resolves to - MeasureTextWidth then
            // measures with the exact font the box itself renders with.
            var reference = (ComboBox)window.FindName("IntervalCombo")!;
            var typeface = new Typeface(reference.FontFamily, reference.FontStyle, reference.FontWeight, reference.FontStretch);
            const double FontSize = 14; // CardCombo style's own Setter

            double MeasureTextWidth(string text) => new FormattedText(
                text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, FontSize, System.Windows.Media.Brushes.Black, 1.25).Width;

            static string Cap(string tag) => char.ToUpperInvariant(tag[0]) + tag[1..];

            TKey Widest<TOption, TKey>(IEnumerable<TOption> options, Func<TOption, string> display, Func<TOption, TKey> key) =>
                key(options.OrderByDescending(o => MeasureTextWidth(display(o))).First());

            // Select the widest German option on every combo, so its closed box actually measures
            // that text once rendered below - a ComboBox's closed box only ever measures its SELECTED
            // item (this file's style comment above), never its widest possible one.
            viewModel.ScanIntervalMinutes = Widest(viewModel.IntervalOptions, o => o.Display, o => o.Minutes);
            viewModel.RegistryBackupRetentionCount = Widest(viewModel.RetentionCountOptions, o => o.Display, o => o.Count);
            viewModel.LogRetentionDays = Widest(viewModel.LogRetentionOptions, o => o.Display, o => o.Days);
            viewModel.HistoryRetentionDays = Widest(viewModel.HistoryRetentionOptions, o => o.Display, o => o.Days);

            var themeTags = new[] { "system", "light", "dark", "hacker", "neon" };
            viewModel.SelectedTheme = Widest(themeTags, tag => viewModel.Loc[$"ThemeOption{Cap(tag)}"], tag => tag);

            var languageTags = new (string Tag, string Text)[]
            {
                ("system", viewModel.Loc["LanguageOptionSystem"]), ("en", "English"), ("de", "Deutsch"),
                ("es", "Español"), ("it", "Italiano"), ("pt", "Português"), ("pl", "Polski"),
                ("zh-Hans", "中文（简体）"), ("ja", "日本語"), ("nl", "Nederlands"), ("tr", "Türkçe"), ("ro", "Română"), ("fr", "Français"),
            };
            viewModel.SelectedLanguage = languageTags.OrderByDescending(t => MeasureTextWidth(t.Text)).First().Tag;

            // Both collapsible cards opened, or the log-retention/history-retention/interval combos
            // downstream of them would sit inside collapsed content that is never realized (an unrelated
            // invariant this file already relies on elsewhere) - not the case for any of these six today,
            // but rendering with everything open keeps the test honest against a future move.
            ((Expander)window.FindName("SourcesExpander")!).IsExpanded = true;
            _ = WpfHarness.RenderWindow(window, "settings-combo-widths-de", (int)window.Width, 2200);

            AssertComboFitsSelection("IntervalCombo", viewModel.IntervalOptions.First(o => o.Minutes == viewModel.ScanIntervalMinutes).Display);
            AssertComboFitsSelection("CleanupRetentionCombo", viewModel.RetentionCountOptions.First(o => o.Count == viewModel.RegistryBackupRetentionCount).Display);
            AssertComboFitsSelection("LogRetentionCombo", viewModel.LogRetentionOptions.First(o => o.Days == viewModel.LogRetentionDays).Display);
            AssertComboFitsSelection("HistoryRetentionCombo", viewModel.HistoryRetentionOptions.First(o => o.Days == viewModel.HistoryRetentionDays).Display);
            AssertComboFitsSelection("AppearanceThemeCombo", viewModel.Loc[$"ThemeOption{Cap(viewModel.SelectedTheme)}"]);
            AssertComboFitsSelection("AppearanceLanguageCombo", languageTags.First(t => t.Tag == viewModel.SelectedLanguage).Text);

            window.Close();

            void AssertComboFitsSelection(string name, string selectedText)
            {
                var combo = (ComboBox)window.FindName(name)!;
                var textWidth = MeasureTextWidth(selectedText);

                Assert.True(combo.ActualWidth >= textWidth,
                    $"'{name}' is {combo.ActualWidth:0.#}px wide, narrower than its selected text " +
                    $"'{selectedText}' at {textWidth:0.#}px.");
            }
        });
    }

    /// <summary>Acceptance check: a runtime language switch must re-render without restart, and this must
    /// hold for one of the nine newly wired locales too, not only the original DE/EN pair. Everything runs
    /// on ONE live SettingsWindow instance - no new window is constructed after the switch - so this proves
    /// the existing live-rebind wiring (<see cref="LocalizationService.Apply"/> raising PropertyChanged,
    /// which every Loc[...] binding listens to) already covers the newly wired locales for free. Checked
    /// two ways: the bound label text actually changes (LanguageLabel: German "Sprache" -&gt; Japanese
    /// "言語"), and the rendered PNG bytes differ, so this is a real visual re-render, not just a
    /// property update nothing draws.
    /// <see cref="WpfHarness.RenderWindow"/> detaches Content from the Window to measure it standalone and
    /// never puts it back (every other caller here renders a window exactly once), so this test restores
    /// Content itself between the two render passes - local to this test, no change to the shared harness
    /// or to any of its other callers.</summary>
    [Fact]
    public void A_runtime_switch_to_a_newly_wired_locale_re_renders_the_live_window_without_restart()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var sourceIds = SourceRegistry.CreateAll(logging).Select(s => s.Id).ToList();
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), sourceIds);
            var window = new SettingsWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            var before = WpfHarness.RenderWindow(window, "settings-lang-switch-before-de", (int)window.Width, (int)window.Height);
            var labelBefore = viewModel.Loc["LanguageLabel"];
            Assert.Equal("Sprache", labelBefore);

            // RenderWindow left `content` parented to its own throwaway Border host (never reattached to
            // any Window) - detach it from that orphan before giving it back to `window`, or WPF refuses
            // the reparent outright ("must disconnect from its current parent first").
            if (VisualTreeHelper.GetParent(content) is Border orphanHost)
                orphanHost.Child = null;

            // Same window, same viewModel - the picker's own action, not a fresh construction. Only the
            // Content hand-back above/below is test plumbing (see summary); the switch itself is real.
            window.Content = content;
            viewModel.SelectedLanguage = "ja";

            var after = WpfHarness.RenderWindow(window, "settings-lang-switch-after-ja", (int)window.Width, (int)window.Height);
            var labelAfter = viewModel.Loc["LanguageLabel"];
            Assert.Equal("言語", labelAfter);

            AssertRendered(before);
            AssertRendered(after);

            var beforeBytes = File.ReadAllBytes(before!.Path);
            var afterBytes = File.ReadAllBytes(after!.Path);
            Assert.False(beforeBytes.AsSpan().SequenceEqual(afterBytes), "re-render after a live language switch produced byte-identical output");

            LocalizationService.Instance.Apply("de");
        });
    }

    /// <summary>The window's own MinWidth/MinHeight (520x480), where the merged cards' German labels
    /// (the longer language) break first. English is checked at the same size too, nothing exercised
    /// this window's min-width layout in that language before.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Settings_window_at_its_minimum_size_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply(language);

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            result = WpfHarness.RenderWindow(window, $"settings-min-size-{language}", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);
    }

    /// <summary>Light-theme counterpart of the min-size snapshot above, the other light-theme Settings
    /// snapshot is at the window's own (larger) default size, never its minimum.</summary>
    [Fact]
    public void Settings_window_at_its_minimum_size_renders_in_the_light_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "settings-min-size-light-de", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>
    /// The excluded-paths list is only ever visible with its Expander open and at least
    /// one entry, every other Settings snapshot shows the empty-state line instead, which proves nothing
    /// about how the list itself looks.
    /// </summary>
    [Fact]
    public void Settings_window_with_excluded_paths_expanded_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            settings.Current.CleanupPermanentlyExcludedPaths.AddRange(
            [
                @"C:\Users\User\AppData\Local\SomeApp\Cache",
                @"C:\ProgramData\AnotherApp\Temp",
                @"D:\Games\ExampleGame\Logs",
            ]);
            LocalizationService.Instance.Apply("de");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            var expander = (Expander)window.FindName("ExcludedPathsExpander")!;
            expander.IsExpanded = true;

            // Laid out once by hand first, same reasoning as the log-card scroll test above: until the
            // content has been measured and arranged the scroll viewer has no scrollable height to scroll
            // by, and the expanded list sits below the fold, scrolling to it by the element's own
            // position (rather than a guessed fraction) survives the card order changing later.
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();

            var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll")!;
            var offset = expander.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
            scroll.ScrollToVerticalOffset(Math.Min(offset, scroll.ScrollableHeight));
            scroll.UpdateLayout();

            result = WpfHarness.RenderWindow(
                window, "settings-excluded-paths-expanded-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Every control in column 2 of a merged card must end on the same right
    /// edge, a WPF-UI control can carry its own invisible inset that a binding alone would never reveal.
    /// Geometry, not a look at the image, same idiom <see cref="AssertCheckboxCentersOnTitle"/> already
    /// uses for a Y-axis version of the same idea.
    /// </summary>
    [Fact]
    public void Settings_window_merged_card_controls_align_on_the_right_edge()
    {
        RenderResult? result = null;
        FrameworkElement? content = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("de");

            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "settings-right-edges-de", (int)window.Width, (int)window.Height);

            var autoScanCard = FindByName<Border>(content!, "AutoScanCard");
            Assert.NotNull(autoScanCard);
            AssertRightEdgesAlign(autoScanCard!,
                FindByName<FrameworkElement>(autoScanCard!, "IntervalCombo"),
                FindByName<FrameworkElement>(autoScanCard!, "ScanOnStartToggle"),
                FindByName<FrameworkElement>(autoScanCard!, "ScanMeteredToggle"));

            var installCard = FindByName<Border>(content!, "InstallCard");
            Assert.NotNull(installCard);
            AssertRightEdgesAlign(installCard!,
                FindByName<FrameworkElement>(installCard!, "InstallPreselectToggle"),
                FindByName<FrameworkElement>(installCard!, "InstallRestorePointToggle"),
                FindByName<FrameworkElement>(installCard!, "InstallScanDownloadsToggle"));

            var cleanupCard = FindByName<Border>(content!, "CleanupCard");
            Assert.NotNull(cleanupCard);
            // The folder box now has a browse button after it, so the lane holding both is what aligns.
            AssertRightEdgesAlign(cleanupCard!,
                FindByName<FrameworkElement>(cleanupCard!, "CleanupBackupFolderLane"),
                FindByName<FrameworkElement>(cleanupCard!, "CleanupRetentionCombo"),
                FindByName<FrameworkElement>(cleanupCard!, "CleanupRecycleToggle"));

            var folderBox = FindByName<FrameworkElement>(cleanupCard!, "CleanupBackupFolderBox");
            var browse = FindByName<FrameworkElement>(cleanupCard!, "BrowseBackupFolderButton");
            Assert.NotNull(folderBox);
            Assert.NotNull(browse);
            Assert.True(folderBox!.ActualWidth > 100, "The folder box should keep its width next to the browse button.");
            Assert.True(browse!.ActualWidth > 0 && browse.ActualHeight > 0, "The browse button should be laid out.");
            Assert.True(browse.TransformToAncestor(cleanupCard!).Transform(new Point(0, 0)).X
                        >= folderBox.TransformToAncestor(cleanupCard!).Transform(new Point(folderBox.ActualWidth, 0)).X,
                "The browse button should sit after the folder box, not on top of it.");
            Assert.Equal(LocalizationService.Instance["SettingsBrowseFolder"],
                System.Windows.Automation.AutomationProperties.GetName(browse));

            var appearanceCard = FindByName<Border>(content!, "AppearanceCard");
            Assert.NotNull(appearanceCard);
            AssertRightEdgesAlign(appearanceCard!,
                FindByName<FrameworkElement>(appearanceCard!, "AppearanceThemeCombo"),
                FindByName<FrameworkElement>(appearanceCard!, "AppearanceLanguageCombo"));
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The reset button's red and white label are style setters (a style trigger loses to a local value),
    /// so a Windows high contrast scheme can swap them for the system button colours; the live property is
    /// bound through its path, a one-time static reference would never update.
    /// </summary>
    [Fact]
    public void Settings_reset_button_gives_way_to_system_colours_in_high_contrast()
    {
        var localBackground = true;
        var localForeground = true;
        var buttonTriggerPaths = new List<string>();
        var labelTriggerPaths = new List<string>();
        Color normalPixel = default;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging);
            LocalizationService.Instance.Apply("en");
            var viewModel = new SettingsViewModel(
                settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
            var window = new SettingsWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            // Tall enough that the whole page is laid out on screen, the reset button sits at its very end.
            WpfHarness.RenderWindow(window, "settings-reset-button-en", (int)window.Width, 4000);

            var button = FindByName<FrameworkElement>(content, "ResetEverythingButton");
            var label = FindByName<TextBlock>(content, "ResetEverythingLabel");
            Assert.NotNull(button);
            Assert.NotNull(label);

            localBackground = button!.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue;
            localForeground = label!.ReadLocalValue(TextBlock.ForegroundProperty) != DependencyProperty.UnsetValue;
            // "(SystemParameters.HighContrast)" is parsed into the path "(0)" plus the property it names.
            static string Describe(Binding binding) => binding.Path.Path + " " + string.Join(",",
                binding.Path.PathParameters.Select(p => p is System.Reflection.PropertyInfo info ? $"{info.DeclaringType?.Name}.{info.Name}" : p?.ToString()));
            buttonTriggerPaths.AddRange(button.Style.Triggers.OfType<DataTrigger>().Select(t => Describe((Binding)t.Binding)));
            labelTriggerPaths.AddRange(label.Style.Triggers.OfType<DataTrigger>().Select(t => Describe((Binding)t.Binding)));

            // Normal state keeps the fixed red the button has always had (the pill's left padding, away from the label).
            var host = (FrameworkElement)VisualTreeHelper.GetParent(content)!;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var origin = button.TransformToAncestor(host).Transform(new Point(8, button.ActualHeight / 2));
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)origin.X, (int)origin.Y, 1, 1), pixel, 4, 0);
            normalPixel = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        });

        Assert.False(localBackground, "A local Background would beat the high contrast trigger.");
        Assert.False(localForeground, "A local label Foreground would beat the high contrast trigger.");
        Assert.Contains("(0) SystemParameters.HighContrast", buttonTriggerPaths);
        Assert.Contains("(0) SystemParameters.HighContrast", labelTriggerPaths);
        Assert.True(Math.Abs(normalPixel.R - 0xC4) <= 6 && Math.Abs(normalPixel.G - 0x2B) <= 6 && Math.Abs(normalPixel.B - 0x1C) <= 6,
            $"The reset button should keep its red, got {normalPixel}.");
    }
}
