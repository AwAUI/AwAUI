using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.Threading;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>An item with a stable, non-null key unique within the tree.</summary>
/// <param name="Key">The key used for reconciliation, expansion, and selection.</param>
/// <param name="Header">The accessible name and default visible label.</param>
/// <param name="Children">The child items, or null for a leaf (or for an item whose children are loaded on demand).</param>
/// <param name="IsEnabled">Whether the item and its descendants can be focused or operated.</param>
/// <param name="HasChildren">
/// With a lazy loader and null <paramref name="Children"/>: false marks a leaf so no expander is shown; null means
/// unknown and the item is treated as expandable. Ignored when <paramref name="Children"/> is supplied.
/// </param>
public sealed record TreeViewItem(object Key, string Header, IReadOnlyList<TreeViewItem>? Children = null, bool IsEnabled = true,
    bool? HasChildren = null);

/// <summary>A hierarchical view with single selection and one tab stop among the visible, enabled items.</summary>
/// <remarks>
/// Use <see cref="Control.AriaLabel"/> or <c>aria-labelledby</c> to name the tree. Up, Down, Home, and End move focus
/// without changing selection. Right opens a branch or enters it; Left closes it or returns to its parent.
/// Clicking a row or pressing Space selects it. Enter selects and activates it. Clicking the indicator toggles expansion.
/// Supply the full hierarchy as a snapshot and replace <see cref="Items"/> when it changes. Collapsed descendants are unmounted.
/// <see cref="IsVirtualized"/> mounts only the visible rows in and near the viewport; scrolling and keyboard navigation
/// realize rows as they come into view. Items whose <see cref="TreeViewItem.Children"/> is null can load children on
/// demand through <see cref="LoadChildren"/>.
/// </remarks>
public sealed partial class TreeView : Control
{
    const float IndentPerLevel = 18;
    static readonly RovingFocusOptions s_navigation = new() { Orientation = Orientation.Vertical };
    readonly ControllableState<object?> _selection = new();
    readonly ControllableState<IReadOnlyCollection<object>> _expansion = new();
    readonly Dictionary<object, IReadOnlyList<TreeViewItem>> _lazyChildren = new();
    readonly HashSet<object> _loading = new();
    readonly IDispatcher? _dispatcher = Reactive.CurrentThreadDispatcher;
    Dictionary<object, Entry> _entries = new();
    List<Entry> _visible = new();
    object? _focusedKey;
    VirtualizerHost? _virtualizerHost;
    object? _pendingFocusKey;

    public IReadOnlyList<TreeViewItem>? Items { get; set; }

    /// <summary>Renders noninteractive content for the label. The item's header is still used as its accessible name.</summary>
    public Func<TreeViewItem, Node>? ItemTemplate { get; set; }

    /// <summary>
    /// Loads the children of an item when it is first expanded and its <see cref="TreeViewItem.Children"/> is null.
    /// Results are cached per key for the lifetime of the component; use <see cref="RefreshChildren"/> to reload.
    /// A null result or an empty list turns the item into a leaf. Failures collapse the item, call
    /// <see cref="ChildLoadFailed"/>, and are retried on the next expansion.
    /// </summary>
    public Func<TreeViewItem, Task<IReadOnlyList<TreeViewItem>>>? LoadChildren { get; set; }

