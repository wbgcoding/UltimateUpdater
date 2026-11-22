using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Views.History;
using UltimateUpdater.Views.Uninstall;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The history row of removed leftovers offers "Restore leftovers" in the shared trailing button column: the
    /// button is visible on that row only, has a size, and sits inside the row's last column without covering the
    /// result text next to it.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void History_row_of_removed_leftovers_shows_the_restore_button(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            ApplyTheme("dark");

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var entries = new List<HistoryEntry>
            {
                new()
                {
                    Timestamp = DateTimeOffset.Now.AddMinutes(-2),
                    SourceId = "uninstaller", ItemId = "Some.Program", Name = "Some Program",
                    Action = "leftovers", Success = true, SizeBytes = 48_000_000,
                    QuarantineUser = stamp, QuarantineMachine = stamp,
                },
                new()
                {
                    Timestamp = DateTimeOffset.Now.AddMinutes(-3),
                    SourceId = "uninstaller", ItemId = "Some.Program", Name = "Some Program",
                    Action = "uninstall", Success = true,
                },
            };

            var viewModel = new HistoryViewModel(HistoryWith(entries), onRestoreLeftovers: _ => { });
            var window = new HistoryWindow(viewModel, new LoggingService());
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"history-restore-leftovers-{language}", 820, 520);

            Assert.Equal(1, viewModel.Entries.Count(row => row.CanRestoreLeftovers));

            var buttons = FindAllByName<Button>(content, "HistoryRestoreLeftoversButton");
            Assert.Equal(2, buttons.Count);
            var visible = Assert.Single(buttons, button => button.Visibility == Visibility.Visible);
            Assert.True(visible.ActualWidth > 0 && visible.ActualHeight > 0, "the restore button was not laid out");

            // Its row's result text ends before the button starts.
            var row = NamedAncestor<Grid>(visible, "HistoryRow");
            Assert.NotNull(row);
            var resultText = FindByName<FrameworkElement>(row!, "HistoryResultText");
            Assert.NotNull(resultText);
            var buttonLeft = visible.TransformToAncestor(row!).Transform(new Point(0, 0)).X;
            var textRight = resultText!.TransformToAncestor(row!).Transform(new Point(resultText.ActualWidth, 0)).X;
            Assert.True(textRight <= buttonLeft + 0.5, $"the result text ({textRight}) overlaps the restore button ({buttonLeft})");
        });

        AssertRendered(result);
    }

    /// <summary>The uninstaller window's note that removed leftovers can be restored, laid out in both languages.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Uninstaller_window_shows_the_restorable_leftovers_line(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            ApplyTheme("dark");

            var viewModel = CreateUninstallerViewModel();
            viewModel.LeftoversRestorable = true;
            var window = new UninstallerWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"uninstaller-restorable-{language}", (int)window.MinWidth, (int)window.MinHeight);

            var line = FindByName<FrameworkElement>(content, "RestorableLine");
            Assert.NotNull(line);
            Assert.True(line!.ActualWidth > 0 && line.ActualHeight > 0, "the restorable line was not laid out");
        });

        AssertRendered(result);
    }

    private static T? NamedAncestor<T>(DependencyObject start, string name) where T : FrameworkElement
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T element && element.Name == name)
                return element;
        }

        return null;
    }
}
