using System.Windows;
using System.Windows.Media.Animation;

namespace UltimateUpdater.Views;

/// <summary>
/// A bar for work that has no figure: one accent segment travels across the track. It moves only while
/// it is on screen, the window is not minimized and the system allows animations; otherwise the segment
/// rests in the middle of the track, so the row still reads as "busy" without any motion.
/// </summary>
public partial class IndeterminateBar
{
    private const double SegmentShare = 0.4;

    private Storyboard? _slide;
    private Window? _window;

    public IndeterminateBar()
    {
        InitializeComponent();

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Restart();
            else
                Stop();
        };

        SizeChanged += (_, _) => Restart();

        Loaded += (_, _) => AttachToWindow();

        Unloaded += (_, _) =>
        {
            Stop();
            DetachFromWindow();
        };
    }

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
        else
            Restart();
    }

    private void Restart()
    {
        Stop();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        Track.CornerRadius = new CornerRadius(height / 2);
        Segment.CornerRadius = new CornerRadius(height / 2);
        var segment = Math.Max(height, width * SegmentShare);
        Segment.Width = segment;

        if (!MotionPreference.AnimationsEnabled)
        {
            SegmentTransform.X = (width - segment) / 2;
            return;
        }

        SegmentTransform.X = -segment;
        if (!IsVisible || _window is { WindowState: WindowState.Minimized })
            return;

        var slide = new DoubleAnimation(-segment, width, TimeSpan.FromSeconds(1.3))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        var storyboard = new Storyboard();
        Timeline.SetDesiredFrameRate(storyboard, 60);
        Storyboard.SetTarget(slide, Segment);
        Storyboard.SetTargetProperty(slide, new PropertyPath("RenderTransform.X"));
        storyboard.Children.Add(slide);

        _slide = storyboard;
        _slide.Begin(this, isControllable: true);
    }

    private void Stop()
    {
        if (_slide is null)
            return;

        _slide.Stop(this);
        _slide.Remove(this);
        _slide = null;
    }
}
