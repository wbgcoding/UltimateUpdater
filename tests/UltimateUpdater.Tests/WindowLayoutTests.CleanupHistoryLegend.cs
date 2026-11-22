using System.IO;
using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The pie legend of earlier cleanups: every percentage ends on one right edge, whatever the length of
    /// the area title before it. Each legend row used to size its own columns, so the figures zigzagged.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void CleanupHistoryWindow_legend_percentages_share_one_right_edge(string language)
    {
        List<double>? rightEdges = null;

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
                ["crash-dumps", "CleanupCrashDumps", "8"],
            ];
            viewModel.History.Add(new CleanupHistoryRowViewModel(new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 8, 7, 21, 40, 0, TimeSpan.Zero),
                FreedBytes = areas.Sum(a => long.Parse(a[2]) * 1024 * 1024),
                Areas = areas.Select(a => new CleanupHistoryArea
                {
                    Id = a[0], TitleKey = a[1], FreedBytes = long.Parse(a[2]) * 1024 * 1024,
                }).ToList(),
            }));

            var window = new CleanupHistoryWindow(viewModel);
            WpfHarness.RenderWindow(window, $"cleanup-history-legend-{language}", (int)window.Width, 560);

            var legend = (FrameworkElement)window.FindName("HistorySliceLegend");
            rightEdges = FindAllDescendants<TextBlock>(legend)
                .Where(t => t.Text.EndsWith('%'))
                .Select(t => Math.Round(t.TranslatePoint(new Point(t.ActualWidth, 0), legend).X, 1))
                .ToList();
        });

        Assert.NotNull(rightEdges);
        Assert.Equal(5, rightEdges!.Count);
        Assert.Single(rightEdges.Distinct());
    }
}