    /// <summary>Reports a failed <see cref="LoadChildren"/> call after the item has collapsed.</summary>
    public Action<TreeViewItem, Exception>? ChildLoadFailed { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Controls selection when non-null. Use <see cref="IsSelectionControlled"/> to control an empty selection.</summary>
    public object? SelectedKey { get; set; }

    public bool IsSelectionControlled { get; set; }
    public object? DefaultSelectedKey { get; set; }
    public Action<object?>? SelectedKeyChanged { get; set; }

    /// <summary>The current selection key, retained even when the item is collapsed or absent from the supplied snapshot.</summary>
    public object? CurrentSelectedKey => _selection.Value;

    /// <summary>The current item, including a collapsed descendant, or null when the selection key has no matching item.</summary>
    public TreeViewItem? SelectedItem => Find(_entries, CurrentSelectedKey)?.Item;

    /// <summary>Controls expansion when supplied. Pass an empty collection to keep all branches collapsed.</summary>
    public IReadOnlyCollection<object>? ExpandedKeys { get; set; }

    public IReadOnlyCollection<object>? DefaultExpandedKeys { get; set; }
    public Action<IReadOnlyCollection<object>>? ExpandedKeysChanged { get; set; }

    /// <summary>Runs when Enter or a synthetic activation invokes an enabled item, even if it is already selected.</summary>
    public Action<TreeViewItem>? ItemActivated { get; set; }

    /// <summary>
    /// Mounts only the visible rows in and near the viewport. Off by default, which mounts every visible item.
    /// The tree needs a constrained height (CSS <c>height</c> or <c>max-height</c>) for a viewport to exist.
    /// Virtualized rows are flat tree items (indentation is applied to each row) instead of nested groups.
    /// </summary>
    public bool IsVirtualized { get; set; }

    /// <summary>
    /// Fixed row height in logical pixels when virtualized, or <c>null</c> to measure each row after layout (variable-height mode).
    /// Fixed-height rows are clipped to these bounds.
    /// </summary>
    public double? ItemHeight { get; set; } = 32;

    /// <summary>Height assumed for rows that have not been measured yet in variable-height mode.</summary>
    public double EstimatedItemHeight { get; set; } = 32;

    /// <summary>Number of rows mounted just outside each edge of the viewport while virtualized.</summary>
    public int Overscan { get; set; } = 2;

    VirtualizerHost Host => _virtualizerHost ??= new VirtualizerHost(InvalidateFromLayout);

    protected override Node Build()
    {
        var selectedKey = _selection.Sync(IsSelectionControlled || SelectedKey is not null,
            SelectedKey, DefaultSelectedKey, SelectedKeyChanged);
        var expandedKeys = _expansion.Sync(ExpandedKeys is not null, ExpandedKeys ?? Array.Empty<object>(),
            DefaultExpandedKeys ?? Array.Empty<object>(), ExpandedKeysChanged);
        var entries = new Dictionary<object, Entry>();
        var visible = new List<Entry>();
        var roots = IndexItems(Items ?? Array.Empty<TreeViewItem>(), null, true,
            new HashSet<object>(expandedKeys), entries, visible);

        var oldEntry = Find(_entries, _focusedKey);
        var oldFocus = oldEntry?.ElementRef?.Current is { IsFocused: true } focused ? focused : null;
        var tabStop = IsEnabled ? FindTabStop(entries, visible, selectedKey, oldEntry) : null;
        var tree = new Div { Role = Role.Tree };
        if (IsVirtualized)
        {
            tree.Style = "overflow:auto;";
            var host = Host;
            host.Mount(Root);
            bool fixedHeight = ItemHeight is double requested && requested > 0 && !double.IsInfinity(requested);
            double height = fixedHeight ? ItemHeight!.Value : 0;
            double estimate = EstimatedItemHeight > 0 && !double.IsInfinity(EstimatedItemHeight) ? EstimatedItemHeight : 32;
            host.SetOptions(new VirtualizerOptions
            {
                ItemSize = fixedHeight ? height : null,
                EstimatedItemSize = estimate,
                Overscan = Math.Max(0, Overscan),
            });
            var realized = host.Begin(visible.Count, tabStop?.VisibleIndex ?? -1);
            tree.ScrollController = host.Controller;
            var extent = new Div { Style = "position:relative;height:" + Pixels(host.Virtualizer.TotalSize) };
            extent.Attributes["part"] = "extent";
            string rowStyle = "position:absolute;inset-inline:0;box-sizing:border-box;overflow:clip;"
                + (fixedHeight ? "height:" + Pixels(height) + ";" : "");
            foreach (var item in realized)
            {
                var entry = visible[item.Index];
                extent.Children.Add(CreateItem(entry, tabStop, selectedKey, rowStyle + "top:" + Pixels(item.Start) + ";"));
                host.Track(item.Index, entry.ElementRef!);
            }
            tree.Children.Add(extent);
        }
        else
        {
            Host.Deactivate();
            tree.Children = RenderItems(roots, tabStop, selectedKey);
        }
        tree.Attributes["aria-disabled"] = IsEnabled ? null : "true";

        var root = oldFocus?.Root;
        bool focusVisible = oldFocus is not null && (oldFocus.States & PseudoStates.FocusVisible) != 0;
        UseEffect(() =>
        {
            if (root is not null && oldFocus is not null)
                RestoreFocus(root, oldFocus, tabStop, focusVisible);
            return null;
        }, oldFocus is null ? null : entries);

        _entries = entries;
        _visible = visible;
        if (LoadChildren is not null)
            foreach (var entry in entries.Values)
                if (entry.Expanded) BeginLoad(entry.Item);
        QueuePendingFocus(entries);
        return Apply(tree, "aw-tree-view");
    }

    protected override void OnMount() => _virtualizerHost?.Mount(Root);

    protected override void OnUnmount() => _virtualizerHost?.Unmount();

    static string Pixels(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture) + "px";

    IReadOnlyList<Entry> IndexItems(IReadOnlyList<TreeViewItem> items, Entry? parent, bool isVisible,
        HashSet<object> expandedKeys, Dictionary<object, Entry> entries, List<Entry> visible)
    {
        var result = new Entry[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item?.Key is not { } key || entries.ContainsKey(key))
                throw new ArgumentException("TreeView items must have non-null keys that are unique within the tree.", nameof(Items));

            var children = ChildrenOf(item);
            bool canExpand = CanExpand(item, children);
            bool expanded = canExpand && expandedKeys.Contains(key);
            var entry = new Entry(item, parent, expanded, canExpand, i + 1, items.Count);
            entries.Add(key, entry);
            result[i] = entry;
            if (isVisible)
            {
                entry.VisibleIndex = visible.Count;
                entry.ElementRef = Find(_entries, key)?.ElementRef ?? new Ref<Element>();
                visible.Add(entry);
            }
            if (children is { Count: > 0 })
                entry.Children = IndexItems(children, entry, isVisible && expanded, expandedKeys, entries, visible);
        }
        return result;
    }

