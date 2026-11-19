using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 列表类控件共用的选择模型，语义对照 Avalonia 的 <c>SelectionModel&lt;T&gt;</c>：
/// 单选 / 多选、锚点（Shift 范围选择的起点）、按索引与按项读写、批量更新合并为一次 <see cref="SelectionChanged"/>，
/// 并跟随数据源的 <see cref="INotifyCollectionChanged"/> 调整索引。
/// <para>
/// 选中的索引以有序区间存放（见 <see cref="IndexRange"/>）：全选一千万项是 O(1) 内存，<see cref="SelectedIndexes"/> /
/// <see cref="SelectedItems"/> 是惰性视图。
/// </para>
/// <para>数据源变更的处理：</para>
/// <list type="bullet">
/// <item>插入：其后的选中索引后移；插入点落在选中区间内部时区间被拆开，新项不被选中。引发 <see cref="IndexesChanged"/>。</item>
/// <item>删除 / 替换：被删除（替换掉）的选中项失去选择，<see cref="SelectionChanged"/> 报告它们（旧编号与旧项）；全部失去时引发 <see cref="LostSelection"/>。</item>
/// <item>移动：选择跟随被移动的项（与 Avalonia 把移动当作删除 + 插入、从而丢失选择不同）。</item>
/// <item>重置：单选模式下，若原选中项仍在新数据中，保留它（索引更新为新位置）；否则（以及多选模式）清空选择。之后引发 <see cref="SourceReset"/>。</item>
/// <item>更换 <see cref="Source"/>：从 null 换成数据源时保留已有的索引选择（裁掉越界部分）并应用此前设置的 <see cref="SelectedItem"/>；
/// 从一个数据源换成另一个时按「重置」处理。</item>
/// </list>
/// <para>
/// 不是线程安全的：只在 UI 线程使用；数据源的变更通知也必须在 UI 线程引发。模型强引用数据源的事件：长寿的集合请在不用时把 <see cref="Source"/> 设为 null。
/// </para>
/// </summary>
public class SelectionModel<T> : INotifyPropertyChanged
{
    ItemsView? _view;
    IEnumerable<T>? _source;
    readonly IndexRangeList _ranges = new();
    RangeIndexList? _indexesView;   // SelectedIndexes 的快照，按 _ranges.Version 缓存
    int _indexesViewVersion;
    int _selectedIndex = -1;
    int _anchorIndex = -1;
    bool _singleSelect = true;

    // 主选中项的缓存：每个操作结束时（包括批量更新内部）按当前数据源刷新，因此在集合变更通知到达时仍是变更前的项
    // （通知到达时数据源已经改变，不能再按旧索引去取）。数据源重置后旧项只能从这里得到。
    bool _hasCachedItem;
    T _cachedItem = default!;

    // 数据源为 null 时设置的 SelectedItem
    bool _hasPendingItem;
    T _pendingItem = default!;

    // 批量更新
    int _batchDepth;
    IndexRangeList? _snapshot;
    int _snapSelectedIndex;
    int _snapAnchor;
    bool _snapHasItem;
    T _snapItem = default!;

    readonly IEqualityComparer<T> _comparer;

    public SelectionModel() : this(null, null) { }

    public SelectionModel(IEnumerable<T>? source) : this(source, null) { }

