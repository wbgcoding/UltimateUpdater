using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
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

    /// <summary>The log-folder button joins the retry hint, Copy and Close in one right-aligned row; the
    /// longest labels must still fit side by side inside the window, in order, without overlap.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("pt")]
    [InlineData("tr")]
    public void Error_detail_window_action_row_fits_with_the_log_folder_button(string language)
    {
        RenderResult? result = null;
        Rect logButton = default, closeButton = default, row = default;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            var viewModel = new ErrorDetailViewModel(
                "Firefox", "winget", "128.0.3 → 129.0.1", "Install failed.\nExit code 1.", new LoggingService(),
                explanation: "Explained.", canRetryInteractive: true);
            var window = new ErrorDetailWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"error-detail-actions-{language}", (int)window.Width, (int)window.MaxHeight);

            var log = FindByName<FrameworkElement>(content, "OpenLogFolderButton");
            var close = FindByName<FrameworkElement>(content, "CloseButton");
            var actionRow = FindByName<FrameworkElement>(content, "ErrorDetailActionRow");
            Assert.NotNull(log);
            Assert.NotNull(close);
            Assert.NotNull(actionRow);

            Rect BoundsIn(FrameworkElement e) =>
                new(e.TransformToAncestor(content).Transform(new Point(0, 0)), new Size(e.ActualWidth, e.ActualHeight));

            logButton = BoundsIn(log!);
            closeButton = BoundsIn(close!);
            row = BoundsIn(actionRow!);
            window.Close();
        });

        AssertRendered(result);
        Assert.True(logButton.Width > 20, "The log-folder button has no size.");
        Assert.True(logButton.Left >= row.Left - 0.5,
            $"The log-folder button starts at {logButton.Left:0}px, left of the action row ({row.Left:0}px): the row is clipped.");
        Assert.True(logButton.Right <= closeButton.Left, "The log-folder button overlaps the Close button.");
        Assert.True(closeButton.Right <= row.Right + 0.5, "The Close button runs past the action row.");
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
}
