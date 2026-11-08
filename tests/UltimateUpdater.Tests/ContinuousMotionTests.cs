using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// The endlessly repeating indicators (toolbar scan icon, install bar shine, queued-row spinner, measuring
/// dots) follow the Windows "Show animations" setting like every other animation: off means the storyboard
/// never starts and the glyph stays still. The on case is asserted alongside so a trigger that never fires
/// cannot pass as "off".
/// </summary>
[Collection("wpf")]
public class ContinuousMotionTests : IDisposable
{
    public void Dispose() => MotionPreference.AnimationsEnabledForTest = null;

    private static Window Show(FrameworkElement content)
    {
        var host = new Window
        {
            Width = 400, Height = 300, ShowInTaskbar = false, Content = content,
            Style = new Style(typeof(Window)),
        };
        host.Show();
        for (var pass = 0; pass < 3; pass++)
        {
            host.UpdateLayout();
            host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        return host;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_queued_row_spinner_turns_only_with_animations_on(bool animations)
    {
        MotionPreference.AnimationsEnabledForTest = animations;

        WpfHarness.OnUiThread(() =>
        {
            var spinner = new WaitSpinner();
            var host = Show(spinner);
            try
            {
                var ring = (System.Windows.Shapes.Ellipse)spinner.Content;
                Assert.Equal(animations, ((RotateTransform)ring.RenderTransform).HasAnimatedProperties);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_measuring_dots_pulse_only_with_animations_on(bool animations)
    {
        MotionPreference.AnimationsEnabledForTest = animations;

        WpfHarness.OnUiThread(() =>
        {
            var dots = new MeasuringDots();
            var host = Show(dots);
            try
            {
                var first = (UIElement)dots.FindName("Dot1")!;
                Assert.Equal(animations, first.HasAnimatedProperties);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_scan_icon_and_the_install_shine_run_only_with_animations_on(bool animations)
    {
        MotionPreference.AnimationsEnabledForTest = animations;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = WindowLayoutTests.CreateMainViewModel("en");
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            var rotate = (RotateTransform)window.FindName("ScanButtonIconRotate")!;
            var installBar = (UIElement)window.FindName("InstallBar")!;
            window.Content = null;

            var host = new Window
            {
                Width = 1100, Height = 700, ShowInTaskbar = false, Content = content,
                DataContext = viewModel, Style = new Style(typeof(Window)),
            };
            host.Show();
            try
            {
                viewModel.IsScanning = true;
                viewModel.IsInstalling = true;
                for (var pass = 0; pass < 3; pass++)
                {
                    host.UpdateLayout();
                    host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                Assert.Equal(animations, rotate.HasAnimatedProperties);
                Assert.Equal(animations, installBar.HasAnimatedProperties);
            }
            finally
            {
                host.Close();
            }
        });
    }
}
