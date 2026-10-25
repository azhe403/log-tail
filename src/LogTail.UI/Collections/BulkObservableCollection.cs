using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace LogTail.UI.Collections;

/// <summary>
/// ObservableCollection with single-notification bulk operations for
/// large log lists. Per-item Add/RemoveAt fires N UI updates which
/// janks at 50k items; these helpers mutate the backing list once and
/// raise a single collection notification.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Append many items with a single Add notification.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // List<T> implements both IList<T> and the non-generic IList the
        // notification needs, so the cast below can never fail after the items
        // were already added to the backing collection.
        var list = items as List<T> ?? items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var startIndex = Items.Count;
        foreach (var item in list)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, list, startIndex));
    }

    /// <summary>
    /// Remove count items from the front with a single Reset notification.
    /// O(n) once via List.RemoveRange instead of O(n*m) RemoveAt(0) loop.
    /// </summary>
    public void RemoveFromFront(int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (count >= Count)
        {
            Clear();
            return;
        }

        if (Items is List<T> list)
        {
            list.RemoveRange(0, count);
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                Items.RemoveAt(0);
            }
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
