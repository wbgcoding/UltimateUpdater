#if DEBUG
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using System.Windows.Automation;

namespace UltimateUpdater.Review;

/// <summary>One audit line: what kind of defect, which element, and the numbers behind it.</summary>
internal sealed record ReviewFinding(string Kind, string Element, string Detail);

/// <summary>
/// Layout audit of a shown element tree: clipped text and controls, overlapping controls, controls outside the
/// window, colours that are not from a theme resource, and (settings surfaces) missing tooltips and long labels.
/// It only reports; nothing here changes a window.
/// </summary>
internal static class ReviewAudit
{
    private const double Tolerance = 1.5;

    public static (List<ReviewFinding> Findings, List<ReviewFinding> Info) Run(FrameworkElement root, ReviewScene scene, bool isPopup)
    {
        var findings = new List<ReviewFinding>();
        var info = new List<ReviewFinding>();
        var all = new List<FrameworkElement>();
        Collect(root, all);

        var leaves = new List<(FrameworkElement Element, Rect Bounds, Rect Visible)>();
        var clippedCandidates = new List<(FrameworkElement Element, string Detail)>();
        var dpi = VisualTreeHelper.GetDpi(root).PixelsPerDip;

        // Only what a user can see counts: a card scrolled out of view or cut off by its parent is not on the shot.
        var inView = new List<FrameworkElement>();
        foreach (var element in all)
        {
            if (!IsShown(element) || !TryBounds(element, root, out var viewBounds) || ClipByAncestors(element, root, viewBounds).IsEmpty)
                continue;

            inView.Add(element);
            if (element is TextBlock block)
                CheckText(block, dpi, findings, info);
            else if (!IsLayoutContainer(element) && LayoutInformation.GetLayoutClip(element) is { } clip)
            {
                var slot = clip.Bounds;
                if (slot.Width + Tolerance < element.RenderSize.Width || slot.Height + Tolerance < element.RenderSize.Height)
                {
                    clippedCandidates.Add((element,
                        $"needs {element.RenderSize.Width:0}x{element.RenderSize.Height:0}, gets {slot.Width:0}x{slot.Height:0}"));
                }
            }

            if (IsLeaf(element) && TryBounds(element, root, out var bounds))
            {
                var visible = ClipByAncestors(element, root, bounds);
                if (!visible.IsEmpty)
                    leaves.Add((element, bounds, visible));
            }
        }

        // Only the innermost clipped element is the defect; its containers are clipped because of it.
        foreach (var (element, detail) in clippedCandidates)
        {
            var hasInner = clippedCandidates.Any(other => !ReferenceEquals(other.Element, element) && IsAncestor(element, other.Element));
            if (!hasInner && !element.Name.EndsWith("IconPresenter", StringComparison.Ordinal))
                findings.Add(new ReviewFinding("CLIPPED", Describe(element), detail));
        }

        CheckOffscreen(root, leaves, findings);
        CheckOverlap(root, scene, leaves, findings);
        CheckTheme(inView, findings);

        if (scene.IsSettings && !isPopup)
            CheckSettings(inView, info);

        return (Dedupe(findings), Dedupe(info));
    }

    private static List<ReviewFinding> Dedupe(List<ReviewFinding> findings) =>
        findings.GroupBy(f => (f.Kind, f.Element, f.Detail)).Select(g => g.First()).ToList();

    private static void Collect(DependencyObject parent, List<FrameworkElement> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element)
                into.Add(element);

            // Adorner layers hold drag handles and focus visuals, never layout.
            if (child is AdornerLayer)
                continue;

