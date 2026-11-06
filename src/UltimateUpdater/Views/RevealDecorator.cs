using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Size = System.Windows.Size;

namespace UltimateUpdater.Views;

/// <summary>
/// The body of a settings card: opens and closes by growing and shrinking its own layout height, one
/// eased move of the same length in both directions, with the content fading along.
///
/// WPF-UI's stock Expander only slides the body with a render transform and leaves its layout height at
/// the full size until a timer flips it to Collapsed: opening looks fine (the slide covers the sudden
/// growth), closing slides the body away and then drops everything below it by the whole body height in
/// one frame, and the scroll viewer clamps its offset in that same frame. Animating the real height
/// removes that last step. The child is measured once at its natural height and only the reported height
/// is scaled, so a frame costs an arrange of the siblings, not a measure of the card's content.
/// </summary>
internal sealed class RevealDecorator : Decorator
{
    /// <summary>Both directions take this long for the full distance; a reversal part-way takes
    /// proportionally less, so the speed stays the same.</summary>
    internal static readonly TimeSpan FullDuration = TimeSpan.FromMilliseconds(170);

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(RevealDecorator), new PropertyMetadata(false, OnIsOpenChanged));

    /// <summary>0 = closed, 1 = fully open; the share of the child's natural height that is shown.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RevealDecorator),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnProgressChanged,
            (_, value) => Math.Clamp((double)value, 0.0, 1.0)));

    /// <summary>Raised (bubbling) when the move has arrived, or when there was none to make.</summary>
    public static readonly RoutedEvent SettledEvent = EventManager.RegisterRoutedEvent(
        nameof(Settled), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RevealDecorator));

    /// <summary>Lets the tests run the animated path: nothing there is ever loaded into a real window, and
    /// without a window the decorator jumps (a snapshot must not catch it half open).</summary>
    internal static bool? AnimateOutsideWindowForTest { get; set; }

    public RevealDecorator()
    {
        ClipToBounds = true;
        // Closed means not measured at all, so the content of a card nobody opened is never built.
        Visibility = Visibility.Collapsed;
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public event RoutedEventHandler Settled
    {
        add => AddHandler(SettledEvent, value);
        remove => RemoveHandler(SettledEvent, value);
    }

    /// <summary>True while a move is under way (the layout height is still changing).</summary>
    internal bool IsSettling => Math.Abs(Progress - (IsOpen ? 1.0 : 0.0)) > 0.0001;

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RevealDecorator)d).MoveTo((bool)e.NewValue);

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        // Fading is a render-only change, so it adds nothing to the layout cost of a frame.
        ((RevealDecorator)d).Opacity = (double)e.NewValue;

    private bool CanAnimate =>
        MotionPreference.AnimationsEnabled && (IsLoaded || AnimateOutsideWindowForTest == true);

    private void MoveTo(bool open)
    {
        var target = open ? 1.0 : 0.0;
        var from = Progress;

        if (!CanAnimate || Math.Abs(target - from) < 0.0001)
        {
            Arrive(open);
            return;
        }

        // Visible from the first frame of opening; Collapsed only once closing has arrived.
        Visibility = Visibility.Visible;

        var animation = new DoubleAnimation(from, target, FullDuration * Math.Abs(target - from))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // Held until Arrive swaps in the plain value, so there is no frame in between.
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            // A move that was reversed meanwhile has its own animation, which owns the arrival.
            if (IsOpen == open)
                Arrive(open);
        };
        BeginAnimation(ProgressProperty, animation);
    }

    private void Arrive(bool open)
    {
        BeginAnimation(ProgressProperty, null);
        Progress = open ? 1.0 : 0.0;
        Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        RaiseEvent(new RoutedEventArgs(SettledEvent));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
            return default;

        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Progress);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        // The child always gets its full height; the clip is what hides the part not yet revealed.
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }
}
