using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UltimateUpdater.Views;

/// <summary>
/// A nested ScrollViewer swallows PreviewMouseWheel even at its own scroll limit, which stalls the
/// outer target list once the cursor sits over an open, capped detail list. Re-raises the event on
/// the parent once the inner viewer can no longer move further in the roll direction.
/// </summary>
public static class BubbleScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(BubbleScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        else viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;

        // PreviewMouseWheel tunnels OUTER viewer first. An outer viewer that cannot scroll itself (one in a
        // StackPanel never can) took the wheel here before a nested, still-scrollable list under the pointer
        // ever saw it, so that list could only be moved by dragging its scrollbar.
        if (InnerViewerCanScroll(viewer, e.OriginalSource as DependencyObject, e.Delta))
            return;

        var atTop = viewer.VerticalOffset <= 0;
        var atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight;
        if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom)) return;

        e.Handled = true;

        var parent = VisualTreeHelper.GetParent(viewer);
        while (parent is not null && parent is not UIElement) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not UIElement target) return;

        target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        });
    }

    private static bool InnerViewerCanScroll(ScrollViewer outer, DependencyObject? element, int delta)
    {
        while (element is not null && !ReferenceEquals(element, outer))
        {
            if (element is ScrollViewer inner
                && (delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }
}
