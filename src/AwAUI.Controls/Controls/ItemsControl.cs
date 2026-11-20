using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>Presents a sequence of data or nodes. Every item is rendered.</summary>
public partial class ItemsControl : Control
{
    ItemView? _items;

    /// <summary>
    /// The items to present. Assign a new source to replace a non-observable sequence. Observable sources
    /// can also change in place and must raise their notifications on the UI thread.
    /// </summary>
    public IEnumerable? ItemsSource { get; set; }

    /// <summary>Builds a node for each item. By default, nodes are presented directly and other items as text.</summary>
    public Func<object?, Node>? ItemTemplate { get; set; }

    /// <summary>Wraps the rendered items in a custom layout, such as a <see cref="StackPanel"/>.</summary>
    public Func<NodeList, Node>? ItemsPanel { get; set; }

    /// <summary>Static items to present when <see cref="ItemsSource"/> is null.</summary>
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var items = ReadItems();
        var children = new NodeList();
        for (int i = 0; i < items.Count; i++) children.Add(PresentItem(items[i]));
        return Apply(new Div { Children = PresentItems(children) }, "aw-items-control");
    }

    protected Node PresentItem(object? item) =>
        ItemTemplate?.Invoke(item) ?? (item is Node node ? node : new UIText(item?.ToString()));

    protected NodeList PresentItems(NodeList items) => ItemsPanel is { } panel ? [panel(items)] : items;

    private protected ItemView ReadItems()
    {
        var source = ItemsSource ?? Children;
        if (_items is not { } items || !ReferenceEquals(items.Original, source))
            _items = items = new ItemView(source, _items);

        UseEffect(() =>
        {
            if (source is not INotifyCollectionChanged observable) return null;
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (ReferenceEquals(_items, items)) OnItemsChanged(() => items.Apply(args));
            };
            observable.CollectionChanged += changed;
            // Child mount effects can change the source before this subscription is attached.
            try
            {
                if (items.ReadChangedRows() is { } rows) OnItemsChanged(() => items.Reset(rows));
            }
            catch
            {
                observable.CollectionChanged -= changed;
                throw;
            }
            return () => observable.CollectionChanged -= changed;
        }, items);
        return items;
    }

    private protected virtual void OnItemsChanged(Action apply)
    {
        apply();
        Invalidate();
    }

    // A row identifies one occurrence, even when values are equal or null. Keep it on add and move
    // so focused options stay mounted and input handlers can resolve their current index.
    internal sealed class ItemRow
    {
        public Ref<Element>? ElementRef { get; set; }
    }

    internal sealed class ItemView : IReadOnlyList<object?>, INotifyCollectionChanged
    {
        public readonly record struct Entry(object? Value, ItemRow Row);

        List<Entry> _rows;

        public ItemView(IEnumerable? source, ItemView? previous)
        {
            Original = source;
            _rows = ReadRows(source, previous?._rows);
        }

        public IEnumerable? Original { get; }
        public int Count => _rows.Count;
        public object? this[int index] => _rows[index].Value;
        public ItemRow RowAt(int index) => _rows[index].Row;

        public int IndexOf(ItemRow row)
        {
            for (int i = 0; i < Count; i++)
                if (ReferenceEquals(_rows[i].Row, row)) return i;
            return -1;
        }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public IEnumerator<object?> GetEnumerator()
        {
            foreach (var row in _rows) yield return row.Value;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public List<Entry>? ReadChangedRows()
        {
            var rows = ReadRows(Original, _rows);
            if (rows.Count != Count) return rows;
            for (int i = 0; i < rows.Count; i++)
            {
                var value = rows[i].Value;
                if (!ReferenceEquals(value, this[i]) && !(value is ValueType && Equals(value, this[i])))
                    return rows;
            }
            return null;
        }

        public void Apply(NotifyCollectionChangedEventArgs args)
        {
            int from = args.OldStartingIndex;
            int to = args.NewStartingIndex;
            int removed = args.OldItems?.Count ?? 0;
            bool canRemove = removed > 0 && from >= 0 && from <= Count - removed;
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add when to >= 0 && to <= Count && args.NewItems is { } added:
                    _rows.InsertRange(to, ReadRows(added));
                    break;
                case NotifyCollectionChangedAction.Remove when canRemove:
                    _rows.RemoveRange(from, removed);
                    break;
                case NotifyCollectionChangedAction.Replace when canRemove && from == to && args.NewItems is { } replacements:
                    var newRows = ReadRows(replacements);
                    _rows.RemoveRange(from, removed);
                    _rows.InsertRange(to, newRows);
                    break;
                case NotifyCollectionChangedAction.Move when canRemove && to >= 0 && to <= Count - removed:
                    var moved = _rows.GetRange(from, removed);
                    _rows.RemoveRange(from, removed);
                    _rows.InsertRange(to, moved);
                    break;
                default:
                    Reset(ReadRows(Original, _rows));
                    return;
            }
            CollectionChanged?.Invoke(this, args);
        }

        public void Reset(List<Entry> rows)
        {
            _rows = rows;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        static List<Entry> ReadRows(IEnumerable? items, List<Entry>? previous = null)
        {
            var rows = new List<Entry>();
            if (items is not null)
            {
                foreach (var item in items)
                {
                    int index = rows.Count;
                    var row = previous is not null && index < previous.Count && Matches(previous[index].Value, item)
                        ? previous[index].Row : new ItemRow();
                    rows.Add(new Entry(item, row));
                }
            }
            return rows;
        }

        // Parents can recreate an equivalent sequence while updating a selection prop. Keep its
        // option elements mounted when the data matches at the same position.
        static bool Matches(object? oldValue, object? value) =>
            oldValue is Node oldNode && value is Node node
                ? oldNode.GetType() == node.GetType() && Equals(oldNode.Key, node.Key)
                : Equals(oldValue, value);
    }
}
