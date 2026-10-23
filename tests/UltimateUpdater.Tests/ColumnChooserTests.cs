using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// The column chooser popup, opened from a header button via
/// <see cref="ColumnChooser.ShowFor"/>, looked like it never closed on a second click. It actually
/// closed and reopened in the same gesture: WPF's ContextMenu light-dismiss reacts to the mouse-DOWN
/// that lands back on the anchor button, but the matching mouse-up still completes as an ordinary
/// Click afterwards, and that Click called <see cref="ColumnChooser.ShowFor"/> again. A fixed timer
/// window and then a one-shot "swallow the next click" flag both missed a real click's exact timing on
/// the actual hardware. Fixed by tracking the menu per anchor (a call while it is still open toggles it
/// shut) and taking the anchor out of hit-testing for as long as the menu is open, so the light-dismiss
/// mouse-down never reaches it as a press to complete into a Click at all.
/// </summary>
[Collection("wpf")]
public class ColumnChooserTests
{
    private static readonly IReadOnlyList<ColumnDescriptor> OneColumn =
    [
        new ColumnDescriptor("name", () => "Name", CanHide: true),
    ];

    private static System.Windows.Controls.Button MakeAnchor(Window window)
    {
        var anchor = new System.Windows.Controls.Button();
        window.Content = anchor;
        window.Show();
        return anchor;
    }

    [Fact]
    public void First_call_opens_the_menu()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
            var anchor = MakeAnchor(window);

            var menu = ColumnChooser.ShowFor(anchor, OneColumn, _ => false, (_, _) => { }, () => { });

            Assert.True(menu?.IsOpen);
        });
    }

    /// <summary>The anchor stops receiving hits for as long as the menu it opened is showing: the state
    /// a real light-dismiss mouse-down needs to pass through instead of completing into a reopening
    /// Click (see the class doc above).</summary>
    [Fact]
    public void The_anchor_is_taken_out_of_hit_testing_while_the_menu_is_open()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
            var anchor = MakeAnchor(window);

            ColumnChooser.ShowFor(anchor, OneColumn, _ => false, (_, _) => { }, () => { });

            Assert.False(anchor.IsHitTestVisible);
        });
    }

    /// <summary>
    /// A second call while the menu is still open toggles it shut instead of replacing it, the
    /// keyboard path (Enter/Space on a focused anchor raises Click regardless of hit-testing) still needs
    /// since the anchor being out of hit-testing only ever blocks a mouse press, never a keyboard
    /// activation.
    /// </summary>
    [Fact]
    public void Toggling_an_open_menu_closes_it_instead_of_replacing_it()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
            var anchor = MakeAnchor(window);

            var menu = ColumnChooser.ShowFor(anchor, OneColumn, _ => false, (_, _) => { }, () => { });
            Assert.True(menu?.IsOpen);

            var second = ColumnChooser.ShowFor(anchor, OneColumn, _ => false, (_, _) => { }, () => { });

            Assert.Null(second);
            Assert.False(menu!.IsOpen);
        });
    }

    /// <summary>
    /// Closed fires inside the light-dismissing mouse-down. Handing hit-testing back right there let that
    /// same press reach the anchor and reopen the menu on release, so it only comes back once the button
    /// is up again.
    /// </summary>
    [Fact]
    public void The_anchor_stays_out_of_hit_testing_until_the_closing_press_is_released()
    {
        WpfHarness.OnUiThread(() =>
        {
            var original = ColumnChooser.IsAnyMouseButtonPressed;
            var pressed = true;
            ColumnChooser.IsAnyMouseButtonPressed = () => pressed;
            try
            {
                var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
                var anchor = MakeAnchor(window);

                var menu = ColumnChooser.ShowFor(anchor, OneColumn, _ => false, (_, _) => { }, () => { });
                var closed = false;
                menu!.Closed += (_, _) => closed = true;
                menu.IsOpen = false;
                for (var i = 0; i < 40 && !closed; i++)
                    PumpFor(TimeSpan.FromMilliseconds(25));
                PumpFor(TimeSpan.FromMilliseconds(120));
                Assert.True(closed);
                Assert.False(anchor.IsHitTestVisible);

                pressed = false;
                PumpFor(TimeSpan.FromMilliseconds(120));
                Assert.True(anchor.IsHitTestVisible);
            }
            finally
            {
                ColumnChooser.IsAnyMouseButtonPressed = original;
            }
        });
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>A genuinely later click (well after the previous menu closed) opens it normally.</summary>
    [Fact]
    public void A_later_call_opens_the_menu_again()
    {
        WpfHarness.OnUiThread(() =>
        {
            var window = new Window { Width = 10, Height = 10, ShowInTaskbar = false, Style = new Style(typeof(Window)) };
            var anchor = MakeAnchor(window);

            var firstMenu = ColumnChooser.ShowFor(anchor, OneColumn, _ => false, (_, _) => { }, () => { });
            firstMenu!.IsOpen = false;

            var second = ColumnChooser.ShowFor(anchor, OneColumn, _ => false, (_, _) => { }, () => { });

            Assert.True(second?.IsOpen);
        });
    }
}
