using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>
/// Presents a sequence of data or nodes. By default every item is rendered; set <see cref="IsVirtualized"/>
/// to mount only the rows in and near the viewport.
/// </summary>
public partial class ItemsControl : Control
{
    ItemView? _items;
    VirtualizerHost? _virtualizerHost;

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

    /// <summary>
    /// Mounts only the rows in and near the viewport. Off by default, which mounts every item. The control needs a
    /// constrained height (CSS <c>height</c> or <c>max-height</c>) for a viewport to exist, and <see cref="ItemsPanel"/>
    /// is ignored while virtualized.
    /// </summary>
    public bool IsVirtualized { get; set; }

    /// <summary>
    /// Fixed row height in logical pixels when virtualized, or <c>null</c> to measure each row after layout (variable-height mode).
    /// Fixed-height rows are clipped to these bounds. Ignored when <see cref="Orientation"/> is horizontal.
    /// </summary>
    public double? ItemHeight { get; set; } = 32;

    /// <summary>Height assumed for rows that have not been measured yet in variable-height mode.</summary>
    public double EstimatedItemHeight { get; set; } = 32;

    /// <summary>
    /// Fixed item width in logical pixels when virtualized horizontally, or <c>null</c> to measure each item after layout.
    /// Ignored in the default vertical mode.
    /// </summary>
    public double? ItemWidth { get; set; } = 32;

    /// <summary>Width assumed for items that have not been measured yet in horizontal variable-width mode.</summary>
    public double EstimatedItemWidth { get; set; } = 32;

    /// <summary>
    /// Main axis of the virtualized list. Horizontal mode scrolls right, uses <see cref="ItemWidth"/> /
    /// <see cref="EstimatedItemWidth"/>, and needs a constrained width.
    /// </summary>
    public Orientation Orientation { get; set; } = Primitives.Orientation.Vertical;

    /// <summary>Number of rows mounted just outside each edge of the viewport while virtualized.</summary>
    public int Overscan { get; set; } = 2;

    private protected VirtualizerHost Host => _virtualizerHost ??= new VirtualizerHost(InvalidateFromLayout);

    protected override Node Build()
    {
        var items = ReadItems();
        return IsVirtualized
            ? Apply(BuildVirtualized(items), "aw-items-control")
            : Apply(BuildAll(items), "aw-items-control");
    }

    Div BuildAll(ItemView items)
    {
        // 从虚拟化切回普通挂载：停止布局回写，避免旧宿主继续请求重建
        _virtualizerHost?.Deactivate();
        var children = new NodeList();
        for (int i = 0; i < items.Count; i++) children.Add(PresentItem(items[i]));
        return new Div { Children = PresentItems(children) };
    }

    Div BuildVirtualized(ItemView items)
    {
        var host = Host;
        host.Mount(Root);
        bool horizontal = Orientation == Orientation.Horizontal;
        double? requestedSize = horizontal ? ItemWidth : ItemHeight;
        bool fixedSize = requestedSize is double requested && requested > 0 && !double.IsInfinity(requested);
        double size = fixedSize ? requestedSize!.Value : 0;
        double estimated = horizontal ? EstimatedItemWidth : EstimatedItemHeight;
        double estimate = estimated > 0 && !double.IsInfinity(estimated) ? estimated : 32;
        // 定长与变长是两种项结构：SetOptions 在两者之间切换时会丢弃测量
        host.SetOptions(new VirtualizerOptions
        {
            ItemSize = fixedSize ? size : null,
            EstimatedItemSize = estimate,
            Overscan = Math.Max(0, Overscan),
            Horizontal = horizontal,
        });
        var realized = host.Begin(items.Count);
        // 外层才是滚动容器；内层按总尺寸撑开并作为绝对定位项的定位上下文
        var viewport = new Div { Style = "overflow:auto;" };
        viewport.ScrollController = host.Controller;
        var extent = new Div
        {
            Style = horizontal
                ? "position:relative;width:" + Pixels(host.Virtualizer.TotalSize) + ";height:100%"
                : "position:relative;height:" + Pixels(host.Virtualizer.TotalSize),
        };
        extent.Attributes["part"] = "extent";
        // 变长项不写固定尺寸：布局后按内容尺寸测量，再把尺寸写回虚拟化器
        string rowStyle = horizontal
            ? "position:absolute;inset-block:0;box-sizing:border-box;overflow:clip;"
                + (fixedSize ? "width:" + Pixels(size) + ";" : "")
            : "position:absolute;inset-inline:0;box-sizing:border-box;overflow:clip;"
                + (fixedSize ? "height:" + Pixels(size) + ";" : "");
        string setSize = items.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var item in realized)
        {
            // Key 与 Ref 都取自该项的 ItemRow：集合重建后同一项仍复用同一个行元素与滚动状态
            var row = items.RowAt(item.Index);
            var reference = row.ElementRef ??= new Ref<Element>();
            host.Track(item.Index, reference);
            var node = new Div
            {
                Key = row,
                Ref = reference,
                Style = rowStyle + (horizontal ? "inset-inline-start:" : "top:") + Pixels(item.Start) + ";",
                Children = [PresentItem(items[item.Index])],
            };
            node.Attributes["part"] = "item";
            node.Attributes["data-index"] = item.Index.ToString(CultureInfo.InvariantCulture);
            node.Attributes["aria-posinset"] = (item.Index + 1).ToString(CultureInfo.InvariantCulture);
            node.Attributes["aria-setsize"] = setSize;
            extent.Children.Add(node);
        }
        viewport.Children.Add(extent);
        return viewport;
    }

    protected override void OnMount() => _virtualizerHost?.Mount(Root);

    protected override void OnUnmount() => _virtualizerHost?.Unmount();

    private protected static string Pixels(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture) + "px";

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
