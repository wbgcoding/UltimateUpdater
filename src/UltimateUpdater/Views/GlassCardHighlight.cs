using System.Windows;
using System.Windows.Controls;

namespace UltimateUpdater.Views;

/// <summary>
/// The dark theme's one-pixel top highlight (ThemeService.ApplyDarkPaletteOverrides sets
/// "GlassTopHighlightBrush" to white at 6 percent there, and to transparent in every other theme): a
/// single hairline just inside a card's own border, which is what reads as glass without a single
/// gradient image. Attached rather than a Style setter on purpose - a Style setter can share one Brush
/// instance across every element that uses the style, but never a Visual, so putting the highlight ITSELF
/// in a style would just hand the same Border to whichever card asked for it last. Each opted-in Border
/// gets its own line, added once.
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

        // The style/content order a XAML parser applies properties in is not something to depend on: a
        // Style setter (how every card actually opts in) resolves before the nested XAML content becomes
        // the border's Child, so Child is usually still null right here. Loaded looked like the obvious
        // fallback and is what ThemeService.AttachWindow itself waits on for a window that is not up yet,
        // but Loaded needs the element on a real PresentationSource - the render harness never shows its
        // windows (WpfHarness.RenderWindow renders the DETACHED content), so it never fires there, and a
        // card built for a test snapshot would silently never get its highlight. LayoutUpdated has no such
        // requirement: it comes straight from the layout system, on-screen or off, so it is what both the
        // real app and the harness can rely on alike.
        if (border.Child is not null)
        {
            Attach(border);
            return;
        }

        // LayoutUpdated is really a layout-manager-wide event - every subscriber anywhere in the tree is
        // notified after any layout pass, and "sender" is not reliably this particular element - so the
        // handler below closes over "border" instead of trusting the sender argument, and removes itself
        // by the same captured reference once it has done its one job.
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
    /// Wraps whatever the border already shows in a Grid and adds the highlight as a second,
    /// hit-test-invisible child on top of it. A card's own Padding would otherwise push the line down
    /// with the rest of the content, so the highlight cancels it out with a negative margin and reaches
    /// back to a 1px inset on every side: the border's own stroke already occupies that outermost pixel,
    /// and rounds it further through the card's own CornerRadius, so landing the highlight exactly on
    /// that edge (a 0px inset) let its 6 percent white get lost in the stroke's own anti-aliasing instead
    /// of reading as a second, separate line. One pixel further in sits on the card's flat fill.
    /// </summary>
    private static void Attach(Border border)
    {
        if (border.Child is Grid existing && ReferenceEquals(existing.Tag, AlreadyAttached))
            return; // Loaded should fire once per instance, but this is cheap insurance against twice.

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
