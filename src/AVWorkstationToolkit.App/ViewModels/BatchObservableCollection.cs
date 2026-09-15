using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AVWorkstationToolkit.App.ViewModels;

internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public int ResetCount { get; private set; }

    public bool ReplaceAll(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Count == values.Count && this.SequenceEqual(values)) return false;

        CheckReentrancy();
        Items.Clear();
        foreach (var value in values) Items.Add(value);

        ResetCount++;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        using (BlockReentrancy())
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }
}
