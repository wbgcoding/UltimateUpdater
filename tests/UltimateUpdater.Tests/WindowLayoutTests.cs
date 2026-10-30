using System.Net.Http;
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
using Wpf.Ui.Appearance;

namespace UltimateUpdater.Tests;

/// <summary>
/// Builds every window with realistic content and renders it, so the layout can actually be looked
/// at. The assertions catch a window that comes up blank or throws while laying out; the PNGs in
/// bin\...\ui-snapshots are there to be inspected by eye.
/// </summary>
[Collection("wpf")]
public partial class WindowLayoutTests
{
    /// <summary>Anything below this is a flat rectangle, i.e. nothing rendered.</summary>
    private const int MinimumDistinctColours = 20;

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
                    ((Binding)c.Binding).Path.Path == nameof(MainViewModel.InstallHasRealProgress) && Equals(c.Value?.ToString(), "False"));
                Assert.Contains(t.Conditions, c =>
                    ((Binding)c.Binding).Path.Path == nameof(MainViewModel.IsInstalling) && Equals(c.Value?.ToString(), "True"));
            }

            var trigger = triggers.Single(t => t.EnterActions.Count > 0);
            Assert.Equal(3, trigger.Conditions.Count);
            Assert.Contains(trigger.Conditions, c =>
                ((Binding)c.Binding).Path.Path == nameof(Window.IsVisible)
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
            Assert.Equal(2, trigger.Conditions.Count);
            Assert.Contains(trigger.Conditions, c =>
                ((Binding)c.Binding).Path.Path == nameof(MainViewModel.IsScanning) && Equals(c.Value?.ToString(), "True"));
            var visibleCondition = trigger.Conditions.Single(c => ((Binding)c.Binding).Path.Path == nameof(Window.IsVisible));
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

    private static string PathDataString(System.Windows.Shapes.Path path) => path.Data?.ToString() ?? string.Empty;

    private static IEnumerable<T> FindAllDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var found in FindAllDescendants<T>(child))
                yield return found;
        }
    }

    /// <summary>The table's resting rows separate by alternating background rather than a grid
    /// of lines. "Software" is always the first group Populate builds (Firefox then VS Code), so its first
    /// two rows land at ItemsControl.AlternationIndex 0 and 1 inside the SAME group, alternation restarts
    /// per group, so this is the one pair guaranteed to differ only by alternation, not by group. Reads
    /// each row's own "Root" Border (its ControlTemplate's TemplatedParent is the ListBoxItem the
    /// alternation index lives on) scoped to the main table's own ListBox, never the separate manual-open
    /// ListBox that names its own row Border "Root" too.</summary>
    [Fact]
    public void The_two_alternation_indices_resolve_to_different_row_brushes()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-row-alternation-de", 1100, 700);

            var updatesListBox = FindByName<ListBox>(content, "UpdatesListBox");
            Assert.NotNull(updatesListBox);

            var rootBordersByIndex = new Dictionary<int, Border>();
            foreach (var border in FindAllByName<Border>(updatesListBox!, "Root"))
            {
                if (border.TemplatedParent is not ListBoxItem item)
                    continue;

                var index = ItemsControl.GetAlternationIndex(item);
                rootBordersByIndex.TryAdd(index, border);
            }

            Assert.True(rootBordersByIndex.ContainsKey(0) && rootBordersByIndex.ContainsKey(1),
                $"expected rows at both alternation index 0 and 1, found indices: {string.Join(",", rootBordersByIndex.Keys)}");

            var evenBrush = rootBordersByIndex[0].Background as System.Windows.Media.SolidColorBrush;
            var oddBrush = rootBordersByIndex[1].Background as System.Windows.Media.SolidColorBrush;
            Assert.NotNull(evenBrush);
            Assert.NotNull(oddBrush);
            Assert.NotEqual(evenBrush!.Color, oddBrush!.Color);
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

            Assert.Equal(Visibility.Visible, note!.Visibility);
            Assert.Equal(LocalizationService.Instance["AboutPortable"], note.Text);
            Assert.True(note.ActualHeight > 0, "the portable note was never laid out");
        });

        AssertRendered(result);
    }

    /// <summary>
    /// `ui:TitleBar` draws its own `Title` text only when its `Header` is null; MainWindow always sets a
    /// `Header`, which silently dropped the app name, confirmed by walking the rendered visual tree and
    /// finding no TextBlock anywhere carrying it. Checked at the window's own minimum width too, where the
    /// icon, the title text and the window's own caption buttons share one row.
    /// </summary>
    [Theory]
    [InlineData(860)]
    [InlineData(1160)]
    public void Main_window_title_bar_shows_the_app_name(int width)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-title-app-name-{width}", width, 700);

            var titleText = FindByName<TextBlock>(content, "AppTitleText");
            Assert.NotNull(titleText);
            Assert.Equal(Visibility.Visible, titleText!.Visibility);
            // the name alone, the version lives in Settings > About
            Assert.Equal(LocalizationService.Instance["AppTitle"], titleText.Text);
            Assert.True(titleText.ActualWidth > 0, "the title text was never actually laid out");

            var titleBar = FindFirstDescendant<Wpf.Ui.Controls.TitleBar>(content);
            Assert.NotNull(titleBar);
            AssertNoChildClips(titleBar!, [titleText]);
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

    /// <summary>Each running row's own card must sit strictly below the one before it, the property that
    /// actually rules out two rows drawn on top of each other, which neither <see cref="AssertNoChildClips"/>
    /// (horizontal only) nor a bare "it rendered" check would catch.</summary>
    private static void AssertRowsStackWithoutOverlap(FrameworkElement container, IReadOnlyList<Grid> cards)
    {
        var tops = cards
            .Select(card => card.TransformToAncestor(container).Transform(new Point(0, 0)).Y)
            .ToList();

        for (var i = 1; i < tops.Count; i++)
        {
            Assert.True(
                tops[i] >= tops[i - 1] + cards[i - 1].ActualHeight - 1,
                $"row {i} at y={tops[i]:0} overlaps row {i - 1} at y={tops[i - 1]:0} height={cards[i - 1].ActualHeight:0}");
        }
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

    private static void AssertNoChildClips(FrameworkElement container, IEnumerable<FrameworkElement> children)
    {
        foreach (var child in children.Where(c => c.Visibility == Visibility.Visible))
        {
            var left = child.TransformToAncestor(container).Transform(new Point(0, 0)).X;
            var right = left + child.ActualWidth;

            Assert.True(
                right <= container.ActualWidth + 1,
                $"'{container.Name}' clips at {container.ActualWidth:0} px wide: a child ends at {right:0} px.");
        }
    }

    /// <summary>
    /// Finds a named element in the VISUAL tree after layout, the render pass is what gives it a size to
    /// check. Visual only: walking both trees revisits most of this window twice per level, and on a tree
    /// this deep that does not finish.
    /// </summary>
    private static T? FindByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (root is T match && match.Name == name)
            return match;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindByName<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Same walk as <see cref="FindByName{T}"/> but for a type with no name to match on, the
    /// vertical <see cref="ScrollBar"/> inside a themed <see cref="ScrollViewer"/> is generated by its
    /// control template, never named in markup.</summary>
    private static T? FindFirstDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
            return match;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindFirstDescendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Same walk as <see cref="FindByName{T}"/> but collects every match instead of the first,
    /// a DataTemplate instantiates its x:Name once per row, so a per-row assertion needs all of them.</summary>
    private static List<T> FindAllByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var results = new List<T>();

        void Collect(DependencyObject node)
        {
            if (node is T match && match.Name == name)
                results.Add(match);

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Collect(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }

        Collect(root);
        return results;
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

    /// <summary>With a blocked update the bottom row carries the three German blocker answers
    /// ("Programm schließen und fortfahren", "Blockierte überspringen", "Trotzdem versuchen") once for the whole
    /// batch, next to Cancel. Rendered at the window's smallest (460) and largest (720) width: none of
    /// the buttons clips, the <see cref="WrapPanel"/> moves the last ones to a second line instead.
    /// Also checks the restore-point band disappears when there is nothing to say, and reappears when there
    /// is.</summary>
    [Theory]
    [InlineData(460)]
    [InlineData(720)]
    public void Preflight_window_shows_all_three_choice_buttons_in_full(int width)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var blocker = new Services.Install.BlockedInstall(null!, "Firefox", ["firefox"]);
            var row = new UpdateRowViewModel(new UpdateItem
            {
                Id = "Mozilla.Firefox", SourceId = "winget", Category = UpdateCategory.Software,
                Name = "Firefox", InstalledVersion = "1.0", AvailableVersion = "2.0",
            });
            var lines = InstallPreflightLineViewModel.FromBlockedGroups([(row, blocker)]);
            var viewModel = new InstallPreflightViewModel(lines, new LoggingService(), true);
            var window = new InstallPreflightWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"preflight-choices-{width}", width, 640);

            var buttons = new[]
            {
                FindByName<Wpf.Ui.Controls.Button>(content, "SkipBlockedButton"),
                FindByName<Wpf.Ui.Controls.Button>(content, "TryAnywayButton"),
                FindByName<Wpf.Ui.Controls.Button>(content, "CloseProgramsButton"),
            };

            foreach (var button in buttons)
            {
                Assert.NotNull(button);
                Assert.Equal(Visibility.Visible, button!.Visibility);
            }

            Assert.Equal(Visibility.Collapsed, FindByName<Wpf.Ui.Controls.Button>(content, "StartButton")!.Visibility);

            var panel = FindByName<WrapPanel>(content, "ButtonRow");
            Assert.NotNull(panel);
            AssertNoChildClips(panel!, buttons!);
        });

        AssertRendered(result);
    }

    /// <summary><c>RestorePointRow</c> now hides when there is nothing to say instead of always
    /// reserving its row's band. <see cref="RestorePointThrottle.DescribeLastCreated"/> currently always
    /// returns some text (a "none created yet" line included), so this only pins the wired-up case; a truly
    /// empty band is not reachable through today's production code path.</summary>
    [Fact]
    public void Preflight_window_shows_the_restore_point_row_when_there_is_text()
    {
        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var viewModel = new InstallPreflightViewModel([], new LoggingService(), true);
            Assert.False(string.IsNullOrEmpty(viewModel.RestorePointText));
            Assert.True(viewModel.HasRestorePointText);

            var window = new InstallPreflightWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            _ = WpfHarness.RenderWindow(window, "preflight-restore-point-row", 460, 640);

            var row = FindByName<StackPanel>(content, "RestorePointRow");
            Assert.NotNull(row);
            Assert.Equal(Visibility.Visible, row!.Visibility);
        });
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
                @"C:\Users\Test\AppData\Local\SomeApp\Cache",
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

    /// <summary>Lays a Settings window out at the given width by hand (an unshown window has no layout
    /// yet) and returns it with its scroll viewer and section list.</summary>
    private static (SettingsWindow Window, System.Windows.Controls.ScrollViewer Scroll, ListBox Nav) LayOutSettingsWindow(
        double width)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging);
        LocalizationService.Instance.Apply("en");

        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), ["winget"]);
        var window = new SettingsWindow(viewModel);

        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, window.Height));
        root.Arrange(new Rect(0, 0, width, window.Height));
        root.UpdateLayout();

        return (window,
            (System.Windows.Controls.ScrollViewer)window.FindName("SettingsScroll")!,
            (ListBox)window.FindName("SettingsNav")!);
    }

    private static double HeaderTopInViewport(
        SettingsWindow window, System.Windows.Controls.ScrollViewer scroll, string headerName)
    {
        var header = (FrameworkElement)window.FindName(headerName)!;
        return header.TransformToAncestor((UIElement)scroll.Content).Transform(new Point(0, 0)).Y - scroll.VerticalOffset;
    }

    /// <summary>Picking a section in the list scrolls that section's header to the top of the page.</summary>
    [Fact]
    public void Settings_window_navigation_scrolls_the_selected_header_to_the_top()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, scroll, nav) = LayOutSettingsWindow(800);
            Assert.Equal(Visibility.Visible, nav.Visibility);
            Assert.True(scroll.ScrollableHeight > 0);

            var entry = nav.Items.OfType<ListBoxItem>().Single(i => (string)i.Tag == "NotifyHeader");
            nav.SelectedItem = entry;
            scroll.UpdateLayout();

            Assert.True(scroll.VerticalOffset > 0, "the page did not scroll at all");
            Assert.True(Math.Abs(HeaderTopInViewport(window, scroll, "NotifyHeader")) < 1.5,
                $"the Notifications header sits {HeaderTopInViewport(window, scroll, "NotifyHeader"):0.#}px from the top");

            window.Close();
        });
    }

    /// <summary>Scrolling the page moves the highlight to the section at the top of it.</summary>
    [Fact]
    public void Settings_window_navigation_highlights_the_section_at_the_top_of_the_page()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, scroll, nav) = LayOutSettingsWindow(800);

            // Install, not a later section: from Appearance on the page already sits on its last screen,
            // where the reading line glides down so the last sections can be reached at all.
            var install = (FrameworkElement)window.FindName("InstallHeader")!;
            var offset = install.TransformToAncestor((UIElement)scroll.Content).Transform(new Point(0, 0)).Y;
            scroll.ScrollToVerticalOffset(offset);
            scroll.UpdateLayout();

            var selected = Assert.IsType<ListBoxItem>(nav.SelectedItem);
            Assert.Equal("InstallHeader", (string)selected.Tag);

            scroll.ScrollToVerticalOffset(0);
            scroll.UpdateLayout();
            Assert.Equal("ScanHeader", (string)((ListBoxItem)nav.SelectedItem).Tag);

            window.Close();
        });
    }

    /// <summary>Choosing About also opens its card; below 760 px the list is hidden.</summary>
    [Fact]
    public void Settings_window_navigation_opens_about_and_hides_below_760_pixels()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, scroll, nav) = LayOutSettingsWindow(800);
            var about = (System.Windows.Controls.Expander)window.FindName("AboutExpander")!;
            Assert.False(about.IsExpanded);

            nav.SelectedItem = nav.Items.OfType<ListBoxItem>().Single(i => (string)i.Tag == "AboutExpander");
            Assert.True(about.IsExpanded);
            window.Close();

            var (narrow, _, narrowNav) = LayOutSettingsWindow(759);
            Assert.Equal(Visibility.Collapsed, narrowNav.Visibility);
            narrow.Close();
        });
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

    private static void AssertRightEdgesAlign(FrameworkElement container, params FrameworkElement?[] controls)
    {
        var edges = controls.Select(c =>
        {
            Assert.NotNull(c);
            return c!.TransformToAncestor(container).Transform(new Point(0, 0)).X + c.ActualWidth;
        }).ToList();

        var first = edges[0];
        for (var i = 1; i < edges.Count; i++)
        {
            Assert.True(Math.Abs(edges[i] - first) <= 1,
                $"Right edges do not align in '{container.Name}': {string.Join(", ", edges.Select(e => e.ToString("0.0")))}");
        }
    }

    /// <summary>
    /// A history entry without a details button used to hold no focusable element at all, so the keyboard
    /// (and a screen reader) could not walk the list. Real keyboard focus needs a shown window.
    /// </summary>
    [Fact]
    public void History_entries_can_be_walked_with_the_arrow_keys_and_are_named_for_screen_readers()
    {
        var firstFocused = false;
        var secondFocusedAfterDown = false;
        var names = new List<string>();
        var expected = new List<string>();

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            var viewModel = new HistoryViewModel(HistoryWith(HistoryEntries()));
            var window = new HistoryWindow(viewModel, new LoggingService()) { Width = 820, Height = 520, ShowInTaskbar = false };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++)
                {
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                var list = FindFirstDescendant<ListBox>(window)!;
                Assert.Equal(2, list.Items.Count);
                var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);

                first.Focus();
                firstFocused = first.IsKeyboardFocused;

                var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first), 0, Key.Down)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                };
                first.RaiseEvent(down);
                secondFocusedAfterDown = second.IsKeyboardFocused;

                foreach (var item in new[] { first, second })
                {
                    names.Add(System.Windows.Automation.AutomationProperties.GetName(item) ?? "");
                    var row = (HistoryRowViewModel)item.DataContext;
                    expected.Add($"{row.Name}, {row.Result}, {row.When}");
                }
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(firstFocused, "A history entry should take keyboard focus.");
        Assert.True(secondFocusedAfterDown, "Down should move focus to the next history entry.");
        Assert.Equal(expected, names);
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

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void History_window_with_entries_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            var viewModel = new HistoryViewModel(HistoryWith(HistoryEntries()));
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"history-{language}", 820, 520);

            // The header grid and every row's grid share the same GridLength resources for columns
            // 0-4 (When/Name/Source/Version/Result), so each column must start at the exact same X
            // once both grids are transformed to a common ancestor, that is what "the rows line up
            // with the header" actually means, independent of how each cell centres its own content.
            var headerRow = FindByName<Grid>(content, "HistoryHeaderRow")!;
            var dataRows = FindAllByName<Grid>(content, "HistoryRow");
            Assert.True(dataRows.Count >= 2, "expected both the failed and the successful sample row to render");

            double ColumnOffsetX(Grid grid, int columnIndex)
            {
                var x = grid.TransformToAncestor(content).Transform(new Point(0, 0)).X;
                for (var i = 0; i < columnIndex; i++)
                    x += grid.ColumnDefinitions[i].ActualWidth;
                return x;
            }

            // One sample row has a visible details button (the failed install), the other does not, both
            // must still line up under the header exactly, which is the mismatch this used to hide.
            foreach (var dataRow in dataRows)
            {
                for (var column = 0; column < 5; column++)
                {
                    var headerX = ColumnOffsetX(headerRow, column);
                    var rowX = ColumnOffsetX(dataRow, column);
                    Assert.True(Math.Abs(headerX - rowX) <= 1.0,
                        $"column {column} starts at {headerX:0.0} in the header but {rowX:0.0} in a row");
                }
            }
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Enough rows to force the list to actually scroll (rendered well below its natural content height),
    /// so the list's own vertical scrollbar is really there rather than merely reserved for. A plain
    /// <c>ListBox</c>'s own default `ScrollViewer` already reserves a column for its bar instead of
    /// floating it over the content (unlike the explicitly-styled `ScrollViewer`s elsewhere in the app that
    /// inherit WPF-UI's overlay style, see `CleanupWindow.xaml`'s own comments on that), confirmed here by
    /// measurement rather than assumed: the trailing details column and the header's own right margin both
    /// stay clear of the bar even while it is genuinely showing.
    /// </summary>
    [Fact]
    public void History_window_scrollbar_never_covers_the_details_column()
    {
        RenderResult? result = null;
        int rowCount = 0;
        bool scrollBarFound = false;
        List<double> detailsButtonRights = [];
        double scrollBarLeft = 0;
        double headerRight = 0;
        List<double> rowRights = [];

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var entries = Enumerable.Range(0, 20).Select(i => new HistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 7, 19, 21, 14, 0, TimeSpan.Zero).AddMinutes(i),
                SourceId = "winget", ItemId = $"Contoso.Tool{i}", Name = $"Contoso Tool {i}",
                FromVersion = "1.0.0", ToVersion = "1.0.1", Success = i % 3 != 0,
                Error = i % 3 == 0 ? "Installation fehlgeschlagen." : null,
            }).ToList();

            var viewModel = new HistoryViewModel(HistoryWith(entries));
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;
            // Well under what 20 rows need, so the list's own ScrollViewer must actually scroll.
            result = WpfHarness.RenderWindow(window, "history-scrollbar-details-column", 820, 400);

            var headerRow = FindByName<Grid>(content, "HistoryHeaderRow");
            var dataRows = FindAllByName<Grid>(content, "HistoryRow").ToList();
            rowCount = dataRows.Count;

            var listBox = FindFirstDescendant<ListBox>(content);
            var verticalScrollBar = listBox is null ? null : FindFirstDescendant<ScrollBar>(listBox);
            scrollBarFound = verticalScrollBar is not null;

            if (verticalScrollBar is not null && headerRow is not null)
            {
                scrollBarLeft = verticalScrollBar.TransformToAncestor(content).Transform(new Point(0, 0)).X;
                headerRight = headerRow.TransformToAncestor(content).Transform(
                    new Point(headerRow.ActualWidth, 0)).X;
                foreach (var dataRow in dataRows)
                    rowRights.Add(dataRow.TransformToAncestor(content).Transform(
                        new Point(dataRow.ActualWidth, 0)).X);
            }

            foreach (var button in FindAllByName<Button>(content, "HistoryDetailsButton"))
                detailsButtonRights.Add(button.TransformToAncestor(content).Transform(new Point(button.ActualWidth, 0)).X);
        });

        AssertRendered(result);
        // The list virtualizes, so only the rows that actually fit in the small render height are
        // realized; a real ScrollBar existing at all is the actual proof that the list overflowed.
        Assert.True(rowCount >= 2, "expected at least a couple of realized rows");
        Assert.True(scrollBarFound, "expected the list to actually need a scrollbar at this render height");

        Assert.True(detailsButtonRights.Count > 0, "expected at least one details button (some rows fail)");
        foreach (var right in detailsButtonRights)
            Assert.True(right <= scrollBarLeft + 1,
                $"Details button right edge ({right:0}) overlaps the scrollbar (starts at {scrollBarLeft:0}).");

        // The header's own right margin must still match every row's, whether the bar happens to be
        // showing at that instant or not.
        foreach (var rowRight in rowRights)
            Assert.True(Math.Abs(headerRight - rowRight) <= 1,
                $"header right edge {headerRight:0.0} does not match a row's right edge {rowRight:0.0}");
    }

    /// <summary>
    /// The history window's own "back to the previous version" button, offered only on the entry
    /// that is BOTH a success from a pinning-capable source AND the newest successful install on record
    /// for that package. Three entries here: an older winget success for the same package (superseded,
    /// must stay hidden), the newest one (must show the button), and a chocolatey success (cannot pin, so
    /// even being newest buys it nothing).
    /// </summary>
    [Fact]
    public void History_row_shows_a_downgrade_button_only_for_the_newest_pinnable_success()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            var entries = new List<HistoryEntry>
            {
                new()
                {
                    Timestamp = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
                    SourceId = "winget", ItemId = "Some.Tool", Name = "Some Tool",
                    FromVersion = "1.0.0", ToVersion = "1.5.0", Success = true,
                },
                new()
                {
                    Timestamp = new DateTimeOffset(2026, 7, 19, 21, 14, 0, TimeSpan.Zero),
                    SourceId = "winget", ItemId = "Some.Tool", Name = "Some Tool",
                    FromVersion = "1.5.0", ToVersion = "2.0.0", Success = true,
                },
                new()
                {
                    Timestamp = new DateTimeOffset(2026, 7, 19, 21, 15, 0, TimeSpan.Zero),
                    SourceId = "chocolatey", ItemId = "nodejs", Name = "Node.js",
                    FromVersion = "20.15.1", ToVersion = "22.6.0", Success = true,
                },
            };

            var pinningCapable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "winget" };
            var viewModel = new HistoryViewModel(HistoryWith(entries), pinningCapable, _ => { });
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "history-downgrade-button", 820, 520);

            var rows = viewModel.Entries;
            Assert.Equal(1, rows.Count(r => r.CanDowngrade));
            var eligible = Assert.Single(rows, r => r.CanDowngrade);
            Assert.Equal("Some Tool", eligible.Name);
            Assert.Contains("1.5.0", eligible.DowngradeHeader);

            // The rendered button itself, not just the view model flag: exactly one of the three rows'
            // own copies of it is actually visible.
            var buttons = FindAllByName<Button>(content, "HistoryDowngradeButton");
            Assert.Equal(3, buttons.Count);
            Assert.Single(buttons, b => b.Visibility == Visibility.Visible);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The version cell used to build one string ("128.0.3 → 129.0.1") in a single centred
    /// TextBlock, so the arrow sat wherever the two version strings happened to push it. Two rows with
    /// strongly different version-string lengths must now still show the arrow at the identical x
    /// position, the same fixed-lane treatment the main table's own version column already uses.
    /// </summary>
    [Fact]
    public void History_version_lane_arrow_lines_up_across_rows_regardless_of_length()
    {
        RenderResult? result = null;
        List<double> arrowX = [];

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var entries = new List<HistoryEntry>
            {
                new()
                {
                    Timestamp = new DateTimeOffset(2026, 7, 19, 21, 14, 0, TimeSpan.Zero),
                    SourceId = "winget", ItemId = "Short.Tool", Name = "Short Tool",
                    FromVersion = "1.0.0", ToVersion = "1.0.1", Success = true,
                },
                new()
                {
                    Timestamp = new DateTimeOffset(2026, 7, 19, 21, 15, 0, TimeSpan.Zero),
                    SourceId = "winget", ItemId = "Long.Tool", Name = "Long Tool",
                    FromVersion = "2025.4.12-rc.1", ToVersion = "2026.11.3-beta.4", Success = true,
                },
            };

            var viewModel = new HistoryViewModel(HistoryWith(entries));
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "history-version-arrow-alignment-de", 820, 520);

            var arrows = FindAllByName<TextBlock>(content, "HistoryVersionArrow");
            Assert.Equal(2, arrows.Count);
            arrowX.AddRange(arrows.Select(arrow => arrow.TransformToAncestor(content).Transform(new Point(0, 0)).X));
        });

        AssertRendered(result);
        Assert.True(Math.Abs(arrowX[0] - arrowX[1]) <= 1.0,
            $"Version arrow x positions differ: {string.Join(", ", arrowX.Select(x => x.ToString("0.0")))}");
    }

    /// <summary>The new size column falls back to a plain hyphen for an entry written before the
    /// field existed (or from a source that never reported a size), and formats a real value through the
    /// same <see cref="ByteSize.Format"/> helper the main table's own size column uses.</summary>
    [Fact]
    public void History_row_size_text_is_a_hyphen_when_missing_and_formatted_when_present()
    {
        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var withoutSize = new HistoryRowViewModel(new HistoryEntry
            {
                Timestamp = DateTimeOffset.Now, SourceId = "winget", ItemId = "NoSize", Name = "No Size",
                Success = true,
            });
            var withSize = new HistoryRowViewModel(new HistoryEntry
            {
                Timestamp = DateTimeOffset.Now, SourceId = "winget", ItemId = "WithSize", Name = "With Size",
                SizeBytes = 5_242_880, Success = true,
            });

            Assert.Equal("-", withoutSize.SizeText);
            Assert.Equal(ByteSize.Format(5_242_880), withSize.SizeText);
        });
    }

    [Fact]
    public void History_window_when_empty_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var viewModel = new HistoryViewModel(HistoryWith([]));
            var window = new HistoryWindow(viewModel, new LoggingService());
            result = WpfHarness.RenderWindow(window, "history-empty-de", 820, 520);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Three entries used to render inside a window fixed at 640px regardless of how little the
    /// list actually held. Same shrink-to-content measurement as the other message windows: measuring at the
    /// window's own Width with unbounded height mirrors what SizeToContent="Height" itself settles on
    /// (clamped between MinHeight/MaxHeight).
    /// </summary>
    [Fact]
    public void History_window_shrinks_to_fit_a_short_list()
    {
        RenderResult? result = null;
        double renderedHeight = 0, lastRowBottom = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var entries = new List<HistoryEntry>(HistoryEntries())
            {
                new()
                {
                    Timestamp = new DateTimeOffset(2026, 7, 19, 21, 16, 0, TimeSpan.Zero),
                    SourceId = "winget", ItemId = "Some.Other.Tool", Name = "Some Other Tool",
                    FromVersion = "3.1.0", ToVersion = "3.2.0", Success = true,
                },
            };
            var viewModel = new HistoryViewModel(HistoryWith(entries));
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);

            result = WpfHarness.RenderWindow(window, "history-shrinks-short-de", (int)window.Width, (int)renderedHeight);

            var dataRows = FindAllByName<Grid>(content, "HistoryRow");
            Assert.Equal(3, dataRows.Count);
            lastRowBottom = dataRows.Max(row =>
                row.TransformToAncestor(content).Transform(new Point(0, 0)).Y + row.ActualHeight);
        });

        AssertRendered(result);
        Assert.True(renderedHeight < 640,
            $"Three entries rendered at {renderedHeight:0}px, expected below the old fixed 640px height.");
        Assert.True(lastRowBottom <= renderedHeight + 0.5,
            $"Last row bottom {lastRowBottom:0.0} exceeds the rendered height {renderedHeight:0}, row is clipped.");
    }

    /// <summary>The empty state used to sit inside the same fixed 640px window as a full list.
    /// Shrunk down, its icon and text must still land centred in whatever height remains, not pinned to
    /// where they used to sit in the old taller window.</summary>
    [Fact]
    public void History_window_shrinks_to_fit_the_empty_state()
    {
        RenderResult? result = null;
        double renderedHeight = 0, stateCenterY = 0, stateBottom = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var viewModel = new HistoryViewModel(HistoryWith([]));
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);

            result = WpfHarness.RenderWindow(window, "history-shrinks-empty-de", (int)window.Width, (int)renderedHeight);

            var emptyState = FindByName<StackPanel>(content, "HistoryEmptyState");
            Assert.NotNull(emptyState);
            var top = emptyState!.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            stateCenterY = top + emptyState.ActualHeight / 2;
            stateBottom = top + emptyState.ActualHeight;
        });

        AssertRendered(result);
        Assert.True(renderedHeight < 640,
            $"Empty state rendered at {renderedHeight:0}px, expected below the old fixed 640px height.");
        Assert.True(stateBottom <= renderedHeight + 0.5,
            $"Empty state bottom {stateBottom:0.0} exceeds the rendered height {renderedHeight:0}, it is clipped.");
        Assert.True(Math.Abs(stateCenterY - renderedHeight / 2) <= renderedHeight * 0.1,
            $"Empty state centre {stateCenterY:0.0} is not centred in the rendered height {renderedHeight:0}.");
    }

    /// <summary>The window's own MinWidth/MinHeight (700x320, see the comment on that constant in
    /// HistoryWindow.xaml), never rendered before at that width in either language.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void History_window_at_its_minimum_width_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            var viewModel = new HistoryViewModel(HistoryWith(HistoryEntries()));
            var window = new HistoryWindow(viewModel, new LoggingService());
            result = WpfHarness.RenderWindow(
                window, $"history-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);
    }

    /// <summary>The light theme is never checked for this window otherwise.</summary>
    [Fact]
    public void History_window_in_the_light_theme_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");
            var viewModel = new HistoryViewModel(HistoryWith(HistoryEntries()));
            var window = new HistoryWindow(viewModel, new LoggingService());
            result = WpfHarness.RenderWindow(window, "history-light-de", 820, 520);
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
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

    /// <summary>
    /// The error-details window: the raw tool output behind a failed install, plus its header. A real
    /// OpenJDK failure, built through the real <see cref="WingetSource.LadderFailure"/>,
    /// not a hand-typed string, so this snapshot proves the shape the app actually produces (a fixture
    /// left on a stale shape would "prove" a layout the app no longer has). Only the rung labels, the
    /// exit-code capsule and the "winget" right after a rung label should read as marked, nothing else.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Error_detail_window_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            string[] attempts =
            [
                "[upgrade] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). " +
                    "No applicable update found.",
                "[upgrade --scope user] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). " +
                    "No applicable update found.",
                "[install] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). " +
                    "No applicable update found.",
            ];
            var detail = WingetSource.LadderFailure(attempts, unchecked((int)0x8A15002B), "17.0.9");

            var viewModel = new ErrorDetailViewModel("OpenJDK 17", "winget", "17.0.8 → 17.0.9", detail, new LoggingService());
            var window = new ErrorDetailWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"error-detail-{language}", (int)window.Width, (int)window.MaxHeight);

            // 22.08.2026: Visible, not Auto, the bar used to stay invisible until the first mouse move.
            var output = FindByName<RichTextBox>(content, "OutputBox");
            Assert.NotNull(output);
            Assert.Equal(ScrollBarVisibility.Visible, output!.VerticalScrollBarVisibility);

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>
    /// A three-line tool output used to leave a large empty band above the action row (the
    /// window carried a fixed Height="520" regardless of content). Same measure-then-clamp approach as
    /// the Changelog window's own shrink test above, mirroring what SizeToContent="Height" settles on.
    /// </summary>
    [Fact]
    public void Error_detail_window_shrinks_to_fit_short_content()
    {
        RenderResult? result = null;
        double renderedHeight = 0, actionRowBottom = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var viewModel = new ErrorDetailViewModel(
                "Firefox", "winget", "128.0.3 → 129.0.1", "Install failed.\nExit code 1.", new LoggingService());
            var window = new ErrorDetailWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);

            result = WpfHarness.RenderWindow(window, "error-detail-shrinks-short-de", (int)window.Width, (int)renderedHeight);

            var actionRow = FindByName<Grid>(content, "ErrorDetailActionRow");
            Assert.NotNull(actionRow);
            actionRowBottom = actionRow!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + actionRow.ActualHeight;

            window.Close();
        });

        AssertRendered(result);
        Assert.True(renderedHeight < 520,
            $"Short content rendered at {renderedHeight:0}px, expected below the old fixed 520px height.");
        Assert.True(actionRowBottom <= renderedHeight + 0.5,
            $"Action row bottom ({actionRowBottom:0}px) falls outside the {renderedHeight:0}px window.");
    }

    /// <summary>
    /// The output row used to be a star row, so the window's old MinHeight="360" left nowhere
    /// for a short output's leftover space to go but into the RichTextBox, stretching it and leaving an
    /// empty band below the text (not above the action row, which still sat flush at the bottom either
    /// way, why the shrink test above never caught this). The row is Auto now, same approach as
    /// ChangelogWindow's own text box; the box (<c>OutputBoxBorder</c>) carries a much smaller,
    /// deliberate floor instead, so the expected height is the LARGER of the text's natural need and that
    /// floor, not the natural need alone, anything past that would be the old band coming back.
    /// </summary>
    [Fact]
    public void Error_detail_window_output_box_has_no_empty_band_below_short_text()
    {
        RenderResult? result = null;
        double expectedHeight = 0, actualOutputHeight = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var viewModel = new ErrorDetailViewModel(
                "Firefox", "winget", "128.0.3 → 129.0.1", "Install failed.\nExit code 1.", new LoggingService());
            var window = new ErrorDetailWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            var outputBox = FindByName<RichTextBox>(content, "OutputBox");
            var outputBorder = FindByName<Border>(content, "OutputBoxBorder");
            Assert.NotNull(outputBox);
            Assert.NotNull(outputBorder);
            expectedHeight = Math.Max(outputBox!.DesiredSize.Height, outputBorder!.MinHeight);

            var renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);
            result = WpfHarness.RenderWindow(window, "error-detail-no-empty-band-de", (int)window.Width, (int)renderedHeight);

            actualOutputHeight = outputBox.ActualHeight;

            window.Close();
        });

        AssertRendered(result);
        Assert.True(actualOutputHeight - expectedHeight < 20,
            $"Output box rendered at {actualOutputHeight:0}px against an expected {expectedHeight:0}px " +
            $"(text need vs. the box's own floor, whichever is bigger), an empty band of " +
            $"{actualOutputHeight - expectedHeight:0}px below the text.");
    }

    /// <summary>
    /// The history window's fallback detail (a log excerpt, or the short sentence, rather than
    /// the entry's own stored output) shows a small note explaining which one this is, never shown at
    /// all for a live main-table row, so this proves the note itself renders sanely when it IS present.
    /// </summary>
    [Fact]
    public void Error_detail_window_shows_a_note_when_the_detail_is_a_fallback()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var viewModel = new ErrorDetailViewModel(
                "Node.js", "Chocolatey", "20.15.1 → 22.6.0", "23:15:00 W inst nodejs update failed",
                new LoggingService(), detailSourceKey: "ErrorDetailSourceLog");
            var window = new ErrorDetailWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "error-detail-fallback-note-de", (int)window.Width, (int)window.MaxHeight);

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>The light theme is a separate brush set, a scrollbar-visibility fix could regress
    /// invisibly in the one theme not otherwise rendered here.</summary>
    [Fact]
    public void Error_detail_window_scrollbar_is_visible_in_the_light_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");

            var viewModel = new ErrorDetailViewModel(
                "Firefox", "winget", "128.0.3 → 129.0.1", "winget install failed (exit 1).", new LoggingService());
            var window = new ErrorDetailWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "error-detail-light-de", (int)window.Width, (int)window.MaxHeight);

            var output = FindByName<RichTextBox>(content, "OutputBox");
            Assert.NotNull(output);
            Assert.Equal(ScrollBarVisibility.Visible, output!.VerticalScrollBarVisibility);

            window.Close();
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>The two tests above only prove the scrollbar's visibility MODE is always "Visible",
    /// neither uses output long enough to actually overflow the box, so neither can tell a reserved-but-
    /// empty lane apart from a genuinely scrolling one. Mirrors
    /// <see cref="Changelog_window_scrollbar_is_visible_dark_theme_long_text"/> for the RichTextBox.</summary>
    [Fact]
    public void Error_detail_window_output_box_actually_overflows_with_long_output()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var longOutput = string.Join("\n", Enumerable.Range(1, 40)
                .Select(i => $"[{i:D2}] winget: some line of diagnostic output worth reading in full."));

            var viewModel = new ErrorDetailViewModel("OpenJDK 17", "winget", "17.0.8 → 17.0.9", longOutput, new LoggingService());
            var window = new ErrorDetailWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "error-detail-long-output-de", (int)window.Width, (int)window.MaxHeight);

            var output = FindByName<RichTextBox>(content, "OutputBox");
            Assert.NotNull(output);
            Assert.Equal(ScrollBarVisibility.Visible, output!.VerticalScrollBarVisibility);
            Assert.True(output.ExtentHeight > output.ViewportHeight, "the long output should actually overflow the box");

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>The window's own MinWidth/MinHeight (460x200), never rendered before at that width;
    /// the ladder failure detail is the widest content this window shows.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Error_detail_window_at_its_minimum_width_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            string[] attempts =
            [
                "[upgrade] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). " +
                    "No applicable update found.",
                "[install] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). " +
                    "No applicable update found.",
            ];
            var detail = WingetSource.LadderFailure(attempts, unchecked((int)0x8A15002B), "17.0.9");

            var viewModel = new ErrorDetailViewModel("OpenJDK 17", "winget", "17.0.8 → 17.0.9", detail, new LoggingService());
            var window = new ErrorDetailWindow(viewModel);
            result = WpfHarness.RenderWindow(
                window, $"error-detail-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>
    /// "Gleicher Handgriff, gleicher Punkt" (same move, same spot) as the error-detail window above, same
    /// Auto-to-Visible fix, checked light+short and dark+long so both the theme and the overflow case
    /// (a track with nothing to scroll still shows the reserved lane) are covered once each.
    /// </summary>
    [Fact]
    public void Changelog_window_scrollbar_is_visible_light_theme_short_text()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");

            var viewModel = new ChangelogViewModel("Firefox", "129.0.1", "Bug fixes.", null, new LoggingService());
            var window = new ChangelogWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "changelog-light-short-de", (int)window.Width, (int)window.MaxHeight);

            var textBox = FindByName<TextBox>(content, "ChangelogTextBox");
            Assert.NotNull(textBox);
            Assert.Equal(ScrollBarVisibility.Visible, textBox!.VerticalScrollBarVisibility);

            window.Close();
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    [Fact]
    public void Changelog_window_scrollbar_is_visible_dark_theme_long_text()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var longText = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"- Release note line {i}: a change worth mentioning."));
            var viewModel = new ChangelogViewModel("Mozilla Firefox", "128.0.3 → 129.0.1", longText, "https://example.invalid/releases", new LoggingService());
            var window = new ChangelogWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "changelog-dark-long-de", (int)window.Width, (int)window.MaxHeight);

            var textBox = FindByName<TextBox>(content, "ChangelogTextBox");
            Assert.NotNull(textBox);
            Assert.Equal(ScrollBarVisibility.Visible, textBox!.VerticalScrollBarVisibility);
            Assert.True(textBox.ExtentHeight > textBox.ViewportHeight, "the long text should actually overflow the box");

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>English is otherwise never rendered for this window, every Changelog snapshot above
    /// applies "de" regardless of what it is named for.</summary>
    [Fact]
    public void Changelog_window_renders_in_english()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");

            var longText = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"- Release note line {i}: a change worth mentioning."));
            var viewModel = new ChangelogViewModel("Mozilla Firefox", "128.0.3 → 129.0.1", longText, "https://example.invalid/releases", new LoggingService());
            var window = new ChangelogWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "changelog-en", (int)window.Width, (int)window.MaxHeight);

            window.Close();
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => LocalizationService.Instance.Apply("de"));
    }

    /// <summary>
    /// A short changelog used to leave a large empty band above the action row (the window
    /// carried a fixed Height="520" regardless of content). Measuring at the window's own Width and
    /// unbounded height mirrors what SizeToContent="Height" itself settles on (clamped between
    /// MinHeight/MaxHeight, same as the Window's own sizing pass); the render harness itself always needs
    /// a caller-supplied pixel size, since it lays out detached content rather than asking the real
    /// Window to run its own SizeToContent pass.
    /// </summary>
    [Fact]
    public void Changelog_window_shrinks_to_fit_short_content()
    {
        RenderResult? result = null;
        double renderedHeight = 0, actionRowBottom = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var viewModel = new ChangelogViewModel("Firefox", "129.0.1", "Bug fixes.", null, new LoggingService());
            var window = new ChangelogWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);

            result = WpfHarness.RenderWindow(window, "changelog-shrinks-short-de", (int)window.Width, (int)renderedHeight);

            var actionRow = FindByName<Grid>(content, "ChangelogActionRow");
            Assert.NotNull(actionRow);
            actionRowBottom = actionRow!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + actionRow.ActualHeight;

            window.Close();
        });

        AssertRendered(result);
        Assert.True(renderedHeight < 520,
            $"Short content rendered at {renderedHeight:0}px, expected below the old fixed 520px height.");
        Assert.True(actionRowBottom <= renderedHeight + 0.5,
            $"Action row bottom ({actionRowBottom:0}px) falls outside the {renderedHeight:0}px window.");
    }

    /// <summary>The text row is Auto now (not a star row) and the window's MinHeight dropped
    /// 320 to 220, so a three-line note must leave no visible gap between the text block and the action
    /// row below it - checked directly, unlike the shrink test above which only proves the action row
    /// still fits somewhere inside the clamped window height.</summary>
    [Fact]
    public void Changelog_window_has_no_gap_below_a_short_note()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var viewModel = new ChangelogViewModel(
                "Firefox", "129.0.1", "Bug fixes.\nSecurity patches.\nMinor UI tweaks.", null, new LoggingService());
            var window = new ChangelogWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            var renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);
            result = WpfHarness.RenderWindow(window, "changelog-no-gap-short-de", (int)window.Width, (int)renderedHeight);

            var textBox = FindByName<TextBox>(content, "ChangelogTextBox");
            var actionRow = FindByName<Grid>(content, "ChangelogActionRow");
            Assert.NotNull(textBox);
            Assert.NotNull(actionRow);

            var textBottom = textBox!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + textBox.ActualHeight;
            var actionRowTop = actionRow!.TransformToAncestor(content).Transform(new Point(0, 0)).Y;

            // The Border around the TextBox carries no extra padding of its own beyond the row margins
            // already in the template, so anything past a few px here would be the old reserved band back.
            Assert.True(actionRowTop - textBottom < 20,
                $"Gap between the note ({textBottom:0}px) and the action row ({actionRowTop:0}px) is " +
                $"{actionRowTop - textBottom:0}px, looks like the old empty band.");

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>A very long note must still cap at the window's own 520px MaxHeight and scroll
    /// internally rather than growing the window past it.</summary>
    [Fact]
    public void Changelog_window_caps_at_520px_for_a_long_note()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var longText = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"- Release note line {i}."));
            var viewModel = new ChangelogViewModel("Mozilla Firefox", "128.0.3 → 129.0.1", longText, null, new LoggingService());
            var window = new ChangelogWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            // window.MaxHeight, not a pre-render Measure/Clamp guess: with 200 lines the real
            // SizeToContent pass settles at the MaxHeight ceiling regardless, and a bare Measure() here
            // (before the dispatcher has pumped once) would still see the Text binding unset - the
            // documented reason the other shrink test above only trusts that technique for a SHORT note.
            result = WpfHarness.RenderWindow(window, "changelog-caps-long-de", (int)window.Width, (int)window.MaxHeight);

            var textBox = FindByName<TextBox>(content, "ChangelogTextBox");
            Assert.NotNull(textBox);
            Assert.Equal(ScrollBarVisibility.Visible, textBox!.VerticalScrollBarVisibility);
            Assert.True(textBox.ExtentHeight > textBox.ViewportHeight, "a 200-line note should overflow the box");

            var actionRow = FindByName<Grid>(content, "ChangelogActionRow");
            Assert.NotNull(actionRow);
            var actionRowBottom = actionRow!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + actionRow.ActualHeight;
            Assert.True(actionRowBottom <= window.MaxHeight + 0.5,
                $"Action row bottom ({actionRowBottom:0}px) falls outside the {window.MaxHeight:0}px window.");

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>The window's own MinWidth/MinHeight (420x220), never rendered before at that width.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Changelog_window_at_its_minimum_width_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            var longText = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"- Release note line {i}: a change worth mentioning."));
            var viewModel = new ChangelogViewModel("Mozilla Firefox", "128.0.3 → 129.0.1", longText, "https://example.invalid/releases", new LoggingService());
            var window = new ChangelogWindow(viewModel);
            result = WpfHarness.RenderWindow(
                window, $"changelog-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);

            window.Close();
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => LocalizationService.Instance.Apply("de"));
    }

    /// <summary>
    /// The title bar used to bind WindowTitle ("{Name}, Error details"), and a
    /// ui:TitleBar hard-truncates instead of wrapping once the window buttons need room, a long package
    /// name cut the whole sentence off mid-word. The bar now binds the short, fixed Loc[ErrorDetailTitle]
    /// text instead (the name itself is already shown big in the body below), so a long name can no
    /// longer affect it at all; this proves that with a name at the 60-character mark the plan itself
    /// used as the concrete case.
    /// </summary>
    [Fact]
    public void Error_detail_window_title_stays_readable_with_a_60_character_package_name()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var longName = "Microsoft Visual Studio Code, Insiders Build (Preview Channel)"[..60];
            Assert.Equal(60, longName.Length);

            var viewModel = new ErrorDetailViewModel(
                longName, "winget", "1.92.0 → 1.93.1", "winget install failed (exit 1).", new LoggingService());
            var window = new ErrorDetailWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "error-detail-long-name-de", (int)window.Width, (int)window.MaxHeight);

            // The title bar's bound text is the SHORT fixed string, never the long combined WindowTitle,
            // structurally immune to the name's length now, not just visually fine at this one length.
            Assert.True(viewModel.Loc["ErrorDetailTitle"].Length < 30, "the title bar's own text must stay short regardless of the package name");
        });

        AssertRendered(result);
    }

    /// <summary>Same title-bar fix, same construction, in the changelog popup (22.08.2026).</summary>
    [Fact]
    public void Changelog_window_title_stays_readable_with_a_60_character_package_name()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var longName = "Microsoft Visual Studio Code, Insiders Build (Preview Channel)"[..60];
            var viewModel = new ChangelogViewModel(
                longName, "1.92.0 → 1.93.1", "- Fixed a crash on startup.\n- Improved performance.",
                "https://example.invalid/releases/1.93.1", new LoggingService());
            var window = new ChangelogWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "changelog-long-name-de", (int)window.Width, (int)window.MaxHeight);

            Assert.True(viewModel.Loc["ChangelogTitle"].Length < 30, "the title bar's own text must stay short regardless of the package name");

            // A WPF Window registers itself with Application.Current.Windows the moment it is constructed,
            // Shown or not, left open it would still count in a LATER test's own Application.Windows check
            // (the suite runs serially, one process, one Application instance for the whole run).
            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>The F1 overview: every entry in <see cref="KeyboardShortcuts.All"/> rendered once, in both
    /// languages, so a missing localization or an overflowing gesture pill would show up here rather than
    /// only on the host.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Shortcuts_window_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            ApplyTheme("light");

            var window = new ShortcutsWindow();
            result = WpfHarness.RenderWindow(window, $"shortcuts-{language}", (int)window.Width, (int)window.MaxHeight);

            window.Close();
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");
        });
    }

    /// <summary>
    /// This fix's inventory pass: IgnoreListWindow had no render coverage at all. Both the populated and
    /// the explicit empty state (HasNoEntries), since the window's own template only ever shows one of the
    /// two, and the empty state is exactly the kind of "leaves a band" defect the item is checking for.
    /// </summary>
    [Fact]
    public void IgnoreList_window_with_entries_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            settings.Current.Excludes.Add("winget:Mozilla.Firefox");
            settings.Current.Excludes.Add("winget:Microsoft.VisualStudioCode@1.92.0");

            var viewModel = new IgnoreListViewModel(settings,
                new Dictionary<string, string> { ["winget:Mozilla.Firefox"] = "Mozilla Firefox" });
            var window = new IgnoreListWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "ignore-list-entries-de", (int)window.Width, (int)window.MaxHeight);

            window.Close();
        });

        AssertRendered(result);
    }

    [Fact]
    public void IgnoreList_window_shrinks_to_fit_the_empty_state()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));

            var viewModel = new IgnoreListViewModel(settings);
            var window = new IgnoreListWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "ignore-list-empty-de", (int)window.Width, (int)window.MaxHeight);

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>
    /// This fix's inventory pass: MessageDialogWindow, the one dialog EVERY notice and confirmation in the
    /// app goes through, had no render coverage at all. Both the ordinary (safe primary) and the destructive
    /// shape, since <see cref="MessageDialogWindow"/>'s own constructor branches on
    /// <c>PrimaryIsDestructive</c> for which button becomes the window's default.
    /// </summary>
    [Fact]
    public void MessageDialog_window_renders_a_question_with_two_buttons()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var viewModel = new MessageDialogViewModel(
                DialogKind.Question, "Bestätigung", "Möchten Sie fortfahren?",
                "Diese Aktion kann rückgängig gemacht werden.", "Fortfahren", "Abbrechen");
            var window = new MessageDialogWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "message-dialog-question-de", (int)window.MinWidth, 260);

            window.Close();
        });

        AssertRendered(result);
    }

    [Fact]
    public void MessageDialog_window_renders_a_destructive_confirmation_with_details()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var viewModel = new MessageDialogViewModel(
                DialogKind.Warning, "Löschen bestätigen", "Alle ignorierten Einträge entfernen?",
                "Diese Aktion kann nicht rückgängig gemacht werden.", "Entfernen", "Abbrechen",
                details: "Excludes: winget:Mozilla.Firefox, winget:Microsoft.VisualStudioCode@1.92.0",
                destructive: true);
            var window = new MessageDialogWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "message-dialog-destructive-de", (int)window.MinWidth, 320);

            window.Close();
        });

        AssertRendered(result);
    }

    /// <summary>
    /// This fix's fourth check across all nine sub-windows/warning dialogs: no clipped German text at
    /// 100% (already covered by each window's own renders above) and at 150%, the most common non-default
    /// Windows scale, same LayoutTransform approach as <see cref="Main_window_at_150_percent_scale_renders"/>.
    /// Realistic German content throughout (longer strings than English, the harder case). A window that
    /// clips here comes out looking wrong in the PNG, which is what AssertRendered's distinct-colour floor
    /// and a human look both catch; a byte-exact "nothing crosses the edge" proof per pixel is not
    /// attempted, same bar the main window's own 150% test already sets.
    /// </summary>
    [Fact]
    public void Every_sub_window_renders_without_clipping_at_150_percent_scale()
    {
        var results = new List<(string Name, RenderResult? Result)>();

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            {
                var viewModel = new ChangelogViewModel("Mozilla Firefox", "128.0.3 → 129.0.1",
                    "Fehlerbehebungen und Sicherheitsupdates für die neueste Version.",
                    "https://example.invalid/releases", new LoggingService());
                var window = new ChangelogWindow(viewModel);
                results.Add(("changelog", WpfHarness.RenderWindow(window,
                    "changelog-scale150-de", (int)(window.Width * 1.5), (int)(window.MaxHeight * 1.5), scale: 1.5)));
                window.Close();
            }
            {
                var logging = new LoggingService();
                var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
                var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
                WpfHarness.WaitForCleanupCatalog(viewModel);
                viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
                {
                    Timestamp = new DateTimeOffset(2026, 8, 7, 21, 40, 0, TimeSpan.Zero),
                    FreedBytes = 620L * 1024 * 1024,
                    Areas = [new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 400L * 1024 * 1024 }],
                }));
                var window = new CleanupHistoryWindow(viewModel);
                results.Add(("cleanup-history", WpfHarness.RenderWindow(window,
                    "cleanup-history-scale150-de", (int)(window.Width * 1.5), (int)(window.MaxHeight * 1.5), scale: 1.5)));
                window.Close();
            }
            {
                string[] attempts = ["[upgrade] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). No applicable update found."];
                var detail = WingetSource.LadderFailure(attempts, unchecked((int)0x8A15002B), "17.0.9");
                var viewModel = new ErrorDetailViewModel("OpenJDK 17", "winget", "17.0.8 → 17.0.9", detail, new LoggingService());
                var window = new ErrorDetailWindow(viewModel);
                results.Add(("error-detail", WpfHarness.RenderWindow(window,
                    "error-detail-scale150-de", (int)(window.Width * 1.5), (int)(window.MaxHeight * 1.5), scale: 1.5)));
                window.Close();
            }
            {
                var viewModel = new HistoryViewModel(HistoryWith(HistoryEntries()));
                var window = new HistoryWindow(viewModel, new LoggingService());
                results.Add(("history", WpfHarness.RenderWindow(window,
                    "history-scale150-de", (int)(window.Width * 1.5), (int)(window.MaxHeight * 1.5), scale: 1.5)));
                window.Close();
            }
            {
                var logging = new LoggingService();
                var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
                settings.Current.Excludes.Add("winget:Mozilla.Firefox");
                settings.Current.Excludes.Add("winget:Microsoft.VisualStudioCode@1.92.0");
                var viewModel = new IgnoreListViewModel(settings,
                    new Dictionary<string, string> { ["winget:Mozilla.Firefox"] = "Mozilla Firefox" });
                var window = new IgnoreListWindow(viewModel);
                results.Add(("ignore-list", WpfHarness.RenderWindow(window,
                    "ignore-list-scale150-de", (int)(window.Width * 1.5), (int)(window.MaxHeight * 1.5), scale: 1.5)));
                window.Close();
            }
            {
                var blocker = new Services.Install.BlockedInstall(null!, "Node.js", ["node"]);
                var row = new UpdateRowViewModel(new UpdateItem
                {
                    Id = "npm", SourceId = "npm", Category = UpdateCategory.Software,
                    Name = "npm", InstalledVersion = "1.0", AvailableVersion = "2.0",
                });
                var lines = InstallPreflightLineViewModel.FromBlockedGroups([(row, blocker)]);
                var viewModel = new InstallPreflightViewModel(lines, new LoggingService(), true);
                var window = new InstallPreflightWindow(viewModel);
                results.Add(("preflight", WpfHarness.RenderWindow(window,
                    "preflight-scale150-de", (int)(window.MinWidth * 1.5), (int)(400 * 1.5), scale: 1.5)));
                window.Close();
            }
            {
                var viewModel = new MessageDialogViewModel(
                    DialogKind.Question, "Bestätigung", "Möchten Sie fortfahren?",
                    "Diese Aktion kann rückgängig gemacht werden.", "Fortfahren", "Abbrechen");
                var window = new MessageDialogWindow(viewModel);
                results.Add(("message-dialog", WpfHarness.RenderWindow(window,
                    "message-dialog-scale150-de", (int)(window.MinWidth * 1.5), (int)(260 * 1.5), scale: 1.5)));
                window.Close();
            }
            {
                var window = new ShortcutsWindow();
                results.Add(("shortcuts", WpfHarness.RenderWindow(window,
                    "shortcuts-scale150-de", (int)(window.Width * 1.5), (int)(window.MaxHeight * 1.5), scale: 1.5)));
                window.Close();
            }

            ApplyTheme("dark");
        });

        foreach (var (name, result) in results)
        {
            Assert.True(result is not null, $"{name} did not render at 150% scale.");
            Assert.True(File.Exists(result!.Path), $"{name}: no image was written to {result.Path}.");
            Assert.True(result.DistinctColours >= MinimumDistinctColours,
                $"{name} at 150% scale came out effectively blank, only {result.DistinctColours} distinct colours.");
        }
    }

    /// <summary>
    /// The cleanup dialog, with a PART-FILLED ring, which is the whole point of rendering it. An arc that is
    /// not full is drawn in only one corner of its box, and a Path sized to its geometry would then be
    /// centred on that corner instead of on the track, which is what made the ring lean.
    /// DisplayPercent is set directly because nothing here animates: the harness never shows the window, so
    /// the view model's own glide never runs. Both languages are checked because German
    /// routinely runs longer than English and is the one more likely to wrap the overview-band facts.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Cleanup_window_renders_with_a_part_filled_ring(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(
                new UltimateUpdater.Services.Cleanup.CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            // Realistic figures: some rows with a size, some with nothing found, none still scanning.
            long size = 96L * 1024 * 1024;
            var index = 0;
            foreach (var item in viewModel.Items)
            {
                item.IsScanning = false;
                item.Bytes = index % 3 == 0 ? 0 : size / (index + 1);
                item.Files = index % 3 == 0 ? 0 : 120 * (index + 1);
                index++;
            }

            // One history entry, so this snapshot also proves the header button's placement
            // against the ring band it shares, every other cleanup-de snapshot leaves HasHistory false,
            // which would hide that button entirely. Also feeds the "last cleanup" line.
            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 7, 21, 40, 0, TimeSpan.Zero),
                FreedBytes = 300L * 1024 * 1024,
                Areas = [new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 300L * 1024 * 1024 }],
            }));

            var window = new CleanupWindow(viewModel) { DisplayPercent = 42 };
            result = WpfHarness.RenderWindow(window, $"cleanup-{language}", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>The window's own MinWidth/MinHeight (860x580), its default 980px snapshot above never
    /// exercises the width the window can actually be shrunk to, which is where German labels on the
    /// ring band and the target rows break first.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Cleanup_window_at_its_minimum_width_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(
                new UltimateUpdater.Services.Cleanup.CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            long size = 96L * 1024 * 1024;
            var index = 0;
            foreach (var item in viewModel.Items)
            {
                item.IsScanning = false;
                item.Bytes = index % 3 == 0 ? 0 : size / (index + 1);
                item.Files = index % 3 == 0 ? 0 : 120 * (index + 1);
                index++;
            }

            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 7, 21, 40, 0, TimeSpan.Zero),
                FreedBytes = 300L * 1024 * 1024,
                Areas = [new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 300L * 1024 * 1024 }],
            }));

            var window = new CleanupWindow(viewModel) { DisplayPercent = 42 };
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(
                window, $"cleanup-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);

            // 23.08.2026: at the window's OLD 720px MinWidth the left action group (Alle
            // auswählen/Neu scannen/recycle-bin checkbox) wanted up to ~595px and the right-aligned
            // Schließen/Bereinigen group sat at a fixed 462px from the left, a ~133px overlap that
            // rendered as garbled text (see cleanup-narrow-de.png before the fix). The Grid gives each
            // StackPanel its own natural size and aligns it independently; it never reserves space between
            // them, so the two only stay apart if the window itself is wide enough. MinWidth is 860,
            // short of the window's own default 980px Width but still what actually leaves room.
            var left = FindByName<StackPanel>(content, "ActionRowLeftPanel");
            var right = FindByName<StackPanel>(content, "ActionRowRightPanel");
            Assert.NotNull(left);
            Assert.NotNull(right);

            var leftRight = left!.TransformToAncestor(content).Transform(new Point(0, 0)).X + left.ActualWidth;
            var rightLeft = right!.TransformToAncestor(content).Transform(new Point(0, 0)).X;
            Assert.True(leftRight <= rightLeft,
                $"the action row's left group (ends at {leftRight:F0}px) overlaps its right group " +
                $"(starts at {rightLeft:F0}px) at the window's {window.MinWidth:F0}px minimum width.");
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

    /// <summary>The finished dialog keeps its Rescan button (the way to another run) and still hides Clean.</summary>
    [Fact]
    public void Cleanup_window_after_a_finished_run_shows_rescan_and_hides_clean()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            viewModel.Phase = CleanupPhase.Finished;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 100 };
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "cleanup-finished-de", (int)window.Width, (int)window.Height);

            var rescan = FindByName<Button>(content, "RescanButton");
            var clean = FindByName<Button>(content, "CleanButton");
            Assert.NotNull(rescan);
            Assert.NotNull(clean);
            Assert.True(rescan!.Visibility == Visibility.Visible && rescan.ActualWidth > 0 && rescan.ActualHeight > 0,
                $"rescan not laid out: {rescan.Visibility} {rescan.ActualWidth:0.0}x{rescan.ActualHeight:0.0}");
            Assert.Equal(LocalizationService.Instance["CleanupRescan"], rescan.Content);
            Assert.True(rescan.IsEnabled);
            Assert.Equal(Visibility.Collapsed, clean!.Visibility);

            var left = rescan.TransformToAncestor(content).Transform(new Point(0, 0)).X;
            Assert.True(left >= 0 && left + rescan.ActualWidth <= content.ActualWidth,
                $"rescan x {left:0.0} width {rescan.ActualWidth:0.0} leaves the window");
        });

        AssertRendered(result);
    }

    /// <summary>Light-theme counterpart of the min-width snapshot above, the other light-theme cleanup
    /// snapshot is at the window's default 980px width, never its narrowest.</summary>
    [Fact]
    public void Cleanup_window_at_its_minimum_width_renders_in_the_light_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            result = WpfHarness.RenderWindow(
                window, "cleanup-narrow-light-de", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    /// <summary>Explicit dark counterpart to the min-width light snapshot above, same reasoning as
    /// Settings_window_in_the_dark_theme_renders: relying on the harness's resting theme would prove
    /// nothing about the dark palette actually being applied.</summary>
    [Fact]
    public void Cleanup_window_at_its_minimum_width_renders_in_the_dark_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            result = WpfHarness.RenderWindow(
                window, "cleanup-narrow-dark-de", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The cleanup window's own "Last restore point: {date}" line and its Open button, read back
    /// from the same timestamp file InstallQueue and CleanupWorker write after actually creating one; only
    /// during selection, same as <see cref="CleanupViewModel.LastCleanupText"/>.
    /// </summary>
    [Fact]
    public void Cleanup_window_shows_the_last_restore_point_line()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");

            var logging = new LoggingService();
            var timestampPath = Path.Combine(AppPaths.DataDirectory, "restore-point.json");
            if (File.Exists(timestampPath))
                File.Delete(timestampPath);
            RestorePointThrottle.TryWriteCreated(logging, new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));

            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            result = WpfHarness.RenderWindow(window, "cleanup-restore-point-line", (int)window.Width, 640);

            Assert.True(viewModel.IsChoosing);
            Assert.StartsWith("Last restore point:", viewModel.RestorePointText);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// "Noch kein Wiederherstellungspunkt von diesem Programm" used to be cut off at the right column's
    /// 260px: a horizontal StackPanel measures its children at infinite width, so the text never wrapped.
    /// The line now sits in a Grid (star column for the text, auto column for the Open button), so the full
    /// sentence has to fit inside the column, wrapped onto a second line when it must.
    /// </summary>
    [Fact]
    public void Cleanup_window_restore_point_line_wraps_inside_the_right_column_in_german()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var timestampPath = Path.Combine(AppPaths.DataDirectory, "restore-point.json");
            if (File.Exists(timestampPath))
                File.Delete(timestampPath);

            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "cleanup-restore-point-none-de", (int)window.MinWidth, (int)window.MinHeight);

            var line = FindByName<TextBlock>(content, "RestorePointLine");
            Assert.NotNull(line);
            Assert.Equal(viewModel.RestorePointText, line!.Text);

            var column = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(line));
            while (column is not StackPanel)
                column = (FrameworkElement)VisualTreeHelper.GetParent(column);

            var lineRight = line.TransformToAncestor(content).Transform(new Point(line.ActualWidth, 0)).X;
            var columnRight = column.TransformToAncestor(content).Transform(new Point(column.ActualWidth, 0)).X;
            Assert.True(lineRight <= columnRight + 0.5,
                $"the restore point text ends at {lineRight:F0}px, past its column's right edge at {columnRight:F0}px.");

            // Wrapped, not squeezed: the whole sentence is laid out (more than one line of text).
            Assert.True(line.ActualHeight > line.FontSize * 1.6,
                $"the restore point text is one line high ({line.ActualHeight:F0}px), it did not wrap.");
        });

        AssertRendered(result);
    }

    /// <summary>
    /// A snapshot while cleaning is running, so the hand-authored hard-disk Path geometry (drive
    /// body, platter ring and actuator arm, replacing the earlier plain rounded surface) can be checked for
    /// being SHARP rather than blurred. The harness never shows the window, so Loaded, and with it
    /// StartSweep's Storyboard, never runs; one glint is nudged visible by hand (FindName reaches it
    /// without needing a public field) so its own shape is provable too, not only the drive's static rest
    /// pose. The platter ring and the arm need no nudge: they are drawn once, always at full opacity, never
    /// animated. Motion itself is out of scope for this test.
    /// </summary>
    [Fact]
    public void Cleanup_window_while_cleaning_shows_the_new_drive_and_glint_vectors()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.Phase = CleanupPhase.Cleaning;
            viewModel.CleanPercent = 47;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 47 };

            if (window.FindName("Glint1") is FrameworkElement glint1)
                glint1.Opacity = 1;

            result = WpfHarness.RenderWindow(window, "cleanup-cleaning-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The hard-disk cleaning symbol (drive body, platter ring and actuator arm, 72 of a 100px
    /// scene box) at three points along a run: just starting, roughly halfway, and effectively done - the
    /// glints' own peak amplitude rises with progress (<see cref="CleanupRingAnimationTests"/> proves the
    /// numbers; this is where the centre figure and percentage text are checked for still having room at
    /// the new size, and where the rendered PNG goes for visual review). German, dark (the
    /// suite's default theme).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(100)]
    public void Cleanup_window_shows_the_bigger_cleaning_symbol_at_various_progress(int percent)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.Phase = CleanupPhase.Cleaning;
            viewModel.CleanPercent = percent;

            var window = new CleanupWindow(viewModel) { DisplayPercent = percent };
            result = WpfHarness.RenderWindow(window, $"cleanup-symbol-{percent}pct-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>Same case, roughly halfway, the light theme AND English, so a colour resource missing from
    /// either combination would still show up.</summary>
    [Fact]
    public void Cleanup_window_shows_the_bigger_cleaning_symbol_in_the_light_theme_english()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            ApplyTheme("light");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.Phase = CleanupPhase.Cleaning;
            viewModel.CleanPercent = 45;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 45 };
            result = WpfHarness.RenderWindow(window, "cleanup-symbol-45pct-light-en", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() =>
        {
            ApplyTheme("dark");
            LocalizationService.Instance.Apply("de");
        });
    }

    /// <summary>
    /// The scene box grew from 72 to 100 for the three glints outside the drive body. Proves that
    /// growth stays clear of the progress ring and the percentage text even at the window's own minimum
    /// size, the tightest room the symbol ever has to fit in.
    /// </summary>
    [Fact]
    public void Cleanup_window_shows_the_cleaning_symbol_without_clipping_at_minimum_size()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.Phase = CleanupPhase.Cleaning;
            viewModel.CleanPercent = 62;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 62 };
            result = WpfHarness.RenderWindow(window, "cleanup-symbol-min-size-de", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Renders the cleanup window with the high-contrast dictionary loaded (same
    /// route as ContrastSweepTests, ApplyHighContrastSurfaceOverrides applied directly, since the real
    /// SystemParameters.HighContrast flag is read-only and Windows' own) and reads the RESOLVED Background
    /// of the window, the header band (CleanupHeaderBand, the row holding the title bar and the gauge) and
    /// the targets card (CleanupTargetsCard), the right way to diagnose a colour: by the actual value,
    /// never by reasoning about which dictionary should win. The finding: the header
    /// band's own Background is a literal Transparent (by design, it lets the window's own
    /// ApplicationBackgroundBrush show through, same pattern the whole window uses at its root), and
    /// CardBackgroundFillColorDefaultBrush (the targets card's key) already resolves to the SAME
    /// SystemColors.WindowBrush as ApplicationBackgroundBrush inside ThemeService.HighContrastSurfaceKeys,
    /// so under WPF's own paint pipeline, which is everything this offscreen harness can exercise, the two
    /// surfaces already match. This test proves exactly that and no more: it cannot touch the DWM/Mica
    /// compositing layer a real window goes through on screen, the same gap ContrastSweepTests' own doc
    /// comment already names for this reason.
    /// </summary>
    [Fact]
    public void Cleanup_window_header_and_targets_card_share_the_high_contrast_background()
    {
        Color? windowColor = null;
        Color? headerColor = null;
        Color? cardColor = null;

        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(ApplicationTheme.Light);
            ThemeService.ApplyHighContrastSurfaceOverrides();
            ThemeService.ApplyAccentContrast(System.Windows.SystemColors.HighlightColor);

            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };

            // FindName has to run before RenderWindow detaches the content from the Window: names are
            // registered in the Window's own NameScope, and FindName walks up the LOGICAL tree to reach
            // it, which a detached root can no longer do.
            var header = window.FindName("CleanupHeaderBand") as Grid;
            var card = window.FindName("CleanupTargetsCard") as Border;

            windowColor = (window.Background as SolidColorBrush)?.Color;

            WpfHarness.RenderWindow(window, "cleanup-high-contrast-header-vs-card", (int)window.Width, (int)window.Height);

            headerColor = (header?.Background as SolidColorBrush)?.Color;
            cardColor = (card?.Background as SolidColorBrush)?.Color;

            ThemeService.RemoveHighContrastSurfaceOverrides();
        });

        Assert.NotNull(windowColor);
        Assert.NotNull(cardColor);
        Assert.Equal(windowColor, cardColor);

        // The header band's own Background is a literal Transparent, not a DynamicResource: what it
        // visually shows is the window's own brush behind it, which the assertion above already proved
        // matches the card. Documented here rather than asserted as equal colours, since Transparent is
        // not comparable to an opaque brush by value.
        Assert.Equal(Colors.Transparent, headerColor);
    }

    /// <summary>
    /// A target's detail list, expanded, the collapsed-by-default Expander only ever
    /// shows its header in a snapshot otherwise (WpfHarness.ExpandCategoryGroups), which would leave the
    /// list's own layout (checkbox/name/date/size columns, the "…and N more" line) unverified by eye.
    /// </summary>
    [Fact]
    public void Cleanup_window_shows_a_targets_expanded_detail_list()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            // A realistic three-row list on the first Directories-kind target ("user-temp"), one small,
            // one large, one "…and N more" summary, so the column layout and the omitted-entries line
            // both show up in the same snapshot.
            var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            target.Bytes = 340L * 1024 * 1024;
            target.Files = 812;
            target.SetEntries(
                [
                    new CleanupEntry(@"C:\Temp\Spotify", "Spotify", 210L * 1024 * 1024, 640,
                        new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache),
                    new CleanupEntry(@"C:\Temp\SomeInstaller", "SomeInstaller", 4L * 1024 * 1024, 3,
                        new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.InstallLeftovers),
                ],
                omittedCount: 27, omittedBytes: 40L * 1024 * 1024);

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            result = WpfHarness.RenderWindow(window, "cleanup-entries-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The live status a run puts on the list: a status capsule on every target that is part of it, and
    /// inside the one open detail list a row already cleaned (ticked, dimmed, size struck through), the row
    /// being worked on and one that stayed behind. Rendered because the only way to judge whether five
    /// quiet words and three small marks read as calm rather than as noise is to look at them.
    /// </summary>
    [Fact]
    public void Cleanup_window_shows_live_per_target_and_per_entry_status()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var running = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            running.Bytes = 340L * 1024 * 1024;
            running.Files = 812;
            running.IsCleaning = true;
            running.RunState = CleanupTargetState.Running;
            running.Percent = 62;
            running.FreedBytes = 210L * 1024 * 1024;
            running.DetailText = @"C:\Temp\SomeInstaller\setup.log";
            running.IsDetailsExpanded = true;
            running.SetEntries(
                [
                    new CleanupEntry(@"C:\Temp\Spotify", "Spotify", 210L * 1024 * 1024, 640,
                        new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache),
                    new CleanupEntry(@"C:\Temp\SomeInstaller", "SomeInstaller", 4L * 1024 * 1024, 3,
                        new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.InstallLeftovers),
                    new CleanupEntry(@"C:\Temp\Locked", "Locked", 12L * 1024 * 1024, 4,
                        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Logs),
                ]);
            // Cleaned first: it is the row with the most to look at (tick, dimmed, size struck through), and
            // the snapshot's own list starts at the top.
            running.Entries[0].State = EntryState.Cleaned;
            running.Entries[0].FreedBytes = 210L * 1024 * 1024;
            running.Entries[1].State = EntryState.Running;
            running.Entries[2].State = EntryState.Skipped;

            // The other four words, one per row, so the whole set is judged in one picture.
            var others = viewModel.Items.Where(i => i.Target.Kind == CleanupKind.Directories && i != running).Take(4).ToList();
            CleanupTargetState[] states =
            [
                CleanupTargetState.Done, CleanupTargetState.Waiting,
                CleanupTargetState.Stopped, CleanupTargetState.Skipped,
            ];
            for (var index = 0; index < others.Count; index++)
            {
                others[index].Bytes = (index + 1) * 8L * 1024 * 1024;
                others[index].RunState = states[index];
            }

            var window = new CleanupWindow(viewModel) { DisplayPercent = 62 };
            result = WpfHarness.RenderWindow(window, "cleanup-live-status-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// The recycle bin's own detail list: one row per deleted file, grouped by drive, each row carrying the
    /// folder it came from and its deletion date, with the per-drive "…and N more" note in the group header.
    /// Rendered because none of that layout (three text lines per row, a note under a group heading) exists
    /// anywhere else in this dialog.
    /// </summary>
    [Fact]
    public void Cleanup_window_shows_the_recycle_bin_grouped_by_drive()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var deleted = new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero);
            var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.RecycleBin);

            target.IsSelected = true;
            target.Bytes = 1400L * 1024 * 1024;
            target.Files = 3;
            target.SetEntries(
                [
                    new CleanupEntry(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RA1B2C3.mp4",
                        "Urlaubsvideo 2026.mp4", 1200L * 1024 * 1024, 1, deleted, deleted, CleanupEntryType.Other,
                        @"C:\Users\Benutzer\Videos\Urlaub\Urlaubsvideo 2026.mp4"),
                    new CleanupEntry(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RD4E5F6.docx",
                        "Bewerbung.docx", 180L * 1024, 1, deleted, deleted, CleanupEntryType.Other,
                        @"C:\Users\Benutzer\Dokumente\Bewerbung.docx"),
                    new CleanupEntry(@"D:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RG7H8I9",
                        "Alte Projekte", 200L * 1024 * 1024, 1, deleted, deleted, CleanupEntryType.Other,
                        @"D:\Archiv\Alte Projekte"),
                ],
                omittedCount: 12, omittedBytes: 90L * 1024 * 1024,
                recycleBinDrives:
                [
                    new RecycleBinDriveSummary(@"C:\", @"C:\ (System)", 14, 2,
                        1290L * 1024 * 1024, 1200L * 1024 * 1024 + (180L * 1024), DetailsAvailable: true),
                    new RecycleBinDriveSummary(@"D:\", @"D:\ (Daten)", 1, 1,
                        200L * 1024 * 1024, 200L * 1024 * 1024, DetailsAvailable: true),
                ]);

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            // The bin's card sits low in the list and its opened detail list needs room, so render taller.
            result = WpfHarness.RenderWindow(window, "cleanup-recycle-bin-de", (int)window.Width, 1500);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// A target with a long detail list must not grow the dialog, its own ScrollViewer
    /// caps at 240px regardless of entry count. Measures the ScrollViewer itself (x:Name="EntriesItemsScroll",
    /// the virtualized entries ItemsControl's own template part, not the outer EntriesScroll that just wraps
    /// the select-all header and the "omitted" line around it), not the card or the Body border around it,
    /// since their margin/padding sit on top of that number.
    /// Also proves the scrollbar-overlap fix: this ScrollViewer inherits WPF-UI's overlay
    /// scrollbar style, MEASURED by <c>ViewportWidth == ActualWidth</c> holding true even while
    /// <c>ScrollableHeight &gt; 0</c>, the default WPF template would instead shrink ViewportWidth to
    /// reserve a column for the bar, so this one draws the thumb ON TOP of the content's own right edge
    /// instead. Row content therefore needs its own right margin to stay clear of the thumb; a size column
    /// with no such margin visibly ran under it before the fix.
    /// </summary>
    [Fact]
    public void Cleanup_details_list_stays_capped_with_many_entries()
    {
        RenderResult? result = null;
        ScrollViewer? scroll = null;
        ScrollBar? verticalScrollBar = null;
        FrameworkElement? sizeColumn = null;
        double scrollActualHeight = 0, scrollableHeight = 0, sizeColumnRight = 0, scrollBarLeft = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            target.Bytes = 500L * 1024 * 1024;
            target.Files = 500;
            target.SetEntries(Enumerable.Range(0, 50).Select(i =>
                new CleanupEntry(@$"C:\Temp\App{i}", $"App{i}", 10L * 1024 * 1024, 10,
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache)));

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement; // RenderWindow detaches Content from the Window
            result = WpfHarness.RenderWindow(window, "cleanup-entries-capped-de", (int)window.Width, (int)window.Height);
            // The 240 cap sits on the entries list's OWN internal ScrollViewer (EntriesItemsScroll,
            // inside the virtualized ItemsControl's template), not on the outer EntriesScroll any
            // more, which now just wraps the select-all header and the "omitted" line around it and
            // sizes to fit rather than capping itself.
            scroll = content is null ? null : FindByName<ScrollViewer>(content, "EntriesItemsScroll");
            verticalScrollBar = scroll is null ? null : FindFirstDescendant<ScrollBar>(scroll);
            // The size text sits in the row Grid's third (rightmost, Auto-width) column - grab that
            // column's own FrameworkElement (the TextBlock itself) via its Grid.Column value rather than by
            // name, since the DataTemplate gives it none.
            // Unnamed elements default to Name == "", so this walk finds every TextBlock the DataTemplate
            // instantiated; the size text is the one bound into the row Grid's third (rightmost) column.
            sizeColumn = scroll is null
                ? null
                : FindAllByName<TextBlock>(scroll, string.Empty)
                    .FirstOrDefault(tb => Grid.GetColumn(tb) == 2 && tb.Text.Contains("MB"));

            if (scroll is not null)
            {
                scrollActualHeight = scroll.ActualHeight;
                scrollableHeight = scroll.ScrollableHeight;
            }

            if (sizeColumn is not null && scroll is not null)
                sizeColumnRight = sizeColumn.TransformToAncestor(scroll).Transform(new Point(sizeColumn.ActualWidth, 0)).X;
            if (verticalScrollBar is not null && scroll is not null)
                scrollBarLeft = verticalScrollBar.TransformToAncestor(scroll).Transform(new Point(0, 0)).X;
        });

        AssertRendered(result);
        Assert.NotNull(scroll);
        Assert.True(scrollActualHeight <= 240, $"EntriesItemsScroll grew to {scrollActualHeight:0} px, expected <= 240.");

        Assert.NotNull(verticalScrollBar);
        Assert.NotNull(sizeColumn);

        Assert.True(sizeColumnRight <= scrollBarLeft,
            $"Size text right edge ({sizeColumnRight:0}) overlaps the scrollbar (starts at {scrollBarLeft:0}).");
        Assert.True(scrollableHeight > 0, "50 entries should overflow the 240px cap and need scrolling.");
    }

    /// <summary>
    /// On a list of several thousand entries the thumb used to shrink to a few points, too small to grab
    /// reliably. Measures the rendered Thumb's own bounds (not the style's MinHeight setter, which proves
    /// only that the setter exists, not that Track actually honours it as the thumb's minimum length).
    /// </summary>
    [Fact]
    public void Cleanup_details_list_scrollbar_thumb_keeps_a_usable_size_with_many_entries()
    {
        RenderResult? result = null;
        Thumb? thumb = null;
        double thumbHeight = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            target.Bytes = 3000L * 1024 * 1024;
            target.Files = 3000;
            target.SetEntries(Enumerable.Range(0, 3000).Select(i =>
                new CleanupEntry(@$"C:\Temp\App{i}", $"App{i}", 1L * 1024 * 1024, 1,
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache)));

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-entries-scrollbar-thumb-de", (int)window.Width, (int)window.Height);
            var scroll = content is null ? null : FindByName<ScrollViewer>(content, "EntriesItemsScroll");
            thumb = scroll is null ? null : FindFirstDescendant<Thumb>(scroll);
            thumbHeight = thumb?.ActualHeight ?? 0;
        });

        AssertRendered(result);
        Assert.NotNull(thumb);
        Assert.True(thumbHeight >= 32, $"Scrollbar thumb is {thumbHeight:0} px tall, expected >= 32.");
    }

    /// <summary>The recycle bin card's age-filter sub-row: checked by default (30 days), reachable in the
    /// rendered tree, and indented past the card's own left edge rather than flush with it. The shared
    /// DataTemplate gives every row a same-named checkbox/number box, so this filters to the one whose
    /// row is actually visible (the recycle bin's own card) rather than asserting on whichever the walk
    /// finds first.</summary>
    [Fact]
    public void Cleanup_recycle_bin_age_filter_sub_row_renders_checked_by_default()
    {
        RenderResult? result = null;
        bool found = false;
        bool isChecked = false;
        bool numberBoxEnabled = false;
        double cardLeftX = 0;
        double subRowLeftX = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-recycle-age-filter-de", (int)window.Width, (int)window.Height);

            if (content is null)
                return;

            ForceExpandAllGroupCards(content);
            content.UpdateLayout();

            // The shared row template puts every target's card through the same DataTemplate, so the
            // sub-row's own Visibility binding (bound to IsRecycleBin) collapses the ANCESTOR StackPanel,
            // never this checkbox's/number box's own Visibility property, which stays at its unbound
            // default of Visible for all cards. A collapsed ancestor is never measured, so a real,
            // positive ActualWidth/Height is what actually tells the one rendered copy apart from the rest.
            var checkBox = FindAllByName<CheckBox>(content, "RecycleAgeCheckBox")
                .SingleOrDefault(cb => cb.ActualWidth > 0 && cb.ActualHeight > 0);
            var numberBox = FindAllByName<Wpf.Ui.Controls.NumberBox>(content, "RecycleAgeNumberBox")
                .SingleOrDefault(nb => nb.ActualWidth > 0 && nb.ActualHeight > 0);
            var card = FindAllByName<Grid>(content, "ItemRoot")
                .FirstOrDefault(root => root.ActualWidth > 0 && root.DataContext is CleanupItemViewModel item && item.IsRecycleBin);

            if (checkBox is null || numberBox is null || card is null)
                return;

            found = true;
            isChecked = checkBox.IsChecked == true;
            numberBoxEnabled = numberBox.IsEnabled;
            cardLeftX = card.TransformToAncestor(content).Transform(new Point(0, 0)).X;
            subRowLeftX = checkBox.TransformToAncestor(content).Transform(new Point(0, 0)).X;
        });

        AssertRendered(result);
        Assert.True(found, "the recycle bin card's age-filter checkbox and number box must both render.");
        Assert.True(isChecked, "the age filter defaults on (30 days).");
        Assert.True(numberBoxEnabled, "the number box must be enabled while the filter is on.");
        Assert.True(subRowLeftX > cardLeftX, "the sub-row must be indented past the card's own left edge.");
    }

    /// <summary>The recycle bin age box has to leave room for its digits: the largest allowed value
    /// (3650) must fit the control's text host, which the spin and clear buttons and the padding used to
    /// squeeze to nothing at a fixed 70 px.</summary>
    [Fact]
    public void Cleanup_recycle_bin_age_box_leaves_room_for_its_digits()
    {
        RenderResult? result = null;
        bool found = false;
        double hostWidth = 0;
        double textWidth = double.MaxValue;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            viewModel.Items.First(i => i.IsRecycleBin).RecycleBinMinAgeDays = 3650;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-recycle-age-box-de", (int)window.Width, (int)window.Height);

            if (content is null)
                return;

            ForceExpandAllGroupCards(content);
            content.UpdateLayout();

            var numberBox = FindAllByName<Wpf.Ui.Controls.NumberBox>(content, "RecycleAgeNumberBox")
                .SingleOrDefault(nb => nb.ActualWidth > 0 && nb.ActualHeight > 0);
            if (numberBox is null)
                return;

            // WPF-UI's NumberBox derives from its own TextBox: the digits live in the template's content host.
            var host = numberBox.Template.FindName("PART_ContentHost", numberBox) as FrameworkElement;
            if (host is null)
                return;

            found = true;
            hostWidth = host.ActualWidth;
            textWidth = new System.Windows.Media.FormattedText("3650",
                System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(numberBox.FontFamily, numberBox.FontStyle, numberBox.FontWeight, numberBox.FontStretch),
                numberBox.FontSize, System.Windows.Media.Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
        });

        AssertRendered(result);
        Assert.True(found, "the recycle bin number box and its text host must render.");
        Assert.True(hostWidth >= textWidth, $"the text host is {hostWidth:0.0} px wide, the digits need {textWidth:0.0} px.");
    }

    /// <summary>
    /// The read-only component store card: the title renders and no analyze button exists (the analysis
    /// runs on its own), the size and recommendation lines stay collapsed until a
    /// result actually arrives, and once one does (set directly on the view model, exactly what the
    /// elevated reply's dispatch does after <c>ApplyComponentStoreMessage</c>) both become visible with the
    /// figures formatted through the card's own text.
    /// </summary>
    [Fact]
    public void Cleanup_component_store_card_shows_its_figures_only_once_an_analysis_has_run()
    {
        RenderResult? result = null;
        bool foundBeforeAnalysis = false;
        bool sizeVisibleBeforeAnalysis = true;
        bool recommendedVisibleBeforeAnalysis = true;
        bool sizeVisibleAfterAnalysis = false;
        bool recommendedVisibleAfterAnalysis = false;
        string sizeTextAfterAnalysis = string.Empty;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-component-store-de", (int)window.Width, (int)window.Height);

            if (content is null)
                return;

            ForceExpandAllGroupCards(content);
            content.UpdateLayout();

            var sizeTextBefore = FindAllByName<TextBlock>(content, "ComponentStoreSizeText").SingleOrDefault();
            var recommendedTextBefore = FindAllByName<TextBlock>(content, "ComponentStoreRecommendedText").SingleOrDefault();

            if (sizeTextBefore is null || recommendedTextBefore is null)
                return;

            foundBeforeAnalysis = true;
            sizeVisibleBeforeAnalysis = sizeTextBefore.ActualWidth > 0 && sizeTextBefore.ActualHeight > 0;
            recommendedVisibleBeforeAnalysis = recommendedTextBefore.ActualWidth > 0 && recommendedTextBefore.ActualHeight > 0;

            // Exactly what the elevated reply's own dispatch does: map the worker message, then assign the
            // result the bound properties read from (CleanupViewModel.ComponentStore.cs).
            viewModel.ComponentStoreResult = CleanupViewModel.ApplyComponentStoreMessage(new CleanupWorkerMessage
            {
                Type = "componentStore",
                ComponentStoreActualSizeBytes = 5L * 1024 * 1024 * 1024,
                ComponentStoreSharedWithWindowsBytes = 4L * 1024 * 1024 * 1024,
                ComponentStoreCleanupRecommended = true,
            });
            content.UpdateLayout();

            var sizeTextAfter = FindAllByName<TextBlock>(content, "ComponentStoreSizeText").Single();
            var recommendedTextAfter = FindAllByName<TextBlock>(content, "ComponentStoreRecommendedText").Single();

            sizeVisibleAfterAnalysis = sizeTextAfter.ActualWidth > 0 && sizeTextAfter.ActualHeight > 0;
            recommendedVisibleAfterAnalysis = recommendedTextAfter.ActualWidth > 0 && recommendedTextAfter.ActualHeight > 0;
            sizeTextAfterAnalysis = sizeTextAfter.Text;
        });

        AssertRendered(result);
        Assert.True(foundBeforeAnalysis, "the component store card's text lines must all render.");
        Assert.False(sizeVisibleBeforeAnalysis, "the size line must stay hidden before any analysis ran.");
        Assert.False(recommendedVisibleBeforeAnalysis, "the recommendation line must stay hidden before any analysis ran.");
        Assert.True(sizeVisibleAfterAnalysis, "the size line must show once a result has arrived.");
        Assert.True(recommendedVisibleAfterAnalysis, "the recommendation line must show once Windows recommends a cleanup.");
        Assert.Contains("5", sizeTextAfterAnalysis);
        Assert.Contains("4", sizeTextAfterAnalysis);
    }

    /// <summary>
    /// The component store analysis sits inside the component cleanup card, below its description, and no
    /// other card carries it: the figures' own card is the one whose title is the component
    /// cleanup target's, and they show up there once a result has arrived. A second row in the
    /// same Windows maintenance group is given entries so the harness expands the group for the snapshot.
    /// </summary>
    [Fact]
    public void Cleanup_component_store_analysis_renders_inside_the_component_cleanup_card()
    {
        RenderResult? result = null;
        string ownerTitle = string.Empty;
        var figuresVisible = false;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var cache = viewModel.Items.First(i => i.Target.Id == "windows-update-cache");
            cache.Bytes = 300L * 1024 * 1024;
            cache.Files = 3;
            cache.SetEntries([new CleanupEntry(@"C:\Windows\SoftwareDistribution\Download\A", "A", 300L * 1024 * 1024, 3,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache)]);

            viewModel.ComponentStoreResult = CleanupViewModel.ApplyComponentStoreMessage(new CleanupWorkerMessage
            {
                Type = "componentStore",
                ComponentStoreActualSizeBytes = 5L * 1024 * 1024 * 1024,
                ComponentStoreSharedWithWindowsBytes = 4L * 1024 * 1024 * 1024,
                ComponentStoreCleanupRecommended = true,
            });

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "cleanup-component-store-card-de", (int)window.Width, 1400);

            var size = FindAllByName<TextBlock>(content, "ComponentStoreSizeText").SingleOrDefault();
            DependencyObject? node = size;
            while (node is not null && !(node is Grid { Name: "ItemRoot" }))
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);

            if (node is Grid { DataContext: CleanupItemViewModel owner })
                ownerTitle = owner.Target.Id;

            figuresVisible = size is { ActualWidth: > 0, ActualHeight: > 0 };
        });

        AssertRendered(result);
        Assert.Equal("component-cleanup", ownerTitle);
        Assert.True(figuresVisible, "the analysis figures must show inside the card once a result exists.");
    }

    /// <summary>
    /// While the component store analysis runs, the component cleanup row's size cell shows the same
    /// measuring dots every measured row shows; no other row gets them and no analyze button exists.
    /// </summary>
    [Fact]
    public void Cleanup_component_row_shows_the_measuring_dots_while_the_analysis_runs()
    {
        RenderResult? result = null;
        var dotsOnComponentRow = false;
        var dotsElsewhere = 0;
        var buttons = -1;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var cache = viewModel.Items.First(i => i.Target.Id == "windows-update-cache");
            cache.Bytes = 300L * 1024 * 1024;
            cache.Files = 3;
            cache.SetEntries([new CleanupEntry(@"C:\Windows\SoftwareDistribution\Download\A", "A", 300L * 1024 * 1024, 3,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache)]);

            viewModel.IsAnalyzingComponentStore = true;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "cleanup-component-store-dots-de", (int)window.Width, 1400);

            buttons = FindAllByName<Wpf.Ui.Controls.Button>(content, "ComponentStoreAnalyzeButton").Count;
            foreach (var dots in FindAllByName<UltimateUpdater.Views.MeasuringDots>(content, "ComponentStoreMeasuringDots"))
            {
                if (dots.ActualWidth <= 0 || dots.ActualHeight <= 0)
                    continue;

                DependencyObject? node = dots;
                while (node is not null && !(node is Grid { Name: "ItemRoot" }))
                    node = System.Windows.Media.VisualTreeHelper.GetParent(node);

                if (node is Grid { DataContext: CleanupItemViewModel { IsComponentCleanup: true } })
                    dotsOnComponentRow = true;
                else
                    dotsElsewhere++;
            }
        });

        AssertRendered(result);
        Assert.Equal(0, buttons);
        Assert.True(dotsOnComponentRow, "the component cleanup row must show the measuring dots while the analysis runs.");
        Assert.Equal(0, dotsElsewhere);
    }

    /// <summary>
    /// The registry group header carries its own inline "backup is mandatory" note and a button
    /// to open the backup folder, visible only on the Registry group's own header (the header template is
    /// shared across every group, so only the truly-rendered Registry instance gets a real, non-zero
    /// ActualWidth/Height, the same reasoning the recycle bin age filter's own snapshot test above uses).
    /// </summary>
    [Fact]
    public void Cleanup_registry_group_header_shows_the_mandatory_backup_note()
    {
        RenderResult? result = null;
        bool found = false;
        string noteText = string.Empty;
        bool buttonVisible = false;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-registry-backup-note-de", (int)window.Width, (int)window.Height);

            if (content is null)
                return;

            content.UpdateLayout();

            var noteBlock = FindAllByName<TextBlock>(content, "RegistryBackupNoteText")
                .SingleOrDefault(tb => tb.ActualWidth > 0 && tb.ActualHeight > 0);

            if (noteBlock is null)
                return;

            var button = FindAllByName<Wpf.Ui.Controls.Button>(content, "RegistryBackupFolderButton")
                .SingleOrDefault(b => b.ActualWidth > 0 && b.ActualHeight > 0);

            found = true;
            noteText = noteBlock.Text;
            buttonVisible = button is { ActualWidth: > 0, ActualHeight: > 0 };
        });

        AssertRendered(result);
        Assert.True(found, "the registry group's own backup note must render.");
        Assert.Contains("5", noteText);
        Assert.True(buttonVisible, "the button to open the backup folder must render alongside the note.");
    }

    /// <summary>A category card whose Body stays Visibility="Collapsed" is never measured, so its
    /// ContentPresenter never materializes the items inside, unlike WpfHarness.ExpandCategoryGroups
    /// (which only opens a card when a contained item has entries), this opens every one so a "nothing
    /// has entries" render still has rows to check.</summary>
    private static void ForceExpandAllGroupCards(DependencyObject root)
    {
        if (root is Expander { DataContext: CleanupGroupViewModel } groupCard)
            groupCard.IsExpanded = true;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ForceExpandAllGroupCards(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>For every target card, the checkbox's Y-centre must land on the title
    /// text's Y-centre (1px tolerance), checked as geometry, not by eyeballing the
    /// image. Shared by the collapsed and expanded variants below.</summary>
    private static void AssertCheckboxCentersOnTitle(FrameworkElement content)
    {
        var roots = FindAllByName<Grid>(content, "ItemRoot");
        Assert.True(roots.Count > 0, "No target cards found to check.");

        foreach (var root in roots)
        {
            var checkBox = FindByName<CheckBox>(root, "ItemCheckBox");
            var title = FindByName<TextBlock>(root, "ItemTitle");
            Assert.NotNull(checkBox);
            Assert.NotNull(title);

            var checkBoxMidY = checkBox!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + checkBox.ActualHeight / 2;
            var titleMidY = title!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + title.ActualHeight / 2;

            Assert.True(Math.Abs(checkBoxMidY - titleMidY) <= 1,
                $"Checkbox centre {checkBoxMidY:0.0} vs title centre {titleMidY:0.0} for '{title.Text}'.");
        }
    }

    [Fact]
    public void Cleanup_item_checkbox_stays_centered_on_title_when_collapsed()
    {
        RenderResult? result = null;
        FrameworkElement? content = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-checkbox-align-collapsed-de", (int)window.Width, (int)window.Height);

            // No item has entries here, so the harness's own auto-expand never opens a single category
            // card (its condition is "a contained item has entries") and no row would be realized at
            // all, force every category card open to reach the actually-common case: rows visible with
            // their OWN details expander absent/collapsed.
            ForceExpandAllGroupCards(content!);
            content!.UpdateLayout();

            // Geometry reads (TransformToAncestor, .Name) need the element's own thread, must run here,
            // not after OnUiThread returns.
            AssertCheckboxCentersOnTitle(content!);
        });

        AssertRendered(result);
    }

    [Fact]
    public void Cleanup_item_checkbox_stays_centered_on_title_when_expanded()
    {
        RenderResult? result = null;
        FrameworkElement? content = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            // Everything that can grow row 1 tall at once: entries, a leftover-install warning and
            // its candidate list, the row-0 alignment must survive all of it.
            var directoriesTarget = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            directoriesTarget.Bytes = 500L * 1024 * 1024;
            directoriesTarget.Files = 500;
            directoriesTarget.SetEntries(Enumerable.Range(0, 10).Select(i =>
                new CleanupEntry(@$"C:\Temp\App{i}", $"App{i}", 10L * 1024 * 1024, 10,
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache)));

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-checkbox-align-expanded-de", (int)window.Width, (int)window.Height);

            AssertCheckboxCentersOnTitle(content!);
        });

        AssertRendered(result);
    }

    /// <summary>
    /// Every group expanded plus real sizes on every item, rendered at the window's own minimum height, so
    /// the targets card's outer list genuinely has to scroll (proven by <c>ScrollableHeight &gt; 0</c>
    /// below, not just assumed). `CleanupWindow.xaml`'s own comment on the sibling `EntriesScroll` describes
    /// measuring WPF-UI's overlay scrollbar style by exactly this signature,
    /// <c>ScrollViewer.ViewportWidth == ActualWidth</c> even while overflowing, versus a genuinely reserving
    /// `ScrollViewer` narrowing its own viewport by the bar's width. This harness could not reproduce that
    /// distinction either way (`ViewportWidth &lt; ActualWidth` held here regardless of whether the
    /// `Style="{x:Null}"` fix on the outer `ScrollViewer` was present), so this asserts the invariant the fix
    /// is meant to guarantee rather than the specific regression; the fix itself follows the same measured,
    /// already-shipped pattern the sibling `EntriesItemsScroll` fix in this file uses. Real confirmation
    /// needs a VM.
    /// </summary>
    [Fact]
    public void Cleanup_window_targets_scrollbar_reserves_its_own_column()
    {
        RenderResult? result = null;
        double viewportWidth = 0;
        double actualWidth = 0;
        double scrollableHeight = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);

            long size = 96L * 1024 * 1024;
            var index = 0;
            foreach (var item in viewModel.Items)
            {
                item.IsScanning = false;
                item.Bytes = size / (index + 1);
                item.Files = 12 * (index + 1);
                index++;
            }

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            // The window's own minimum, well short of what six expanded groups need.
            result = WpfHarness.RenderWindow(window, "cleanup-targets-scrollbar", (int)window.MinWidth, (int)window.MinHeight);

            foreach (var card in FindAllByName<Expander>(content!, "GroupCard"))
                card.IsExpanded = true;
            content!.UpdateLayout();

            var targetsCard = FindByName<Border>(content!, "CleanupTargetsCard");
            var scrollViewer = targetsCard is null ? null : FindFirstDescendant<ScrollViewer>(targetsCard);
            if (scrollViewer is not null)
            {
                viewportWidth = scrollViewer.ViewportWidth;
                actualWidth = scrollViewer.ActualWidth;
                scrollableHeight = scrollViewer.ScrollableHeight;
            }
        });

        AssertRendered(result);
        Assert.True(scrollableHeight > 0, "expected the targets list to actually need to scroll at the window's minimum height");
        Assert.True(viewportWidth < actualWidth - 1,
            $"ViewportWidth ({viewportWidth:0}) should be narrower than ActualWidth ({actualWidth:0}): the " +
            "scrollbar must reserve its own column instead of floating over the content.");
    }

    /// <summary>
    /// The driver cleanup category's two lists (disconnected devices, old packages) used to
    /// float their scrollbar over the row's own right-aligned size text instead of reserving a column for
    /// it, same bug the leftover-installs list already had fixed above. 30 fabricated packages force the
    /// old-packages list past its own MaxHeight so it actually needs to scroll. Also covers the dark
    /// theme heading colors and the disconnected device row's date text, the package row's version/date
    /// line and tooltip, in the same render, so all of it can be looked at in one picture.
    /// </summary>
    [Fact]
    public void Cleanup_window_shows_driver_candidates_dark_theme()
    {
        RenderResult? result = null;
        double packageListViewportWidth = 0;
        double packageListActualWidth = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var driverItem = viewModel.Items.First(i => i.Target.Kind == CleanupKind.DriverCleanup);

            var devices = new[]
            {
                new DriverDeviceCandidateDto
                {
                    InstanceId = "USB\\VID_1234", DisplayName = "Alter Scanner", ClassName = "Image",
                    PackageName = "oem12.inf", LastConnected = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
                    LastConnectedKind = DriverLastSeenKind.Connected, Provider = "Acme", OriginalName = "acmescan.inf",
                    StorePath = @"C:\Windows\System32\DriverStore\FileRepository\acmescan.inf_amd64_1a2b3c",
                    Version = "2.5.1.0", Date = new DateTimeOffset(2025, 2, 20, 0, 0, 0, TimeSpan.Zero),
                    SizeBytes = 12L * 1024 * 1024,
                },
                new DriverDeviceCandidateDto
                {
                    InstanceId = "USB\\VID_5678", DisplayName = "Installationsdatum unbekannt", ClassName = "HIDClass",
                    PackageName = "oem13.inf", LastConnected = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero),
                    LastConnectedKind = DriverLastSeenKind.Installed, Provider = "OtherCo", OriginalName = "otherhid.inf",
                    SizeBytes = 2L * 1024 * 1024,
                },
            };

            var packages = Enumerable.Range(1, 30).Select(i => new DriverPackageCandidateDto
            {
                PackageName = $"oem{i}.inf",
                Provider = "Acme",
                ClassName = "Net",
                Version = $"{i}.0.0.0",
                Date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
                SizeBytes = (i + 1) * 4L * 1024 * 1024,
                OriginalName = "acmenet.inf",
                StorePath = $@"C:\Windows\System32\DriverStore\FileRepository\acmenet.inf_amd64_{i:x8}",
                // The last one is superseded by the first: proves the "installed now" tooltip fact renders.
                NewerVersion = i == 30 ? "30.0.0.0" : null,
                NewerDate = i == 30 ? new DateTimeOffset(2024, 1, 31, 0, 0, 0, TimeSpan.Zero) : null,
            }).ToArray();

            driverItem.SetDriverCandidates(devices, packages);

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-driver-candidates-dark-de", (int)window.Width, (int)window.Height);

            var packageScrollViewer = FindAllDescendants<ScrollViewer>(content!)
                .FirstOrDefault(sv => sv.Content is ItemsControl { ItemsSource: System.Collections.ObjectModel.ObservableCollection<DriverPackageCandidateViewModel> col }
                    && col.Count == 30);
            if (packageScrollViewer is not null)
            {
                packageListViewportWidth = packageScrollViewer.ViewportWidth;
                packageListActualWidth = packageScrollViewer.ActualWidth;
            }
        });

        AssertRendered(result);
        Assert.True(packageListActualWidth > 0, "expected to find the 30-package driver list's own ScrollViewer");
        Assert.True(packageListViewportWidth < packageListActualWidth - 1,
            $"ViewportWidth ({packageListViewportWidth:0}) should be narrower than ActualWidth ({packageListActualWidth:0}): " +
            "the driver package list's scrollbar must reserve its own column instead of floating over the size text.");
    }

    /// <summary>
    /// Both driver group headings ("Devices no longer connected", "Old driver packages")
    /// had no Foreground at all, which reads as invisible-on-invisible in the dark theme depending on
    /// what a TextBlock's ambient default happens to be. A render can be eyeballed once; this is the
    /// mechanical guard that keeps it fixed.
    /// </summary>
    [Fact]
    public void Driver_group_headings_carry_an_explicit_foreground()
    {
        var xaml = File.ReadAllText(Path.Combine(FindViewsDirectory(), "CleanupWindow.xaml"));

        foreach (var binding in new[] { "DriverGroupDisconnectedText", "DriverGroupOldPackagesText" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                xaml,
                $@"<TextBlock Text=""\{{Binding {binding}\}}""[\s\S]*?/>");

            Assert.True(match.Success, $"expected to find the {binding} TextBlock");
            Assert.Contains("Foreground=", match.Value);
        }
    }

    /// <summary>The category card's header is a ToggleButton (expand/collapse) whose
    /// content includes a tri-state CheckBox (select all). A mouse click has to stay on the checkbox,
    /// <see cref="System.Windows.Controls.Primitives.ButtonBase"/> marks its own mouse-down/up Handled,
    /// which is what stops the click from ALSO reaching the ToggleButton underneath and expanding the
    /// card the user only meant to (de)select. Proven by actually raising the routed events, not by
    /// reasoning about the template.</summary>
    [Fact]
    public void Cleanup_group_checkbox_click_does_not_also_expand_the_card()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-group-checkbox-click-de", (int)window.Width, (int)window.Height);

            var card = FindByName<Expander>(content!, "GroupCard");
            var checkBox = FindByName<CheckBox>(content!, "GroupCheckBox");
            Assert.NotNull(card);
            Assert.NotNull(checkBox);
            Assert.False(card!.IsExpanded, "Test assumes the card starts collapsed (no item has entries here).");

            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            checkBox!.RaiseEvent(down);
            Assert.True(down.Handled, "CheckBox did not mark its own mouse-down handled.");

            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            checkBox.RaiseEvent(up);
            Assert.True(up.Handled, "CheckBox did not mark its own mouse-up handled.");

            content!.UpdateLayout();
            Assert.False(card.IsExpanded, "Clicking the checkbox must not also expand the card.");
        });

        AssertRendered(result);
    }

    /// <summary>The card's ToggleButton and its header CheckBox must be reachable by Tab
    /// as two separate stops, both default to focusable/tab-stop, so this is a regression guard against
    /// either one being turned off by a future template change.</summary>
    [Fact]
    public void Cleanup_group_card_toggle_and_checkbox_are_both_separately_tabbable()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = window.Content as FrameworkElement;
            result = WpfHarness.RenderWindow(window, "cleanup-group-card-tab-de", (int)window.Width, (int)window.Height);

            var card = FindByName<Expander>(content!, "GroupCard");
            var checkBox = FindByName<CheckBox>(content!, "GroupCheckBox");
            Assert.NotNull(card);
            Assert.NotNull(checkBox);

            var toggle = FindFirstToggleButton(card!);
            Assert.NotNull(toggle);
            Assert.True(toggle!.Focusable && toggle.IsTabStop, "The card's own toggle button must stay a tab stop.");
            Assert.True(checkBox!.Focusable && checkBox.IsTabStop, "The header checkbox must stay a tab stop.");
            Assert.NotSame(toggle, checkBox);
        });

        AssertRendered(result);
    }

    private static ToggleButton? FindFirstToggleButton(DependencyObject root)
    {
        if (root is ToggleButton tb && root is not CheckBox)
            return tb;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindFirstToggleButton(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Covers light AND dark, the group card's own template is exactly
    /// what a resource missing from one theme's dictionary would show up as an invisible border or
    /// mismatched background on, which the dark-only snapshots above cannot see.
    /// </summary>
    [Fact]
    public void Cleanup_window_card_expanders_render_in_the_light_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            target.Bytes = 340L * 1024 * 1024;
            target.Files = 812;
            target.SetEntries(
                [
                    new CleanupEntry(@"C:\Temp\Spotify", "Spotify", 210L * 1024 * 1024, 640,
                        new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache),
                ],
                omittedCount: 27, omittedBytes: 40L * 1024 * 1024);

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            result = WpfHarness.RenderWindow(window, "cleanup-entries-light-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
    }

    // LeftoverInstalls has no render test here: its catalogue entry stays disabled (the underlying
    // heuristic is unsafe on a real machine), so a snapshot for a row nothing can reach would prove a
    // layout users never see.

    /// <summary>
    /// A target with mixed entry types shows both risk dot colours on the same list (a cache
    /// row stays the target's own safe green, a crash-reports row reads considered amber even though the
    /// target itself is safe), plus the one-line legend above the list, none of it pushing the row's own
    /// content out from under the scrollbar. German, dark (the suite's default theme).
    /// </summary>
    [Fact]
    public void Cleanup_details_list_shows_mixed_risk_entries_with_the_legend()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            target.Bytes = 220L * 1024 * 1024;
            target.Files = 640;
            target.SetEntries(
                [
                    new CleanupEntry(@"C:\Temp\Spotify", "Spotify", 180L * 1024 * 1024, 600,
                        new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache),
                    new CleanupEntry(@"C:\Temp\CrashDumps", "CrashDumps", 30L * 1024 * 1024, 30,
                        new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.CrashReports),
                    new CleanupEntry(@"C:\Temp\SetupLogs", "SetupLogs", 8L * 1024 * 1024, 8,
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Logs),
                    new CleanupEntry(@"C:\Temp\OldInstaller", "OldInstaller", 2L * 1024 * 1024, 2,
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.InstallLeftovers),
                ]);

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            result = WpfHarness.RenderWindow(window, "cleanup-entries-mixed-risk-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>Same case as above, the light theme AND English, so a colour resource missing from either
    /// combination would still show up.</summary>
    [Fact]
    public void Cleanup_details_list_shows_mixed_risk_entries_with_the_legend_in_the_light_theme_english()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            ApplyTheme("light");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            foreach (var item in viewModel.Items)
                item.IsScanning = false;

            var target = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
            target.Bytes = 220L * 1024 * 1024;
            target.Files = 640;
            target.SetEntries(
                [
                    new CleanupEntry(@"C:\Temp\Spotify", "Spotify", 180L * 1024 * 1024, 600,
                        new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.Cache),
                    new CleanupEntry(@"C:\Temp\CrashDumps", "CrashDumps", 30L * 1024 * 1024, 30,
                        new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), CleanupEntryType.CrashReports),
                ]);

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            result = WpfHarness.RenderWindow(window, "cleanup-entries-mixed-risk-light-en", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);

        // put the harness back so the remaining snapshots are not rendered half-light
        WpfHarness.OnUiThread(() =>
        {
            ApplyTheme("dark");
            LocalizationService.Instance.Apply("de");
        });
    }

    /// <summary>Earlier cleanups, pulled out of the target list into their own window, empty
    /// state, same convention HistoryWindow's own empty state uses.</summary>
    [Fact]
    public void CleanupHistoryWindow_when_empty_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            var window = new CleanupHistoryWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "cleanup-history-empty-de", (int)window.Width, (int)window.MaxHeight);
        });

        AssertRendered(result);
    }

    /// <summary>Three entries used to render inside a window fixed at 700px regardless of how
    /// little history there actually was. Same shrink-to-content measurement as the other message windows.</summary>
    [Fact]
    public void CleanupHistoryWindow_shrinks_to_fit_a_short_list()
    {
        RenderResult? result = null;
        double renderedHeight = 0, listBottom = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 7, 21, 40, 0, TimeSpan.Zero),
                FreedBytes = 620L * 1024 * 1024,
                Areas = [new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 400L * 1024 * 1024 }],
            }));
            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 5, 9, 12, 0, TimeSpan.Zero),
                FreedBytes = 40L * 1024 * 1024,
                Stopped = true,
                Areas = [new CleanupHistoryArea { Id = "windows-update-cache", TitleKey = "CleanupUpdateCache", FreedBytes = 40L * 1024 * 1024 }],
            }));
            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
                FreedBytes = 12L * 1024 * 1024,
                Areas = [new CleanupHistoryArea { Id = "thumbnail-cache", TitleKey = "CleanupThumbnails", FreedBytes = 12L * 1024 * 1024 }],
            }));

            var window = new CleanupHistoryWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);

            result = WpfHarness.RenderWindow(window, "cleanup-history-shrinks-short-de", (int)window.Width, (int)renderedHeight);

            var list = FindByName<ScrollViewer>(content, "CleanupHistoryList");
            Assert.NotNull(list);
            listBottom = list!.TransformToAncestor(content).Transform(new Point(0, 0)).Y + list.ActualHeight;
        });

        AssertRendered(result);
        Assert.True(renderedHeight < 700,
            $"Three entries rendered at {renderedHeight:0}px, expected below the old fixed 700px height.");
        Assert.True(listBottom <= renderedHeight + 0.5,
            $"List bottom {listBottom:0.0} exceeds the rendered height {renderedHeight:0}, it is clipped.");
    }

    /// <summary>The empty state used to sit inside the same fixed 700px window as a full list.
    /// Shrunk down, its icon and text must still land centred in whatever height remains.</summary>
    [Fact]
    public void CleanupHistoryWindow_shrinks_to_fit_the_empty_state()
    {
        RenderResult? result = null;
        double renderedHeight = 0, stateCenterY = 0, stateBottom = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            // Its own empty history: other tests in the run leave entries in the shared test data folder.
            var history = new CleanupHistoryService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-cleanup-history-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings, history: history);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            var window = new CleanupHistoryWindow(viewModel);
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(window.Width, double.PositiveInfinity));
            renderedHeight = Math.Clamp(content.DesiredSize.Height, window.MinHeight, window.MaxHeight);

            result = WpfHarness.RenderWindow(window, "cleanup-history-shrinks-empty-de", (int)window.Width, (int)renderedHeight);

            var emptyState = FindByName<StackPanel>(content, "CleanupHistoryEmptyState");
            Assert.NotNull(emptyState);
            var top = emptyState!.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            stateCenterY = top + emptyState.ActualHeight / 2;
            stateBottom = top + emptyState.ActualHeight;
        });

        AssertRendered(result);
        Assert.True(renderedHeight < 700,
            $"Empty state rendered at {renderedHeight:0}px, expected below the old fixed 700px height.");
        Assert.True(stateBottom <= renderedHeight + 0.5,
            $"Empty state bottom {stateBottom:0.0} exceeds the rendered height {renderedHeight:0}, it is clipped.");
        Assert.True(Math.Abs(stateCenterY - renderedHeight / 2) <= renderedHeight * 0.1,
            $"Empty state centre {stateCenterY:0.0} is not centred in the rendered height {renderedHeight:0}.");
    }

    /// <summary>The filled state: a normal run and a stopped one, each with its own areas,
    /// exactly the CleanupHistoryRowViewModel shape the window shares with the cleanup dialog itself.
    /// English is checked here too, nothing rendered this window in that language before.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void CleanupHistoryWindow_with_entries_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 7, 21, 40, 0, TimeSpan.Zero),
                FreedBytes = 620L * 1024 * 1024,
                SkippedFiles = 4,
                SkippedBytes = 12L * 1024 * 1024,
                Stopped = false,
                Areas =
                [
                    new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 400L * 1024 * 1024 },
                    new CleanupHistoryArea
                    {
                        Id = "thumbnail-cache", TitleKey = "CleanupThumbnails",
                        FreedBytes = 220L * 1024 * 1024, SkippedBytes = 12L * 1024 * 1024,
                    },
                ],
            }));
            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 5, 9, 12, 0, TimeSpan.Zero),
                FreedBytes = 40L * 1024 * 1024,
                Stopped = true,
                Areas = [new CleanupHistoryArea { Id = "windows-update-cache", TitleKey = "CleanupUpdateCache", FreedBytes = 40L * 1024 * 1024 }],
            }));

            var window = new CleanupHistoryWindow(viewModel);
            result = WpfHarness.RenderWindow(window, $"cleanup-history-{language}", (int)window.Width, (int)window.MaxHeight);
        });

        AssertRendered(result);
    }

    /// <summary>The window's own MinWidth/MinHeight (560x420), never exercised before, and the row
    /// carries a full timestamp, a freed-size figure and an area list that could all clip first here.</summary>
    [Fact]
    public void CleanupHistoryWindow_at_its_minimum_width_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 7, 21, 40, 0, TimeSpan.Zero),
                FreedBytes = 620L * 1024 * 1024,
                SkippedFiles = 4,
                SkippedBytes = 12L * 1024 * 1024,
                Areas =
                [
                    new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 400L * 1024 * 1024 },
                    new CleanupHistoryArea
                    {
                        Id = "thumbnail-cache", TitleKey = "CleanupThumbnails",
                        FreedBytes = 220L * 1024 * 1024, SkippedBytes = 12L * 1024 * 1024,
                    },
                ],
            }));

            var window = new CleanupHistoryWindow(viewModel);
            result = WpfHarness.RenderWindow(
                window, "cleanup-history-narrow-de", (int)window.MinWidth, (int)window.MinHeight);
        });

        AssertRendered(result);
    }

    /// <summary>The light theme is never checked for this window otherwise, same resource-swap
    /// trick <see cref="ApplyTheme"/> uses for every other secondary window in this suite.</summary>
    [Fact]
    public void CleanupHistoryWindow_in_the_light_theme_renders()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("light");

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 5, 9, 12, 0, TimeSpan.Zero),
                FreedBytes = 40L * 1024 * 1024,
                Stopped = true,
                Areas = [new CleanupHistoryArea { Id = "windows-update-cache", TitleKey = "CleanupUpdateCache", FreedBytes = 40L * 1024 * 1024 }],
            }));

            var window = new CleanupHistoryWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "cleanup-history-light-de", (int)window.Width, (int)window.MaxHeight);
        });

        AssertRendered(result);

        WpfHarness.OnUiThread(() => ApplyTheme("dark"));
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

    /// <summary>A catalogue that failed to build says so in the caution colour (a recoverable state, not a
    /// destructive one) and the longer message still wraps inside its column in the longest locales.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ro")]
    public void Cleanup_window_catalogue_failure_uses_the_caution_brush_and_fits(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(
                new UltimateUpdater.Services.Cleanup.CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);
            viewModel.CatalogLoadFailed = true;

            var window = new CleanupWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"cleanup-catalog-failed-{language}", (int)window.Width, (int)window.Height);

            var text = FindByName<TextBlock>(content, "CatalogLoadFailedText");
            Assert.NotNull(text);
            Assert.Equal(LocalizationService.Instance["CleanupCatalogLoadFailed"], text!.Text);
            Assert.Same(window.FindResource("SystemFillColorCautionBrush"), text.Foreground);
            Assert.True(text.ActualWidth <= 360.5, $"message is {text.ActualWidth:0.0} wide");
        });

        AssertRendered(result);
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

    /// <summary>
    /// Sanity render of one of this session's new InstallErrorExplainer summary lines, the longest
    /// combined What+Now pair added, to confirm the explanation Border (TextWrapping="Wrap", no fixed
    /// width) actually wraps a long German sentence instead of overflowing the window.
    /// </summary>
    [Fact]
    public void Error_detail_window_wraps_a_long_new_german_explanation()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var detail = "winget: refusing to install 'some.package.id', that is not a plain package id.";
            var viewModel = new ErrorDetailViewModel("Some Package", "winget", "1.0 → 1.1", detail, new LoggingService(),
                InstallErrorExplainer.Describe("winget", detail, key => LocalizationService.Instance[key]));
            var window = new ErrorDetailWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "error-detail-long-explanation-de", (int)window.Width, (int)window.MaxHeight);
        });

        AssertRendered(result);
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

    private static void AssertRendered(RenderResult? result)
    {
        Assert.NotNull(result);
        Assert.True(File.Exists(result.Path), $"No image was written to {result.Path}.");
        Assert.True(
            result.DistinctColours >= MinimumDistinctColours,
            $"{Path.GetFileName(result.Path)} came out effectively blank, only {result.DistinctColours} distinct colours.");
    }

    /// <summary>
    /// Routes through <see cref="WpfHarness.ApplyTheme"/> rather than a bare <c>new ThemeService().Apply</c>,
    /// so a fresh instance's own high-contrast cleanup (<c>ApplyCore</c>'s
    /// <c>RemoveHighContrastSurfaceOverrides</c>) still runs: <c>_highContrastSurfaceKeysApplied</c> lives
    /// on the instance and is always false on a brand-new one, so a leak from an earlier high-contrast test
    /// in the same run would otherwise survive into these renders unnoticed.
    /// </summary>
    private static void ApplyTheme(string name) =>
        WpfHarness.ApplyTheme(name == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light);

    internal static (MainViewModel ViewModel, ThemeService Theme, SettingsService Settings) CreateMainViewModel(string language)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging);
        settings.Current.Ui.Language = language;
        settings.Current.Ui.WindowPlacement = null;

        var theme = new ThemeService();
        var sources = MockUpdateSource.CreateAll();
        var history = new HistoryService(logging);

        var viewModel = new MainViewModel(
            settings,
            logging,
            theme,
            new ScanOrchestrator(sources, logging),
            new IconCacheService(logging),
            new InstallQueue(sources, settings, history, logging),
            history,
            new AutostartService(logging),
            new NotificationService(settings, logging),
            new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)),
            sources);

        return (viewModel, theme, settings);
    }

    /// <summary>
    /// One row per shape the table has to cope with: an ordinary update, a critical one, a very long
    /// name, a Tier C row with no versions at all, and a row in the middle of installing.
    /// </summary>
    private static void Populate(MainViewModel viewModel)
    {
        var rows = new List<UpdateRowViewModel>
        {
            new(Item("Mozilla.Firefox", "Mozilla Firefox", "winget", UpdateCategory.Software, "128.0.3", "129.0.1")),
            new(Item("Microsoft.VisualStudioCode",
                "Microsoft Visual Studio Code, Systeminstallation für alle Benutzerkonten",
                "winget", UpdateCategory.Software, "1.92.0", "1.93.1")),
            new(Item("KB5041585", "2024-08 Kumulatives Update für Windows 11 Version 23H2 (KB5041585)",
                "windowsupdate", UpdateCategory.SystemAndDrivers, null, "1.0", critical: true,
                published: new DateTimeOffset(2024, 8, 13, 0, 0, 0, TimeSpan.Zero))),
            new(Item("eslint", "eslint", "npm", UpdateCategory.DeveloperTools, "9.8.0", "9.9.1")),
            // Steam is a launcher row that carries its pending game updates as a collapsible sub-list
            new(Item("steam", "Steam", "steam", UpdateCategory.Games, null, null,
                open: "steam://open/downloads",
                children:
                [
                    Item("730", "Counter-Strike 2", "steam", UpdateCategory.Games, "1.41.2.0", "1.41.3.0",
                        open: "steam://open/downloads",
                        published: new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero)),
                    Item("440", "Team Fortress 2", "steam", UpdateCategory.Games, "12345", "12999",
                        open: "steam://open/downloads",
                        published: new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero)),
                ])),
            new(Item("epic", "Epic Games Launcher", "epic", UpdateCategory.Games, null, null,
                open: @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")),
            new(Item("msstore", "Microsoft Store & Xbox", "msstore", UpdateCategory.Store, null, null,
                open: "ms-windows-store://downloadsandupdates")),
        };

        rows[2].IsSelected = true;
        rows[0].BeginInstall();
        rows[0].ApplyInstallStatus(new InstallItemStatus(
            rows[0].Item.SourceId, "Mozilla.Firefox", 62, "Wird heruntergeladen…", null, null));

        viewModel.Updates.AddRange(rows);

        AddStatus(viewModel, "winget", ProviderScanState.Done, 2);
        AddStatus(viewModel, "chocolatey", ProviderScanState.Error, 0, "choco outdated returned nothing usable");
        AddStatus(viewModel, "windowsupdate", ProviderScanState.Done, 1, category: UpdateCategory.SystemAndDrivers);
        AddStatus(viewModel, "npm", ProviderScanState.Done, 1, category: UpdateCategory.DeveloperTools);

        // Stores, launchers and Steam get NO pill at all any more, they have their own
        // "Manual update" category in the table, and the pill row is for the sources that install something.
        // Deliberately nothing added here: it keeps the snapshot honest about what the app really shows.

        foreach (var quiet in new[] { "scoop", "pip", "dotnet-tool", "psgallery" })
            AddStatus(viewModel, quiet, ProviderScanState.Done, 0);
    }

    private static void AddStatus(
        MainViewModel viewModel, string sourceId, ProviderScanState state, int found, string? error = null,
        UpdateCategory category = UpdateCategory.Software, SourceTier tier = SourceTier.A)
    {
        var vm = new ProviderStatusViewModel(sourceId, category, tier);
        vm.Apply(new ProviderStatus { SourceId = sourceId, State = state, FoundCount = found, ErrorMessage = error });
        viewModel.ProviderStatuses.Add(vm);
    }

    private static UpdateItem Item(
        string id, string name, string sourceId, UpdateCategory category,
        string? installed, string? available, bool critical = false, string? open = null,
        IReadOnlyList<UpdateItem>? children = null, DateTimeOffset? published = null) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = category,
        Name = name,
        InstalledVersion = installed,
        AvailableVersion = available,
        IsCritical = critical,
        OpenTarget = open,
        Tier = open is null ? SourceTier.A : SourceTier.C,
        Children = children ?? [],
        PublishDate = published,
    };

    private static List<HistoryEntry> HistoryEntries() =>
    [
        new()
        {
            Timestamp = new DateTimeOffset(2026, 7, 19, 21, 14, 0, TimeSpan.Zero),
            SourceId = "winget", ItemId = "Mozilla.Firefox", Name = "Mozilla Firefox",
            FromVersion = "128.0.3", ToVersion = "129.0.1", Success = true,
        },
        new()
        {
            Timestamp = new DateTimeOffset(2026, 7, 19, 21, 15, 0, TimeSpan.Zero),
            SourceId = "chocolatey", ItemId = "nodejs", Name = "Node.js",
            FromVersion = "20.15.1", ToVersion = "22.6.0", Success = false,
            Error = "Installation fehlgeschlagen. Einzelheiten stehen im Protokoll.",
        },
    ];

    /// <summary>Uses the real service against a freshly emptied history file, so the read path is exercised too.</summary>
    private static HistoryService HistoryWith(List<HistoryEntry> entries)
    {
        // EffectivePath, not the real AppPaths.DataDirectory file directly: WpfHarness redirects every
        // explicit-path-less HistoryService in this run to one shared temp file, and clearing the real,
        // unused one would leave that shared file's entries from an earlier test still in place here.
        var path = HistoryService.EffectivePath;
        if (File.Exists(path))
            File.Delete(path);

        var history = new HistoryService(new LoggingService());
        foreach (var entry in entries)
            history.Append(entry);

        return history;
    }

}
