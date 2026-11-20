using System;
using System.Collections.Generic;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// <see cref="Virtualizer"/> 的选项（对照 TanStack Virtual）。所有长度是沿滚动轴的逻辑像素。
/// </summary>
public sealed class VirtualizerOptions
{
    public static readonly VirtualizerOptions Default = new();

    /// <summary>定高：设置后所有项（行）都是这个尺寸，测量被忽略，计算全部 O(1)、不分配与项数相关的内存。</summary>
    public double? ItemSize { get; init; }

    /// <summary>变高时未测量项的估计尺寸（<see cref="EstimateSize"/> 为 null 时使用）。</summary>
    public double EstimatedItemSize { get; init; } = 50;

    /// <summary>变高时按索引估计尺寸（TanStack 的 <c>estimateSize</c>）。在数量变化或重建时对每项调用一次。</summary>
    public Func<int, double>? EstimateSize { get; init; }

    /// <summary>可见范围前后额外渲染的行数（TanStack 默认 1）。</summary>
    public int Overscan { get; init; } = 1;

    /// <summary>列表开头的内边距（计入总尺寸）。</summary>
    public double PaddingStart { get; init; }

    public double PaddingEnd { get; init; }

    /// <summary>滚动到某项时在视口开头保留的距离（例如被粘性表头遮住的高度）。</summary>
    public double ScrollPaddingStart { get; init; }

    public double ScrollPaddingEnd { get; init; }

    /// <summary>相邻行之间的间距。</summary>
    public double Gap { get; init; }

    /// <summary>列表在滚动内容中的起始偏移（列表前面还有其他内容时）。项的 <see cref="VirtualItem.Start"/> 包含它。</summary>
    public double ScrollMargin { get; init; }

    /// <summary>
    /// 每行的项数（网格）。第 i 项位于第 i / Lanes 行、第 i % Lanes 列；行高取该行各项尺寸的最大值。
    /// </summary>
    public int Lanes { get; init; } = 1;

    /// <summary>沿水平轴滚动（宿主据此读取 <see cref="ScrollController"/> 的 x 而不是 y）。</summary>
    public bool Horizontal { get; init; }

    /// <summary>粘性项的索引（如分组标题）：当前分组的标题总是包含在渲染范围内，并给出被下一个标题推走的位置。</summary>
    public IReadOnlyList<int>? StickyIndices { get; init; }

    /// <summary>一次最多渲染的项数（保护：零尺寸的项、巨大的视口或预渲染不会使渲染数量失控）。超出时优先保留与视口相交的项。</summary>
    public int MaxRenderedItems { get; init; } = 10_000;

    /// <summary>位于视口开头之上的项测量尺寸变化时，调整滚动偏移使可见内容不跳动（滚动锚定）。</summary>
    public bool AdjustScrollOnMeasure { get; init; } = true;
}

/// <summary>一个需要渲染的项。</summary>
public readonly struct VirtualItem
{
    internal VirtualItem(int index, int lane, double start, double size, bool isSticky, bool isActiveSticky, double stickyStart)
    {
        Index = index;
        Lane = lane;
        Start = start;
        Size = size;
        IsSticky = isSticky;
        IsActiveSticky = isActiveSticky;
        StickyStart = stickyStart;
    }

    public int Index { get; }

    /// <summary>列（网格时）。</summary>
    public int Lane { get; }

    /// <summary>沿滚动轴的起点（滚动内容坐标，含 <see cref="VirtualizerOptions.ScrollMargin"/> 与开头内边距）。</summary>
    public double Start { get; }

    /// <summary>尺寸（已测量的值，否则为估计值）。</summary>
    public double Size { get; }

    public double End => Start + Size;

    /// <summary>是粘性项。</summary>
    public bool IsSticky { get; }

    /// <summary>是当前吸附在视口开头的粘性项。</summary>
    public bool IsActiveSticky { get; }

    /// <summary>当前吸附的粘性项应绘制的起点（不小于滚动偏移，被下一个粘性项向上推）；其他项等于 <see cref="Start"/>。</summary>
    public double StickyStart { get; }

    public override string ToString() => FormattableString.Invariant($"#{Index} [{Start}, {End})");
}

