using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace UltimateUpdater.Views;

/// <summary>
/// The one progress bar every hand-drawn bar in the app uses: a pill-shaped track (radius = height / 2)
/// and a fill that is clipped to that same pill. Two Borders with a CornerRadius cannot do this: a Border
/// clips its child to a rectangle, and a fill narrower than twice its radius gets the radius squeezed
/// flat, so a bar entering from the left showed a hard square edge.
///
/// Here the fill is drawn as a pill that starts one bar height to the LEFT of the track and ends at
/// <c>Value / Maximum * width</c>, then clipped to the track. The left edge is therefore always the
/// track's own round cap, the right edge is the fill's own round cap at every width, and a value of 0
/// shows nothing at all, a rounded nub slides in as it grows.
///
/// A moving segment (the indeterminate bar, the install strip's "still alive" shine) is the same fill
/// with a fixed length at a travelling position, so it enters and leaves round too: set
/// <see cref="SegmentLength"/> above 0 and animate <see cref="SegmentPhase"/> from 0 to 1.
///
/// The brushes default to live theme resources, so a theme switch or high contrast recolours the bar.
/// </summary>
public sealed class PillProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PillProgressBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(PillProgressBar),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentLengthProperty = DependencyProperty.Register(
        nameof(SegmentLength), typeof(double), typeof(PillProgressBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentPhaseProperty = DependencyProperty.Register(
        nameof(SegmentPhase), typeof(double), typeof(PillProgressBar),
        new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentFadeProperty = DependencyProperty.Register(
        nameof(SegmentFade), typeof(bool), typeof(PillProgressBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(PillProgressBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(PillProgressBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public PillProgressBar()
    {
        SetResourceReference(TrackBrushProperty, "ControlFillColorSecondaryBrush");
        SetResourceReference(FillBrushProperty, "AccentFillColorDefaultBrush");
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>The determinate fill, from 0 to <see cref="Maximum"/>. Ignored while a segment is set.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The <see cref="Value"/> that fills the whole track; 100 for a percentage.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Length in device-independent pixels of a travelling segment; 0 = determinate fill instead.</summary>
    public double SegmentLength
    {
        get => (double)GetValue(SegmentLengthProperty);
        set => SetValue(SegmentLengthProperty, value);
    }

    /// <summary>Where the segment is: 0 = just left of the track (hidden), 0.5 = centred, 1 = just right of it (hidden).</summary>
    public double SegmentPhase
    {
        get => (double)GetValue(SegmentPhaseProperty);
        set => SetValue(SegmentPhaseProperty, value);
    }

    /// <summary>Fades the segment out towards both ends (a soft shine instead of a solid block).</summary>
    public bool SegmentFade
    {
        get => (bool)GetValue(SegmentFadeProperty);
        set => SetValue(SegmentFadeProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>The fill's horizontal extent inside a track of the given size, or null when nothing shows.</summary>
    internal (double Left, double Right)? FillExtent(double width, double height)
    {
        double left, right;
        if (SegmentLength > 0)
        {
            left = -SegmentLength + Math.Clamp(SegmentPhase, 0d, 1d) * (width + SegmentLength);
            right = left + SegmentLength;
        }
        else
        {
            var max = Maximum > 0 ? Maximum : 100d;
            var share = Math.Clamp(Value / max, 0d, 1d);
            left = -height;
            right = share * width;
        }

        if (right <= 0 || left >= width || right <= left)
            return null;

        return (left, right);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var radius = height / 2;
        var track = new Rect(0, 0, width, height);
        drawingContext.DrawRoundedRectangle(TrackBrush, null, track, radius, radius);

        var fill = FillBrush;
        if (fill is null || FillExtent(width, height) is not { } extent)
            return;

        if (SegmentLength > 0 && SegmentFade && fill is SolidColorBrush solid)
            fill = FadedBrush(solid.Color, extent.Left, extent.Right);

        var clip = new RectangleGeometry(track, radius, radius);
        clip.Freeze();
        drawingContext.PushClip(clip);
        drawingContext.DrawRoundedRectangle(
            fill, null, new Rect(extent.Left, 0, extent.Right - extent.Left, height), radius, radius);
        drawingContext.Pop();
    }

    /// <summary>The accent colour fading in and out across the segment. Built from the theme colour itself
    /// (not an opacity mask) so the fade stays tied to the whole segment even where the track clips it.</summary>
    private static Brush FadedBrush(Color color, double left, double right)
    {
        static Color WithAlpha(Color c, byte a) => Color.FromArgb((byte)(c.A * a / 255), c.R, c.G, c.B);

        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(left, 0),
            EndPoint = new Point(right, 0),
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x00), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x99), 0.5));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x00), 1));
        brush.Freeze();
        return brush;
    }
}
