using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// The toolbar cleanup button's travelling light (MainWindow.xaml.cs, UpdateCleanupIconSweep) used to
/// keep animating at 30 fps forever while the window was minimised or hidden behind another (e.g.
/// close-to-tray), the same waste CleanupWindow.xaml.cs's own ApplySweepVisibility already guards
/// against for its dialog. ApplyToolbarSweepVisibility is called directly rather than through a real
/// WindowState/IsVisible change, which a window never Show()-n in a test cannot exercise faithfully.
/// </summary>
[Collection("wpf")]
public class MainWindowSweepPauseTests : IDisposable
{
    /// <summary>The sweep only runs with Windows animations on, which a build server often has off.</summary>
    public MainWindowSweepPauseTests() => MotionPreference.AnimationsEnabledForTest = true;

    public void Dispose() => MotionPreference.AnimationsEnabledForTest = null;

    [Fact]
    public void Hiding_the_window_pauses_the_toolbar_sweep_and_restoring_it_resumes()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = WindowLayoutTests.CreateMainViewModel("de");
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;

            viewModel.IsCleanupRunning = true;
            content.UpdateLayout();

            var clock = TickUntilField(window);
            Assert.NotNull(clock);
            Assert.True(TickUntil(() => (clock!.CurrentGlobalSpeed ?? 1) > 0), "the sweep never started");

            CallWithArg(window, "ApplyToolbarSweepVisibility", false);
            Assert.True(TickUntil(() => clock!.CurrentGlobalSpeed == 0), "the sweep kept running while hidden");

            CallWithArg(window, "ApplyToolbarSweepVisibility", true);
            Assert.True(
                TickUntil(() => (clock!.CurrentGlobalSpeed ?? 0) > 0),
                "restoring the window never resumed the sweep");

            viewModel.IsCleanupRunning = false;
            content.UpdateLayout();
        });
    }

    private static AnimationClock? TickUntilField(MainWindow window)
    {
        AnimationClock? clock = null;
        TickUntil(() => (clock = Field(window)) is not null);
        return clock;
    }

    private static AnimationClock? Field(MainWindow window) =>
        typeof(MainWindow)
            .GetField("_toolbarSweepClock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window) as AnimationClock;

    private static void CallWithArg<T>(MainWindow window, string method, T arg) =>
        typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [arg]);

    /// <summary>Same trick CleanupRingAnimationTests uses: nothing composites an unshown window on a
    /// test machine, so the render loop that would normally advance a Pause or a Resume never runs.</summary>
    private static bool TickUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Tick();

            if (condition())
                return true;

            Thread.Sleep(20);
        }

        return false;
    }

    private static void Tick()
    {
        var mediaContextType = typeof(System.Windows.Media.Visual).Assembly.GetType("System.Windows.Media.MediaContext")
            ?? throw new InvalidOperationException("WPF's MediaContext is no longer where it was.");
        var context = mediaContextType
            .GetMethod("From", BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, [Dispatcher.CurrentDispatcher])
            ?? throw new InvalidOperationException("WPF's MediaContext could not be reached.");
        var timeManager = context.GetType()
            .GetField("_timeManager", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(context)
            ?? throw new InvalidOperationException("WPF's TimeManager could not be reached.");

        timeManager.GetType()
            .GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(timeManager, null);
    }
}
