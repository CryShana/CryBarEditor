using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;

namespace CryBarEditor.Classes;

public class ObservableCollectionExtended<T> : ObservableCollection<T>
{
    internal static readonly PropertyChangedEventArgs CountPropertyChanged = new PropertyChangedEventArgs("Count");
    internal static readonly PropertyChangedEventArgs IndexerPropertyChanged = new PropertyChangedEventArgs("Item[]");

    protected override void InsertItem(int index, T item)
    {
        // base.InsertItem already fires Count/Item[] PropertyChanged and CollectionChanged Add
        base.InsertItem(index, item);
    }

    public void AddItems(IEnumerable<T> items_to_add)
    {
        CheckReentrancy();

        var index = Count;
        foreach (var item in items_to_add)
            Items.Insert(index++, item);
        
        OnPropertyChanged(CountPropertyChanged);
        OnPropertyChanged(IndexerPropertyChanged);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
