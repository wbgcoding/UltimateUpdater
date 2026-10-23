using System.Windows;

namespace UltimateUpdater.Views;

/// <summary>
/// WPF's SizeToContent still measures a window as if it had a normal, non-client caption bar even when
/// ExtendsContentIntoTitleBar moves that bar into the content itself, so every dialog using SizeToContent
/// (MessageDialogWindow and the small sub-windows) ends up roughly one caption's height taller than its
/// own content - which showed up as a big empty band under the button row, seen on the uninstall warning
/// and many other dialogs. Reducing margins cannot fix this: the extra space comes from the window's own
/// auto-sizing, not from anything in the XAML. Measured live (a diagnostic build logging
/// window.ActualHeight against the root element's own Measure-time DesiredSize): the window's client area
/// really is bigger than its content by a constant amount regardless of what the content is, confirming
/// this is exactly that phantom caption, not a slack/stretch issue inside the content's own layout.
///
/// Rather than guess at the exact non-client chrome involved (it differs by DPI, by whether the window is
/// resizable, and reportedly even by which Windows version is running), this cancels the bug directly: it
/// pins the window's height to the root element's own honest Measure-time DesiredSize, clamped to
/// whatever Min/MaxHeight the window already declares. No chrome constant to get wrong, and it self-
/// corrects if a later layout pass changes the desired size again (an expander opening, a longer message).
/// </summary>
internal static class DialogSizing
{
    /// <summary>
    /// Wires <paramref name="window"/> so its height fits its content exactly once it is actually shown,
    /// and keeps re-fitting whenever the content's own desired height changes afterwards (e.g. the error
    /// dialog's Details expander opening, or a value that only arrives after an async load). Call once,
    /// right after InitializeComponent.
    /// </summary>
    public static void FitHeightToContent(Window window)
    {
        if (window.Content is not FrameworkElement root)
            return;

        var keepAutoWidth = window.SizeToContent == SizeToContent.WidthAndHeight;
        var refitting = false;

        void Refit()
        {
            // Re-entrancy guard: setting window.Height below runs another layout pass, which can raise
            // these same events again before this call has returned.
            if (refitting || window.ActualHeight <= 0)
                return;

            refitting = true;
            try
            {
                // Re-measure against the width the window has actually settled on, so this reflects the
                // SAME wrapping the window is currently showing, not an unconstrained guess. Height is
                // unbounded only when the window itself declares no MaxHeight: a virtualizing list (the
                // history window's table) answers an infinite height constraint by fully realizing every
                // row to compute its true extent, which defeats its own virtualization on EVERY
                // LayoutUpdated this handler runs on, including one raised by ordinary scrolling - measured
                // at over a second for just 20 scroll steps against 500 rows, versus 0 ms bounded. A finite
                // MaxHeight already clamps the result below (ComputeFittedHeight), so measuring against it
                // directly changes nothing for content that fits within it, and lets a virtualizing panel
                // stay bounded to what the window could ever show anyway.
                var width = root.ActualWidth > 0 ? root.ActualWidth : double.PositiveInfinity;
                var height = ComputeMeasureHeight(window.MaxHeight);
                root.Measure(new System.Windows.Size(width, height));

                var target = ComputeFittedHeight(root.DesiredSize.Height, window.MinHeight, window.MaxHeight);
                if (System.Math.Abs(target - window.ActualHeight) < 0.5)
                    return; // already tight, nothing to correct

                window.SizeToContent = keepAutoWidth ? SizeToContent.Width : SizeToContent.Manual;
                window.Height = target;
            }
            finally
            {
                refitting = false;
            }
        }

        window.ContentRendered += (_, _) => Refit();
        root.SizeChanged += (_, _) => Refit();
        root.LayoutUpdated += (_, _) => Refit();
    }

    /// <summary>
    /// The height constraint <see cref="FitHeightToContent"/>'s own re-measure uses, pulled out so it can be
    /// unit tested without a real window. A finite MaxHeight is used directly rather than PositiveInfinity:
    /// the result gets clamped to it anyway (<see cref="ComputeFittedHeight"/>), and measuring a
    /// virtualizing list against an unbounded height forces it to realize every item to answer "how tall
    /// would this be", which is exactly what defeated the history window's own virtualization on every
    /// scroll-driven LayoutUpdated.
    /// </summary>
    internal static double ComputeMeasureHeight(double maxHeight) =>
        double.IsNaN(maxHeight) || maxHeight >= double.PositiveInfinity ? double.PositiveInfinity : maxHeight;

    /// <summary>
    /// The clamping half of the arithmetic, pulled out on its own so it can be unit tested without a real
    /// window: the content's own desired height must still respect whatever Min/MaxHeight the window
    /// already declares in XAML.
    /// </summary>
    internal static double ComputeFittedHeight(double desiredHeight, double minHeight, double maxHeight)
    {
        var target = desiredHeight;

        if (minHeight > 0)
            target = System.Math.Max(target, minHeight);

        if (!double.IsNaN(maxHeight) && maxHeight < double.PositiveInfinity)
            target = System.Math.Min(target, maxHeight);

        return target;
    }

    /// <summary>
    /// Pulls a tall dialog fully onto the monitor its owner is on, once the window handle exists: the
    /// dialogs declare a fixed height (cleanup 880, settings 720) that a 1366x768 laptop (about 728 DIP of
    /// work area) or a 1080p screen at 150 % cannot hold, which left the title bar or the bottom action row
    /// off screen. Minimums shrink with the window so WPF does not grow it back.
    /// </summary>
    public static void FitToWorkArea(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var measured = window.Owner is { } owner
                && new System.Windows.Interop.WindowInteropHelper(owner).Handle != IntPtr.Zero
                    ? owner
                    : window;
            var handle = new System.Windows.Interop.WindowInteropHelper(measured).Handle;

            // The DPI of the same window the work area comes from: the dialog's own handle is not placed on
            // the owner's monitor yet, so its DPI can belong to another screen on a mixed-DPI setup.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(measured);
            var pixels = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var workArea = new Rect(pixels.Left / dpi.DpiScaleX, pixels.Top / dpi.DpiScaleY,
                pixels.Width / dpi.DpiScaleX, pixels.Height / dpi.DpiScaleY);

            window.MinHeight = System.Math.Min(window.MinHeight, workArea.Height);
            window.MinWidth = System.Math.Min(window.MinWidth, workArea.Width);

            // Before WPF has placed a CenterOwner window its position can still be NaN; centring then
            // happens with the already reduced size, so only the size needs fitting.
            var placed = !double.IsNaN(window.Left) && !double.IsNaN(window.Top);
            var fitted = ClampToWorkArea(
                new Rect(placed ? window.Left : workArea.Left, placed ? window.Top : workArea.Top, window.Width, window.Height),
                workArea);
            if (placed)
            {
                window.Left = fitted.Left;
                window.Top = fitted.Top;
            }

            window.Width = fitted.Width;
            window.Height = fitted.Height;
        };
    }

    /// <summary>Shrinks <paramref name="bounds"/> to the work area if it is larger, then slides it inside.</summary>
    internal static Rect ClampToWorkArea(Rect bounds, Rect workArea)
    {
        var width = System.Math.Min(bounds.Width, workArea.Width);
        var height = System.Math.Min(bounds.Height, workArea.Height);
        var left = System.Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        var top = System.Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);
        return new Rect(left, top, width, height);
    }
}
