using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// ObservableCollection that can take a whole batch at once. The table's view groups and filters,
/// and every individual Add makes it re-group the entire collection, with a large scan that alone
/// stalled the window. AddRange raises a single change notification instead.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;

        CheckReentrancy();

        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        // Reset rather than Add: a grouped/filtered view handles one reset far more cheaply than
        // one incremental notification per item.
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Same idea as <see cref="AddRange"/> for the other direction: a caller dropping several
    /// rows in one pass (a stale-row sweep, a finished-row cleanup) raises one Reset instead of one
    /// Remove per row, each of which would re-run the bound view's whole filter.</summary>
    public void RemoveRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;

        CheckReentrancy();

        var removedAny = false;
        foreach (var item in items)
            removedAny |= Items.Remove(item);

        if (!removedAny)
            return;

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