    public SelectionModel(IEnumerable<T>? source, IEqualityComparer<T>? comparer)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        if (source is not null) Source = source;
    }

    // ------------------------------------------------------------ 事件

    /// <summary>选中集合变化（一次操作或一次批量更新只引发一次）。</summary>
    public event EventHandler<SelectionModelSelectionChangedEventArgs<T>>? SelectionChanged;

    /// <summary>数据源插入 / 删除使选中索引平移。</summary>
    public event EventHandler<SelectionModelIndexesChangedEventArgs>? IndexesChanged;

    /// <summary>数据源的删除、替换或重置使选择变空（<see cref="SelectionMode.AlwaysSelected"/> 的控件据此重新选择一项）。</summary>
    public event EventHandler? LostSelection;

    /// <summary>数据源重置（<see cref="NotifyCollectionChangedAction.Reset"/>）处理完毕。</summary>
    public event EventHandler? SourceReset;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ------------------------------------------------------------ 属性

    /// <summary>数据源。实现 <see cref="IReadOnlyList{T}"/> / <see cref="IList"/> 的直接按索引访问，其他可枚举对象复制一份快照。</summary>
    public IEnumerable<T>? Source
    {
        get => _source;
        set => SetSource(value);
    }

    /// <summary>是否单选（默认 true）。从多选切换为单选时只保留 <see cref="SelectedIndex"/>。</summary>
    public bool SingleSelect
    {
        get => _singleSelect;
        set
        {
            if (_singleSelect == value) return;
            _singleSelect = value;
            Raise(nameof(SingleSelect));
            if (value && _ranges.Count > 1)
            {
                BeginBatchUpdate();
                try
                {
                    int keep = _selectedIndex;
                    _ranges.Clear();
                    if (keep >= 0) _ranges.Add(keep, keep);
                }
                finally { EndBatchUpdate(); }
            }
        }
    }

    /// <summary>主选中索引：第一个被选中的（多选时之后再选的项不改变它；它被取消时变为最小的选中索引）。设置它会清除其他选择。</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            BeginBatchUpdate();
            try
            {
                ClearCore();
                if (value >= 0) SelectCore(value, setAnchor: true);
            }
            finally { EndBatchUpdate(); }
        }
    }

    /// <summary>
    /// 全部选中的索引（升序）。返回的是取值时的快照（不展开区间，按区间数复制）：之后的选择变化不影响它，
    /// 因此可以一边枚举一边取消选择。选择不变时重复读取返回同一个对象。
    /// </summary>
    public IReadOnlyList<int> SelectedIndexes
    {
        get
        {
            if (_ranges.IsEmpty) return RangeIndexList.Empty;
            if (_indexesView is null || _indexesViewVersion != _ranges.Version)
            {
                _indexesView = new RangeIndexList(_ranges.ToArray());
                _indexesViewVersion = _ranges.Version;
            }
            return _indexesView;
        }
    }

    /// <summary>主选中项。数据源为 null 时可以先设置，赋予数据源后再解析为索引。设置为不在数据源中的项会清空选择。</summary>
    public T SelectedItem
    {
        get
        {
            if (_view is null) return _hasPendingItem ? _pendingItem : default!;
            return _selectedIndex >= 0 ? ItemAt(_selectedIndex) : default!;
        }
        set
        {
            if (_view is null)
            {
                BeginBatchUpdate();
                try
                {
                    ClearCore();
                    _hasPendingItem = true;
                    _pendingItem = value;
                }
                finally { EndBatchUpdate(); }
                return;
            }
            int index = _view.IndexOf(value, _comparer);
            SelectedIndex = index;
        }
    }

    /// <summary>全部选中的项（按索引升序）：索引是取值时的快照（见 <see cref="SelectedIndexes"/>），项在读取时按当前数据源解析。</summary>
    public IReadOnlyList<T> SelectedItems
    {
        get
        {
            if (_view is null && _hasPendingItem) return new[] { _pendingItem };
            return new RangeItemList<T>((RangeIndexList)SelectedIndexes, ItemAt);
        }
    }

    /// <summary>范围选择的锚点。</summary>
    public int AnchorIndex
    {
        get => _anchorIndex;
        set
        {
            int v = value < 0 || (_view is not null && value >= _view.Count) ? -1 : value;
            if (v == _anchorIndex) return;
            _anchorIndex = v;
            Raise(nameof(AnchorIndex));
        }
    }

    /// <summary>选中的项数。</summary>
    public int Count => _ranges.Count;

    /// <summary>选中区间（升序、不相交）。</summary>
    public IReadOnlyList<IndexRange> SelectedRanges => _ranges.Ranges;

    /// <summary>数据源的项数（没有数据源时为 0）。</summary>
    public int ItemCount => _view?.Count ?? 0;

    // ------------------------------------------------------------ 操作

    public bool IsSelected(int index) => index >= 0 && _ranges.Contains(index);

    /// <summary>选中一项并设为锚点；单选时替换原选择。越界索引被忽略。</summary>
    public void Select(int index)
    {
        BeginBatchUpdate();
        try { SelectCore(index, setAnchor: true); }
        finally { EndBatchUpdate(); }
    }

    public void Deselect(int index)
    {
        BeginBatchUpdate();
        try
        {
            if (index < 0) return;
            _ranges.Remove(index, index);
            if (index == _selectedIndex) _selectedIndex = _ranges.First;
        }
        finally { EndBatchUpdate(); }
    }

    /// <summary>选中 [start, end]（两端可以颠倒，越界部分被裁掉）；不改变锚点。单选模式下不可用。</summary>
    public void SelectRange(int start, int end)
    {
        if (_singleSelect) throw new InvalidOperationException("单选模式下不能选择范围。");
        if (start > end) (start, end) = (end, start);
        start = Math.Max(0, start);
        if (_view is not null) end = Math.Min(end, _view.Count - 1);
        if (end < start) return;
        BeginBatchUpdate();
        try
        {
            _ranges.Add(start, end);
            if (_selectedIndex < 0) _selectedIndex = start;
            _hasPendingItem = false;
        }
        finally { EndBatchUpdate(); }
    }

    public void DeselectRange(int start, int end)
    {
        if (start > end) (start, end) = (end, start);
        start = Math.Max(0, start);
        if (end < start) return;
        BeginBatchUpdate();
        try
        {
            _ranges.Remove(start, end);
            if (_selectedIndex >= start && _selectedIndex <= end) _selectedIndex = _ranges.First;
        }
        finally { EndBatchUpdate(); }
    }

    /// <summary>全选（需要数据源；单选模式下不可用）。</summary>
    public void SelectAll()
    {
        if (_singleSelect) throw new InvalidOperationException("单选模式下不能全选。");
        if (_view is null || _view.Count == 0) return;
        SelectRange(0, _view.Count - 1);
    }

    /// <summary>清空选择（锚点保留，供随后的范围选择使用）。</summary>
    public void Clear()
    {
        BeginBatchUpdate();
        try { ClearCore(); }
        finally { EndBatchUpdate(); }
    }

    /// <summary>开始批量更新：直到配对的 <see cref="EndBatchUpdate"/>，变化合并为一次事件。可嵌套。</summary>
    public void BeginBatchUpdate()
    {
        if (_batchDepth++ == 0) TakeSnapshot();
    }

    public void EndBatchUpdate()
    {
        if (_batchDepth == 0) throw new InvalidOperationException("EndBatchUpdate 没有配对的 BeginBatchUpdate。");
        UpdateCache();
        if (--_batchDepth == 0) RaiseDiff();
    }

    /// <summary><c>using (model.BatchUpdate()) { ... }</c>。</summary>
    public BatchUpdateOperation BatchUpdate()
    {
        BeginBatchUpdate();
        return new BatchUpdateOperation(this);
    }

    /// <summary>批量更新的作用域。</summary>
    public readonly struct BatchUpdateOperation : IDisposable
    {
        readonly SelectionModel<T>? _owner;

        internal BatchUpdateOperation(SelectionModel<T> owner) => _owner = owner;

        public void Dispose() => _owner?.EndBatchUpdate();
    }

    // ------------------------------------------------------------ 内部操作（调用方负责批量作用域）

    void SelectCore(int index, bool setAnchor)
    {
        if (index < 0 || (_view is not null && index >= _view.Count)) return;
        _hasPendingItem = false;
        if (_singleSelect)
        {
            _ranges.Clear();
            _ranges.Add(index, index);
            _selectedIndex = index;
        }
        else
        {
            _ranges.Add(index, index);
            if (_selectedIndex < 0) _selectedIndex = index;
        }
        if (setAnchor) _anchorIndex = index;
    }

    void ClearCore()
    {
        _ranges.Clear();
        _selectedIndex = -1;
        _hasPendingItem = false;
    }

    T ItemAt(int index) => _view is not null && index >= 0 && index < _view.Count ? _view[index] : default!;

    void TakeSnapshot()
    {
        _snapshot = _ranges.Clone();
        _snapSelectedIndex = _selectedIndex;
        _snapAnchor = _anchorIndex;
        _snapHasItem = _view is null ? _hasPendingItem : _selectedIndex >= 0;
        // 用缓存而不是按索引取：批量更新中收到集合变更时，这里的数据源已经改变
        _snapItem = _view is null ? (_hasPendingItem ? _pendingItem : default!) : (_hasCachedItem ? _cachedItem : default!);
    }

    /// <param name="addedItemAt">按事件中的编号取新选中的项；null 时按当前数据源取。</param>
    /// <param name="removedItemAt">按事件中的编号取失去选择的项；null 时按当前数据源取（数据源没有更换时两者相同）。</param>
    /// <param name="itemsUnknown">数据源已被重置、旧编号的项不可得：项列表只包含缓存的主选中项（若它在其中）。</param>
    void RaiseDiff(Func<int, T>? addedItemAt = null, Func<int, T>? removedItemAt = null, bool itemsUnknown = false)
    {
        var before = _snapshot!;
        _snapshot = null;
        // 先把快照字段取到局部：SelectionChanged 的处理器可能再次修改选择（嵌套的批量更新会覆盖这些字段）
        int snapSelectedIndex = _snapSelectedIndex;
        int snapAnchor = _snapAnchor;
        bool snapHasItem = _snapHasItem;
        T snapItem = _snapItem;
        _snapItem = default!;
        var added = IndexRangeList.Except(_ranges, before);
        var removed = IndexRangeList.Except(before, _ranges);
        if (added.Count > 0 || removed.Count > 0)
        {
            var addedIdx = new RangeIndexList(added);
            var removedIdx = new RangeIndexList(removed);
            IReadOnlyList<T> addedItems, removedItems;
            if (itemsUnknown)
            {
                addedItems = CachedItemIfIn(addedIdx);
                removedItems = CachedItemIfIn(removedIdx);
            }
            else
            {
                addedItems = new RangeItemList<T>(addedIdx, addedItemAt ?? ItemAt);
                removedItems = new RangeItemList<T>(removedIdx, removedItemAt ?? ItemAt);
            }
            SelectionChanged?.Invoke(this, new SelectionModelSelectionChangedEventArgs<T>(addedIdx, removedIdx, addedItems, removedItems));
        }
        if (snapSelectedIndex != _selectedIndex) Raise(nameof(SelectedIndex));
        bool hasItem = _view is null ? _hasPendingItem : _selectedIndex >= 0;
        T currentItem = _view is null ? (_hasPendingItem ? _pendingItem : default!) : (_hasCachedItem ? _cachedItem : default!);
        if (hasItem != snapHasItem || (hasItem && !_comparer.Equals(snapItem, currentItem))) Raise(nameof(SelectedItem));
        if (snapAnchor != _anchorIndex) Raise(nameof(AnchorIndex));
        if (before.Count != _ranges.Count) Raise(nameof(Count));
    }

    IReadOnlyList<T> CachedItemIfIn(RangeIndexList indexes) =>
        _hasCachedItem && _selectedIndex >= 0 && indexes.Count > 0 && IndexRangeContains(indexes, _selectedIndex) ? new[] { _cachedItem } : Array.Empty<T>();

    static bool IndexRangeContains(RangeIndexList indexes, int index)
    {
        // RangeIndexList 升序：二分
        int lo = 0, hi = indexes.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int v = indexes[mid];
            if (v == index) return true;
            if (v < index) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    void UpdateCache()
    {
        if (_view is not null && _selectedIndex >= 0)
        {
            _hasCachedItem = true;
            _cachedItem = ItemAt(_selectedIndex);
        }
        else
        {
            _hasCachedItem = false;
            _cachedItem = default!;
        }
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ------------------------------------------------------------ 数据源

    void SetSource(IEnumerable<T>? value)
    {
        if (ReferenceEquals(value, _source)) return;
        if (_batchDepth > 0) FlushBatch();
        else TakeSnapshot();
        var oldView = _view;
        if (oldView is not null) oldView.Detach(OnCollectionChanged);
        _source = value;
        _view = value is null ? null : new ItemsView(value);
        _view?.Attach(OnCollectionChanged);
        Raise(nameof(Source));
        try
        {
            if (_view is null)
            {
                // 失去数据源：选择保持为索引（无法再解析项）
            }
            else if (oldView is null)
            {
                _ranges.Trim(_view.Count);
                if (_selectedIndex >= _view.Count) _selectedIndex = _ranges.First;
                if (_anchorIndex >= _view.Count) _anchorIndex = -1;
                if (_hasPendingItem)
                {
                    var item = _pendingItem;
                    _hasPendingItem = false;
                    int idx = _view.IndexOf(item, _comparer);
                    ClearCore();
                    if (idx >= 0) SelectCore(idx, setAnchor: true);
                }
            }
            else
            {
                ApplyReset(out _);
            }
        }
        finally
        {
            // 更换数据源立即作为一次变化报告；批量更新进行中则随后重新开始累积。
            // 失去选择的项按旧数据源（变更前编号）取，而不是新数据源中同一位置的项
            UpdateCache();
            RaiseDiff(removedItemAt: oldView is null ? null : i => i >= 0 && i < oldView.Count ? oldView[i] : default!);
            if (_batchDepth > 0) TakeSnapshot();
        }
    }

    /// <summary>重置后的选择：单选且旧选中项仍在 → 保留；否则清空。返回是否保留。</summary>
    void ApplyReset(out bool kept)
    {
        kept = false;
        int idx = -1;
        if (_singleSelect && _hasCachedItem && _view is not null) idx = _view.IndexOf(_cachedItem, _comparer);
        _ranges.Clear();
        _selectedIndex = -1;
        _anchorIndex = -1;
        if (idx >= 0)
        {
            SelectCore(idx, setAnchor: true);
            kept = true;
        }
    }

    /// <summary>批量更新进行中收到集合变更：先把已累积的变化作为一次事件发出，再处理变更。</summary>
    void FlushBatch(Func<int, T>? itemAt = null, bool itemsUnknown = false)
    {
        RaiseDiff(itemAt, itemAt, itemsUnknown);
        TakeSnapshot();
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_view is null) return;
        // 已累积的变化按变更前的编号报告；而通知到达时数据源已经改变，所以项要经变更的逆映射去取
        if (_batchDepth > 0)
        {
            if (_view.IsSnapshot) FlushBatch();
            else
            {
                var resolver = PreChangeItemResolver(e);
                FlushBatch(resolver, itemsUnknown: resolver is null);
            }
        }
        _view.Refresh();
        int oldCount = _ranges.Count;
        int oldSelected = _selectedIndex;
        int oldAnchor = _anchorIndex;
        bool hadItem = _selectedIndex >= 0;
        bool hadCachedItem = _hasCachedItem;
        var oldItem = _cachedItem;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0 && e.NewItems is not null:
                OnInserted(e.NewStartingIndex, e.NewItems.Count);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is not null:
                OnRemoved(e.OldStartingIndex, e.OldItems, insertCount: 0);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldStartingIndex >= 0 && e.OldItems is not null && e.NewItems is not null:
                OnRemoved(e.OldStartingIndex, e.OldItems, insertCount: e.NewItems.Count);
                break;
            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0 && e.OldItems is not null:
                OnMoved(e.OldStartingIndex, e.NewStartingIndex, e.OldItems.Count);
                break;
            default:
                OnReset(oldItem, hadCachedItem);
                break;
        }
        UpdateCache();
        if (oldSelected != _selectedIndex) Raise(nameof(SelectedIndex));
        if (hadItem != (_selectedIndex >= 0) || (hadItem && !_comparer.Equals(oldItem, SelectedItem))) Raise(nameof(SelectedItem));
        if (oldAnchor != _anchorIndex) Raise(nameof(AnchorIndex));
        if (oldCount != _ranges.Count) Raise(nameof(Count));
        if (_batchDepth > 0) TakeSnapshot();
    }

    /// <summary>按变更前的编号取项（数据源已经应用了 <paramref name="e"/>）。重置后旧项不可得，返回 null。</summary>
    Func<int, T>? PreChangeItemResolver(NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0 && e.NewItems is not null:
            {
                int at = e.NewStartingIndex, n = e.NewItems.Count;
                return j => ItemAt(j < at ? j : j + n);
            }
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is not null:
            {
                int at = e.OldStartingIndex;
                var old = e.OldItems;
                return j => j < at ? ItemAt(j) : j < at + old.Count ? (old[j - at] is T t ? t : default!) : ItemAt(j - old.Count);
            }
            case NotifyCollectionChangedAction.Replace when e.OldStartingIndex >= 0 && e.OldItems is not null && e.NewItems is not null:
            {
                int at = e.OldStartingIndex, added = e.NewItems.Count;
                var old = e.OldItems;
                return j => j < at ? ItemAt(j) : j < at + old.Count ? (old[j - at] is T t ? t : default!) : ItemAt(j - old.Count + added);
            }
            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0 && e.OldItems is not null:
            {
                int from = e.OldStartingIndex, to = e.NewStartingIndex, n = e.OldItems.Count;
                return j => ItemAt(MapMove(j, from, to, n));
            }
            default:
                return null;
        }
    }

    void OnInserted(int index, int count)
    {
        if (count <= 0) return;
        bool shifts = !_ranges.IsEmpty && _ranges.Ranges[_ranges.Ranges.Count - 1].End >= index;
        _ranges.Insert(index, count);
        if (_selectedIndex >= index) _selectedIndex += count;
        if (_anchorIndex >= index) _anchorIndex += count;
        if (shifts) IndexesChanged?.Invoke(this, new SelectionModelIndexesChangedEventArgs(index, count));
    }

    void OnRemoved(int index, IList oldItems, int insertCount)
    {
        int count = oldItems.Count;
        bool wasEmpty = _ranges.IsEmpty;
        bool shifts = !_ranges.IsEmpty && _ranges.Ranges[_ranges.Ranges.Count - 1].End >= index + count && count != insertCount;
        var removed = _ranges.RemoveItems(index, count);
        if (insertCount > 0) _ranges.Insert(index, insertCount);
        int delta = insertCount - count;
        if (_selectedIndex >= index + count) _selectedIndex += delta;
        else if (_selectedIndex >= index) _selectedIndex = _ranges.First;
        if (_anchorIndex >= index + count) _anchorIndex += delta;
        else if (_anchorIndex >= index) _anchorIndex = -1;
        if (shifts) IndexesChanged?.Invoke(this, new SelectionModelIndexesChangedEventArgs(index + count, delta));
        if (removed.Count > 0)
        {
            var idx = new RangeIndexList(removed);
            var items = new T[idx.Count];
            for (int i = 0; i < items.Length; i++) items[i] = oldItems[idx[i] - index] is T t ? t : default!;
            SelectionChanged?.Invoke(this, new SelectionModelSelectionChangedEventArgs<T>(
                RangeIndexList.Empty, idx, Array.Empty<T>(), items));
            if (!wasEmpty && _ranges.IsEmpty) LostSelection?.Invoke(this, EventArgs.Empty);
        }
    }

    void OnMoved(int from, int to, int count)
    {
        if (count <= 0 || from == to) return;
        // 记下被移动块内的选中区间（相对块首），删除后在新位置重新加入
        var inBlock = new List<IndexRange>();
        foreach (var r in _ranges.Ranges)
        {
            if (r.End < from) continue;
            if (r.Begin > from + count - 1) break;
            inBlock.Add(new IndexRange(Math.Max(r.Begin, from) - from, Math.Min(r.End, from + count - 1) - from));
        }
        _ranges.RemoveItems(from, count);
        _ranges.Insert(to, count);
        foreach (var r in inBlock) _ranges.Add(r.Begin + to, r.End + to);
        _selectedIndex = MapMove(_selectedIndex, from, to, count);
        _anchorIndex = MapMove(_anchorIndex, from, to, count);
    }

    static int MapMove(int i, int from, int to, int count)
    {
        if (i < 0) return i;
        if (i >= from && i < from + count) return to + (i - from);
        // 先删除再插入
        int j = i >= from + count ? i - count : i;
        return j >= to ? j + count : j;
    }

    void OnReset(T oldItem, bool hadItem)
    {
        var before = _ranges.Clone();
        ApplyReset(out bool kept);
        if (!before.IsEmpty && !kept)
        {
            var idx = new RangeIndexList(before.Ranges);
            IReadOnlyList<T> items = _singleSelect && hadItem ? new[] { oldItem } : Array.Empty<T>();
            SelectionChanged?.Invoke(this, new SelectionModelSelectionChangedEventArgs<T>(RangeIndexList.Empty, idx, Array.Empty<T>(), items));
            LostSelection?.Invoke(this, EventArgs.Empty);
        }
        SourceReset?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ 数据源视图

    sealed class ItemsView
    {
        readonly IEnumerable<T> _source;
        readonly IReadOnlyList<T>? _readOnly;
        readonly IList<T>? _list;
        readonly IList? _untyped;
        List<T>? _copy;

        public ItemsView(IEnumerable<T> source)
        {
            _source = source;
            if (source is IReadOnlyList<T> ro) _readOnly = ro;
            else if (source is IList<T> l) _list = l;
            else if (source is IList u) _untyped = u;
            else _copy = new List<T>(source);
        }

        public int Count => _readOnly?.Count ?? _list?.Count ?? _untyped?.Count ?? _copy!.Count;

        /// <summary>是复制的快照（变更通知到达后、<see cref="Refresh"/> 之前仍是变更前的内容）。</summary>
        public bool IsSnapshot => _copy is not null;

        public T this[int index] =>
            _readOnly is not null ? _readOnly[index]
            : _list is not null ? _list[index]
            : _untyped is not null ? (_untyped[index] is T t ? t : default!)
            : _copy![index];

        public int IndexOf(T item, IEqualityComparer<T> comparer)
        {
            int n = Count;
            for (int i = 0; i < n; i++)
                if (comparer.Equals(this[i], item)) return i;
            return -1;
        }

        /// <summary>快照型视图在变更后重新复制（O(n)；只用于既不是列表、又引发变更通知的数据源）。</summary>
        public void Refresh()
        {
            if (_copy is not null) _copy = new List<T>(_source);
        }

        public void Attach(NotifyCollectionChangedEventHandler handler)
        {
            if (_source is INotifyCollectionChanged incc) incc.CollectionChanged += handler;
        }

        public void Detach(NotifyCollectionChangedEventHandler handler)
        {
            if (_source is INotifyCollectionChanged incc) incc.CollectionChanged -= handler;
        }
    }
}
