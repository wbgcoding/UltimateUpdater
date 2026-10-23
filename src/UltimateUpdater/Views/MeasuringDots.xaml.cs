using System.Windows;
using System.Windows.Media.Animation;

namespace UltimateUpdater.Views;

/// <summary>
/// Three dots pulsing in turn: "this row is still being measured".
/// The animation runs only while the control is actually on screen. A cleanup list holds a dozen of these
/// and every row stops measuring at a different moment; leaving their clocks running afterwards would keep
/// the composition thread busy for nothing. IsVisible stays true while the window is minimized, so the
/// owning window's state is watched as well.
/// </summary>
public partial class MeasuringDots
{
    private Storyboard? _pulse;
    private Window? _window;

    public MeasuringDots()
    {
        InitializeComponent();

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Start();
            else
                Stop();
        };

        Loaded += (_, _) => AttachToWindow();

        Unloaded += (_, _) =>
        {
            Stop();
            DetachFromWindow();
        };
    }

    /// <summary>Subscribed on Loaded and removed on Unloaded, so a recycled row never holds a handler on
    /// its window twice and never keeps one after it has left the tree.</summary>
    private void AttachToWindow()
    {
        DetachFromWindow();
        _window = Window.GetWindow(this);
        if (_window is null)
            return;

        _window.StateChanged += OnWindowStateChanged;
        OnWindowStateChanged(_window, EventArgs.Empty);
    }

    private void DetachFromWindow()
    {
        if (_window is not null)
            _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window is { WindowState: WindowState.Minimized })
            Stop();
        else if (IsVisible)
            Start();
    }

    private void Start()
    {
        if (_pulse is not null
            || _window is { WindowState: WindowState.Minimized }
            || TryFindResource("PulseStoryboard") is not Storyboard storyboard)
            return;

        _pulse = storyboard;
        _pulse.Begin(this, isControllable: true);
    }

    private void Stop()
    {
        if (_pulse is null)
            return;

        // Stop AND remove: a stopped storyboard leaves its last animated value in place, which would freeze
        // the three dots at whatever opacity they happened to be at.
        _pulse.Stop(this);
        _pulse.Remove(this);
        _pulse = null;
    }
}