            Collect(child, into);
        }
    }

    private static bool IsShown(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Opacity: < 0.02 })
                return false;
        }

        return true;
    }

    private static bool IsLayoutContainer(FrameworkElement element) =>
        element is ScrollViewer or ScrollContentPresenter or ItemsPresenter or AdornerDecorator or Window
            or ScrollBar or Track or System.Windows.Controls.Primitives.Popup or Decorator { Child: null };

    private static bool IsAncestor(FrameworkElement ancestor, FrameworkElement descendant)
    {
        for (var current = VisualTreeHelper.GetParent(descendant); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static bool IsLeaf(FrameworkElement element) => element switch
    {
        TextBlock block => !string.IsNullOrWhiteSpace(block.Text),
        Image image => image.Source is not null,
        ButtonBase or ComboBox or TextBoxBase or ProgressBar or Slider or Wpf.Ui.Controls.SymbolIcon => true,
        _ => false,
    };

    private static bool TryBounds(FrameworkElement element, FrameworkElement root, out Rect bounds)
    {
        try
        {
            bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            return true;
        }
        catch (InvalidOperationException)
        {
            bounds = Rect.Empty;
            return false;
        }
    }

    /// <summary>The part of an element a user can see: its bounds cut by every scrolling or clipping ancestor.</summary>
    private static Rect ClipByAncestors(FrameworkElement element, FrameworkElement root, Rect bounds)
    {
        var visible = bounds;
        for (var current = VisualTreeHelper.GetParent(element); current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement ancestor)
                continue;

            if ((ancestor is ScrollContentPresenter || ancestor.ClipToBounds) && TryBounds(ancestor, root, out var clip))
            {
                visible.Intersect(clip);
                if (visible.IsEmpty)
                    break;
            }
        }

        return visible;
    }

    private static void CheckText(TextBlock block, double dpi, List<ReviewFinding> findings, List<ReviewFinding> info)
    {
        var text = block.Text;
        if (string.IsNullOrWhiteSpace(text) || block.ActualWidth <= 0)
            return;

        var available = block.ActualWidth - block.Padding.Left - block.Padding.Right;
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);

        FormattedText Format(double maxWidth, int maxLines = 0) => new(
            text, CultureInfo.CurrentUICulture, block.FlowDirection, typeface, block.FontSize, Brushes.Black, dpi)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = maxLines > 0 ? maxLines : int.MaxValue,
            Trimming = TextTrimming.None,
        };

        var wraps = block.TextWrapping != TextWrapping.NoWrap;
        if (wraps && Format(100000).WidthIncludingTrailingWhitespace <= available + 3)
            return;

        if (!wraps)
        {
            // One line: the unconstrained width is the width it needs.
            var line = Format(100000);
            if (line.WidthIncludingTrailingWhitespace <= available + Tolerance && line.Width <= available + Tolerance)
                return;

            var detail = $"needs {line.Width:0} px, gets {available:0} px: \"{Shorten(text)}\"";
            if (block.TextTrimming != TextTrimming.None)
                info.Add(new ReviewFinding("TRIMMED", Describe(block), detail));
            else
                findings.Add(new ReviewFinding("CLIPPED", Describe(block), detail));

            return;
        }

        // A wrapped block is as wide as its longest line, so a hair of rounding in the measurement would add a line.
        var needed = Format(available + 3).Height;
        var allowed = block.ActualHeight - block.Padding.Top - block.Padding.Bottom;
        if (needed > allowed + Tolerance + 2)
            findings.Add(new ReviewFinding("CLIPPED", Describe(block), $"needs {needed:0} px height, gets {allowed:0} px: \"{Shorten(text)}\""));
    }

    private static void CheckOffscreen(FrameworkElement root, List<(FrameworkElement Element, Rect Bounds, Rect Visible)> leaves, List<ReviewFinding> findings)
    {
        var window = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        foreach (var (element, _, visible) in leaves)
        {
            if (visible.Left < -Tolerance || visible.Top < -Tolerance || visible.Right > window.Right + Tolerance || visible.Bottom > window.Bottom + Tolerance)
            {
                findings.Add(new ReviewFinding("OFFSCREEN", Describe(element),
                    $"at {visible.Left:0},{visible.Top:0} to {visible.Right:0},{visible.Bottom:0} in a {window.Width:0}x{window.Height:0} window"));
            }
        }
    }

    private static void CheckOverlap(
        FrameworkElement root, ReviewScene scene, List<(FrameworkElement Element, Rect Bounds, Rect Visible)> leaves, List<ReviewFinding> findings)
    {
        for (var first = 0; first < leaves.Count; first++)
        {
            for (var second = first + 1; second < leaves.Count; second++)
            {
                var (a, _, visibleA) = leaves[first];
                var (b, _, visibleB) = leaves[second];
                var overlap = Rect.Intersect(visibleA, visibleB);
                if (overlap.IsEmpty || overlap.Width <= 2 || overlap.Height <= 2)
                    continue;

                if (IsAncestor(a, b) || IsAncestor(b, a))
                    continue;

                if (IsIntentional(a, scene, root) || IsIntentional(b, scene, root) || SharesGridCell(a, b))
                    continue;

                findings.Add(new ReviewFinding("OVERLAP", $"{Describe(a)}  X  {Describe(b)}",
                    $"{overlap.Width:0}x{overlap.Height:0} px at {overlap.Left:0},{overlap.Top:0}"));
            }
        }
    }

    private static bool IsIntentional(FrameworkElement element, ReviewScene scene, FrameworkElement root)
    {
        if (scene.IntentionalOverlap.Count == 0)
            return false;

        for (DependencyObject? current = element; current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Name.Length: > 0 } named && scene.IntentionalOverlap.ContainsKey(named.Name))
                return true;
        }

        return false;
    }

    /// <summary>Two elements in the same grid cell range are layered on purpose (a badge over an icon, text over a bar).</summary>
    private static bool SharesGridCell(FrameworkElement a, FrameworkElement b)
    {
        var chainA = Chain(a);
        var chainB = Chain(b);
        FrameworkElement? common = null;
        FrameworkElement? branchA = null;
        FrameworkElement? branchB = null;
        foreach (var candidate in chainA)
        {
            var index = chainB.IndexOf(candidate);
            if (index < 0)
                continue;

            common = candidate;
            branchA = chainA.IndexOf(candidate) > 0 ? chainA[chainA.IndexOf(candidate) - 1] : null;
            branchB = index > 0 ? chainB[index - 1] : null;
            break;
        }

        if (common is not Grid grid || branchA is null || branchB is null)
            return common is Canvas;

        static (int Row, int RowEnd, int Column, int ColumnEnd) Cell(UIElement element) =>
            (Grid.GetRow(element), Grid.GetRow(element) + Grid.GetRowSpan(element),
                Grid.GetColumn(element), Grid.GetColumn(element) + Grid.GetColumnSpan(element));

        var cellA = Cell(branchA);
        var cellB = Cell(branchB);
        return cellA.Row < cellB.RowEnd && cellB.Row < cellA.RowEnd && cellA.Column < cellB.ColumnEnd && cellB.Column < cellA.ColumnEnd;
    }

    private static List<FrameworkElement> Chain(FrameworkElement element)
    {
        var chain = new List<FrameworkElement>();
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement fe)
                chain.Add(fe);
        }

        return chain;
    }

    private static void CheckTheme(List<FrameworkElement> all, List<ReviewFinding> findings)
    {
        var allowed = ThemeColours();
        foreach (var element in all)
        {
            if (!IsShown(element))
                continue;

            switch (element)
            {
                case TextBlock block when !string.IsNullOrWhiteSpace(block.Text):
                    Check(element, "foreground", block.Foreground);
                    break;
                case Control control when control is not (ScrollBar or Thumb):
                    // Text colour only: a control's Background and BorderBrush are often ignored by its template
                    // (the look comes from theme brushes inside it), so they say nothing about what is drawn.
                    Check(element, "foreground", control.Foreground);
                    break;
            }
        }

        void Check(FrameworkElement element, string role, Brush? brush)
        {
            if (brush is not SolidColorBrush solid)
                return;

            Color colour;
            try
            {
                colour = solid.Color;
            }
            catch (NullReferenceException)
            {
                return;
            }

            if (colour.A == 0 || allowed.Contains((colour.R, colour.G, colour.B)))
                return;

            findings.Add(new ReviewFinding("OFF-THEME", Describe(element), $"{role} #{colour.R:X2}{colour.G:X2}{colour.B:X2} is no theme colour"));
        }
    }

    private static HashSet<(byte R, byte G, byte B)> ThemeColours()
    {
        var colours = new HashSet<(byte, byte, byte)> { (0, 0, 0), (255, 255, 255) };
        Walk(Application.Current.Resources);
        return colours;

        void Walk(ResourceDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                try
                {
                    switch (dictionary[key])
                    {
                        case SolidColorBrush brush:
                            colours.Add((brush.Color.R, brush.Color.G, brush.Color.B));
                            break;
                        case Color colour:
                            colours.Add((colour.R, colour.G, colour.B));
                            break;
                    }
                }
                catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
                {
                    // a brush whose colour resolves through another dictionary; its owner lists the colour itself
                }
            }

            foreach (var merged in dictionary.MergedDictionaries)
                Walk(merged);
        }
    }

    private static void CheckSettings(List<FrameworkElement> all, List<ReviewFinding> findings)
    {
        foreach (var control in all)
        {
            if (!IsShown(control) || control is not (ToggleButton or ComboBox or Slider or TextBoxBase) || control is ListBoxItem)
                continue;

            var label = AutomationProperties.GetName(control);
            if (string.IsNullOrEmpty(label) || control.FindAncestor<ListBox>() is not null)
                continue;

            if (label.Length > 40)
                findings.Add(new ReviewFinding("LONG-LABEL", Describe(control), $"{label.Length} characters: \"{Shorten(label)}\""));

            if (!RowHasTooltip(control))
                findings.Add(new ReviewFinding("NO-TOOLTIP", Describe(control), $"\"{Shorten(label)}\" has no tooltip on its row"));
        }
    }

    private static bool RowHasTooltip(FrameworkElement control)
    {
        // The row is the nearest grid that also holds text; the tooltip may sit on the control or on its label block.
        for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { ToolTip: not null })
                return true;

            if (current is Grid grid && grid.ColumnDefinitions.Count >= 2 && HasTooltipBelow(grid))
                return true;

            if (current is Grid { ColumnDefinitions.Count: >= 2 })
                return false;
        }

        return false;
    }

    private static bool HasTooltipBelow(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement { ToolTip: not null } || HasTooltipBelow(child))
                return true;
        }

        return false;
    }

    private static T? FindAncestor<T>(this DependencyObject element) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }

    private static string Shorten(string text) => text.Length <= 48 ? text : text[..45] + "...";

    /// <summary>A name a person can find in the XAML: the type, its own name or text, and the nearest named containers.</summary>
    public static string Describe(FrameworkElement element)
    {
        var own = element switch
        {
            TextBlock block => $"\"{Shorten(block.Text)}\"",
            _ when !string.IsNullOrEmpty(element.Name) => element.Name,
            _ when AutomationProperties.GetName(element) is { Length: > 0 } name => $"'{Shorten(name)}'",
            _ => string.Empty,
        };

        var path = new List<string>();
        for (var current = VisualTreeHelper.GetParent(element); current is not null && path.Count < 2; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Name.Length: > 0 } named)
                path.Add(named.Name);
        }

        var where = path.Count == 0 ? string.Empty : " in " + string.Join(" < ", path);
        return $"{element.GetType().Name} {own}{where}".Replace("  ", " ").Trim();
    }
}
#endif
