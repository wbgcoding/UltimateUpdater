using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace UltimateUpdater.Views;

/// <summary>
/// A translucent snapshot of the row being dragged, following the cursor, WPF's own DoDragDrop shows
/// none by default, just a plain move cursor. Rendered as a VisualBrush of the actual dragged element rather than a copy,
/// so it always matches whatever that row currently looks like (theme, hover state included).
/// </summary>
internal sealed class DragAdorner : Adorner
{
    private readonly VisualBrush _brush;
    private readonly Size _size;
    private Point _topLeft;

    public DragAdorner(UIElement adornedElement, UIElement dragged) : base(adornedElement)
    {
        var frameworkElement = (FrameworkElement)dragged;
        _size = new Size(frameworkElement.ActualWidth, frameworkElement.ActualHeight);
        _brush = new VisualBrush(dragged) { Opacity = 0.7, Stretch = Stretch.None };
        IsHitTestVisible = false;
    }

    /// <param name="topLeft">In the adorned element's own coordinate space.</param>
    public void UpdatePosition(Point topLeft)
    {
        _topLeft = topLeft;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext) =>
        drawingContext.DrawRectangle(_brush, null, new Rect(_topLeft, _size));
}