/// <summary>渲染范围（索引，闭区间）。</summary>
public readonly struct VirtualRange
{
    internal VirtualRange(int start, int end, int overscanStart, int overscanEnd)
    {
        StartIndex = start;
        EndIndex = end;
        OverscanStartIndex = overscanStart;
        OverscanEndIndex = overscanEnd;
    }

    /// <summary>第一个与视口相交的项（没有项时为 -1）。</summary>
    public int StartIndex { get; }

    /// <summary>最后一个与视口相交的项。</summary>
    public int EndIndex { get; }

    /// <summary>含预渲染的范围。</summary>
    public int OverscanStartIndex { get; }

    public int OverscanEndIndex { get; }

    public bool IsEmpty => StartIndex < 0;

    public override string ToString() => IsEmpty ? "(empty)" : FormattableString.Invariant($"[{StartIndex}, {EndIndex}] overscan [{OverscanStartIndex}, {OverscanEndIndex}]");
}

/// <summary>
/// 虚拟化的纯计算部分（TanStack Virtual 的 <c>Virtualizer</c> 核心）：给定项数、尺寸（定高，或估计 + 测量缓存）、滚动偏移与视口尺寸，
/// 求出需要渲染的项、总尺寸，以及滚动到某项的目标偏移。
/// <list type="bullet">
/// <item>定高（<see cref="VirtualizerOptions.ItemSize"/>）：一切 O(1)，一千万项也不分配与项数成比例的内存。</item>
/// <item>变高：每行的尺寸存于树状数组，测量 O(log n)，按偏移定位 O(log n)，项数变化时 O(n) 重建。</item>
/// </list>
/// <para>
/// 与滚动的关系（design/21）：宿主在 <see cref="ScrollController.Scrolled"/> 中把偏移（<see cref="VirtualizerOptions.Horizontal"/> 时取 x）
/// 与视口尺寸写入 <see cref="ScrollOffset"/> / <see cref="ViewportSize"/>，按 <see cref="GetVirtualItems"/> 重建；把 <see cref="TotalSize"/>
/// 作为内容尺寸；<see cref="Measure"/> 返回的锚定调整量非零时用 <see cref="ScrollController.JumpTo"/> 施加；滚动到某项时用
/// <see cref="GetOffsetForIndex"/> 求目标偏移再交给 <see cref="ScrollController.ScrollTo"/>。见 <see cref="VirtualizerScrolling"/>。
/// </para>
/// </summary>
public sealed class Virtualizer
{
    VirtualizerOptions _options;
    int _count;
    int _lanes;
    int _rows;
    double _fixedExtent;          // 定高：尺寸 + 间距
    bool _fixed;

    // 变高
    double[] _itemSizes = Array.Empty<double>();   // NaN = 未测量
    double[] _rowSizes = Array.Empty<double>();
    double[] _scratch = Array.Empty<double>();
    readonly PrefixSumTree _tree = new();

    int[] _sticky = Array.Empty<int>();
    readonly List<VirtualItem> _items = new();
    double _scrollOffset;
    double _viewport;

