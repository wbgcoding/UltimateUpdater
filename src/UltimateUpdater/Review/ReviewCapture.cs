#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace UltimateUpdater.Review;

/// <summary>Writes a shown element to a PNG with <see cref="RenderTargetBitmap"/>, over the window background so a transparent backdrop never turns black.</summary>
internal static class ReviewCapture
{
    public static void Save(FrameworkElement element, string path, Brush fallbackBackground)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(BackgroundOf(element, fallbackBackground), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        bitmap.Render(background);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Brush BackgroundOf(FrameworkElement element, Brush fallback)
    {
        var brush = element switch
        {
            Control control => control.Background,
            _ => null,
        };

        return brush is SolidColorBrush { Color.A: > 200 } ? brush : fallback;
    }

    /// <summary>The brush shots use behind a window that paints no surface of its own.</summary>
    public static Brush FallbackBackground(FrameworkElement element) =>
        element.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
}
#endif
