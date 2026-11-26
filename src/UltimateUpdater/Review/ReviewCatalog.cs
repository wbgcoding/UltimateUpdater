#if DEBUG
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ContextMenu = System.Windows.Controls.ContextMenu;
using ToolTip = System.Windows.Controls.ToolTip;

namespace UltimateUpdater.Review;

/// <summary>
/// Every window state the harness can open, by stable id. Ids follow the real navigation path
/// (<c>area.window.state</c>), one state per id; the list is grouped by area, one partial file each.
/// </summary>
internal static partial class ReviewCatalog
{
    private static IReadOnlyList<ReviewSurface>? _all;

    /// <summary>Built on first use: the partial files hold static tables whose initialisation order is not defined.</summary>
    public static IReadOnlyList<ReviewSurface> All => _all ??= Build();

    private static List<ReviewSurface> Build()
    {
        var list = new List<ReviewSurface>();
        AddMain(list);
        AddDialogs(list);
        AddSettings(list);
        AddCleanup(list);
        AddUninstaller(list);

        var duplicate = list.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Review surface id '{duplicate.Key}' is registered twice.");

        return list;
    }

    private static void Add(List<ReviewSurface> list, string id, string area, string description, Func<ReviewSession, Task<ReviewScene>> build) =>
        list.Add(new ReviewSurface(id, area, description, build));

    /// <summary>Waits on the dispatcher until the condition holds; false when the time ran out.</summary>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                return false;

            await Task.Delay(100);
        }

        return true;
    }

    /// <summary>Opens a context menu on a target and registers it for its own shot; the cleanup shuts it again.</summary>
    internal static void OpenMenu(ReviewScene scene, string name, ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        scene.Popups.Add((name, menu));
        scene.AddCleanup(() => menu.IsOpen = false);
    }

    /// <summary>Shows an element's tooltip the way hovering would, and registers it for its own shot.</summary>
    internal static void OpenToolTip(ReviewScene scene, string name, FrameworkElement owner)
    {
        if (owner.ToolTip is null)
            return;

        var tip = owner.ToolTip as ToolTip ?? new ToolTip { Content = owner.ToolTip };
        tip.PlacementTarget = owner;
        tip.Placement = PlacementMode.Bottom;
        tip.IsOpen = true;
        scene.Popups.Add((name, tip));
        scene.AddCleanup(() => tip.IsOpen = false);
    }
}
#endif
