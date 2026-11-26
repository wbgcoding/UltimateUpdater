using System.Collections.Concurrent;
using System.Windows.Threading;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>The row icons: read from files and the registry on pool threads, handed to the rows on the UI thread.</summary>
public sealed partial class UninstallerViewModel
{
    /// <summary>How many icons are read at the same time: enough to fill the list quickly, few enough to leave the disk to the user.</summary>
    private const int IconWorkers = 3;

    /// <summary>Runs an action on the UI thread. Null: the owner's dispatcher at Background priority (tests set their own).</summary>
    internal Action<Action>? PostToUi { get; set; }

    /// <summary>Completes when every icon read queued so far has finished; for tests.</summary>
    internal Task IconsLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Queues the icon read of every row that has not had one yet. Never blocks: the reads run on pool threads and each
    /// finished icon is handed to its row through the UI thread. A row without an icon keeps its kind's symbol.
    /// </summary>
    internal void ResolveIcons()
    {
        var pending = _all.Where(row => row.WantsIcon && !row.IconRequested).ToList();
        if (pending.Count == 0)
            return;

        foreach (var row in pending)
            row.IconRequested = true;

        var post = PostToUi ?? DispatcherPost();
        var queue = new ConcurrentQueue<UninstallRowViewModel>(pending);
        var workers = Enumerable.Range(0, Math.Min(IconWorkers, pending.Count))
            .Select(_ => Task.Run(() => ReadIcons(queue, post)))
            .Prepend(IconsLoaded);

        IconsLoaded = Task.WhenAll(workers);
    }

    private static Action<Action> DispatcherPost()
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        return action => dispatcher.InvokeAsync(action, DispatcherPriority.Background);
    }

    private void ReadIcons(ConcurrentQueue<UninstallRowViewModel> queue, Action<Action> post)
    {
        while (queue.TryDequeue(out var row))
        {
            try
            {
                var entry = row.Entry;
                if (_backend.ResolveIcon(entry) is not { } icon)
                    continue;

                icon.Freeze();
                post(() => row.Icon = icon);
            }
            catch (Exception ex)
            {
                // One entry with a locked file or an odd registry value must not stop the others.
                _logging.Warn($"Uninstaller icon failed for '{row.Name}': {ex.Message}");
            }
        }
    }
}