    public Virtualizer(int count, VirtualizerOptions? options = null)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        _options = options ?? VirtualizerOptions.Default;
        _count = count;
        Rebuild(keepMeasurements: false);
    }

    public VirtualizerOptions Options => _options;

    /// <summary>更换选项。行结构（列数、定高与否）不变时保留已测量的尺寸。</summary>
    public void SetOptions(VirtualizerOptions options)
    {
        var old = _options;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        bool keep = Math.Max(1, old.Lanes) == Math.Max(1, options.Lanes) && old.ItemSize.HasValue == options.ItemSize.HasValue;
        Rebuild(keep);
    }

    /// <summary>项数。</summary>
    public int Count => _count;

    /// <summary>每次影响布局的变化加一。</summary>
    public int Version { get; private set; }

    /// <summary>是否为定高模式。</summary>
    public bool IsFixedSize => _fixed;

    /// <summary>当前滚动偏移（滚动容器坐标，沿滚动轴）。</summary>
    public double ScrollOffset
    {
        get => _scrollOffset;
        set => _scrollOffset = Sanitize(value, _scrollOffset);
    }

    /// <summary>视口尺寸（沿滚动轴）。</summary>
    public double ViewportSize
    {
        get => _viewport;
        set => _viewport = Math.Max(0, Sanitize(value, _viewport));
    }

    /// <summary>行数。</summary>
    public int RowCount => _rows;

    /// <summary>列表的总尺寸（开头内边距 + 各行 + 间距 + 结尾内边距；不含 <see cref="VirtualizerOptions.ScrollMargin"/>）。</summary>
    public double TotalSize
    {
        get
        {
            double pad = Finite(_options.PaddingStart) + Finite(_options.PaddingEnd);
            if (_rows == 0) return Math.Max(0, pad);
            double sum = _fixed ? _rows * _fixedExtent : _tree.Prefix(_rows);
            return Math.Max(0, sum - Gap + pad);
        }
    }

    double Gap => Math.Max(0, Finite(_options.Gap));

    double Base => Finite(_options.PaddingStart) + Finite(_options.ScrollMargin);

    /// <summary>改变项数（变高时新项用估计尺寸，已有项的测量保留）。</summary>
    public void SetCount(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == _count) return;
        if (!_fixed)
        {
            EnsureItemCapacity(count);
            for (int i = _count; i < count; i++) _itemSizes[i] = double.NaN;
        }
        _count = count;
        Rebuild(keepMeasurements: true);
    }

    /// <summary>在 <paramref name="index"/> 处插入项：其后项的测量随之后移（与集合的插入通知配合）。</summary>
    public void InsertItems(int index, int count)
    {
        if (index < 0 || index > _count) throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 0 || (long)_count + count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        if (!_fixed)
        {
            EnsureItemCapacity(_count + count);
            Array.Copy(_itemSizes, index, _itemSizes, index + count, _count - index);
            for (int i = index; i < index + count; i++) _itemSizes[i] = double.NaN;
        }
        _count += count;
        Rebuild(keepMeasurements: true);
    }

    /// <summary>删除 [index, index + count) 的项。</summary>
    public void RemoveItems(int index, int count)
    {
        if (index < 0 || count < 0 || (long)index + count > _count) throw new ArgumentOutOfRangeException(nameof(index));
        if (count == 0) return;
        if (!_fixed) Array.Copy(_itemSizes, index + count, _itemSizes, index, _count - index - count);
        _count -= count;
        Rebuild(keepMeasurements: true);
    }

    /// <summary>把 [from, from + count) 的项移到删除后的位置 <paramref name="to"/>（与 <c>NotifyCollectionChangedAction.Move</c> 相同的编号）。</summary>
    public void MoveItems(int from, int to, int count)
    {
        if (from < 0 || count < 0 || (long)from + count > _count || to < 0 || (long)to + count > _count)
            throw new ArgumentOutOfRangeException(nameof(from));
        if (count == 0 || from == to) return;
        if (!_fixed)
        {
            var block = new double[count];
            Array.Copy(_itemSizes, from, block, 0, count);
            Array.Copy(_itemSizes, from + count, _itemSizes, from, _count - from - count);
            Array.Copy(_itemSizes, to, _itemSizes, to + count, _count - count - to);
            Array.Copy(block, 0, _itemSizes, to, count);
        }
        Rebuild(keepMeasurements: true);
    }

    /// <summary>清除全部测量（例如宽度变化导致所有项需要重新测量）。</summary>
    public void ResetMeasurements()
    {
        if (_fixed) return;
        for (int i = 0; i < _count; i++) _itemSizes[i] = double.NaN;
        Rebuild(keepMeasurements: true);
    }

    /// <summary>该项是否已测量（定高模式恒为 true）。</summary>
    public bool IsMeasured(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        return _fixed || !double.IsNaN(_itemSizes[index]);
    }

    /// <summary>
    /// 记录项的实测尺寸（TanStack 的 <c>resizeItem</c>）。返回为保持可见内容不动而施加到 <see cref="ScrollOffset"/> 的调整量
    /// （该项所在行位于视口开头之上且行尺寸变化时非零），宿主应把滚动容器同步移动这个量。定高模式下忽略测量，返回 0。
    /// 非有限或负的尺寸被忽略。
    /// </summary>
    public double Measure(int index, double size)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_fixed || double.IsNaN(size) || double.IsInfinity(size) || size < 0) return 0;
        double old = _itemSizes[index];
        if (old == size) return 0;
        _itemSizes[index] = size;
        int row = index / _lanes;
        double newRow = ComputeRowSize(row);
        double delta = newRow - _rowSizes[row];
        if (delta == 0) return 0;
        double start = RowStart(row);
        _rowSizes[row] = newRow;
        _tree.Add(row, delta);
        Version++;
        if (_options.AdjustScrollOnMeasure && start < _scrollOffset)
        {
            _scrollOffset += delta;
            return delta;
        }
        return 0;
    }

    /// <summary>项的位置与尺寸。</summary>
    public VirtualItem GetItem(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        return MakeItem(index, false, 0);
    }

    /// <summary>覆盖偏移 <paramref name="offset"/> 的项（偏移落在间距或列表之外时取之前最近的一项，再钳制到 [0, Count)）；没有项时为 -1。</summary>
    public int GetIndexAtOffset(double offset)
    {
        if (_count == 0) return -1;
        return Math.Min(_count - 1, RowAt(offset, strict: false) * _lanes);
    }

    /// <summary>当前滚动偏移与视口下的渲染范围。</summary>
    public VirtualRange GetRange()
    {
        if (_count == 0) return new VirtualRange(-1, -1, -1, -1);
        int startRow = FirstVisibleRow(_scrollOffset);
        int endRow = Math.Max(startRow, RowAt(_scrollOffset + _viewport, strict: true));
        int overscan = Math.Max(0, _options.Overscan);
        int os = (int)Math.Max(0, (long)startRow - overscan);
        int oe = (int)Math.Min(_rows - 1, (long)endRow + overscan);
        return new VirtualRange(
            startRow * _lanes,
            (int)Math.Min(_count - 1, (long)endRow * _lanes + _lanes - 1),
            os * _lanes,
            (int)Math.Min(_count - 1, (long)oe * _lanes + _lanes - 1));
    }

    /// <summary>需要渲染的项（含预渲染与当前粘性项，按索引升序）。返回的列表在下次调用时被复用。</summary>
    public IReadOnlyList<VirtualItem> GetVirtualItems()
    {
        _items.Clear();
        var range = GetRange();
        if (range.IsEmpty) return _items;
        int first = range.OverscanStartIndex;
        int last = range.OverscanEndIndex;
        int cap = Math.Max(1, _options.MaxRenderedItems);
        if ((long)last - first + 1 > cap)
        {
            // 超过上限时优先保留与视口相交的项，剩余名额平分给前后的预渲染（不能只从预渲染的开头截取，否则可见项会被挤掉）
            long visible = (long)range.EndIndex - range.StartIndex + 1;
            if (visible >= cap)
            {
                first = range.StartIndex;
                last = first + cap - 1;
            }
            else
            {
                int spare = cap - (int)visible;
                int before = Math.Min(range.StartIndex - first, spare / 2);
                int after = Math.Min(last - range.EndIndex, spare - before);
                before = Math.Min(range.StartIndex - first, spare - after);
                first = range.StartIndex - before;
                last = range.EndIndex + after;
            }
        }

        int active = -1;
        double activeStart = 0;
        if (_sticky.Length > 0)
        {
            int k = UpperBound(_sticky, range.StartIndex) - 1;
            if (k >= 0)
            {
                active = _sticky[k];
                var a = MakeItem(active, false, 0);
                activeStart = Math.Max(_scrollOffset, a.Start);
                if (k + 1 < _sticky.Length)
                {
                    var next = MakeItem(_sticky[k + 1], false, 0);
                    activeStart = Math.Min(activeStart, next.Start - a.Size);
                }
            }
        }
        if (active >= 0 && active < first) _items.Add(MakeItem(active, true, activeStart));
        for (int i = first; i <= last; i++)
            _items.Add(MakeItem(i, i == active, activeStart));
        return _items;
    }

    /// <summary>
    /// 滚动到第 <paramref name="index"/> 项的目标偏移（TanStack 的 <c>getOffsetForIndex</c>）：
    /// <see cref="ScrollAlignment.Nearest"/> 时已完整可见则返回当前偏移，否则对齐到较近的一端；结果钳制到 [0, 最大偏移]。
    /// </summary>
    public double GetOffsetForIndex(int index, ScrollAlignment alignment = ScrollAlignment.Nearest)
    {
        if (_count == 0) return 0;
        index = Math.Max(0, Math.Min(_count - 1, index));
        var item = MakeItem(index, false, 0);
        double padStart = Finite(_options.ScrollPaddingStart);
        double padEnd = Finite(_options.ScrollPaddingEnd);
        if (alignment == ScrollAlignment.Nearest)
        {
            if (item.End >= _scrollOffset + _viewport - padEnd) alignment = ScrollAlignment.End;
            else if (item.Start <= _scrollOffset + padStart) alignment = ScrollAlignment.Start;
            else return _scrollOffset;
        }
        double to = alignment == ScrollAlignment.End ? item.End + padEnd : item.Start - padStart;
        if (alignment == ScrollAlignment.Center) to += (item.Size - _viewport) / 2;
        else if (alignment == ScrollAlignment.End) to -= _viewport;
        return ClampOffset(to);
    }

    /// <summary>最大滚动偏移（总尺寸 + ScrollMargin − 视口）。</summary>
    public double MaxScrollOffset => Math.Max(0, TotalSize + Finite(_options.ScrollMargin) - _viewport);

    double ClampOffset(double v) => Math.Max(0, Math.Min(MaxScrollOffset, v));

    // ------------------------------------------------------------ 内部

    VirtualItem MakeItem(int index, bool activeSticky, double stickyStart)
    {
        int row = index / _lanes;
        double start = RowStart(row);
        double size = _fixed ? _fixedExtent - Gap : ItemSize(index);
        bool isSticky = _sticky.Length > 0 && Array.BinarySearch(_sticky, index) >= 0;
        return new VirtualItem(index, index % _lanes, start, size, isSticky, activeSticky, activeSticky ? stickyStart : start);
    }

    double RowStart(int row) => Base + (_fixed ? row * _fixedExtent : _tree.Prefix(row));

    /// <summary>
    /// 与 [offset, ∞) 相交的第一行：末端在 offset 之后的行，或起点不早于 offset 的零尺寸行，取较前者；钳制到 [0, rows)。
    /// 与「起点 ≤ offset 的最后一行」不同之处在于零尺寸的行（估计为 0、折叠的项）：它们与下一行同起点，
    /// 不能因此被跳过——否则开头的零尺寸项永远不进入渲染范围，也就永远得不到测量。
    /// </summary>
    int FirstVisibleRow(double offset)
    {
        if (_rows == 0) return 0;
        double t = offset - Base;
        if (!(t > 0)) return 0;
        if (_fixed)
        {
            if (_fixedExtent <= 0) return _rows - 1;   // 全部行都在开头、尺寸为 0，且都在 t 之前
            double size = _fixedExtent - Gap;
            double est = size > 0
                ? (t < size ? 0 : Math.Floor((t - size) / _fixedExtent) + 1)
                : Math.Ceiling(t / _fixedExtent);
            if (est >= _rows) return _rows - 1;
            int row = (int)est;
            // 浮点误差修正（估计值至多差一行）
            while (row > 0 && FixedRowVisible(row - 1, t, size)) row--;
            while (row < _rows - 1 && !FixedRowVisible(row, t, size)) row++;
            return row;
        }
        // 末端 ≤ t 的行数：第 j − 1 行的末端 = prefix(j) − gap
        int endedBefore = _tree.Search(t + Gap, strict: false);
        // 起点 < t 的最后一行是 Search(t, strict)（prefix(k) 即第 k 行的起点；t > 0 时第 0 行总在其中），其后一行是第一个起点 ≥ t 的行
        int startsBefore = _tree.Search(t, strict: true) + 1;
        return Math.Max(0, Math.Min(_rows - 1, Math.Min(endedBefore, startsBefore)));
    }

    bool FixedRowVisible(int row, double t, double size)
    {
        double start = row * _fixedExtent;
        return start + size > t || start >= t;
    }

    /// <summary>起点 ≤ offset（strict：&lt;）的最后一行，钳制到 [0, rows)。</summary>
    int RowAt(double offset, bool strict)
    {
        if (_rows == 0) return 0;
        double t = offset - Base;
        if (t < 0 || (strict && t <= 0)) return 0;
        int row;
        if (_fixed)
        {
            if (_fixedExtent <= 0) return _rows - 1;   // 全部行都在同一位置
            double r = t / _fixedExtent;
            if (strict) r = Math.Ceiling(r) - 1;
            row = r >= _rows ? _rows - 1 : (int)Math.Floor(r);
        }
        else
        {
            // 前 k 行之和 ≤ t 的最大 k 就是该行的序号
            row = _tree.Search(t, strict);
        }
        return Math.Max(0, Math.Min(_rows - 1, row));
    }

    double ItemSize(int index)
    {
        double s = _itemSizes[index];
        return double.IsNaN(s) ? Estimate(index) : s;
    }

    double Estimate(int index)
    {
        var f = _options.EstimateSize;
        double v = f is null ? _options.EstimatedItemSize : f(index);
        return double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? 0 : v;
    }

    double ComputeRowSize(int row)
    {
        int first = row * _lanes;
        int last = Math.Min(_count, first + _lanes);
        double max = 0;
        for (int i = first; i < last; i++) max = Math.Max(max, ItemSize(i));
        return max;
    }

    void Rebuild(bool keepMeasurements)
    {
        _lanes = Math.Max(1, _options.Lanes);
        _rows = (int)(((long)_count + _lanes - 1) / _lanes);
        _sticky = NormalizeSticky(_options.StickyIndices);
        var fixedSize = _options.ItemSize;
        _fixed = fixedSize.HasValue;
        Version++;
        if (_fixed)
        {
            double s = fixedSize!.Value;
            if (double.IsNaN(s) || double.IsInfinity(s) || s < 0) s = 0;
            _fixedExtent = s + Gap;
            _itemSizes = Array.Empty<double>();
            _rowSizes = Array.Empty<double>();
            _tree.Build(Array.Empty<double>(), 0);
            return;
        }
        EnsureItemCapacity(_count);
        if (!keepMeasurements)
            for (int i = 0; i < _count; i++) _itemSizes[i] = double.NaN;
        if (_rowSizes.Length < _rows) _rowSizes = new double[Math.Max(_rows, _rowSizes.Length * 2)];
        if (_scratch.Length < _rows) _scratch = new double[_rowSizes.Length];
        double gap = Gap;
        for (int r = 0; r < _rows; r++)
        {
            double size = ComputeRowSize(r);
            _rowSizes[r] = size;
            _scratch[r] = size + gap;
        }
        _tree.Build(_scratch, _rows);
    }

    void EnsureItemCapacity(int count)
    {
        if (_itemSizes.Length >= count) return;
        var n = new double[Math.Max(count, Math.Min(int.MaxValue / 2, _itemSizes.Length * 2))];
        Array.Copy(_itemSizes, n, _itemSizes.Length);
        for (int i = _itemSizes.Length; i < n.Length; i++) n[i] = double.NaN;
        _itemSizes = n;
    }

    int[] NormalizeSticky(IReadOnlyList<int>? sticky)
    {
        if (sticky is null || sticky.Count == 0) return Array.Empty<int>();
        var list = new List<int>(sticky.Count);
        foreach (var i in sticky)
            if (i >= 0 && i < _count) list.Add(i);
        list.Sort();
        int w = 0;
        for (int r = 0; r < list.Count; r++)
            if (w == 0 || list[w - 1] != list[r]) list[w++] = list[r];
        list.RemoveRange(w, list.Count - w);
        return list.ToArray();
    }

    static int UpperBound(int[] a, int value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    static double Sanitize(double v, double fallback) => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;
}
