using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    private const string LongCleanupPath =
        @"C:\Users\Someone\AppData\Local\Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache\Microsoft\Windows\INetCache\IE\Content.IE5\ABCDEFGH\very-long-file-name-that-keeps-going-1234567890.tmp";

    /// <summary>
    /// The status block beside the ring in its four faces (nothing run yet, cleaning with a long path
    /// behind a closed expander, done, stopped), in both languages. The block has a fixed height, so the
    /// header band must be exactly as tall in every face: nothing may push the list below.
    /// </summary>
    [Theory]
    [InlineData("idle", "de")]
    [InlineData("idle", "en")]
    [InlineData("running", "de")]
    [InlineData("running", "en")]
    [InlineData("done", "de")]
    [InlineData("done", "en")]
    [InlineData("stopped", "de")]
    [InlineData("stopped", "en")]
    public void Cleanup_window_status_block_fits_in_every_face(string face, string language)
    {
        RenderResult? result = null;
        double bandHeight = 0;
        double panelHeight = 0;
        var stepCount = 0;
        string headline = string.Empty;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);

            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);

            long size = 96L * 1024 * 1024;
            var index = 0;
            foreach (var item in viewModel.Items)
            {
                item.IsScanning = false;
                item.Bytes = index % 5 == 3 ? 0 : size / (index + 1);
                item.Files = index % 5 == 3 ? 0 : 12 * (index + 1);
                index++;
            }

            var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = (FrameworkElement)window.Content;

            if (face != "idle")
            {
                var areas = viewModel.Items.Where(i => i.IsSelected).Take(14).ToList();
                if (areas.Count < 14)
                    areas = viewModel.Items.Take(14).ToList();

                viewModel.StepItems = areas;
                var now = new DateTime(2026, 1, 1, 12, 0, 0);
                viewModel.Clock = () => now;
                viewModel.StartRunClock();
                now += TimeSpan.FromSeconds(95);

                for (var i = 0; i < areas.Count; i++)
                {
                    var area = areas[i];
                    area.IsSelected = true;
                    area.RunState = face == "running"
                        ? i < 6 ? CleanupTargetState.Done : i == 6 ? CleanupTargetState.Running : CleanupTargetState.Waiting
                        : face == "done" ? CleanupTargetState.Done
                        : i < 6 ? CleanupTargetState.Done : i == 6 ? CleanupTargetState.Stopped : CleanupTargetState.Skipped;
                    area.IsDone = area.RunState == CleanupTargetState.Done;
                    area.FreedBytes = area.IsDone ? area.Bytes : 0;
                }

                viewModel.FreedBytes = 1_468_006_400;

                if (face == "running")
                {
                    var running = areas[6];
                    running.IsCleaning = true;
                    running.Percent = 62;
                    running.DetailText = LongCleanupPath;
                    viewModel.CurrentItemText = running.Title + " +1";
                    viewModel.CurrentGroupKey = CleanupGroups.For(running.Target);
                    viewModel.CurrentAreaPercentText = "62 %";
                    viewModel.CurrentDetailText = LongCleanupPath;
                    viewModel.CleanPercent = 47;
                    window.DisplayPercent = 47;
                    viewModel.Phase = CleanupPhase.Cleaning;
                }
                else
                {
                    viewModel.StopRunClock();
                    viewModel.WasStopped = face == "stopped";
                    viewModel.CleanPercent = face == "done" ? 100 : 47;
                    window.DisplayPercent = viewModel.CleanPercent;
                    viewModel.Phase = CleanupPhase.Finished;
                }
            }

            result = WpfHarness.RenderWindow(window, $"cleanup-status-{face}-{language}", (int)window.Width, (int)window.Height);

            var band = FindByName<Grid>(content, "CleanupHeaderBand")!;
            bandHeight = band.ActualHeight;

            if (face != "idle")
            {
                var panel = FindByName<Grid>(content, "CleanupRunPanel")!;
                panelHeight = panel.ActualHeight;
                var bar = FindByName<ItemsControl>(content, "CleanupStepBar")!;
                stepCount = bar.Items.Count;
                headline = FindByName<TextBlock>(content, "CleanupStatusHeadline")!.Text;
            }
        });

        AssertRendered(result);

        if (face == "idle")
            return;

        Assert.Equal(14, stepCount);
        Assert.False(string.IsNullOrWhiteSpace(headline));
        Assert.Equal(206, panelHeight, 1);
        Assert.True(bandHeight >= 228 - 0.5, $"the header band is {bandHeight:F0}px high");
    }

    /// <summary>
    /// Moving through a whole run, the header band never changes its height and the list card never moves.
    /// </summary>
    [Fact]
    public void Cleanup_window_status_block_does_not_move_the_list_between_faces()
    {
        var tops = new List<double>();
        var heights = new List<double>();

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            foreach (var face in new[] { "idle", "running", "done" })
            {
                var logging = new LoggingService();
                var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
                var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);
                WpfHarness.WaitForCleanupCatalog(viewModel);
                foreach (var item in viewModel.Items)
                    item.IsScanning = false;

                if (face != "idle")
                {
                    viewModel.StepItems = viewModel.Items.Take(12).ToList();
                    viewModel.CurrentItemText = "Eine sehr lange Bezeichnung eines Bereichs, die nicht in die Zeile passt +2";
                    viewModel.CurrentDetailText = LongCleanupPath;
                    viewModel.Phase = face == "running" ? CleanupPhase.Cleaning : CleanupPhase.Finished;
                }

                var window = new CleanupWindow(viewModel) { DisplayPercent = 0 };
                var content = (FrameworkElement)window.Content;
                WpfHarness.RenderWindow(window, $"cleanup-status-stability-{face}", (int)window.Width, (int)window.Height);

                var card = FindByName<Border>(content, "CleanupTargetsCard")!;
                var band = FindByName<Grid>(content, "CleanupHeaderBand")!;
                tops.Add(card.TransformToAncestor(content).Transform(new Point(0, 0)).Y);
                heights.Add(band.ActualHeight);
            }
        });

        Assert.All(tops, t => Assert.Equal(tops[0], t, 0.5));
        Assert.All(heights, h => Assert.Equal(heights[0], h, 0.5));
    }
}
