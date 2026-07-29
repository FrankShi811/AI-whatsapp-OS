using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WAFlow.Desktop.Collections;

/// <summary>
/// Replaces a bound list with one reset notification so WPF performs one
/// layout pass instead of rebuilding the visual tree once per item.
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
