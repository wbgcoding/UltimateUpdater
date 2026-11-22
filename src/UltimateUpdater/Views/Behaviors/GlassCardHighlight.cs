using System.Windows;
using System.Windows.Controls;

namespace UltimateUpdater.Views.Behaviors;

/// <summary>
/// The dark theme's one-pixel top highlight (ThemeService.ApplyDarkPaletteOverrides sets "GlassTopHighlightBrush"
/// to white at 6 percent there, transparent in every other theme): a hairline just inside a card's border, which
/// reads as glass without a gradient image. Attached rather than a Style setter: a style can share a Brush across
/// elements but never a Visual, so every card would get the same Border. Each opted-in Border gets its own line,
/// added once.
/// </summary>
public static class GlassCardHighlight
{
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(GlassCardHighlight), new PropertyMetadata(false, OnShowChanged));

    public static void SetShow(Border border, bool value) => border.SetValue(ShowProperty, value);

    public static bool GetShow(Border border) => (bool)border.GetValue(ShowProperty);

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border || !Equals(e.NewValue, true))
            return;

        // A Style setter (how every card opts in) resolves before the nested XAML content becomes the border's Child,
        // so Child is usually still null here. Loaded needs a real PresentationSource, and the render harness renders
        // detached content that never gets one, so a test snapshot would never get its highlight. LayoutUpdated comes
        // straight from the layout system, on-screen or off.
        if (border.Child is not null)
        {
            Attach(border);
            return;
        }

        // LayoutUpdated is a layout-manager-wide event and "sender" is not reliably this element, so the handler
        // below closes over "border" and removes itself by that same reference after its one job.
        EventHandler? onLayoutUpdated = null;
        onLayoutUpdated = (_, _) =>
        {
            if (border.Child is null)
                return; // content still not assigned; wait for the next layout pass

            border.LayoutUpdated -= onLayoutUpdated;
            Attach(border);
        };
        border.LayoutUpdated += onLayoutUpdated;
    }

    /// <summary>
    /// Wraps whatever the border already shows in a Grid and adds the highlight as a second, hit-test-invisible child.
    /// The card's Padding would push the line down with the content, so a negative margin cancels it and reaches
    /// back to a 1px inset on every side: the border's own stroke occupies the outermost pixel, and a 0px inset lost
    /// the 6 percent white in the stroke's anti-aliasing. One pixel further in sits on the card's flat fill.
    /// </summary>
    private static void Attach(Border border)
    {
        if (border.Child is Grid existing && ReferenceEquals(existing.Tag, AlreadyAttached))
            return; // the layout hook fires once per card, but this is cheap insurance against twice.

        var content = border.Child;
        border.Child = null;

        var grid = new Grid { Tag = AlreadyAttached };
        if (content is not null)
            grid.Children.Add(content);

        var padding = border.Padding;
        var highlight = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            CornerRadius = new CornerRadius(border.CornerRadius.TopLeft, border.CornerRadius.TopRight, 0, 0),
            Margin = new Thickness(1 - padding.Left, 1 - padding.Top, 1 - padding.Right, 0),
        };
        highlight.SetResourceReference(Border.BackgroundProperty, "GlassTopHighlightBrush");
        grid.Children.Add(highlight);

        border.Child = grid;
    }

    /// <summary>Marks the wrapper Grid this class creates, so <see cref="Attach"/> can recognise its own
    /// work and never wrap it a second time.</summary>
    private static readonly object AlreadyAttached = new();
}
