using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    [InlineData("nl")]
    public void Main_window_with_results_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, $"main-{language}", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>Before any result exists the empty table offers the scan itself: the prompt text wraps inside
    /// the empty-state column and the button sits fully inside the window, in every layout-critical language.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("ro")]
    [InlineData("tr")]
    [InlineData("ja")]
    [InlineData("fr")]
    public void Main_window_not_checked_state_shows_the_prompt_and_a_scan_button(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-not-checked-{language}", 1100, 700);

            var text = FindByName<TextBlock>(content, "EmptyStateTextBlock");
            var button = FindByName<Button>(content, "EmptyStateScanButton");
            Assert.NotNull(text);
            Assert.NotNull(button);
            Assert.Equal(viewModel.Loc["EmptyStateNotChecked"], text!.Text);
            Assert.True(button!.Visibility == Visibility.Visible && button.ActualWidth > 0 && button.ActualHeight > 0,
                $"scan button not laid out: {button.Visibility} {button.ActualWidth:0.0}x{button.ActualHeight:0.0}");
            Assert.Equal(viewModel.Loc["ScanButton"], button.Content);

            var topLeft = button.TransformToAncestor(content).Transform(new Point(0, 0));
            Assert.True(topLeft.X >= 0 && topLeft.X + button.ActualWidth <= content.ActualWidth,
                $"scan button x {topLeft.X:0.0} width {button.ActualWidth:0.0} leaves the window");
            Assert.True(topLeft.Y >= 0 && topLeft.Y + button.ActualHeight <= content.ActualHeight,
                $"scan button y {topLeft.Y:0.0} height {button.ActualHeight:0.0} leaves the window");
            Assert.True(text.ActualWidth <= 380.5, $"prompt text is {text.ActualWidth:0.0} wide");
        });

        AssertRendered(result);
    }

    /// <summary>The second Activated (the first is the start-up paint and must stay silent) logs
    /// exactly one "Window activated" line once the next frame callback fires, naming the elapsed time, the
    /// realized row count and the current backdrop. <see cref="MainWindow.NextRenderFrameOverride"/> stands
    /// in for the real CompositionTarget.Rendering hook, which never fires in this headless harness (it
    /// bitmaps a detached content tree, never a real shown HwndSource) - everything else in the path
    /// (Stopwatch, row counting, the log line itself) runs for real.</summary>
    [Fact]
    public void Second_activation_logs_one_frame_timing_line_with_a_row_count()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var lines = new List<string>();
            var capturingLogger = new LoggingService(lines.Add);

            var window = new MainWindow(viewModel, theme, settings, capturingLogger);
            WpfHarness.RenderWindow(window, "main-activation-timing-de", 1100, 700);

            window.NextRenderFrameOverride = callback => callback();

            var onActivated = typeof(Window).GetMethod(
                "OnActivated", BindingFlags.NonPublic | BindingFlags.Instance)!;
            onActivated.Invoke(window, new object[] { EventArgs.Empty });
            onActivated.Invoke(window, new object[] { EventArgs.Empty });

            var activationLines = lines.Where(l => l.Contains("Window activated:")).ToList();
            Assert.Single(activationLines);
            Assert.Matches(@"Window activated: first frame after \d+ ms, \d+ rows realized, backdrop \w+",
                activationLines[0]);
        });
    }

    /// <summary>Ctrl+A used to do nothing once focus moved from the window's own start focus into a row:
    /// the ListBox's own class command binding answered ApplicationCommands.SelectAll itself (selecting
    /// list items, not this app's row checkboxes) and consumed the key first. The instance CommandBinding
    /// added on UpdatesListBox forwards the same command to MainViewModel's SelectAllCommand; proven here
    /// by executing ApplicationCommands.SelectAll targeted at the ListBox itself, exactly how WPF's own
    /// key-binding machinery would route it once that element holds keyboard focus.</summary>
    [Fact]
    public void Ctrl_A_selects_every_installable_row_with_the_list_focused()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            WpfHarness.RenderWindow(window, "ctrl-a-list-focused-de", 1100, 700);

            var listBox = (ListBox)window.FindName("UpdatesListBox")!;
            var installable = viewModel.Updates.Where(r => r.CanInstall).ToList();
            Assert.NotEmpty(installable);
            // A critical row starts pre-selected on purpose (unrelated feature); what matters here is
            // that SOME row starts unselected, so the toggle below can only reach "all selected" if the
            // command actually ran.
            Assert.Contains(installable, r => !r.IsSelected);

            Assert.True(ApplicationCommands.SelectAll.CanExecute(null, listBox));
            ApplicationCommands.SelectAll.Execute(null, listBox);

            Assert.All(installable, r => Assert.True(r.IsSelected));
        });
    }

    /// <summary>The cached-result banner shows when a cached scan result is on screen and the
    /// current scan has not found an offline condition.</summary>
    [Fact]
    public void Scan_status_banner_shows_the_cached_result_text()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            viewModel.CachedResultSavedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            viewModel.WasOffline = false;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, "scan-status-banner-cached-de", 1100, 700);

            var banner = FindByName<Border>(content, "ScanStatusBanner")!;
            var text = FindByName<TextBlock>(content, "ScanStatusBannerText")!;
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Equal(viewModel.CachedResultAgeText, text.Text);
        });
    }

    /// <summary>The connection banner wins when both texts would otherwise apply, since it
    /// explains the cached one too.</summary>
    [Fact]
    public void Scan_status_banner_shows_the_offline_text_over_the_cached_text()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            viewModel.CachedResultSavedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            viewModel.WasOffline = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, "scan-status-banner-offline-de", 1100, 700);

            var banner = FindByName<Border>(content, "ScanStatusBanner")!;
            var text = FindByName<TextBlock>(content, "ScanStatusBannerText")!;
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Equal(viewModel.OfflineBannerText, text.Text);
        });
    }

    /// <summary>With neither condition true the strip takes no space at all.</summary>
    [Fact]
    public void Scan_status_banner_is_collapsed_when_neither_condition_applies()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            viewModel.CachedResultSavedAt = null;
            viewModel.WasOffline = false;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, "scan-status-banner-hidden-de", 1100, 700);

            var banner = FindByName<Border>(content, "ScanStatusBanner")!;
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
        });
    }

    /// <summary>The header's own "select all" checkbox sits in a Grid that carries the card's usual 12px
    /// left margin, while a category's checkbox sits flush against the ListBox's left edge with no such
    /// margin - left uncompensated, the header box renders 12px to the right of every category box below
    /// it. A visual check caught this; this test pins the fix (the header checkbox's compensating negative
    /// margin) so it cannot silently drift back.</summary>
    [Fact]
    public void Select_all_checkbox_lines_up_with_the_category_checkboxes()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-select-all-alignment-de", 1100, 700);

            var selectAll = FindByName<CheckBox>(content, "SelectAllCheckBox");
            var category = FindByName<CheckBox>(content, "CategoryCheckBox");
            Assert.NotNull(selectAll);
            Assert.NotNull(category);

            var selectAllX = selectAll!.TransformToAncestor(content).Transform(new Point(0, 0)).X;
            var categoryX = category!.TransformToAncestor(content).Transform(new Point(0, 0)).X;
            Assert.True(Math.Abs(selectAllX - categoryX) <= 1.0,
                $"select-all left edge {selectAllX:0.0} vs category checkbox left edge {categoryX:0.0}");
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The category chevron sits between the category checkbox and the icon (no right-hand chevron any more),
    /// pointing down when collapsed and turned over when expanded, and the whole header row stays the toggle.
    /// </summary>
    [Theory]
    [InlineData(true, 180.0)]
    [InlineData(false, 0.0)]
    public void The_category_chevron_sits_left_of_the_icon_in_both_states(bool expanded, double expectedAngle)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-group-chevron-{(expanded ? "open" : "closed")}-de", 1100, 700,
                expandGroups: expanded);

            // The first group in the table is the software one.
            var chevron = FindAllByName<System.Windows.Shapes.Path>(content, "GroupChevron").First();
            var header = (Panel)chevron.Parent;
            var checkBox = FindByName<CheckBox>(header, "CategoryCheckBox")!;
            var icon = header.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().First();

            double X(FrameworkElement e) => e.TransformToAncestor(content).Transform(new Point(0, 0)).X;
            Assert.True(X(chevron) >= X(checkBox) + checkBox.ActualWidth - 1, "chevron must come after the checkbox");
            Assert.True(X(chevron) + chevron.ActualWidth <= X(icon) + 1, "chevron must come before the icon");

            Assert.Equal(expectedAngle, ((RotateTransform)chevron.RenderTransform).Angle);

            // The whole header row is the toggle, not a small chevron target.
            var toggle = FindByName<System.Windows.Controls.Primitives.ToggleButton>(content, "GroupHeaderToggle");
            Assert.NotNull(toggle);
            Assert.Equal(expanded, toggle!.IsChecked);
        });

        AssertRendered(result);
    }

    /// <summary>Every point along a category header row (left of the name, on it, far to the right) lands on
    /// the header toggle, so a click anywhere on the row expands or collapses; only the category checkbox
    /// keeps its own click.</summary>
    [Fact]
    public void The_whole_category_header_row_is_hit_testable_as_the_toggle()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-group-header-hit-de", 1100, 700, expandGroups: true);

            var toggle = FindAllByName<System.Windows.Controls.Primitives.ToggleButton>(content, "GroupHeaderToggle").First();
            Assert.True(toggle.ActualWidth > 600, $"the header toggle is only {toggle.ActualWidth:0} px wide");

            bool HitsToggle(double xInToggle)
            {
                var p = toggle.TransformToAncestor(content).Transform(new Point(xInToggle, toggle.ActualHeight / 2));
                DependencyObject? hit = VisualTreeHelper.HitTest(content, p)?.VisualHit;
                for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
                {
                    if (d is CheckBox)
                        return false;
                    if (ReferenceEquals(d, toggle))
                        return true;
                }

                return false;
            }

            Assert.True(HitsToggle(toggle.ActualWidth * 0.30), "a click at 30 % of the header row misses the toggle");
            Assert.True(HitsToggle(toggle.ActualWidth * 0.60), "a click at 60 % of the header row misses the toggle");
            Assert.True(HitsToggle(toggle.ActualWidth - 40), "a click at the right end of the header row misses the toggle");

            // The checkbox still answers its own click.
            var checkBox = FindByName<CheckBox>(toggle, "CategoryCheckBox")!;
            if (checkBox.IsVisible)
            {
                var c = checkBox.TransformToAncestor(content).Transform(new Point(checkBox.ActualWidth / 2, checkBox.ActualHeight / 2));
                var hit = VisualTreeHelper.HitTest(content, c)?.VisualHit;
                var inCheckBox = false;
                for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
                    inCheckBox |= ReferenceEquals(d, checkBox);
                Assert.True(inCheckBox, "the category checkbox no longer takes its own click");
            }
        });

        AssertRendered(result);
    }

    /// <summary>The install bar's shine sweep must require an install actually running, not just
    /// "no real progress yet": that alone is also true whenever the batch is empty, which used to leave
    /// the sweep animating forever from app launch with nothing installing.</summary>
    [Fact]
    public void The_install_bar_shine_needs_an_install_running()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-install-bar-shine-de", 1100, 700);

            var shine = FindByName<PillProgressBar>(content, "InstallBar");
            Assert.NotNull(shine);

            // Two triggers: one shows the strip, the other runs the sweep and also needs the window shown,
            // so a window hidden to the tray does not keep the storyboard ticking.
            var triggers = shine!.Style.Triggers.OfType<MultiDataTrigger>().ToList();
            Assert.Equal(2, triggers.Count);
            foreach (var t in triggers)
            {
                Assert.Contains(t.Conditions, c =>
                    ((Binding)c.Binding).Path?.Path == nameof(MainViewModel.InstallHasRealProgress) && Equals(c.Value?.ToString(), "False"));
                Assert.Contains(t.Conditions, c =>
                    ((Binding)c.Binding).Path?.Path == nameof(MainViewModel.IsInstalling) && Equals(c.Value?.ToString(), "True"));
            }

            var trigger = triggers.Single(t => t.EnterActions.Count > 0);
            // plus the system animation setting: a PC with animations off never runs the shine
            Assert.Equal(4, trigger.Conditions.Count);
            Assert.Contains(trigger.Conditions, c =>
                ((Binding)c.Binding).Source is bool && Equals(c.Value?.ToString(), "True"));
            Assert.Contains(trigger.Conditions, c =>
                ((Binding)c.Binding).Path?.Path == nameof(Window.IsVisible)
                && ((Binding)c.Binding).RelativeSource?.AncestorType == typeof(Window)
                && Equals(c.Value?.ToString(), "True"));

            var beginStoryboard = (BeginStoryboard)trigger.EnterActions.Single();
            var stop = (StopStoryboard)trigger.ExitActions.Single();
            Assert.Equal(beginStoryboard.Name, stop.BeginStoryboardName);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The indeterminate shine has to be visible on the light theme's near-white track: its peak pixel is
    /// compared with the bare track beside it, with the sweep frozen at the middle of the bar.
    /// </summary>
    [Fact]
    public void The_install_bar_shine_stands_out_from_the_track_in_the_light_theme()
    {
        double contrast = 0;
        var diag = "";

        WpfHarness.OnUiThread(() =>
        {
            ApplyTheme("light");
            try
            {
                var (viewModel, theme, settings) = CreateMainViewModel("en");
                var row = new UpdateRowViewModel(Item("Contoso.Tool", "Contoso Tool", "winget", UpdateCategory.Software, "1.0", "1.1"));
                viewModel.Updates.Add(row);
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
                viewModel.IsInstalling = true;

                var window = new MainWindow(viewModel, theme, settings);
                // Attaching the window applies the saved theme; put the light one back afterwards.
                ApplyTheme("light");
                var content = (FrameworkElement)window.Content;
                var result = WpfHarness.RenderWindow(window, "main-install-bar-shine-light-en", 1100, 700);
                AssertRendered(result);

                var bar = FindByName<PillProgressBar>(content, "InstallBar");
                Assert.NotNull(bar);
                Assert.Equal(Visibility.Visible, bar!.Visibility);
                Assert.True(bar.SegmentLength > 0, "the bar shows no travelling segment while an install has no real progress");
                var track = bar;

                // Freeze the sweep: drop the storyboard's animation and park the segment in the middle.
                bar.BeginAnimation(PillProgressBar.SegmentPhaseProperty, null);
                bar.SegmentPhase = 0.5;

                var host = (FrameworkElement)VisualTreeHelper.GetParent(content)!;
                host.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);

                Color PixelAt(double x, double y)
                {
                    var origin = track.TransformToAncestor(host).Transform(new Point(x, y));
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect((int)origin.X, (int)origin.Y, 1, 1), pixel, 4, 0);
                    return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
                }

                var middle = track.ActualHeight / 2;
                contrast = AccentPalette.Contrast(PixelAt(track.ActualWidth / 2, middle), PixelAt(6, middle));
                diag = $"track {track.ActualWidth}x{track.ActualHeight} at {track.TransformToAncestor(host).Transform(new Point(0, 0))} peak {PixelAt(track.ActualWidth / 2, middle)} edge {PixelAt(6, middle)} segment {bar.SegmentLength}";
            }
            finally
            {
                ApplyTheme("dark");
            }
        });

        Assert.True(contrast > 1.5, $"The shine peak against the track is only {contrast:0.00}:1 in the light theme ({diag}).");
    }

    /// <summary>The scan button's icon spins while a scan is running, same pattern as the
    /// cleanup button's sweeping broom (Storyboard.SetTarget aimed straight at a transform is silently
    /// ignored, so the storyboard must be found sitting on the icon itself and checked, not merely
    /// trusted from the XAML).</summary>
    [Fact]
    public void The_scan_buttons_icon_spins_forever_while_scanning()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-scan-button-icon-de", 1100, 700);

            var icon = FindByName<Wpf.Ui.Controls.SymbolIcon>(content, "ScanButtonIcon");
            Assert.NotNull(icon);
            Assert.IsType<RotateTransform>(icon!.RenderTransform);

            // The spin runs only while scanning AND while the window is visible: a background interval
            // scan with the window hidden to the tray must not keep a 60 fps animation clock alive.
            var trigger = icon.Style.Triggers.OfType<MultiDataTrigger>().Single();
            // IsScanning, IsVisible and the system animation setting
            Assert.Equal(3, trigger.Conditions.Count);
            Assert.Contains(trigger.Conditions, c =>
                ((Binding)c.Binding).Source is bool && Equals(c.Value?.ToString(), "True"));
            Assert.Contains(trigger.Conditions, c =>
                ((Binding)c.Binding).Path?.Path == nameof(MainViewModel.IsScanning) && Equals(c.Value?.ToString(), "True"));
            var visibleCondition = trigger.Conditions.Single(c => ((Binding)c.Binding).Path?.Path == nameof(Window.IsVisible));
            Assert.Equal("True", visibleCondition.Value?.ToString());
            Assert.Equal(typeof(Window), ((Binding)visibleCondition.Binding).RelativeSource.AncestorType);
            var beginStoryboard = (BeginStoryboard)trigger.EnterActions.Single();
            var animation = (DoubleAnimation)beginStoryboard.Storyboard.Children.Single();

            Assert.Equal("RenderTransform.Angle", Storyboard.GetTargetProperty(animation)!.Path);
            Assert.Equal(0d, animation.From);
            Assert.Equal(360d, animation.To);
            Assert.Equal(RepeatBehavior.Forever, animation.RepeatBehavior);

            var stop = (StopStoryboard)trigger.ExitActions.Single();
            Assert.Equal(beginStoryboard.Name, stop.BeginStoryboardName);
        });

        AssertRendered(result);
    }

    /// <summary>The toolbar cleanup button shows the plain broom glyph at rest and swaps to the animated
    /// cleaning disk (same body/platter/hub/arm geometry the cleanup dialog's own centre animation uses)
    /// while a cleanup is running, with none of the dialog's sparkle geometry: the two icons are separate
    /// sibling elements switched by <c>Visibility</c>, so this checks both are actually present and that
    /// the running one shows exactly the disk shapes and nothing shaped like a spark.</summary>
    [Fact]
    public void The_toolbar_cleanup_buttons_icon_switches_to_the_disk_while_running()
    {
        RenderResult? resultIdle = null;
        RenderResult? resultRunning = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            resultIdle = WpfHarness.RenderWindow(window, "main-cleanup-button-idle-de", 1100, 700);

            var broom = FindByName<Wpf.Ui.Controls.SymbolIcon>(content, "ToolbarCleaningBroom");
            var disk = FindByName<Grid>(content, "ToolbarCleaningDisk");
            Assert.NotNull(broom);
            Assert.NotNull(disk);
            Assert.Equal(Visibility.Visible, broom!.Visibility);
            Assert.Equal(Visibility.Collapsed, disk!.Visibility);

            // RenderWindow detaches `content` into its own throwaway Border host and never reattaches it
            // to `window`; re-parent it back before rendering the same window a second time, same pattern
            // WindowLayoutTests's own language-switch re-render test already uses.
            if (VisualTreeHelper.GetParent(content) is Border orphanHost)
                orphanHost.Child = null;
            window.Content = content;

            viewModel.IsCleanupRunning = true;
            content.UpdateLayout();
            resultRunning = WpfHarness.RenderWindow(window, "main-cleanup-button-running-de", 1100, 700);

            Assert.Equal(Visibility.Collapsed, broom.Visibility);
            Assert.Equal(Visibility.Visible, disk.Visibility);

            var sparkleData = "M12 3.2 C12.9 8.4 14.6 10.1 20.8 12";
            var diskPaths = FindAllDescendants<System.Windows.Shapes.Path>(disk).ToList();
            Assert.NotEmpty(diskPaths);
            Assert.All(diskPaths, p => Assert.DoesNotContain(sparkleData, PathDataString(p)));

            // The disk keeps its size: a breathing scale had no room inside the button, only the light moves.
            Assert.Null(disk.Style);
            Assert.True(disk.RenderTransform is null || disk.RenderTransform.Value.IsIdentity);

            viewModel.IsCleanupRunning = false;
            content.UpdateLayout();
            Assert.Equal(Visibility.Visible, broom.Visibility);
            Assert.Equal(Visibility.Collapsed, disk.Visibility);
        });

        AssertRendered(resultIdle);
        AssertRendered(resultRunning);
    }

    /// <summary>
    /// The disk used to be drawn scaled down to the same 20x20 slot an icon glyph takes, but its own
    /// artwork only fills about three quarters of its native 24x24 canvas, so next to a glyph that fills
    /// most of its own slot it looked visibly undersized. Fixed by drawing it at the canvas's native
    /// size and letting it overflow the fixed 20x20 slot, centered, into the button's own padding
    /// (MainWindow.xaml's ToolbarCleaningDisk): this checks the grown disk actually reaches close to the
    /// button's own inner width, not just that it renders.
    /// </summary>
    [Fact]
    public void The_toolbar_cleanup_disk_fills_most_of_the_buttons_inner_width()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            viewModel.IsCleanupRunning = true;
            content.UpdateLayout();
            result = WpfHarness.RenderWindow(window, "main-cleanup-button-disk-fill-de", 1100, 700);

            var button = FindByName<Wpf.Ui.Controls.Button>(content, "ToolbarCleanupButton");
            var disk = FindByName<Grid>(content, "ToolbarCleaningDisk");
            Assert.NotNull(button);
            Assert.NotNull(disk);

            // The disk's own drawn height against the button's outer height: most of the button, with a
            // little air left above and below.
            var drawing = disk!.Children.OfType<Viewbox>().Single();
            Assert.True(drawing.ActualHeight >= 0.75 * button!.ActualHeight && drawing.ActualHeight <= 0.9 * button.ActualHeight,
                $"disk height {drawing.ActualHeight} vs button height {button.ActualHeight}");
        });

        AssertRendered(result);
    }

    /// <summary>The toolbar cleanup button's UI Automation name is the visible label, in English and
    /// German (a voice-control user says what they read; "Free up space" next to a "Clean up" label
    /// does not match).</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void The_toolbar_cleanup_button_is_named_by_its_visible_label(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-cleanup-button-name-{language}", 1100, 700);

            var button = FindByName<Wpf.Ui.Controls.Button>(content, "ToolbarCleanupButton");
            Assert.NotNull(button);

            static IEnumerable<TextBlock> TextBlocks(DependencyObject d)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var child = VisualTreeHelper.GetChild(d, i);
                    if (child is TextBlock tb)
                        yield return tb;
                    foreach (var inner in TextBlocks(child))
                        yield return inner;
                }
            }

            var visible = TextBlocks(button!).Select(t => t.Text).First(t => t.Any(char.IsLetter));
            Assert.Equal(visible, System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.StartsWith(visible, button!.ToolTip as string);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The toolbar cleanup disk used to only breathe (a slight resize) while running, with no travelling
    /// light at all, unlike the cleanup dialog's own centre animation it is meant to mirror at a smaller
    /// scale. Driven from MainWindow.xaml.cs's UpdateCleanupIconSweep (a plain BeginAnimation on the
    /// transform, not a Style-triggered Storyboard through a Brush's own RelativeTransform: that shape
    /// corrupted later renders of completely unrelated windows in this same test class), reacting to
    /// MainViewModel.IsCleanupRunning through the constructor's existing PropertyChanged subscription.
    /// </summary>
    [Fact]
    public void The_toolbar_cleanup_disks_light_sweeps_across_it_while_running()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            viewModel.IsCleanupRunning = true;
            content.UpdateLayout();
            result = WpfHarness.RenderWindow(window, "main-cleanup-button-sweep-de", 1100, 700);

            var light = FindByName<System.Windows.Shapes.Path>(content, "ToolbarCleaningLight");
            Assert.NotNull(light);
            var mask = Assert.IsType<LinearGradientBrush>(light!.OpacityMask);
            var slide = Assert.IsType<TranslateTransform>(mask.RelativeTransform);

            // HasAnimatedProperties is the direct way to tell "an animation clock is attached" apart from
            // "the value happens to be sitting still"; the animated value itself is also within the
            // storyboard's own from/to range, proof the clock is not merely attached but actually moving.
            Assert.True(slide.HasAnimatedProperties);
            Assert.InRange(slide.X, -0.75, 0.75);

            viewModel.IsCleanupRunning = false;
            content.UpdateLayout();

            Assert.False(slide.HasAnimatedProperties);
            Assert.Equal(-0.75, slide.X);
        });

        AssertRendered(result);
    }

    /// <summary>The table is one surface: rows are told apart by a hairline above each, never by a zebra
    /// background, and only a ticked row carries the faint accent tint. Reads each row's own "Root" Border
    /// (its ControlTemplate's TemplatedParent is the ListBoxItem) scoped to the main table's own ListBox,
    /// never the separate manual-open ListBox that names its own row Border "Root" too. Populate ticks
    /// exactly one row (the critical Windows update).</summary>
    [Fact]
    public void Table_rows_are_separated_by_hairlines_and_only_a_ticked_row_is_tinted()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-row-lines-de", 1100, 700);

            var updatesListBox = FindByName<ListBox>(content, "UpdatesListBox");
            Assert.NotNull(updatesListBox);

            var rows = FindAllByName<Border>(updatesListBox!, "Root")
                .Where(border => border.TemplatedParent is ListBoxItem)
                .ToList();
            Assert.True(rows.Count >= 4, $"expected the populated rows, found {rows.Count}");

            foreach (var row in rows)
            {
                Assert.Equal(1.0, row.BorderThickness.Top);
                Assert.Equal(0.0, row.BorderThickness.Bottom);
                // a resting row has no fill of its own: no zebra
                Assert.True(row.Background is null || (row.Background is System.Windows.Media.SolidColorBrush { Color.A: 0 }),
                    "a resting row must not carry a background fill");
            }

            var tinted = rows.Count(row => FindByName<Border>(row, "SelectedTint")?.Visibility == Visibility.Visible);
            Assert.Equal(1, tinted);
        });

        AssertRendered(result);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Main_window_with_nothing_found_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, $"main-empty-{language}", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The light theme is a separate set of brushes, and a resource that only resolves in one of them
    /// shows up as invisible text or a missing background, which no other test can see.
    /// </summary>
    [Fact]
    public void Main_window_in_the_light_theme_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            theme.Apply("light");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-light-de", 1100, 700);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>
    /// Explicit dark counterpart to the light snapshot above, applied the same way rather than relying on
    /// whatever theme an earlier test left behind (most other snapshots here render in dark only because
    /// that happens to be the harness's own resting theme). Populated, like the light one is not, so the
    /// deep page/card backgrounds and the glass top highlight both show on real content, not an empty list.
    /// </summary>
    [Fact]
    public void Main_window_in_the_dark_theme_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            theme.Apply("dark");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-dark-de", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>The self-update banner is a visible part of the window that no other test ever shows.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Main_window_offering_a_self_update_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            viewModel.SelfUpdateVersion = "0.2.0";
            viewModel.SelfUpdateAvailable = true;
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, $"main-selfupdate-{language}", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// A batch mid-install: the toolbar is replaced by the live progress strip, and the rows show a
    /// running bar with the percentage beside it, a done row and a failed one, all in the main window,
    /// since there is no separate progress window any more.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Main_window_installing_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(3).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            rows[0].ApplyInstallStatus(new InstallItemStatus(
                rows[0].Item.SourceId, rows[0].Item.Id, 62, "Wird heruntergeladen…", null, null)
            {
                BytesReceived = 41_000_000,
                BytesTotal = 66_000_000,
            });
            rows[1].ApplyInstallStatus(new InstallItemStatus(rows[1].Item.SourceId, rows[1].Item.Id, 100, null, true, null));
            if (rows.Count > 2)
                rows[2].ApplyInstallStatus(new InstallItemStatus(
                    rows[2].Item.SourceId, rows[2].Item.Id, 0, null, false, "Installation fehlgeschlagen."));

            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, $"main-installing-{language}", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>The band's own visuals (secondary background, accent edge) are a separate brush set in the
    /// light theme, the same trap the whole window already hit once (see the light-theme test above).</summary>
    [Fact]
    public void Main_window_installing_in_the_light_theme_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            theme.Apply("light");
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(2).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            rows[0].ApplyInstallStatus(new InstallItemStatus(
                rows[0].Item.SourceId, rows[0].Item.Id, 45, "Wird installiert…", null, null));

            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-installing-light-de", 1100, 700);
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>
    /// A running row's percentage text must fit fully inside the action column at the window's
    /// own minimum width, in German (the wider language). The bar (96px) + percent text + per-row stop
    /// button need about 171px; the action column used to reserve only 150px, so the right-aligned group
    /// overflowed to the LEFT past the column's own boundary. Measures the real rendered position of the
    /// percent TextBlock against the column boundary computed from the row's own ColumnDefinitions, not a
    /// screenshot guess.
    /// </summary>
    [Fact]
    public void Main_table_running_row_percent_text_fits_the_action_column_at_minimum_width()
    {
        RenderResult? result = null;
        double percentLeft = 0;
        double percentRight = 0;
        double actionColumnLeft = 0;
        double tableRowWidth = 0;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var row = viewModel.Updates.First(r => r.Item.CanInstall);
            row.BeginInstall();
            viewModel.InstallBatch.Add(row);
            row.ApplyInstallStatus(new InstallItemStatus(row.Item.SourceId, row.Item.Id, 42, "Wird installiert…", null, null));
            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-table-running-row-narrow-de", (int)window.MinWidth, (int)window.MinHeight);

            var tableRow = FindAllByName<Grid>(content, "TableRow")
                .First(g => g.DataContext == row);
            tableRowWidth = tableRow.ActualWidth;

            // Sum the actual widths of the first 7 columns (check/icon/name/publisher/date/version/size)
            // to get the real left boundary of the action column, whatever it is currently set to.
            for (var i = 0; i < 7; i++)
                actionColumnLeft += tableRow.ColumnDefinitions[i].ActualWidth;

            var percentText = FindByName<TextBlock>(tableRow, "RunningPercentText");
            Assert.NotNull(percentText);
            Assert.Equal("42 %", percentText!.Text);

            percentLeft = percentText.TransformToAncestor(tableRow).Transform(new Point(0, 0)).X;
            percentRight = percentLeft + percentText.ActualWidth;
        });

        AssertRendered(result);
        Assert.True(percentLeft >= actionColumnLeft - 1,
            $"percent text starts at {percentLeft:0} px, before the action column's own left edge at {actionColumnLeft:0} px.");
        Assert.True(percentRight <= tableRowWidth + 1,
            $"percent text ends at {percentRight:0} px, past the row's own width {tableRowWidth:0} px.");
    }

    /// <summary>
    /// A row in its install phase shows the moving bar and the "Installing… m:ss" line, and neither the
    /// percent text nor the filled bar: there is no figure to show.
    /// </summary>
    [Fact]
    public void Main_table_installing_row_shows_the_moving_bar_and_no_percent()
    {
        RenderResult? result = null;
        var percentShown = true;
        var barShown = false;
        var lineShown = false;
        string lineText = string.Empty;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);

            var row = viewModel.Updates.First(r => r.Item.CanInstall);
            row.BeginInstall();
            viewModel.InstallBatch.Add(row);
            row.ApplyInstallStatus(new InstallItemStatus(row.Item.SourceId, row.Item.Id, 0, null, null, null)
            {
                Phase = UltimateUpdater.Models.InstallPhase.Installing,
            });
            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-table-installing-row", (int)window.MinWidth, (int)window.MinHeight);

            var tableRow = FindAllByName<Grid>(content, "TableRow").First(g => g.DataContext == row);
            percentShown = FindByName<TextBlock>(tableRow, "RunningPercentText")!.Visibility == Visibility.Visible;
            barShown = FindByName<UltimateUpdater.Views.IndeterminateBar>(tableRow, "RunningIndeterminateBar")!.Visibility == Visibility.Visible;
            var line = FindByName<TextBlock>(tableRow, "InstallingPhaseText")!;
            lineShown = line.Visibility == Visibility.Visible;
            lineText = line.Text;
        });

        AssertRendered(result);
        Assert.False(percentShown);
        Assert.True(barShown);
        Assert.True(lineShown);
        Assert.StartsWith("Installing", lineText);
    }

    /// <summary>
    /// The state lane of a finished row (the state word plus the reboot marker, or plus the Retry button)
    /// must stay inside the 176 px action column in the languages with the longest words. Every visible child
    /// is measured against the column's real edges, and a text that does not fit must be trimmed, never cut.
    /// </summary>
    [Theory]
    [InlineData("pl")]
    [InlineData("pt")]
    [InlineData("tr")]
    [InlineData("es")]
    [InlineData("nl")]
    public void Main_table_state_lane_stays_inside_the_action_column(string language)
    {
        RenderResult? result = null;
        var problems = new List<string>();

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(2).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            rows[0].ApplyInstallStatus(new InstallItemStatus(rows[0].Item.SourceId, rows[0].Item.Id, 100, null, true, null)
            {
                RebootRequired = true,
            });
            rows[1].ApplyInstallStatus(new InstallItemStatus(rows[1].Item.SourceId, rows[1].Item.Id, 100, null, null, null)
            {
                IsCancelled = true,
            });
            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-state-lane-{language}", (int)window.MinWidth, (int)window.MinHeight);

            foreach (var row in rows)
            {
                var tableRow = FindAllByName<Grid>(content, "TableRow").First(g => g.DataContext == row);
                var left = 0.0;
                for (var i = 0; i < 7; i++)
                    left += tableRow.ColumnDefinitions[i].ActualWidth;
                var right = left + tableRow.ColumnDefinitions[7].ActualWidth;

                var lane = (Panel)FindByName<TextBlock>(tableRow, "RowStateWord")!.Parent;
                foreach (UIElement child in lane.Children)
                {
                    if (child.Visibility != Visibility.Visible || child.RenderSize.Width <= 0)
                        continue;

                    var x = child.TransformToAncestor(tableRow).Transform(new Point(0, 0)).X;
                    if (x < left - 1 || x + child.RenderSize.Width > right + 1)
                        problems.Add($"{row.InstallState}: {child.GetType().Name} spans {x:0}-{x + child.RenderSize.Width:0}, column {left:0}-{right:0}");

                    // fitting by trimming is a fallback; the shipped wording itself has to be readable in full
                    if (child is TextBlock text)
                    {
                        var shown = text.RenderSize.Width;
                        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var wanted = text.DesiredSize.Width - text.Margin.Left - text.Margin.Right;
                        if (wanted > shown + 1)
                            problems.Add($"{row.InstallState}: \"{text.Text}\" is trimmed to {shown:0} of {wanted:0} px");
                    }
                }
            }
        });

        AssertRendered(result);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>A single queued row, the smallest non-empty state the queue panel can be in, distinct from
    /// "several" (which several other snapshots already cover) and from the empty/collapsed case.</summary>
    [Fact]
    public void Main_window_installing_with_one_queued_row_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(2).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            rows[0].ApplyInstallStatus(new InstallItemStatus(
                rows[0].Item.SourceId, rows[0].Item.Id, 30, "Wird heruntergeladen…", null, null)
            {
                BytesReceived = 5_000_000,
                BytesTotal = 40_000_000,
            });
            viewModel.QueueRows.Add(rows[1]);

            viewModel.IsInstalling = true;
            viewModel.IsQueuePanelExpanded = true;

            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-queue-one-row-de", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Once the batch's own automatic second attempt actually starts, its header is its own
    /// visibly separate section in the install band, not folded into the ordinary progress text.
    /// </summary>
    [Fact]
    public void Main_window_with_a_second_attempt_underway_renders_its_own_header()
    {
        RenderResult? result = null;
        Visibility? headerVisibility = null;
        string? headerText = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var row = viewModel.Updates.First(r => r.Item.CanInstall);
            row.BeginInstall();
            viewModel.InstallBatch.Add(row);
            row.ApplyInstallStatus(new InstallItemStatus(row.Item.SourceId, row.Item.Id, 20, null, null, null));

            viewModel.IsInstalling = true;
            viewModel.IsSecondAttemptActive = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-second-attempt-de", 1100, 700);

            var header = FindByName<TextBlock>(content, "SecondAttemptHeader");
            headerVisibility = header?.Visibility;
            headerText = header?.Text;
        });

        AssertRendered(result);
        Assert.Equal(Visibility.Visible, headerVisibility);
        Assert.Equal("Zweiter Versuch", headerText);
    }

    /// <summary>
    /// The tightest real combination: six queued rows (past the ~3-4 the scroller shows before it needs to
    /// scroll) at the window's own 860px minimum width, in German (the longer of the two languages). Only
    /// four installable rows exist in <see cref="Populate"/>, so three more are added directly here,
    /// scoped to this one test rather than inflating every other snapshot's row count.
    /// </summary>
    [Fact]
    public void Main_window_installing_with_six_queued_rows_at_minimum_width_renders()
    {
        RenderResult? result = null;
        ScrollViewer? scroll = null;
        ScrollBar? verticalScrollBar = null;
        List<double> removeButtonRights = new();
        List<Visibility> gripVisibilities = new();
        double scrollBarLeft = 0;
        int fittingRows = 0;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            viewModel.Updates.Add(new UpdateRowViewModel(
                Item("Contoso.Extra1", "Contoso Extra Tool 1", "winget", UpdateCategory.Software, "1.0", "1.1")));
            viewModel.Updates.Add(new UpdateRowViewModel(
                Item("Contoso.Extra2", "Contoso Extra Tool 2", "winget", UpdateCategory.Software, "2.0", "2.1")));
            viewModel.Updates.Add(new UpdateRowViewModel(
                Item("Contoso.Extra3", "Contoso Extra Tool 3", "winget", UpdateCategory.Software, "3.0", "3.1")));

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(7).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            rows[0].ApplyInstallStatus(new InstallItemStatus(
                rows[0].Item.SourceId, rows[0].Item.Id, 55, "Wird heruntergeladen…", null, null)
            {
                BytesReceived = 30_000_000,
                BytesTotal = 60_000_000,
            });
            foreach (var row in rows.Skip(1))
                viewModel.QueueRows.Add(row);

            viewModel.IsInstalling = true;
            viewModel.IsQueuePanelExpanded = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            result = WpfHarness.RenderWindow(
                window, "main-queue-six-rows-narrow-de", (int)window.MinWidth, (int)window.MinHeight);

            var band = FindByName<DockPanel>(content, "InstallStrip");
            Assert.NotNull(band);
            AssertNoChildClips(band!, band!.Children.OfType<FrameworkElement>());

            // The queue panel's scrollbar must not overlay the per-row remove button.
            scroll = FindByName<ScrollViewer>(content, "QueueRowsScroll");
            verticalScrollBar = scroll is null ? null : FindFirstDescendant<ScrollBar>(scroll);
            if (verticalScrollBar is not null && scroll is not null)
                scrollBarLeft = verticalScrollBar.TransformToAncestor(scroll).Transform(new Point(0, 0)).X;
            if (scroll is not null)
            {
                foreach (var button in FindAllByName<Button>(scroll, "RemoveQueueButton"))
                    removeButtonRights.Add(
                        button.TransformToAncestor(scroll).Transform(new Point(button.ActualWidth, 0)).X);

                // The grip is permanent now, not hover/focus-only, so it must already be Visible
                // at rest, before this test ever touches the mouse or focus.
                gripVisibilities.AddRange(
                    FindAllByName<Wpf.Ui.Controls.SymbolIcon>(scroll, "QueueGripIcon").Select(icon => icon.Visibility));

                // The tightened card fits at least 4 full rows in the scroller's 240px cap,
                // where only 3 fitted before.
                fittingRows = FindAllByName<Border>(scroll, "QueueRowCard")
                    .Count(card => card.TransformToAncestor(scroll).Transform(new Point(0, card.ActualHeight)).Y <= 240);
            }
        });

        AssertRendered(result);
        Assert.NotNull(scroll);
        Assert.NotNull(verticalScrollBar);
        Assert.True(removeButtonRights.Count > 0, "Expected at least one queue row's remove button.");
        foreach (var right in removeButtonRights)
            Assert.True(right <= scrollBarLeft,
                $"Remove button right edge ({right:0}) overlaps the scrollbar (starts at {scrollBarLeft:0}).");

        Assert.True(gripVisibilities.Count > 0, "Expected at least one queue row's grip icon.");
        Assert.All(gripVisibilities, v => Assert.Equal(Visibility.Visible, v));

        Assert.True(fittingRows >= 4, $"Only {fittingRows} queue rows fit in the 240px scroller, expected at least 4.");
    }

    /// <summary>
    /// A long package name in the queue must trim with an ellipsis and leave the critical icon that follows
    /// it on screen at the 860 px minimum width, instead of the name running on unbounded and pushing the
    /// icon out of the card.
    /// </summary>
    [Fact]
    public void Queue_row_long_name_trims_and_keeps_the_critical_icon_visible_at_minimum_width()
    {
        RenderResult? result = null;
        double nameActual = 0;
        double nameFull = 0;
        double iconRight = 0;
        double iconWidth = 0;
        double scrollWidth = 0;
        string nameText = "";

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);
            var running = viewModel.Updates.First(r => r.Item.CanInstall);
            running.BeginInstall();
            viewModel.InstallBatch.Add(running);

            var queued = new UpdateRowViewModel(Item(
                "Microsoft.VCRedist.2015+.x64",
                "Microsoft Visual C++ 2015-2022 Redistributable (x64) with a deliberately very long trailing description that goes on and on far beyond what any queue row can hold at the minimum window width",
                "winget", UpdateCategory.Software, "14.38.33135.0", "14.40.33810.0", critical: true));
            viewModel.Updates.Add(queued);
            queued.BeginInstall();
            viewModel.InstallBatch.Add(queued);
            viewModel.QueueRows.Add(queued);
            viewModel.IsInstalling = true;
            viewModel.IsQueuePanelExpanded = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(
                window, "main-queue-long-name-narrow-en", (int)window.MinWidth, (int)window.MinHeight);

            var scroll = FindByName<ScrollViewer>(content, "QueueRowsScroll");
            var name = FindAllByName<TextBlock>(content, "QueueRowName").FirstOrDefault(t => t.Text == queued.Name);
            Assert.NotNull(scroll);
            Assert.NotNull(name);
            var icon = FindAllByName<Wpf.Ui.Controls.SymbolIcon>(scroll!, "QueueRowCriticalIcon").FirstOrDefault(i => ReferenceEquals(i.DataContext, queued));
            Assert.NotNull(icon);

            var typeface = new Typeface(name!.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch);
            nameFull = new FormattedText(name.Text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, name.FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
            nameActual = name.ActualWidth;
            nameText = name.Text;
            iconWidth = icon!.ActualWidth;
            iconRight = icon.TransformToAncestor(scroll!).Transform(new Point(icon.ActualWidth, 0)).X;
            scrollWidth = scroll!.ActualWidth;
        });

        AssertRendered(result);
        Assert.True(nameActual > 0 && nameActual < nameFull,
            $"The long name should be trimmed (shown {nameActual:0} of {nameFull:0} px, text '{nameText}').");
        Assert.True(iconWidth > 0, "The critical icon should be laid out.");
        Assert.True(iconRight <= scrollWidth, $"The critical icon ends at {iconRight:0}, past the {scrollWidth:0} px list.");
    }

    /// <summary>
    /// The remove button is Hidden until its card is hovered or focused, and a Hidden element cannot hold
    /// keyboard focus: if the reveal keys on the card's own focus, Tab from the card to the button turns
    /// the card's focus off and hides the button that just received it. Real keyboard focus needs a
    /// shown window, so the content is re-hosted in a plain one.
    /// </summary>
    [Fact]
    public void Queue_row_remove_button_stays_visible_while_it_holds_keyboard_focus()
    {
        var cardFocused = false;
        var buttonVisibleOnCardFocus = false;
        var buttonFocused = false;
        var buttonVisibleOnButtonFocus = false;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);
            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(3).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            foreach (var row in rows.Skip(1))
                viewModel.QueueRows.Add(row);

            viewModel.IsInstalling = true;
            viewModel.IsQueuePanelExpanded = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            window.Content = null;

            var host = new Window
            {
                Width = 1100, Height = 700, ShowInTaskbar = false, Content = content,
                DataContext = viewModel, Style = new Style(typeof(Window)),
            };
            host.Show();
            try
            {
                // The queue list is realized over a few dispatcher rounds after the first layout.
                for (var pass = 0; pass < 4; pass++)
                {
                    host.UpdateLayout();
                    host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                var card = FindByName<Border>(content, "QueueRowCard");
                var button = FindByName<Wpf.Ui.Controls.Button>(content, "RemoveQueueButton");
                Assert.NotNull(card);
                Assert.NotNull(button);

                card!.Focus();
                host.UpdateLayout();
                cardFocused = card.IsKeyboardFocused;
                buttonVisibleOnCardFocus = button!.Visibility == Visibility.Visible;

                // What Tab does from the card: move keyboard focus to the (now visible) button.
                button.Focus();
                host.UpdateLayout();
                buttonFocused = button.IsKeyboardFocused;
                buttonVisibleOnButtonFocus = button.Visibility == Visibility.Visible;
            }
            finally
            {
                host.Close();
            }
        });

        Assert.True(cardFocused, "The queue card should take keyboard focus.");
        Assert.True(buttonVisibleOnCardFocus, "The remove button should appear while its card is focused.");
        Assert.True(buttonFocused, "The remove button should accept keyboard focus.");
        Assert.True(buttonVisibleOnButtonFocus, "The remove button must stay visible while it holds focus.");
    }

    /// <summary>
    /// Keyboard reorder (Alt+Up/Down in <c>MainWindow.xaml.cs</c>'s <c>OnQueueRowKeyDown</c>) is
    /// just a call into <see cref="MainViewModel.MoveQueueRow"/> with the neighbouring index, so this
    /// proves the method itself does the "move exactly one place, no wraparound past either end" contract
    /// the gesture depends on, rather than faking a hardware Alt keypress through the render harness (Alt
    /// only ever surfaces through real OS input, <see cref="System.Windows.Input.Keyboard.Modifiers"/> is
    /// not something a raised <see cref="System.Windows.Input.KeyEventArgs"/> can spoof).
    /// </summary>
    [Fact]
    public void Moving_a_queue_row_down_shifts_it_one_place_and_does_nothing_from_the_last_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, _, _) = CreateMainViewModel("de");
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(3).ToList();
            foreach (var row in rows)
                viewModel.QueueRows.Add(row);

            var movedRow = viewModel.QueueRows[1];
            viewModel.MoveQueueRow(1, 2);
            Assert.Equal(movedRow, viewModel.QueueRows[2]);
            Assert.Equal(3, viewModel.QueueRows.Count);

            var lastIndex = viewModel.QueueRows.Count - 1;
            var lastRow = viewModel.QueueRows[lastIndex];
            viewModel.MoveQueueRow(lastIndex, lastIndex + 1);
            Assert.Equal(lastRow, viewModel.QueueRows[lastIndex]);
            Assert.Equal(3, viewModel.QueueRows.Count);
        });
    }

    /// <summary>
    /// Two or more rows running at once (running more than one item in parallel at all) switches the
    /// strip from its single "current item" look, every other install-strip
    /// snapshot's own case, to a compact per-row list, one bar and one stop button each. Three rows, at
    /// three different percentages, proves the list actually holds every one of them rather than repeating
    /// the first.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Main_window_installing_three_rows_at_once_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(3).ToList();
            var percentages = new double[] { 15, 48, 91 };
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].BeginInstall();
                viewModel.InstallBatch.Add(rows[i]);
                rows[i].ApplyInstallStatus(new InstallItemStatus(
                    rows[i].Item.SourceId, rows[i].Item.Id, percentages[i], "Wird installiert…", null, null));
            }

            viewModel.IsInstalling = true;
            Assert.Equal(3, viewModel.RunningCount);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-installing-three-running-{language}", 1100, 700);

            var multiPanel = FindByName<StackPanel>(content, "InstallStripMultiRunning");
            Assert.NotNull(multiPanel);
            Assert.Equal(Visibility.Visible, multiPanel!.Visibility);
            AssertNoChildClips(multiPanel, multiPanel.Children.OfType<FrameworkElement>());

            var cards = FindAllByName<Grid>(content, "RunningRowCard");
            Assert.Equal(3, cards.Count);
            AssertNoChildClips(multiPanel, cards);
            AssertRowsStackWithoutOverlap(multiPanel, cards);
        });

        AssertRendered(result);
    }

    /// <summary>Same three-running-rows case at the window's own 860px minimum width, the per-row name
    /// column is what gives way first (star, not auto, same reasoning as the single-row strip), never a
    /// clip or an overlap.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Main_window_installing_three_rows_at_once_at_minimum_width_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(3).ToList();
            var percentages = new double[] { 15, 48, 91 };
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].BeginInstall();
                viewModel.InstallBatch.Add(rows[i]);
                rows[i].ApplyInstallStatus(new InstallItemStatus(
                    rows[i].Item.SourceId, rows[i].Item.Id, percentages[i], "Wird installiert…", null, null));
            }

            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            result = WpfHarness.RenderWindow(
                window, $"main-installing-three-running-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);

            var multiPanel = FindByName<StackPanel>(content, "InstallStripMultiRunning");
            Assert.NotNull(multiPanel);
            AssertNoChildClips(multiPanel!, multiPanel!.Children.OfType<FrameworkElement>());

            var cards = FindAllByName<Grid>(content, "RunningRowCard");
            Assert.Equal(3, cards.Count);
            AssertNoChildClips(multiPanel!, cards);
            AssertRowsStackWithoutOverlap(multiPanel!, cards);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The action buttons are DOCKED to the right of the toolbar (not inside the WrapPanel any more,
    /// 2026-08-22) and always keep their full natural size; the WrapPanel now holds only search and the
    /// filter switches, and it is THAT panel which gives way at 860px. This proves the WrapPanel keeps
    /// its place and nothing inside it clips, the property the layout relies on.
    /// </summary>
    [Fact]
    public void The_toolbar_fits_at_the_windows_minimum_width()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            _ = WpfHarness.RenderWindow(
                window, "main-toolbar-narrow-de", (int)window.MinWidth, (int)window.MinHeight);

            var toolbar = FindByName<WrapPanel>(content, "ToolbarWrapPanel");
            Assert.NotNull(toolbar);
            Assert.True(toolbar!.ActualWidth > 0, "the toolbar was never laid out");
            AssertNoChildClips(toolbar!, toolbar!.Children.OfType<FrameworkElement>());
        });
    }

    /// <summary>
    /// 22.08.2026: the "Selbst öffnen" block used to sit in a ListBox whose own template ScrollViewer
    /// defaulted to Auto horizontal, which measures at infinite width and never wraps, the fix is
    /// <c>ScrollViewer.HorizontalScrollBarVisibility="Disabled"</c> on <c>ManualOpenListBox</c>. Twelve
    /// entries at the window's own 860px minimum width must wrap onto several rows, nothing clipped.
    /// </summary>
    [Fact]
    public void Manual_open_block_wraps_onto_several_rows_at_minimum_width()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            for (var i = 0; i < 12; i++)
            {
                viewModel.Updates.Add(new UpdateRowViewModel(Item(
                    $"manual-{i}", $"Manual Launcher Number {i}", "manual-source", UpdateCategory.Games,
                    null, null, open: "manual://open")));
            }

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            // Tall enough to show the manual-open block without scrolling, the assertions below measure
            // real layout regardless of viewport height, but a snapshot that scrolls the thing under test
            // out of frame is useless for actually looking at it. Populate()'s own fixture rows (Software,
            // System & Treiber, Entwickler-Tools, plus Steam and Epic already in the compact block) already
            // fill ~800px before the 13th (Epic) and 12 synthetic chips even start, so this needs real headroom.
            result = WpfHarness.RenderWindow(window, "main-manual-open-wrap-narrow-de", (int)window.MinWidth, 1400);

            var wrap = FindByName<WrapPanel>(content, "ManualOpenWrapPanel");
            Assert.NotNull(wrap);
            var items = wrap!.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).ToList();
            Assert.True(items.Count >= 12, $"expected at least 12 manual-open entries, found {items.Count}");
            AssertNoChildClips(wrap, items);

            var rowTops = items
                .Select(c => c.TransformToAncestor(wrap).Transform(new Point(0, 0)).Y)
                .Select(y => Math.Round(y))
                .Distinct()
                .Count();
            Assert.True(rowTops > 1, "the manual-open entries never wrapped onto a second row");
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The block now scrolls WITH the table instead of sitting in its own capped box.
    /// With enough rows to overflow the card, there is exactly ONE scrollable area (<c>ResultsScrollViewer</c>)
    ///, <c>ManualOpenListBox</c>'s own template ScrollViewer is disabled
    /// (<c>ScrollViewer.VerticalScrollBarVisibility="Disabled"</c>), so it reports no scrollable height of
    /// its own; the outer one does.
    /// </summary>
    [Fact]
    public void Manual_open_block_scrolls_with_the_table_not_in_its_own_box()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            for (var i = 0; i < 40; i++)
            {
                viewModel.Updates.Add(new UpdateRowViewModel(Item(
                    $"row-{i}", $"Contoso Tool {i}", "winget", UpdateCategory.Software, "1.0", "1.1")));
            }
            for (var i = 0; i < 12; i++)
            {
                viewModel.Updates.Add(new UpdateRowViewModel(Item(
                    $"manual-{i}", $"Manual Launcher Number {i}", "manual-source", UpdateCategory.Games,
                    null, null, open: "manual://open")));
            }

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            result = WpfHarness.RenderWindow(window, "main-queue-scroll-shared-de", 1100, 700);

            var outer = FindByName<ScrollViewer>(content, "ResultsScrollViewer");
            Assert.NotNull(outer);
            Assert.True(outer!.ScrollableHeight > 0, "40 rows + 12 manual entries should overflow the shared scroll area");

            var manualList = FindByName<ListBox>(content, "ManualOpenListBox");
            Assert.NotNull(manualList);
            // Structural, not runtime state: this attached property is what stops ManualOpenListBox's own
            // template ScrollViewer from ever becoming a second, independently scrollable area.
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetVerticalScrollBarVisibility(manualList!));
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Strg+F must never report the key as handled when the search box could not
    /// actually take focus, doing so would silently swallow the keystroke. <see
    /// cref="MainWindow.TryFocusSearch"/> is the guarded piece the window's own Ctrl+F command routes
    /// to, checked directly here since the real gesture reads live global keyboard modifier state a
    /// headless test cannot fake. The affirmative case (an enabled box actually taking
    /// focus) is not checked here either: this harness deliberately never shows its window (see WpfHarness's
    /// own doc comment), and WPF's <c>Focus()</c> cannot succeed without a live HWND regardless of the
    /// element's own state, the same limitation <see cref="KeyboardOperabilityTests"/> already documents
    /// for key routing in general. Disabling the box makes the failure unambiguous and proves the method
    /// propagates it instead of assuming success, which is the actual regression this guards against.
    /// </summary>
    [Fact]
    public void TryFocusSearch_reports_failure_when_the_search_box_cannot_take_focus()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            ((FrameworkElement)window.Content).UpdateLayout();

            window.SearchBox.IsEnabled = false;
            Assert.False(window.TryFocusSearch(), "a disabled search box must not report success");
        });
    }

    /// <summary>
    /// Every other install-strip snapshot only populates <c>InstallBatch</c>, and the queue panel's own
    /// rows come from the separate, otherwise-empty <c>QueueRows</c> collection, so this is the only
    /// coverage of the queue panel's row template (position number, brand tile, version arrow, date,
    /// critical/admin marks). Row 0 is the one actively running (excluded from the panel, same as
    /// production); the rest sit Queued and show up in it.
    /// </summary>
    [Fact]
    public void Install_queue_panel_with_several_rows_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(4).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            rows[0].ApplyInstallStatus(new InstallItemStatus(
                rows[0].Item.SourceId, rows[0].Item.Id, 40, "Wird heruntergeladen…", null, null)
            {
                BytesReceived = 20_000_000,
                BytesTotal = 50_000_000,
            });

            foreach (var row in rows.Skip(1))
                viewModel.QueueRows.Add(row);

            viewModel.IsInstalling = true;
            viewModel.IsQueuePanelExpanded = true;

            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-queue-panel-de", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>Every state a row can be in after an install batch, side by side.</summary>
    [Fact]
    public void Main_window_after_an_install_batch_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var rows = viewModel.Updates.ToList();
            rows[1].BeginInstall();
            rows[1].ApplyInstallStatus(new InstallItemStatus(rows[1].Item.SourceId, rows[1].Item.Id, 100, null, true, null));
            rows[2].BeginInstall();
            rows[2].ApplyInstallStatus(new InstallItemStatus(
                rows[2].Item.SourceId, rows[2].Item.Id, 0, null, false,
                "Installation fehlgeschlagen. Einzelheiten stehen im Protokoll."));
            rows[3].BeginInstall();

            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-installed-de", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// 200% display scaling at the smallest window the app allows, the worst case that must stay
    /// usable. Rendering below MinWidth/MinHeight would only prove that a size the window refuses to
    /// take looks bad, so the pixel size is the minimum multiplied by the scale.
    /// </summary>
    [Fact]
    public void Main_window_at_double_scale_and_minimum_size_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);

            var width = (int)(window.MinWidth * 2);
            var height = (int)(window.MinHeight * 2);
            result = WpfHarness.RenderWindow(window, "main-scale200-de", width, height, scale: 2.0);
        });

        AssertRendered(result);
    }

    /// <summary>150% is the most common non-default Windows display scale (the harness's LayoutTransform
    /// approach approximates real per-monitor DPI scaling, not identical, but it exercises the same
    /// layout code path a genuinely scaled window would).</summary>
    [Fact]
    public void Main_window_at_150_percent_scale_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);

            var width = (int)(window.MinWidth * 1.5);
            var height = (int)(window.MinHeight * 1.5);
            result = WpfHarness.RenderWindow(window, "main-scale150-de", width, height, scale: 1.5);
        });

        AssertRendered(result);
    }

    /// <summary>A search with no hits must say so, not leave an empty card standing there.</summary>
    [Fact]
    public void Main_window_with_a_search_that_matches_nothing_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            viewModel.SearchQuery = "zzzzz";
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-nomatch-de", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The install strip at the window's own minimum width. A snapshot would only SHOW the clipping; this
    /// measures it, the current item, the bar, the batch figures and the pause/cancel buttons together used
    /// to want far more room than 860px leaves, so the buttons were cut off. Numeric on purpose: the render
    /// suite is warning-only, and losing the pause and cancel buttons mid-batch is not a cosmetic defect.
    /// </summary>
    [Fact]
    public void The_install_strip_fits_at_the_windows_minimum_width()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(3).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
            }

            // The widest state: a long name, a live transfer figure and the whole stats line.
            rows[0].ApplyInstallStatus(new InstallItemStatus(
                rows[0].Item.SourceId, rows[0].Item.Id, 62, "Wird heruntergeladen…", null, null)
            {
                BytesReceived = 1_234_000_000,
                BytesTotal = 4_567_000_000,
            });

            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);

            // Held on to: rendering DETACHES the content from the window, so afterwards the window has none
            // and the laid-out tree is only reachable through this.
            var content = (FrameworkElement)window.Content;

            _ = WpfHarness.RenderWindow(
                window, "main-installing-narrow-de", (int)window.MinWidth, (int)window.MinHeight);

            // The strip itself is a DockPanel now: the stop button is DOCKED, not a Grid
            // column, so it keeps its full size unconditionally instead of being squeezed to nothing
            // next to the star columns, which is what left it unreachable before. Checked at two
            // levels: the DockPanel's own two children (the button panel, and everything else) must not
            // overflow the strip, and the content Grid's own columns must not overflow ITS width either.
            var strip = FindByName<DockPanel>(content, "InstallStrip");
            Assert.NotNull(strip);
            Assert.True(strip!.ActualWidth > 0, "the install strip was never laid out");
            AssertNoChildClips(strip, strip.Children.OfType<FrameworkElement>());

            var stripContent = FindByName<Grid>(content, "InstallStripContent");
            Assert.NotNull(stripContent);
            AssertNoChildClips(stripContent!, stripContent!.Children.OfType<FrameworkElement>());
        });
    }

    /// <summary>The narrowest the window can be made, where German labels break first. English is
    /// checked too even though it runs shorter: nothing before this exercised the min-width layout
    /// in that language at all.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Main_window_at_its_minimum_width_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, $"main-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);
    }

    /// <summary>The light-theme counterpart of the narrow-width snapshot above, every other min-width
    /// render of this window is dark, and a resource that only resolves in the light dictionary would
    /// otherwise never be checked at the width where text is tightest.</summary>
    [Fact]
    public void Main_window_at_its_minimum_width_renders_in_the_light_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            theme.Apply("light");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-narrow-light-de", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>
    /// The window used to declare Height AND MinHeight both 820, so it could never be made to fit
    /// a 1366x768 screen (roughly 728 usable points once the taskbar is subtracted). MinHeight is now 640;
    /// this renders at the window's own MinWidth/MinHeight (860x640) to check what the smaller height
    /// breaks: the toolbar, the table header (SelectAllCheckBox) and the install strip must all still be
    /// visible above the fold, and the table's own scroll area must still be able to scroll rather than
    /// having been squeezed to nothing.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Main_window_fits_a_1366x768_screen(string language)
    {
        RenderResult? result = null;
        double checkboxBottom = 0, stripBottom = 0, scrollableHeight = 0;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-1366x768-{language}", 860, 640);

            var checkbox = FindByName<FrameworkElement>(content, "SelectAllCheckBox");
            Assert.NotNull(checkbox);
            checkboxBottom = checkbox!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + checkbox.ActualHeight;

            var strip = FindByName<FrameworkElement>(content, "InstallStrip");
            Assert.NotNull(strip);
            stripBottom = strip!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + strip.ActualHeight;

            var scroll = FindByName<ScrollViewer>(content, "ResultsScrollViewer");
            Assert.NotNull(scroll);
            scrollableHeight = scroll!.ScrollableHeight;
        });

        AssertRendered(result);
        Assert.True(checkboxBottom <= 640 + 0.5,
            $"Table header bottom {checkboxBottom:0.0} exceeds the 640px window, it is clipped.");
        Assert.True(stripBottom <= 640 + 0.5,
            $"Install strip bottom {stripBottom:0.0} exceeds the 640px window, it is clipped.");
        Assert.True(scrollableHeight > 0, "the populated table should still overflow and scroll at 640px");
    }

    /// <summary>The light-theme counterpart of the 1366x768 snapshot above.</summary>
    [Fact]
    public void Main_window_fits_a_1366x768_screen_in_the_light_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            theme.Apply("light");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-1366x768-light-de", 860, 640);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>A wide window gives the extra width to the name column, so the table still reads as one
    /// aligned grid with the same columns in both themes and languages. Snapshot only: the review is by eye.</summary>
    [Theory]
    [InlineData("en", "dark")]
    [InlineData("en", "light")]
    [InlineData("de", "light")]
    public void Main_window_wide_renders(string language, string themeName)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            theme.Apply(themeName);
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, $"main-wide-{themeName}-{language}", 1600, 800);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>The install strip trims long names with an ellipsis, so the full name has to be one hover
    /// away, for the single running row and for each row of the compact multi-row list.</summary>
    [Theory]
    [InlineData(1, "InstallStripItemName")]
    [InlineData(3, "RunningRowName")]
    public void Install_strip_names_carry_a_tooltip_with_the_full_name(int running, string nameElement)
    {
        var names = new List<string>();
        var toolTips = new List<string>();

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);
            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(running).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
                row.ApplyInstallStatus(new InstallItemStatus(
                    row.Item.SourceId, row.Item.Id, 40, "Installing", null, null));
            }

            viewModel.IsInstalling = true;
            Assert.Equal(running, viewModel.RunningCount);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, $"main-install-strip-names-{running}-en", 1100, 700);

            foreach (var name in FindAllByName<TextBlock>(content, nameElement))
            {
                names.Add(name.Text);
                toolTips.Add(name.ToolTip as string ?? "");
            }
        });

        Assert.Equal(running, names.Count);
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names, toolTips);
    }

    /// <summary>
    /// Steam as a launcher row with its pending game updates hanging under it as a collapsible
    /// sub-list, the shape that replaced Steam's own table category.
    /// </summary>
    [Fact]
    public void Main_window_shows_steam_games_as_a_sub_list()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            viewModel.SearchQuery = "steam";
            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-steam-de", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// A Steam game row offers Uninstall, so it has to show what that does: a running game removal shows
    /// its progress lane and a finished one reads "Uninstalled", the same state words the parent rows use.
    /// </summary>
    [Fact]
    public void Steam_child_rows_show_running_and_uninstalled_state()
    {
        RenderResult? result = null;
        var runningLaneVisible = false;
        var runningLaneWidth = 0.0;
        string? doneWord = null;
        var doneWordVisible = false;
        var idleLaneVisible = true;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);
            viewModel.SearchQuery = "steam";
            var steam = viewModel.Updates.First(r => r.Item.Id == "steam");
            var running = steam.Children[0];
            var done = steam.Children[1];

            running.BeginInstall();
            running.ApplyInstallStatus(new InstallItemStatus(running.Item.SourceId, running.Item.Id, 40, "Removing", null, null));
            done.BeginInstall();
            done.WasUninstalled = true;
            done.ApplyInstallStatus(new InstallItemStatus(done.Item.SourceId, done.Item.Id, 100, "Done", true, null));

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-steam-child-state-en", 1100, 700);

            var runningLane = FindAllByName<FrameworkElement>(content, "ChildRunningLane")
                .FirstOrDefault(e => ReferenceEquals(e.DataContext, running));
            runningLaneVisible = runningLane is { Visibility: Visibility.Visible };
            runningLaneWidth = runningLane?.ActualWidth ?? 0;

            var word = FindAllByName<TextBlock>(content, "ChildStateWord")
                .FirstOrDefault(e => ReferenceEquals(e.DataContext, done));
            doneWord = word?.Text;
            doneWordVisible = word is { Visibility: Visibility.Visible, ActualWidth: > 0 };

            // A child that never ran anything keeps both lanes collapsed.
            idleLaneVisible = FindAllByName<FrameworkElement>(content, "ChildStateLane")
                .Any(e => e.DataContext is UpdateRowViewModel r && r.InstallState == RowInstallState.Idle && e.ActualWidth > 0);
        });

        AssertRendered(result);
        Assert.True(runningLaneVisible, "A running child should show its progress lane.");
        Assert.True(runningLaneWidth > 0);
        Assert.Equal("Uninstalled", doneWord);
        Assert.True(doneWordVisible, "A finished uninstall should show its state word.");
        Assert.False(idleLaneVisible, "An idle child should not reserve any state lane width.");
    }

    /// <summary>
    /// A row with a long version string on both sides of the arrow AND a changelog link is the
    /// worst case for the version lane, checks it at the window's minimum width, where there is the
    /// least room to begin with, and at 200% scale.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Main_window_keeps_the_changelog_button_inside_a_long_version_row(double scale)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var longVersionItem = new UpdateItem
            {
                Id = "LongVersion.Tool",
                SourceId = "winget",
                Category = UpdateCategory.Software,
                Name = "A Tool With A Very Long Version Number",
                InstalledVersion = "2025.11.30-preview.987654",
                AvailableVersion = "2026.08.15-preview.123456",
                Meta = new Dictionary<string, string> { ["changelogBody"] = "Fixed things." },
            };
            var longVersionRow = new UpdateRowViewModel(longVersionItem);
            viewModel.Updates.Add(longVersionRow);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            result = WpfHarness.RenderWindow(
                window, $"main-long-version-{scale:0.0}x", (int)window.MinWidth, (int)window.MinHeight, scale);

            var row = FindAllByName<Grid>(content, "TableRow")
                .FirstOrDefault(g => g.DataContext is UpdateRowViewModel vm && vm.Name == longVersionRow.Name);
            Assert.NotNull(row);

            // The info button is gone from the version lane: the available version itself is the link.
            Assert.Null(FindByName<Button>(row!, "ChangelogButton"));
            var link = FindByName<Button>(row!, "AvailableVersionLink");
            Assert.NotNull(link);
            Assert.Equal(Visibility.Visible, link!.Visibility);

            var lane = FindByName<Grid>(row!, "VersionLane")!;
            var laneRight = lane.TransformToAncestor(row!).Transform(new Point(0, 0)).X + lane.ActualWidth;
            var right = link.TransformToAncestor(row!).Transform(new Point(0, 0)).X + link.ActualWidth;
            Assert.True(
                right <= laneRight + 1 && right <= row!.ActualWidth + 1,
                $"changelog link ends at {right:0} px, lane at {laneRight:0}, row only {row.ActualWidth:0} px wide.");
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Every sortable header shows a sort mark: the neutral up/down mark in the tertiary tone on the inactive
    /// ones, the direction in the accent on the active one, and each carries a "sort by" tooltip.
    /// </summary>
    [Fact]
    public void Every_sortable_header_shows_a_sort_mark_and_a_tooltip()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            // Date and size need data, or the auto-hide would take their headers away.
            viewModel.Updates.Add(new UpdateRowViewModel(new UpdateItem
            {
                Id = "x.sized", SourceId = "winget", Category = UpdateCategory.Software, Name = "Sized",
                InstalledVersion = "1.0", AvailableVersion = "2.0", DownloadSize = 5_000_000,
                PublishDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            }));
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-sort-marks-de", 1100, 700);

            var headers = new[] { "Name", "Date", "Version", "Size" }
                .Select(key => FindByName<StackPanel>(content, "SortHeader" + key))
                .ToList();
            Assert.DoesNotContain(null, headers);

            foreach (var header in headers.Select(h => h!))
            {
                Assert.False(string.IsNullOrEmpty(header.ToolTip as string), "a sortable header has no tooltip");
                Assert.StartsWith("Nach ", (string)header.ToolTip);
                Assert.EndsWith(" sortieren", (string)header.ToolTip);

                var glyph = header.Children.OfType<TextBlock>().Last();
                var active = glyph.Text is " ▲" or " ▼";
                var expected = (SolidColorBrush)window.FindResource("AccentTextFillColorPrimaryBrush");
                Assert.True(active || glyph.Text.Length == 0, $"unexpected sort mark '{glyph.Text}'");
                Assert.Equal(expected.Color, ((SolidColorBrush)glyph.Foreground).Color);
            }

            // Name is the active column by default: exactly one header is active, the other three are neutral.
            Assert.Equal(1, headers.Count(h => h!.Children.OfType<TextBlock>().Last().Text.Trim() is "▲" or "▼"));
            Assert.Equal(3, headers.Count(h => h!.Children.OfType<TextBlock>().Last().Text.Length == 0));
        });

        AssertRendered(result);
    }

    /// <summary>
    /// A row with a changelog shows its available version as a link (accent text, hand cursor, tooltip and
    /// accessible name from the changelog header) that runs the changelog command; a loading fetch puts a
    /// small ring right of it; a row without one keeps plain text and no link.
    /// </summary>
    [Fact]
    public void The_available_version_is_a_changelog_link_only_where_a_changelog_exists()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            var opened = new List<string>();

            var withChangelog = new UpdateRowViewModel(
                new UpdateItem
                {
                    Id = "a.changelog", SourceId = "winget", Category = UpdateCategory.Software,
                    Name = "Row with changelog", InstalledVersion = "1.0.0", AvailableVersion = "1.1.0",
                    Meta = new Dictionary<string, string> { ["changelogBody"] = "Fixed things." },
                },
                onShowChangelog: row =>
                {
                    opened.Add(row.Name);
                    return Task.CompletedTask;
                });
            var plain = new UpdateRowViewModel(new UpdateItem
            {
                Id = "b.plain", SourceId = "winget", Category = UpdateCategory.Software,
                Name = "Row without changelog", InstalledVersion = "2.0.0", AvailableVersion = "2.1.0",
            });
            viewModel.Updates.AddRange([withChangelog, plain]);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, "main-version-link-en", 1100, 700);

            Grid RowOf(UpdateRowViewModel vm) =>
                FindAllByName<Grid>(content, "TableRow").First(g => g.DataContext is UpdateRowViewModel row && row.Name == vm.Name);

            var linkRow = RowOf(withChangelog);
            var link = FindByName<Button>(linkRow, "AvailableVersionLink")!;
            Assert.Equal(Visibility.Visible, link.Visibility);
            Assert.True(link.Focusable);
            Assert.Equal(System.Windows.Input.Cursors.Hand, link.Cursor);
            Assert.Equal("View changelog", link.ToolTip);
            Assert.Equal("View changelog", System.Windows.Automation.AutomationProperties.GetName(link));
            Assert.Equal(Visibility.Collapsed, FindByName<TextBlock>(linkRow, "AvailableVersionText")!.Visibility);

            // Activating the focused link (what Enter or Space does) runs the changelog command.
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(link);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
            // The peer posts the click to the dispatcher instead of running it inline.
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal(["Row with changelog"], opened);

            var plainRow = RowOf(plain);
            Assert.Equal(Visibility.Collapsed, FindByName<Button>(plainRow, "AvailableVersionLink")!.Visibility);
            Assert.Equal(Visibility.Visible, FindByName<TextBlock>(plainRow, "AvailableVersionText")!.Visibility);
        });
    }

    /// <summary>
    /// The version lane's arrow used to sit at a different X per row (a centred
    /// StackPanel's width depends on which children that row shows) and lower than the version text
    /// whenever the changelog button's height stretched the row (only the arrow had its own
    /// VerticalAlignment). A fixed-column Grid fixes both, this measures the real layout, not the image.
    /// </summary>
    [Fact]
    public void Version_lane_arrow_lines_up_across_rows_regardless_of_what_each_row_shows()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");

            var withChangelog = new UpdateRowViewModel(new UpdateItem
            {
                Id = "a.installed.changelog", SourceId = "winget", Category = UpdateCategory.Software,
                Name = "Row A, installed, changelog", InstalledVersion = "1.0.0", AvailableVersion = "1.1.0",
                Meta = new Dictionary<string, string> { ["changelogBody"] = "Fixed things." },
            });
            var installedOnly = new UpdateRowViewModel(new UpdateItem
            {
                Id = "b.installed.only", SourceId = "winget", Category = UpdateCategory.Software,
                Name = "Row B, installed, no changelog", InstalledVersion = "2.0.0", AvailableVersion = "2.1.0",
            });
            var noInstalled = new UpdateRowViewModel(new UpdateItem
            {
                Id = "c.no.installed", SourceId = "winget", Category = UpdateCategory.Software,
                Name = "Row C, no installed version", AvailableVersion = "3.0.0",
            });
            var longVersions = new UpdateRowViewModel(new UpdateItem
            {
                Id = "d.long.versions", SourceId = "winget", Category = UpdateCategory.Software,
                Name = "Row D, very long version numbers",
                InstalledVersion = "2024.11.30-preview.987654321", AvailableVersion = "2026.08.22-preview.123456789",
            });

            viewModel.Updates.AddRange([withChangelog, installedOnly, noInstalled, longVersions]);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-version-arrow-alignment-de", 1100, 700);

            Grid RowOf(UpdateRowViewModel vm) =>
                FindAllByName<Grid>(content, "TableRow").First(g => g.DataContext is UpdateRowViewModel row && row.Name == vm.Name);

            // (a), (b), (d) all show an arrow (they carry an installed version); (c) does not.
            var arrowXs = new[] { withChangelog, installedOnly, longVersions }
                .Select(vm =>
                {
                    var row = RowOf(vm);
                    var arrow = FindByName<TextBlock>(row, "VersionArrow");
                    Assert.NotNull(arrow);
                    Assert.Equal(Visibility.Visible, arrow!.Visibility);
                    return Math.Round(arrow.TransformToAncestor(content).Transform(new Point(0, 0)).X, 1);
                })
                .ToList();
            Assert.True(
                arrowXs.Max() - arrowXs.Min() <= 0.5,
                $"the arrow moved between rows: {string.Join(", ", arrowXs)}");

            var noArrowRow = RowOf(noInstalled);
            var hiddenArrow = FindByName<TextBlock>(noArrowRow, "VersionArrow");
            Assert.NotNull(hiddenArrow);
            Assert.NotEqual(Visibility.Visible, hiddenArrow!.Visibility);

            // Row (c) has no installed version to point an arrow away from: the available-version text
            // centres exactly under the arrow of the rows around it, so a lone version lines up with the
            // middle of every "old → new" pair instead of drifting right by the spinner column.
            var referenceArrow = FindByName<TextBlock>(RowOf(installedOnly), "VersionArrow")!;
            var noVersionText = FindByName<TextBlock>(noArrowRow, "AvailableVersionText")!;
            var arrowMidX = referenceArrow.TransformToAncestor(content).Transform(new Point(referenceArrow.ActualWidth / 2, 0)).X;
            var textMidX = noVersionText.TransformToAncestor(content).Transform(new Point(noVersionText.ActualWidth / 2, 0)).X;
            Assert.True(Math.Abs(arrowMidX - textMidX) <= 1.0,
                $"available-version centre {textMidX:0.0} vs arrow centre {arrowMidX:0.0} for a row with no installed version");

            // Row (a): the arrow's vertical centre must land on the same line as the version text either
            // side of it, even though its own changelog button stretches the lane to 20px tall.
            var changelogRow = RowOf(withChangelog);
            var changelogArrow = FindByName<TextBlock>(changelogRow, "VersionArrow")!;
            var installedText = FindByName<TextBlock>(changelogRow, "InstalledVersionText")!;
            // With a changelog the available version is the link button, not the plain text.
            var availableText = FindByName<Button>(changelogRow, "AvailableVersionLink")!;

            double MidY(FrameworkElement e) =>
                e.TransformToAncestor(content).Transform(new Point(0, e.ActualHeight / 2)).Y;

            var arrowMid = MidY(changelogArrow);
            Assert.True(Math.Abs(arrowMid - MidY(installedText)) <= 1.0,
                $"arrow mid-Y {arrowMid:0.0} vs installed-version mid-Y {MidY(installedText):0.0}");
            Assert.True(Math.Abs(arrowMid - MidY(availableText)) <= 1.0,
                $"arrow mid-Y {arrowMid:0.0} vs available-version mid-Y {MidY(availableText):0.0}");
        });

        AssertRendered(result);
    }

    /// <summary>
    /// During a scan every source's pill shows, not just the "worth a look" ones, waiting,
    /// running, done-with-a-count and failed side by side, running ones sorted first, a cancel button on
    /// the running one.
    /// </summary>
    [Fact]
    public void Main_window_shows_every_source_pill_while_a_scan_is_running()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            // Distinct from every source id Populate() already gave a status to, or this would add a
            // second pill for the same source instead of showing a new one.
            AddStatus(viewModel, "rustup", ProviderScanState.Waiting, 0);
            AddStatus(viewModel, "gcloud", ProviderScanState.Scanning, 0);
            AddStatus(viewModel, "vcpkg", ProviderScanState.Cancelled, 0);
            viewModel.IsScanning = true;

            var window = new MainWindow(viewModel, theme, settings);
            result = WpfHarness.RenderWindow(window, "main-scanning-pills-de", 1100, 700);
        });

        AssertRendered(result);
    }

    /// <summary>The Size header with its sort arrow showing: "Dimensione" and "Dimensiune" at 14 px SemiBold plus the
    /// glyph used to fill the whole column, so the title touched the neighbouring column.</summary>
    [Theory]
    [InlineData("it")]
    [InlineData("ro")]
    [InlineData("de")]
    [InlineData("fr")]
    public void Main_table_size_header_with_the_sort_arrow_leaves_room_in_its_column(string language)
    {
        RenderResult? result = null;
        double headerWidth = 0, columnWidth = 0;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            // pinned: the column hides itself while no row has a size
            viewModel.SetMainColumnHidden("size", hidden: false);
            viewModel.SortByCommand.Execute("size");
            Assert.NotEqual(string.Empty, viewModel.SizeSortGlyph);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-size-header-{language}", (int)window.MinWidth, (int)window.MinHeight);

            var header = FindByName<StackPanel>(content, "SortHeaderSize");
            Assert.NotNull(header);
            var grid = (Grid)header!.Parent;
            headerWidth = header.ActualWidth;
            columnWidth = grid.ColumnDefinitions[6].ActualWidth;
            viewModel.ResetMainColumns();
        });

        AssertRendered(result);
        Assert.Equal(96, columnWidth);
        Assert.True(headerWidth <= columnWidth - 4,
            $"the Size header with its sort arrow is {headerWidth:0.0} px in a {columnWidth:0.0} px column.");
    }

    /// <summary>With every column shown (date, version, size, publisher) the default window keeps the name
    /// column roomy and the table needs no horizontal scrolling.</summary>
    [Fact]
    public void Main_table_with_every_column_shown_fits_the_default_window_without_a_horizontal_scrollbar()
    {
        RenderResult? result = null;
        double nameWidth = 0, publisherWidth = 0;
        var horizontalBars = new List<string>();

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            viewModel.ResetMainColumns();
            foreach (var id in new[] { "date", "size", "publisher" })
                viewModel.SetMainColumnHidden(id, hidden: false);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-all-columns-default-de", (int)window.Width, (int)window.Height);

            var header = FindByName<StackPanel>(content, "SortHeaderSize");
            Assert.NotNull(header);
            var grid = (Grid)header!.Parent;
            nameWidth = grid.ColumnDefinitions[2].ActualWidth;
            publisherWidth = grid.ColumnDefinitions[3].ActualWidth;

            foreach (var viewer in AllDescendants<ScrollViewer>(content))
            {
                if (viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
                    horizontalBars.Add(viewer.Name);
            }
        });

        AssertRendered(result);
        Assert.Equal(120, publisherWidth);
        Assert.True(nameWidth >= 240, $"the name column is only {nameWidth:0.0} px wide with every column shown.");
        Assert.Empty(horizontalBars);
    }

    /// <summary>
    /// RowStuck/RowHandedOff used to carry a full German sentence in the fixed 150px
    /// action column and got cut off there the same way a long row NAME once did in this exact table (see
    /// the comment on the name column's own Grid-instead-of-StackPanel fix above). Both now show a single
    /// word with the sentence moved to the tooltip, and gained a working error-detail button, checks all
    /// three land correctly rendered, not just that the window comes up.
    /// </summary>
    [Fact]
    public void Main_window_shows_stuck_and_handed_off_rows_in_german()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var stuck = new UpdateRowViewModel(
                Item("stuck-driver", "Some Stuck Driver", "windowsupdate", UpdateCategory.SystemAndDrivers, "1.0", "1.1"));
            stuck.ApplyInstallStatus(new InstallItemStatus("windowsupdate", "stuck-driver", 100, "no applicable upgrade", null, null) { IsNotApplicable = true });

            var handedOff = new UpdateRowViewModel(
                Item("handed-off-tool", "Some Handed Off Tool", "nvidia-driver", UpdateCategory.SystemAndDrivers, "1.0", "1.1"));
            handedOff.ApplyInstallStatus(new InstallItemStatus("nvidia-driver", "handed-off-tool", 100, "opened the vendor installer", null, null) { IsManualHandoff = true });

            viewModel.Updates.Add(stuck);
            viewModel.Updates.Add(handedOff);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-stuck-handedoff-de", 1100, 700);

            var rows = FindAllByName<Grid>(content, "TableRow")
                .Where(g => g.DataContext is UpdateRowViewModel vm && (vm.Name == stuck.Name || vm.Name == handedOff.Name))
                .ToList();
            Assert.Equal(2, rows.Count);

            foreach (var row in rows)
            {
                var vm = (UpdateRowViewModel)row.DataContext;
                Assert.Equal(vm.InstallState == RowInstallState.Stuck
                    ? LocalizationService.Instance["RowStuck"]
                    : LocalizationService.Instance["RowHandedOff"], vm.InstallStateText);
                Assert.DoesNotContain("…", vm.InstallStateText);
                Assert.True(vm.HasInstallError, $"{vm.Name} should have a working error-detail button");
            }
        });

        AssertRendered(result);
    }

    /// <summary>The main window never shows the pill of a source whose tool is not on this machine, with no
    /// switch to bring it back; a source that merely failed keeps its pill.</summary>
    [Fact]
    public void Main_window_leaves_out_the_pill_of_a_source_without_a_tool()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            // Switched off, so the strip lists both pills (a quiet strip only lists the noteworthy ones).
            viewModel.ProviderStatuses.Add(new ProviderStatusViewModel("npm") { State = ProviderScanState.NotInstalled, IsEnabled = false });
            viewModel.ProviderStatuses.Add(new ProviderStatusViewModel("winget") { State = ProviderScanState.Done, IsEnabled = false });

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, "main-unavailable-pill-en", 1100, 700);

            var pills = FindAllByName<Border>(content, "PillBorder");
            var absent = Assert.Single(pills, p => p.DataContext is ProviderStatusViewModel { SourceId: "npm" });
            var present = Assert.Single(pills, p => p.DataContext is ProviderStatusViewModel { SourceId: "winget" });
            Assert.Equal(Visibility.Collapsed, absent.Visibility);
            Assert.Equal(Visibility.Visible, present.Visibility);
        });
    }

    /// <summary>Every state word of every locale must fit the 140 px state column untrimmed: each state
    /// that shows a word is rendered, not only the ones that happened to be longest when this was written
    /// (Italian "handed off" once needed far more than the column).</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("ja")]
    [InlineData("nl")]
    [InlineData("pl")]
    [InlineData("pt")]
    [InlineData("ro")]
    [InlineData("tr")]
    [InlineData("zh-Hans")]
    [InlineData("fr")]
    public void Main_window_row_state_words_fit_the_state_column_untrimmed(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);

            // Failed and an installable NeedsManualWork row show an icon instead of a word; Running and
            // Idle show none.
            var states = new (RowInstallState State, bool Uninstalled)[]
            {
                (RowInstallState.Queued, false),
                (RowInstallState.Succeeded, false),
                (RowInstallState.Succeeded, true),
                (RowInstallState.HandedOff, false),
                (RowInstallState.Stuck, false),
                (RowInstallState.Cancelled, false),
            };

            var rows = new List<UpdateRowViewModel>();
            foreach (var (state, uninstalled) in states)
            {
                var id = $"state-{state}-{uninstalled}";
                var row = new UpdateRowViewModel(
                    Item(id, $"State {state} {uninstalled} Tool", "winget", UpdateCategory.Software, "1.0", "1.1"));
                row.InstallState = state;
                row.WasUninstalled = uninstalled;
                Assert.True(row.ShowStateWord, $"{state} is expected to show a state word.");
                rows.Add(row);
                viewModel.Updates.Add(row);
            }

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-state-words-{language}", 1100, 700);

            foreach (var row in rows)
            {
                var grid = FindAllByName<Grid>(content, "TableRow")
                    .Single(g => g.DataContext is UpdateRowViewModel vm && vm.Name == row.Name);
                var word = FindByName<TextBlock>(grid, "RowStateWord");
                Assert.NotNull(word);
                Assert.Equal(row.InstallStateText, word!.Text);

                var probe = new TextBlock { Text = word.Text, FontSize = word.FontSize, FontFamily = word.FontFamily };
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(probe.DesiredSize.Width <= word.MaxWidth,
                    $"'{word.Text}' needs {probe.DesiredSize.Width:0.0} px of {word.MaxWidth:0} in {language}");
                Assert.True(word.ActualWidth + 0.5 >= probe.DesiredSize.Width,
                    $"'{word.Text}' is trimmed to {word.ActualWidth:0.0} of {probe.DesiredSize.Width:0.0} px in {language}");
            }
        });

        AssertRendered(result);
    }

    /// <summary>A failed row says why in the second line under its name (the translated explainer, in the
    /// critical brush, one line) instead of the source, and never shows the raw tool output; that stays
    /// behind the Details click. German, the longest of the texts, must fit the row's fixed height.</summary>
    [Fact]
    public void Main_window_shows_the_failure_reason_in_a_failed_row_in_german()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var failed = new UpdateRowViewModel(
                Item("failed-tool", "Some Failed Tool", "winget", UpdateCategory.Software, "1.0", "1.1"));
            failed.ApplyInstallStatus(new InstallItemStatus(
                "winget", "failed-tool", 0, "raw tool banner SECRET-RAW-LINE", false, "raw tool banner SECRET-RAW-LINE")
            {
                Detail = "Installer failed with exit code: 1602",
            });
            viewModel.Updates.Add(failed);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-failed-reason-de", 1100, 700);

            Assert.True(failed.IsInstallFailed);
            Assert.False(failed.ShowSourceText);
            Assert.DoesNotContain("SECRET-RAW-LINE", failed.FailureLine);

            var row = FindAllByName<Grid>(content, "TableRow")
                .Single(g => g.DataContext is UpdateRowViewModel vm && vm.Name == failed.Name);
            var line = FindByName<TextBlock>(row, "FailureLineText");
            Assert.NotNull(line);
            Assert.Equal(Visibility.Visible, line!.Visibility);
            Assert.Equal(failed.FailureLine, line.Text);
            Assert.True(line.ActualWidth > 0, "the failure line was never laid out");

            var bottom = line.TransformToAncestor(row).Transform(new Point(0, line.ActualHeight)).Y;
            Assert.True(bottom <= row.ActualHeight + 1,
                $"the failure line ends at {bottom:0} px in a row {row.ActualHeight:0} px high.");
        });

        AssertRendered(result);
    }

    /// <summary>A skipped blocked row says why under its name (secondary brush, one line, in place of the
    /// source) and carries the same text as its state tooltip; German must fit the fixed row height.</summary>
    [Fact]
    public void Main_window_shows_the_skip_reason_in_a_cancelled_row_in_german()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var skipped = new UpdateRowViewModel(
                Item("skipped-tool", "Some Skipped Tool", "winget", UpdateCategory.Software, "1.0", "1.1"));
            skipped.BeginInstall();
            skipped.ApplyInstallStatus(new InstallItemStatus(
                "winget", "skipped-tool", 0, "Übersprungen, Discord ist noch geöffnet", null, null)
            {
                IsCancelled = true,
            });
            viewModel.Updates.Add(skipped);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-cancelled-reason-de", 1100, 700);

            var row = FindAllByName<Grid>(content, "TableRow")
                .Single(g => g.DataContext is UpdateRowViewModel vm && vm.Name == skipped.Name);
            var line = FindByName<TextBlock>(row, "CancelledReasonText");
            Assert.NotNull(line);
            Assert.Equal(Visibility.Visible, line!.Visibility);
            Assert.Equal("Übersprungen, Discord ist noch geöffnet", line.Text);
            Assert.True(line.ActualWidth > 0, "the reason line was never laid out");

            var bottom = line.TransformToAncestor(row).Transform(new Point(0, line.ActualHeight)).Y;
            Assert.True(bottom <= row.ActualHeight + 1,
                $"the reason line ends at {bottom:0} px in a row {row.ActualHeight:0} px high.");
        });

        AssertRendered(result);
    }

    /// <summary>Caution-colored, not red, and the German state word must fit the state column without
    /// truncation, the whole point of a separate word from "failed" is that it reads as a distinct,
    /// less alarming outcome.</summary>
    [Fact]
    public void Main_window_shows_the_needs_manual_work_state_in_german()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var manualWork = new UpdateRowViewModel(
                Item("manual-work-tool", "Some Manual Work Tool", "winget", UpdateCategory.Software, "1.0", "1.1"));
            manualWork.InstallState = RowInstallState.NeedsManualWork;
            manualWork.InstallErrorDetail = "Installer failed with exit code: 1602";
            viewModel.Updates.Add(manualWork);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-needs-manual-work-de", 1100, 700);

            var row = FindAllByName<Grid>(content, "TableRow")
                .Single(g => g.DataContext is UpdateRowViewModel vm && vm.Name == manualWork.Name);
            var vm2 = (UpdateRowViewModel)row.DataContext;
            Assert.Equal(LocalizationService.Instance["RowNeedsManualWork"], vm2.InstallStateText);
            Assert.DoesNotContain("…", vm2.InstallStateText);
            Assert.True(vm2.HasInstallError, "should have a working error-detail button");
            Assert.False(vm2.IsInstallFailed, "must not show the red failed-triangle icon");

            // the way back is a visible button in the row, not only the context menu
            var retry = FindAllDescendants<Button>(row)
                .Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "RowResetAndRetry");
            Assert.Equal(Visibility.Visible, retry.Visibility);
            Assert.True(retry.ActualWidth > 0, "a needs-manual-work row should show its retry button");
            Assert.False(vm2.ShowStateWord, "the state word moves into the caution icon's hover text");
            Assert.Equal(LocalizationService.Instance["RetryButton"], retry.Content);
            var left = retry.TransformToAncestor(row).Transform(new Point(0, 0)).X;
            Assert.True(left >= 0 && left + retry.ActualWidth <= row.ActualWidth + 0.5,
                $"retry button runs outside the row: {left}+{retry.ActualWidth} of {row.ActualWidth}");
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The persistent-failure hint ("3 mal am selben Grund gescheitert. Paket ignorieren?") is a
    /// third line stacked under the name and the source line, inside a row whose own Grid is a fixed
    /// Height="48" with ClipToBounds="True", a backstop against overflow elsewhere in the same row, but
    /// here the hint is shown as a ui:Button rather than a plain TextBlock, and a WPF-UI button's own
    /// default template reserves more vertical room than a same-size TextBlock, tipping the stack's total
    /// height past 48px and clipping the last line's descenders ("gescheitert" ends in a "g"). Measures the
    /// button's own rendered bottom edge against the row's real height, the same shape of check
    /// <see cref="AssertNoChildClips"/> already does horizontally elsewhere in this class.
    /// </summary>
    [Fact]
    public void Main_window_persistent_failure_hint_is_not_clipped_by_the_row()
    {
        RenderResult? result = null;
        double hintBottom = 0, rowHeight = 0, hintHeight = 0, hintFontSize = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var flaky = new UpdateRowViewModel(
                Item("flaky-tool", "Some Flaky Tool", "winget", UpdateCategory.Software, "1.0", "1.1"));
            flaky.InstallState = RowInstallState.NeedsManualWork;
            flaky.PersistentFailureAttempts = 3;
            viewModel.Updates.Add(flaky);

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-persistent-failure-hint-de", 1100, 700);

            var row = FindAllByName<Grid>(content, "TableRow")
                .Single(g => g.DataContext is UpdateRowViewModel vm && vm.Name == flaky.Name);
            rowHeight = row.ActualHeight;

            var hintButton = FindFirstDescendant<Button>(row);
            Assert.NotNull(hintButton);
            hintBottom = hintButton!.TransformToAncestor(row).Transform(new Point(0, 0)).Y + hintButton.ActualHeight;
            hintHeight = hintButton.ActualHeight;
            hintFontSize = hintButton.FontSize;
        });

        AssertRendered(result);
        Assert.True(hintBottom <= rowHeight + 0.5,
            $"Persistent-failure hint ends at {hintBottom:0}px, the row itself is only {rowHeight:0}px tall " +
            $"(ClipToBounds='True' on the row), its descenders are clipped.");
        // 1.6x the font size is comfortably more than a single line's own natural height, the margin
        // the padding added on top of it (see MainWindow.xaml's comment on this button) is meant to buy.
        Assert.True(hintHeight >= 1.6 * hintFontSize,
            $"Persistent-failure hint box is {hintHeight:0.0}px tall against a {hintFontSize:0} FontSize, too tight for its own descenders.");
    }

    /// <summary>The "remind me later" badge and its due-date label, rendered on a real row, the feature
    /// had unit-test coverage for the underlying state but no render fixture at all before this.</summary>
    [Fact]
    public void Main_window_shows_a_snoozed_row_with_its_badge_in_german()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);

            var snoozed = new UpdateRowViewModel(
                Item("snoozed-tool", "Some Snoozed Tool", "choco", UpdateCategory.Software, "1.0", "1.1"));
            snoozed.IsSnoozed = true;
            snoozed.SnoozedUntil = DateTimeOffset.UtcNow.AddDays(3);
            viewModel.Updates.Add(snoozed);
            viewModel.ShowIgnored = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-snoozed-row-de", 1100, 700);

            var row = FindAllByName<Grid>(content, "TableRow")
                .Single(g => g.DataContext is UpdateRowViewModel vm && vm.Name == snoozed.Name);
            var vm2 = (UpdateRowViewModel)row.DataContext;
            Assert.False(string.IsNullOrWhiteSpace(vm2.SnoozedBadgeLabel));
        });

        AssertRendered(result);
    }
}