    /// <summary>具有加载器且子项尚未加载的项也显示展开指示；加载结果为空后不再显示。</summary>
    bool CanExpand(TreeViewItem item, IReadOnlyList<TreeViewItem>? children)
    {
        if (children is { Count: > 0 }) return true;
        return item.Children is null && LoadChildren is not null && item.HasChildren != false
            && !_lazyChildren.ContainsKey(item.Key);
    }

    IReadOnlyList<TreeViewItem>? ChildrenOf(TreeViewItem item) =>
        item.Children ?? (_lazyChildren.TryGetValue(item.Key, out var loaded) ? loaded : null);

    static Entry? Find(Dictionary<object, Entry> entries, object? key) =>
        key is not null && entries.TryGetValue(key, out var entry) ? entry : null;

    static Entry? FindTabStop(Dictionary<object, Entry> entries, List<Entry> visible, object? selectedKey, Entry? former)
    {
        var current = Find(entries, former?.Item.Key);
        for (var entry = current; entry is not null; entry = entry.Parent)
            if (entry.CanFocus) return entry;
        // If an item was removed or moved into a hidden subtree, its former ancestors may still be visible.
        for (var entry = former?.Parent; entry is not null; entry = entry.Parent)
            if (Find(entries, entry.Item.Key) is { CanFocus: true } ancestor) return ancestor;
        for (var entry = Find(entries, selectedKey); entry is not null; entry = entry.Parent)
            if (entry.CanFocus) return entry;
        int first = RovingFocus.Navigate(FocusIntent.First, -1, visible.Count, i => visible[i].CanFocus, loop: false);
        return first < 0 ? null : visible[first];
    }

    void RestoreFocus(UIRoot root, Element oldFocus, Entry? tabStop, bool focusVisible)
    {
        if (root.FocusManager.FocusedElement is { } active && !ReferenceEquals(active, oldFocus)) return;
        if (tabStop is null)
        {
            oldFocus.Blur();
            return;
        }
        if (ReferenceEquals(tabStop.ElementRef?.Current, oldFocus)) return;

        // Closing or replacing a branch can detach the focused element. Wait until its replacement is laid out.
        root.RequestAfterLayout(() =>
        {
            if (!ReferenceEquals(Root, root)) return;
            if (root.FocusManager.FocusedElement is { } current && !ReferenceEquals(current, oldFocus)) return;
            if (IsEnabled && Find(_entries, tabStop.Item.Key) is { CanFocus: true, ElementRef.Current: { } next }
                && next.IsVisible && !root.IsPointerBlocked(next) && next.Focus(new FocusOptions { FocusVisible = focusVisible })) return;
            if (!oldFocus.IsFocusable) oldFocus.Blur();
        });
    }

