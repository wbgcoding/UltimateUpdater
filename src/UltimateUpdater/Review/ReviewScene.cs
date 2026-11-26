#if DEBUG
using System.Windows;

namespace UltimateUpdater.Review;

/// <summary>One surface ready to be shown: its window, plus popups and menus that are open on top of it and are shot as their own pictures.</summary>
internal sealed class ReviewScene
{
    public required Window Window { get; init; }

    /// <summary>Named open popups, context menus and drop-down lists; each ends up as its own PNG beside the window shot.</summary>
    public List<(string Name, FrameworkElement Element)> Popups { get; } = [];

    private readonly List<Action> _cleanups = [];

    /// <summary>Registers something to undo once the window is shot and before it is closed (popups shut, work stopped); run last-in first-out.</summary>
    public void AddCleanup(Action action) => _cleanups.Add(action);

    /// <summary>Runs every registered cleanup; one that throws does not stop the others. Returns the first failure, if any.</summary>
    public Exception? RunCleanups()
    {
        Exception? first = null;
        for (var i = _cleanups.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanups[i]();
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }

        _cleanups.Clear();
        return first;
    }

    /// <summary>Settings windows additionally get the tooltip and label-length checks.</summary>
    public bool IsSettings { get; init; }

    /// <summary>Opens popups once the window is shown and laid out (a popup needs a placement target that exists on screen).</summary>
    public Func<Task>? AfterShown { get; set; }

    /// <summary>Control names whose overlap is intended, with the reason, so the audit skips them.</summary>
    public Dictionary<string, string> IntentionalOverlap { get; } = new(StringComparer.Ordinal);
}

/// <summary>A stable id for one window state. Ids are dotted, lower case; the area is the group prefix a <c>area:NAME</c> selector uses.</summary>
internal sealed record ReviewSurface(string Id, string Area, string Description, Func<ReviewSession, Task<ReviewScene>> Build);
#endif
