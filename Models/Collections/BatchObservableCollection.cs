using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GitManager.Models;

public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        if (Count == items.Count && this.SequenceEqual(items)) return;
        CheckReentrancy();
        Items.Clear();
        foreach (T item in items) Items.Add(item);
        // WPF 不支持范围变更；一次重置避免几万个文件逐项通知并触发布局。
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