    NodeList RenderItems(IReadOnlyList<Entry> entries, Entry? tabStop, object? selectedKey)
    {
        var items = new NodeList();
        foreach (var entry in entries)
        {
            var item = CreateItem(entry, tabStop, selectedKey, rowStyle: null);
            if (entry.Expanded && entry.Children.Count > 0)
            {
                var group = new Div { Role = Role.Group, Children = RenderItems(entry.Children, tabStop, selectedKey) };
                group.Attributes["part"] = "group";
                group.Attributes["data-state"] = "open";
                item.Children.Add(group);
            }
            items.Add(item);
        }
        return items;
    }

    /// <summary>一行树项；虚拟化时由调用方写入绝对定位样式与按层级的内边距（此时没有嵌套的 group）。</summary>
    Div CreateItem(Entry entry, Entry? tabStop, object? selectedKey, string? rowStyle)
    {
        bool enabled = IsEnabled && entry.CanFocus;
        bool selected = Equals(selectedKey, entry.Item.Key);
        bool focused = enabled && entry.ElementRef?.Current is { IsFocused: true };
        bool loading = _loading.Contains(entry.Item.Key);
        string? state = entry.HasChildren ? PartProps.OpenState(entry.Expanded) : null;

        var indicator = new Div
        {
            OnClick = entry.HasChildren ? e =>
            {
                if (e.DefaultPrevented) return;
                e.PreventDefault();
                if (IsEnabled && entry.CanFocus) SetExpanded(entry, expanded: null);
            } : null,
        };
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        indicator.Attributes["data-state"] = state;
        indicator.Attributes["data-loading"] = loading ? "true" : null;

        var label = new Div { Children = [ItemTemplate?.Invoke(entry.Item) ?? new UIText(entry.Item.Header)] };
        label.Attributes["part"] = "label";
        var row = new Div { Children = [indicator, label], OnClick = e => Click(entry, e, onItem: false) };
        SetPart(row, "row", state, selected, enabled, focused);

        var item = new Div
        {
            Key = entry.Item.Key,
            Ref = entry.ElementRef,
            Role = Role.TreeItem,
            AriaLabel = entry.Item.Header,
            TabIndex = enabled ? (ReferenceEquals(entry, tabStop) ? 0 : -1) : null,
            Children = [row],
            OnFocus = _ => { _focusedKey = entry.Item.Key; Invalidate(); },
            OnBlur = _ => Invalidate(),
            OnKeyDown = e => HandleKey(entry, e),
            OnPointerDown = enabled ? null : static e => e.PreventDefault(),
            OnClick = e => Click(entry, e, onItem: true),
        };
        if (rowStyle is not null)
            item.Style = rowStyle + "padding-inline-start:" + Pixels((entry.Level - 1) * IndentPerLevel) + ";";
        SetPart(item, "item", state, selected, enabled, focused);
        item.Attributes["aria-level"] = entry.Level.ToString(CultureInfo.InvariantCulture);
        item.Attributes["aria-posinset"] = entry.Position.ToString(CultureInfo.InvariantCulture);
        item.Attributes["aria-setsize"] = entry.SetSize.ToString(CultureInfo.InvariantCulture);
        item.Attributes["aria-selected"] = PartProps.Bool(selected);
        item.Attributes["aria-busy"] = loading ? "true" : null;
        item.Attributes["data-loading"] = loading ? "true" : null;
        item.Attributes["aria-disabled"] = enabled ? null : "true";
        item.Attributes["aria-expanded"] = entry.HasChildren ? PartProps.Bool(entry.Expanded) : null;
        return item;
    }

    static void SetPart(Div element, string part, string? state, bool selected, bool enabled, bool focused)
    {
        element.Attributes["part"] = part;
        element.Attributes["data-state"] = state;
        element.Attributes["data-selected"] = PartProps.Bool(selected);
        element.Attributes["data-disabled"] = PartProps.Bool(!enabled);
        element.Attributes["data-focused"] = PartProps.Bool(focused);
    }

