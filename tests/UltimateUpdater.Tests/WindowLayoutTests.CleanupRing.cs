using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    private const double RingMinClearance = 12;

    private static bool IsShown(FrameworkElement element, DependencyObject stopAt)
    {
        for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, stopAt); node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui && ui.Visibility != Visibility.Visible)
                return false;
        }

        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
                return true;
        }

        return false;
    }

    private static IEnumerable<FrameworkElement> RingDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element)
                yield return element;

            foreach (var inner in RingDescendants(child))
                yield return inner;
        }
    }

    private static double GapBetween(Rect a, Rect b)
    {
        var dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        var dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The progress ring is 226 wide (radius 105, stroke 16) in a 240 box. At the default and at the minimum
    /// window size it keeps at least 12 px to every other piece of text or control around it, its track
    /// and fill are the same size and sit on the same centre, and the text inside it stays within the
    /// inner edge of the track on every face.
    /// </summary>
    [Theory]
    [InlineData("choosing", false)]
    [InlineData("choosing", true)]
    [InlineData("cleaning", false)]
    [InlineData("cleaning", true)]
    [InlineData("finished", false)]
    [InlineData("finished", true)]
    public void Cleanup_window_ring_is_big_and_keeps_its_distance(string face, bool minimumSize)
    {
        RenderResult? result = null;
        var failures = new List<string>();

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            ApplyTheme("dark");

            var viewModel = NewMeasuredCleanupViewModel();
            var window = new Views.CleanupWindow(viewModel) { DisplayPercent = 0 };
            var content = (FrameworkElement)window.Content;

            if (face != "choosing")
            {
                var areas = viewModel.Items.Take(10).ToList();
                viewModel.StepItems = areas;
                viewModel.FreedBytes = 1_468_006_400;

                if (face == "cleaning")
                {
                    var running = areas[3];
                    running.IsCleaning = true;
                    running.Percent = 40;
                    viewModel.CurrentItemText = running.Title;
                    viewModel.CurrentGroupKey = CleanupGroups.For(running.Target);
                    viewModel.CleanPercent = 47;
                    window.DisplayPercent = 47;
                    viewModel.RefreshAreaProgress();
                    viewModel.Phase = CleanupPhase.Cleaning;
                    viewModel.IsPaused = true;
                }
                else
                {
                    foreach (var area in areas)
                    {
                        area.IsSelected = true;
                        area.RunState = CleanupTargetState.Done;
                        area.IsDone = true;
                        area.FreedBytes = area.Bytes;
                    }

                    viewModel.CleanPercent = 100;
                    window.DisplayPercent = 100;
                    viewModel.Phase = CleanupPhase.Finished;
                    window.DisplayFreed = 1_468_006_400;
                }
            }

            var width = minimumSize ? (int)window.MinWidth : (int)window.Width;
            var height = minimumSize ? (int)window.MinHeight : (int)window.Height;
            result = WpfHarness.RenderWindow(window, $"cleanup-ring-{face}-{(minimumSize ? "min" : "default")}", width, height);

            var box = FindByName<FrameworkElement>(content, "CleanupRingBox")!;
            var track = FindByName<System.Windows.Shapes.Ellipse>(content, "RingTrack")!;
            var fill = FindByName<System.Windows.Shapes.Path>(content, "RingFill")!;

            if (box.ActualWidth != 240 || box.ActualHeight != 240)
                failures.Add($"ring box is {box.ActualWidth}x{box.ActualHeight}, expected 240x240");
            if (Math.Abs(track.ActualWidth - 226) > 0.1 || Math.Abs(fill.ActualWidth - 226) > 0.1 || track.StrokeThickness != 16 || fill.StrokeThickness != 16)
                failures.Add($"track {track.ActualWidth}/{track.StrokeThickness}, fill {fill.ActualWidth}/{fill.StrokeThickness}, expected 226/16");

            var trackRect = track.TransformToAncestor(content).TransformBounds(new Rect(0, 0, track.ActualWidth, track.ActualHeight));
            var fillRect = fill.TransformToAncestor(content).TransformBounds(new Rect(0, 0, fill.ActualWidth, fill.ActualHeight));
            if (Math.Abs(trackRect.Left - fillRect.Left) > 0.5 || Math.Abs(trackRect.Top - fillRect.Top) > 0.5)
                failures.Add($"fill {fillRect} is off the track {trackRect}");

            var contentRect = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            if (trackRect.Left < RingMinClearance || contentRect.Right - trackRect.Right < RingMinClearance || trackRect.Top < RingMinClearance)
                failures.Add($"ring {trackRect} is closer than {RingMinClearance}px to the window edge {contentRect}");

            var centre = new Point(trackRect.Left + trackRect.Width / 2, trackRect.Top + trackRect.Height / 2);
            const double inner = 105 - 8 - 2;
            var measured = 0;

            foreach (var element in RingDescendants(content))
            {
                if (ReferenceEquals(element, box) || ReferenceEquals(element, track) || ReferenceEquals(element, fill))
                    continue;

                var isText = element is TextBlock { Text.Length: > 0 };
                if (!isText && element is not ButtonBase && element is not CheckBox)
                    continue;
                if (!IsShown(element, content))
                    continue;

                var rect = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

                if (IsInside(element, box))
                {
                    foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight })
                    {
                        var distance = Math.Sqrt(Math.Pow(corner.X - centre.X, 2) + Math.Pow(corner.Y - centre.Y, 2));
                        if (distance > inner)
                        {
                            failures.Add($"{element.GetType().Name} '{(element as TextBlock)?.Text}' reaches {distance:F1}px from the ring centre (inner edge {inner})");
                            break;
                        }
                    }

                    continue;
                }

                if (IsInside(box, element))
                    continue;

                // Whatever scrolled out of the window cannot touch the ring; only what is on screen counts.
                if (rect.Bottom <= 0 || rect.Top >= contentRect.Bottom)
                    continue;

                measured++;
                var gap = GapBetween(rect, trackRect);
                if (gap < RingMinClearance)
                    failures.Add($"{element.GetType().Name} '{(element as TextBlock)?.Text}' {rect} is {gap:F1}px from the ring");
            }

            if (measured < 3)
                failures.Add($"only {measured} neighbours were measured");
        });

        AssertRendered(result);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
