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

public partial class WindowLayoutTests
{
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
    /// The hard-disk cleaning symbol (drive body, platter ring and actuator arm, 108 of a 140px
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
                        @"C:\Users\User\Videos\Urlaub\Urlaubsvideo 2026.mp4"),
                    new CleanupEntry(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RD4E5F6.docx",
                        "Bewerbung.docx", 180L * 1024, 1, deleted, deleted, CleanupEntryType.Other,
                        @"C:\Users\User\Dokumente\Bewerbung.docx"),
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
        var xaml = ViewSources.ReadWindow(FindViewsDirectory(), "CleanupWindow.xaml");

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

    /// <summary>The overview card with a pie over eight areas (six slices and "other"), in both languages.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void CleanupHistoryWindow_renders_the_overview_pie(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var history = new CleanupHistoryService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-cleanup-history-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings, history: history);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            string[][] areas =
            [
                ["user-temp", "CleanupUserTemp", "900"], ["windows-update-cache", "CleanupUpdateCache", "600"],
                ["thumbnail-cache", "CleanupThumbnails", "300"], ["recycle-bin", "CleanupRecycleBin", "200"],
                ["browser-cache", "CleanupShaderCache", "120"], ["crash-dumps", "CleanupCrashDumps", "80"],
                ["windows-logs", "CleanupWindowsLogs", "40"], ["system-temp", "CleanupWindowsTemp", "20"],
            ];
            for (var run = 0; run < 3; run++)
            {
                var runAreas = areas.Where((_, i) => i % 3 != run).Select(a => new CleanupHistoryArea
                {
                    Id = a[0], TitleKey = a[1], FreedBytes = long.Parse(a[2]) * 1024 * 1024,
                }).ToList();
                if (run == 0)
                    runAreas.Add(new CleanupHistoryArea { Id = "system-repair-sfc", TitleKey = "CleanupSystemRepairSfc", RepairResult = RepairResult.Repaired });
                if (run == 1)
                    runAreas.Add(new CleanupHistoryArea { Id = "system-repair-dism", TitleKey = "CleanupSystemRepairDism", RepairResult = RepairResult.ProblemsFound });
                viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
                {
                    Timestamp = new DateTimeOffset(2026, 8, 7 - run * 2, 21, 40, 0, TimeSpan.Zero),
                    FreedBytes = runAreas.Sum(a => a.FreedBytes),
                    Areas = runAreas,
                }));
            }

            var window = new CleanupHistoryWindow(viewModel);
            result = WpfHarness.RenderWindow(window, $"cleanup-history-pie-{language}", (int)window.Width, 560);
        });

        AssertRendered(result);
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
}