    void Click(Entry entry, PointerEvent e, bool onItem)
    {
        if (!IsEnabled || !entry.CanFocus || e.DefaultPrevented) return;
        if (onItem && !ReferenceEquals(e.Target, e.CurrentTarget)) return;
        for (var target = e.Target; target is not null && !ReferenceEquals(target, e.CurrentTarget); target = target.Parent)
            if (target.IsFocusable || target.CanBeDisabled || target.Role == Role.Link) return;
        Select(entry, activate: e.IsSynthetic);
    }

    void Select(Entry entry, bool activate)
    {
        _selection.Set(entry.Item.Key);
        if (activate) ItemActivated?.Invoke(entry.Item);
    }

    void SetExpanded(Entry entry, bool? expanded)
    {
        var keys = new HashSet<object>(_expansion.Value);
        var key = entry.Item.Key;
        bool open = expanded ?? !keys.Contains(key);
        if (!(open ? keys.Add(key) : keys.Remove(key))) return;
        var snapshot = new object[keys.Count];
        keys.CopyTo(snapshot);
        _expansion.Set(Array.AsReadOnly(snapshot));
        if (open) BeginLoad(entry.Item);
    }

    /// <summary>展开尚未加载的项时启动 <see cref="LoadChildren"/>；结果缓存，失败折叠以便重试。</summary>
    void BeginLoad(TreeViewItem item)
    {
        if (LoadChildren is not { } loader) return;
        if (item.Children is not null || _lazyChildren.ContainsKey(item.Key) || item.HasChildren == false) return;
        if (!_loading.Add(item.Key)) return;

        Task<IReadOnlyList<TreeViewItem>> task;
        try
        {
            task = loader(item) ?? Task.FromResult<IReadOnlyList<TreeViewItem>>(Array.Empty<TreeViewItem>());
        }
        catch (Exception e)
        {
            CompleteLoad(item, null, e);
            return;
        }

        if (task.IsCompleted)
        {
            IReadOnlyList<TreeViewItem>? children = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            Exception? error = task.IsFaulted ? Unwrap(task.Exception) : task.IsCanceled ? new TaskCanceledException(task) : null;
            CompleteLoad(item, children, error);
            return;
        }

        task.ContinueWith(t =>
        {
            IReadOnlyList<TreeViewItem>? children = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            Exception? error = t.IsFaulted ? Unwrap(t.Exception) : t.IsCanceled ? new TaskCanceledException(t) : null;
            // 宿主有调度器时把完成回调封送 UI 线程；没有调度器时（如无头测试）调用方应自行在 UI 线程完成
            if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                dispatcher.Post(() => CompleteLoad(item, children, error));
                return;
            }
            CompleteLoad(item, children, error);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    void CompleteLoad(TreeViewItem item, IReadOnlyList<TreeViewItem>? children, Exception? error)
    {
        _loading.Remove(item.Key);
        if (!IsMounted) return;

        if (error is not null)
        {
            RemoveExpanded(item.Key);
            ChildLoadFailed?.Invoke(item, error);
            Invalidate();
            return;
        }

        _lazyChildren[item.Key] = children ?? Array.Empty<TreeViewItem>();
        if (children is not { Count: > 0 }) RemoveExpanded(item.Key); // 空结果：收起并移除展开指示
        Invalidate();
    }

    static Exception Unwrap(AggregateException? exception)
    {
        var flattened = exception?.Flatten();
        if (flattened?.InnerException is { } inner) return inner;
        return (Exception?)flattened ?? new InvalidOperationException("Child loading failed.");
    }

    void RemoveExpanded(object key)
    {
        var keys = new HashSet<object>(_expansion.Value);
        if (!keys.Remove(key)) return;
        var snapshot = new object[keys.Count];
        keys.CopyTo(snapshot);
        _expansion.Set(Array.AsReadOnly(snapshot));
    }

    /// <summary>
    /// Drops cached <see cref="LoadChildren"/> results for <paramref name="key"/>, or for every key when null,
    /// and reloads items that are still expanded.
    /// </summary>
    public void RefreshChildren(object? key = null)
    {
        if (key is null) _lazyChildren.Clear();
        else _lazyChildren.Remove(key);

        if (!IsMounted) return;
        if (key is null)
        {
            foreach (var entry in _entries.Values)
                if (entry.Expanded) BeginLoad(entry.Item);
        }
        else if (Find(_entries, key) is { Expanded: true } entry)
        {
            BeginLoad(entry.Item);
        }
        Invalidate();
    }

    void HandleKey(Entry entry, KeyEvent e)
    {
        if (!IsEnabled || !entry.CanFocus || e.DefaultPrevented || !ReferenceEquals(e.Target, e.CurrentTarget)
            || e.Modifiers != KeyModifiers.None) return;

        switch (e.Key)
        {
            case "ArrowUp": case "ArrowDown": case "Home": case "End":
                e.PreventDefault();
                int next = RovingFocus.HandleKeyDown(e.Key, e.Modifiers, entry.VisibleIndex, _visible.Count,
                    i => CanMoveTo(_visible[i]), s_navigation);
                if (next >= 0 && next != entry.VisibleIndex) FocusEntry(_visible[next]);
                break;
            case "ArrowRight":
                e.PreventDefault();
                if (!entry.HasChildren) break;
                if (!entry.Expanded) SetExpanded(entry, true);
                else
                    foreach (var child in entry.Children)
                        if (CanMoveTo(child)) { FocusEntry(child); break; }
                break;
            case "ArrowLeft":
                e.PreventDefault();
                if (entry.Expanded) SetExpanded(entry, false);
                else
                    for (var parent = entry.Parent; parent is not null; parent = parent.Parent)
                        if (CanMoveTo(parent)) { FocusEntry(parent); break; }
                break;
            case "Enter": case " ":
                e.PreventDefault();
                if (!e.IsRepeat) Select(entry, activate: e.Key == "Enter");
                break;
        }
    }

    /// <summary>虚拟化时未实现的行也可以成为导航目标（聚焦时再滚动并实现它）。</summary>
    bool CanMoveTo(Entry entry) => entry.CanFocus
        && (IsVirtualized || entry.ElementRef?.Current is { IsFocusable: true, IsVisible: true });

    void FocusEntry(Entry entry)
    {
        if (!entry.CanFocus) return;
        if (entry.ElementRef?.Current is { } element && element.IsAttached && element.IsFocusable)
        {
            element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
            return;
        }
        if (!IsVirtualized) return;
        _pendingFocusKey = entry.Item.Key;
        ScrollEntryIntoView(entry);
        Invalidate();
    }

    void ScrollEntryIntoView(Entry entry)
    {
        if (entry.VisibleIndex < 0) return;
        var host = Host;
        host.SyncViewport();
        double target = host.Virtualizer.GetOffsetForIndex(entry.VisibleIndex);
        var offset = host.Controller.Offset;
        if (Math.Abs(target - offset.Y) < 0.5) return;
        host.Controller.JumpTo(new Point(offset.X, (float)target));
    }

    /// <summary>本次构建实现了待聚焦的行之后，在布局完成时把焦点移过去。</summary>
    void QueuePendingFocus(Dictionary<object, Entry> entries)
    {
        var key = _pendingFocusKey;
        if (key is null) return;
        _pendingFocusKey = null;
        if (Root is not { } root || !entries.TryGetValue(key, out var entry)) return;
        root.RequestAfterLayout(() =>
        {
            if (!ReferenceEquals(Root, root)) return;
            if (entry.ElementRef?.Current is { IsFocusable: true } element && element.IsAttached)
                element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
        });
    }

    sealed class Entry(TreeViewItem item, Entry? parent, bool expanded, bool hasChildren, int position, int setSize)
    {
        public TreeViewItem Item { get; } = item;
        public Entry? Parent { get; } = parent;
        public bool Expanded { get; } = expanded;
        public bool HasChildren { get; } = hasChildren;
        public int Level { get; } = (parent?.Level ?? 0) + 1;
        public int Position { get; } = position;
        public int SetSize { get; } = setSize;
        public bool Enabled { get; } = item.IsEnabled && (parent?.Enabled ?? true);
        public int VisibleIndex { get; set; } = -1;
        public Ref<Element>? ElementRef { get; set; }
        public IReadOnlyList<Entry> Children { get; set; } = Array.Empty<Entry>();
        public bool CanFocus => VisibleIndex >= 0 && Enabled;
    }
}
