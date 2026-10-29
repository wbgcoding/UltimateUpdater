using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// While an install runs without a percentage, the strip shows the phase plus the elapsed time
    /// ("Installing… 12:34") where a percentage normally sits. A fixed 40 px box cut that text off in the
    /// list of running installs; it must show in full in every row, for one and for several running rows,
    /// and the bars must still start at the same x.
    /// </summary>
    [Theory]
    [InlineData("de", 1)]
    [InlineData("de", 3)]
    [InlineData("pt", 3)]
    [InlineData("nl", 3)]
    [InlineData("pl", 1)]
    public void Install_strip_phase_text_is_never_cut_off(string language, int running)
    {
        var cut = new List<string>();
        var barLefts = new List<double>();
        var texts = new List<string>();

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            var rows = viewModel.Updates.Where(r => r.Item.CanInstall).Take(running).ToList();
            foreach (var row in rows)
            {
                row.BeginInstall();
                viewModel.InstallBatch.Add(row);
                row.ApplyInstallStatus(new InstallItemStatus(row.Item.SourceId, row.Item.Id, 0, null, null, null)
                {
                    Phase = UltimateUpdater.Models.InstallPhase.Installing,
                });
            }

            viewModel.IsInstalling = true;

            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, $"main-install-phase-text-{running}-{language}", (int)window.MinWidth, (int)window.MinHeight);

            var name = running == 1 ? "InstallStripProgressText" : "RunningRowProgressText";
            foreach (var text in FindAllByName<TextBlock>(content, name).Where(t => t.ActualHeight > 0))
            {
                texts.Add(text.Text);
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // Against the layout slot, not ActualWidth: a TextBlock keeps its full width and is only
                // clipped by its slot, so its own width never shows the cut. Both sizes include the margin.
                var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(text).Width;
                if (text.DesiredSize.Width > slot + 0.5)
                    cut.Add($"'{text.Text}' needs {text.DesiredSize.Width:0.#} but has {slot:0.#}");
            }

            if (running > 1)
            {
                foreach (var card in FindAllByName<Grid>(content, "RunningRowCard"))
                {
                    var bar = FindFirstDescendant<UltimateUpdater.Views.IndeterminateBar>(card);
                    if (bar is not null)
                        barLefts.Add(bar.TransformToAncestor(content).Transform(new Point(0, 0)).X);
                }
            }
        });

        Assert.Equal(running, texts.Count);
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.True(cut.Count == 0, string.Join("; ", cut));
        if (running > 1)
        {
            Assert.Equal(running, barLefts.Count);
            Assert.All(barLefts, x => Assert.Equal(barLefts[0], x, 0.5));
        }
    }
}
