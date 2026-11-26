using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltimateUpdater.ViewModels.Uninstall;
using UltimateUpdater.Views.Uninstall;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>A real left press on <paramref name="element"/>: the mouse-down event the framework turns into the preview left-button event.</summary>
    private static void PressLeft(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = element,
        });

    /// <summary>What a real click at the middle of <paramref name="element"/> would hit first.</summary>
    private static UIElement HitAtCentre(FrameworkElement content, FrameworkElement element)
    {
        var point = element.TransformToAncestor(content).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var hit = VisualTreeHelper.HitTest(content, point)?.VisualHit;
        Assert.NotNull(hit);
        while (hit is not null and not UIElement)
            hit = VisualTreeHelper.GetParent(hit);

        return (UIElement)hit!;
    }

    /// <summary>A click on a selectable row ticks it, wherever on the row it lands (the box, the name, the empty space); a row that cannot be removed stays unticked and says why.</summary>
    [Fact]
    public void Uninstaller_a_click_on_the_box_or_the_row_toggles_the_tick()
    {
        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            ApplyTheme("dark");

            var viewModel = CreateUninstallerViewModel();
            var window = new UninstallerWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, "uninstaller-click-en", (int)window.Width, (int)window.Height);

            var items = VisualDescendants<ListBoxItem>(content).ToList();
            ListBoxItem ItemOf(string name) =>
                items.FirstOrDefault(item => item.DataContext is UninstallRowViewModel row && row.Name == name)
                ?? throw new InvalidOperationException($"no container for '{name}'; contexts: {string.Join(" | ", items.Select(item => item.DataContext?.GetType().Name))}");

            var firefox = ItemOf("Mozilla Firefox (x64 de)");
            var row = (UninstallRowViewModel)firefox.DataContext;
            var box = VisualDescendants<CheckBox>(firefox).Single();
            Assert.False(box.IsHitTestVisible, "the box must not swallow the click the row handles");

            // The click on the box lands on whatever is under it.
            PressLeft(HitAtCentre(content, box));
            Assert.True(row.IsSelected);
            Assert.True(box.IsChecked);

            PressLeft(HitAtCentre(content, box));
            Assert.False(row.IsSelected);

            // On the name, and on the empty space of the row.
            PressLeft(VisualDescendants<TextBlock>(firefox).First(text => text.Text == row.Name));
            Assert.True(row.IsSelected);
            PressLeft(VisualDescendants<Grid>(firefox).First());
            Assert.False(row.IsSelected);

            // A row that cannot be removed ignores the click and explains itself.
            var own = ItemOf("Uninstall Itself");
            var ownRow = (UninstallRowViewModel)own.DataContext;
            PressLeft(VisualDescendants<Grid>(own).First());
            Assert.False(ownRow.IsSelected);
            Assert.Equal(ownRow.UnavailableReason, VisualDescendants<Grid>(own).First().ToolTip);
            Assert.NotEmpty(ownRow.UnavailableReason);
            Assert.Null(VisualDescendants<Grid>(firefox).First().ToolTip);
        });
    }
}
