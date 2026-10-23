using System.Windows;
using UltimateUpdater.Services;
// ContextMenu/MenuItem/Separator collide with System.Windows.Forms (referenced for the tray icon,
// see GlobalUsings.cs); fully qualified here rather than adding more global aliases for a single file.
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace UltimateUpdater.Views;

/// <summary>
/// One column's identity for the shared column chooser: a stable, persisted, never-localized
/// id, a display name read live (so a language switch needs no special handling here), and whether the
/// column may be hidden at all. The name column and any pure action column never offer a CanHide entry.
/// </summary>
public sealed record ColumnDescriptor(string Id, Func<string> DisplayName, bool CanHide);

/// <summary>
/// Builds the small "Columns" checkbox menu shared by every hand-built table (main, history, the
/// cleanup detail list); kept as one static builder rather than copied per window.
/// A table hands in its own column list plus three callbacks
/// (current hidden state, toggle one column, reset all) and gets back a ready-made ContextMenu it can
/// open from its own header button.
/// </summary>
public static class ColumnChooser
{
    /// <summary>
    /// WPF's ContextMenu light-dismiss closes the menu on the mouse-DOWN that lands outside
    /// it (including back on the anchor button itself), but that same mouse-down/up pair still finishes
    /// as an ordinary Click on the button afterwards, which reopens the menu the light-dismiss just
    /// closed. A second click on the header button looked like it did nothing, when it actually closed
    /// and immediately reopened the popup. Two earlier fixes (a fixed timer window, then a one-shot
    /// "swallow the next click" flag cleared on the window's own mouse-up) both missed a real click's
    /// exact timing on the actual hardware. Tracked per anchor only for the still-open menu, so a second
    /// call while it is open toggles it shut (see <see cref="ShowFor"/>).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, Tracker> Trackers = new();

    private sealed class Tracker
    {
        public ContextMenu? Menu;
    }

    /// <summary>Opens the chooser as a context menu anchored on <paramref name="anchor"/> (typically the
    /// button that was clicked). A second call while the menu from a previous call is still open toggles
    /// it shut instead of reopening it. The reopen that a real mouse click causes on its own (the same
    /// mouse-down that light-dismisses the menu still completing as a Click on the anchor once the mouse
    /// comes back up) never reaches here at all: for as long as the menu is open the anchor is taken out
    /// of hit-testing, so that mouse-down passes straight through it and there is no press left to
    /// complete into a Click. Returns the menu that ended up open, or null when this call closed instead
    /// of opening one (a caller does not need the return value; tests use it to see what happened).</summary>
    public static ContextMenu? ShowFor(
        FrameworkElement anchor,
        IReadOnlyList<ColumnDescriptor> columns,
        Func<string, bool> isHidden,
        Action<string, bool> setHidden,
        Action reset)
    {
        return ShowMenu(anchor, () => Build(columns, isHidden, setHidden, reset));
    }

    /// <summary>The open/toggle-shut/hit-testing dance shared by every anchored checkbox menu (the column
    /// chooser and the filter menu), see <see cref="ShowFor"/> for what it guards against.</summary>
    internal static ContextMenu? ShowMenu(FrameworkElement anchor, Func<ContextMenu> build)
    {
        var tracker = Trackers.GetOrCreateValue(anchor);

        if (tracker.Menu is { IsOpen: true } openMenu)
        {
            openMenu.IsOpen = false;
            return null;
        }

        var menu = build();
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        // Restored after the menu closed no matter how (light-dismiss, Escape, a column toggled), so the
        // anchor is never left stuck refusing clicks. Closed fires INSIDE the light-dismissing mouse-down,
        // before that press is routed on: restoring hit-testing right there would hand the press to the
        // anchor and reopen the menu on release. So it waits until no mouse button is held any more.
        anchor.IsHitTestVisible = false;
        menu.Closed += (_, _) => RestoreHitTestingAfterRelease(anchor);

        tracker.Menu = menu;
        menu.IsOpen = true;

        // A menu that failed to open never raises Closed; the anchor must not stay dead because of it.
        if (!menu.IsOpen)
            anchor.IsHitTestVisible = true;

        return menu;
    }

    /// <summary>Seam for tests: whether any mouse button is currently held.</summary>
    internal static Func<bool> IsAnyMouseButtonPressed = () =>
        System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed
        || System.Windows.Input.Mouse.RightButton == System.Windows.Input.MouseButtonState.Pressed;

    /// <summary>Gives <paramref name="anchor"/> its hit-testing back once no mouse button is held, checked
    /// every 30 ms; at most 5 s, so a lost button-up can never leave the anchor dead.</summary>
    internal static void RestoreHitTestingAfterRelease(FrameworkElement anchor)
    {
        if (!IsAnyMouseButtonPressed())
        {
            anchor.IsHitTestVisible = true;
            return;
        }

        var started = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Input, anchor.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(30),
        };
        timer.Tick += (_, _) =>
        {
            if (IsAnyMouseButtonPressed() && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
                return;
            timer.Stop();
            anchor.IsHitTestVisible = true;
        };
        timer.Start();
    }

    private static ContextMenu Build(
        IReadOnlyList<ColumnDescriptor> columns,
        Func<string, bool> isHidden,
        Action<string, bool> setHidden,
        Action reset)
    {
        var menu = new ContextMenu();

        foreach (var column in columns)
        {
            if (!column.CanHide)
                continue;

            var item = new MenuItem
            {
                Header = column.DisplayName(),
                IsCheckable = true,
                // Checked = shown, same sense the checkbox's own label reads in.
                IsChecked = !isHidden(column.Id),
            };
            // WPF flips IsCheckable's IsChecked BEFORE raising Click, so by the time this runs the
            // property already carries the new, intended state.
            item.Click += (_, _) => setHidden(column.Id, !item.IsChecked);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var resetItem = new MenuItem { Header = LocalizationService.Instance["ColumnsReset"] };
        resetItem.Click += (_, _) => reset();
        menu.Items.Add(resetItem);

        return menu;
    }
}
