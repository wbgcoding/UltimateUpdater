using System.Windows;

namespace UltimateUpdater.Views;

/// <summary>
/// WPF's SizeToContent still measures a window as if it had a normal caption bar even when
/// ExtendsContentIntoTitleBar moves that bar into the content, so every SizeToContent dialog ends up about one
/// caption's height taller than its content (a big empty band under the button row). Margins cannot fix it: the
/// extra space comes from the window's own auto-sizing.
///
/// The exact non-client chrome differs by DPI, resizability and Windows version, so instead of guessing it this
/// pins the window's height to the root element's Measure-time DesiredSize, clamped to the window's own
/// Min/MaxHeight. No chrome constant to get wrong, and it re-fits when the desired size changes (an expander
/// opening, a longer message).
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
                // Re-measure against the width the window has settled on, so the wrapping matches what is shown. Height is
                // unbounded only when the window declares no MaxHeight: a virtualizing list (the history table) answers an
                // infinite height by realizing every row, which would defeat its virtualization on every LayoutUpdated,
                // scrolling included. A finite MaxHeight clamps the result anyway (ComputeFittedHeight), so measuring against
                // it changes nothing for content that fits.
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
    /// The height constraint <see cref="FitHeightToContent"/>'s re-measure uses, pulled out so it can be unit tested
    /// without a window. A finite MaxHeight is used directly rather than PositiveInfinity: the result is clamped to
    /// it anyway (<see cref="ComputeFittedHeight"/>), and an unbounded measure forces a virtualizing list to realize
    /// every item.
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
