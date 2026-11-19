using System;
using System.Collections;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// <see cref="SelectionModel{T}.SelectionChanged"/> 的参数（Avalonia 的 <c>SelectionModelSelectionChangedEventArgs&lt;T&gt;</c>）。
/// 索引列表是区间上的惰性视图；项列表按需从数据源取（因删除而失去选择的项取自集合变更通知里的旧项）。
/// </summary>
public sealed class SelectionModelSelectionChangedEventArgs<T> : EventArgs
{
    internal SelectionModelSelectionChangedEventArgs(IReadOnlyList<int> selectedIndexes, IReadOnlyList<int> deselectedIndexes,
        IReadOnlyList<T> selectedItems, IReadOnlyList<T> deselectedItems)
    {
        SelectedIndexes = selectedIndexes;
        DeselectedIndexes = deselectedIndexes;
        SelectedItems = selectedItems;
        DeselectedItems = deselectedItems;
    }

    /// <summary>新选中的索引（当前编号）。</summary>
    public IReadOnlyList<int> SelectedIndexes { get; }

    /// <summary>
    /// 失去选择的索引。普通的取消选择用当前编号；因数据源删除、替换而失去的选择用变更前的编号（这些项已不在数据源中）。
    /// 数据源重置（Reset）时为重置前的编号。
    /// </summary>
    public IReadOnlyList<int> DeselectedIndexes { get; }

    public IReadOnlyList<T> SelectedItems { get; }

    /// <summary>失去选择的项。数据源重置后旧项已不可得：此时只包含单选模式下缓存的旧选中项（若它没有被保留）。</summary>
    public IReadOnlyList<T> DeselectedItems { get; }
}

/// <summary>数据源插入 / 删除使选中索引整体平移（Avalonia 的 <c>SelectionModelIndexesChangedEventArgs</c>）。</summary>
public sealed class SelectionModelIndexesChangedEventArgs : EventArgs
{
    internal SelectionModelIndexesChangedEventArgs(int startIndex, int delta)
    {
        StartIndex = startIndex;
        Delta = delta;
    }

    /// <summary>从这个索引（变更前编号）开始的选中索引平移了 <see cref="Delta"/>。</summary>
    public int StartIndex { get; }

    public int Delta { get; }
}

/// <summary>选择模式（与 Avalonia 的 <c>SelectionMode</c> 相同的标志位）。</summary>
[Flags]
public enum SelectionMode
{
    /// <summary>单选。</summary>
    Single = 0,

    /// <summary>多选（Shift 范围、Ctrl/Cmd 切换）。</summary>
    Multiple = 1,

    /// <summary>不按修饰键时点击也切换选中状态。</summary>
    Toggle = 2,

    /// <summary>总是至少选中一项（交互不会清空选择）。</summary>
    AlwaysSelected = 4,
}

/// <summary>按区间取项的惰性列表。</summary>
internal sealed class RangeItemList<T> : IReadOnlyList<T>
{
    readonly RangeIndexList _indexes;
    readonly Func<int, T> _get;

    public RangeItemList(RangeIndexList indexes, Func<int, T> get)
    {
        _indexes = indexes;
        _get = get;
    }

    public int Count => _indexes.Count;

    public T this[int index] => _get(_indexes[index]);

    public IEnumerator<T> GetEnumerator()
    {
        foreach (var i in _indexes) yield return _get(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
