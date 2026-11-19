using System;
using System.Collections;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>闭区间索引范围 [Begin, End]。</summary>
public readonly struct IndexRange : IEquatable<IndexRange>
{
    public IndexRange(int begin, int end)
    {
        if (end < begin) throw new ArgumentException("End 不能小于 Begin。");
        Begin = begin;
        End = end;
    }

    public int Begin { get; }

    public int End { get; }

    public int Count => End - Begin + 1;

    public bool Contains(int index) => index >= Begin && index <= End;

    public bool Equals(IndexRange o) => Begin == o.Begin && End == o.End;

    public override bool Equals(object? obj) => obj is IndexRange r && Equals(r);

    public override int GetHashCode() => HashCodeCompat.Combine(Begin, End);

    public override string ToString() => Begin == End ? Begin.ToString(System.Globalization.CultureInfo.InvariantCulture) : FormattableString.Invariant($"{Begin}..{End}");
}

/// <summary>
/// 有序、互不相交、不相邻的闭区间集合。选择模型用它存放选中的索引：全选一千万项只占一个区间，
/// 插入 / 删除项时的索引平移与区间数成线性关系，与项数无关。
/// </summary>
internal sealed class IndexRangeList
{
    readonly List<IndexRange> _ranges = new();
    long _count;

    public IReadOnlyList<IndexRange> Ranges => _ranges;

    /// <summary>集合中的索引总数（没有数据源约束时可能超过 int 范围，此时钳制为 <see cref="int.MaxValue"/>）。</summary>
    public int Count => (int)Math.Min(_count, int.MaxValue);

    public bool IsEmpty => _ranges.Count == 0;

    public int First => _ranges.Count == 0 ? -1 : _ranges[0].Begin;

    /// <summary>每次内容变化加一（选择模型据此缓存 <see cref="RangeIndexList"/> 快照）。</summary>
    public int Version { get; private set; }

    /// <summary>区间的副本（快照视图用，之后的修改不影响它）。</summary>
    public IndexRange[] ToArray() => _ranges.ToArray();

    public IndexRangeList Clone()
    {
        var c = new IndexRangeList();
        c._ranges.AddRange(_ranges);
        c._count = _count;
        return c;
    }

    public void Clear()
    {
        if (_ranges.Count == 0) return;
        _ranges.Clear();
        _count = 0;
        Version++;
    }

    /// <summary>第一个 End ≥ index 的区间序号。</summary>
    int LowerBound(int index)
    {
        int lo = 0, hi = _ranges.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ranges[mid].End < index) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public bool Contains(int index)
    {
        int i = LowerBound(index);
        return i < _ranges.Count && _ranges[i].Begin <= index;
    }

    /// <summary>加入 [begin, end]；返回新增的索引数。</summary>
    public int Add(int begin, int end)
    {
        if (end < begin) return 0;
        // 与 begin - 1 相邻的区间也要合并
        int i = LowerBound(begin == int.MinValue ? begin : begin - 1);
        long existing = 0;
        int newBegin = begin, newEnd = end;
        int j = i;
        while (j < _ranges.Count && (_ranges[j].Begin <= (end == int.MaxValue ? end : end + 1)))
        {
            var r = _ranges[j];
            existing += Math.Max(0, (long)Math.Min(r.End, end) - Math.Max(r.Begin, begin) + 1);
            if (r.Begin < newBegin) newBegin = r.Begin;
            if (r.End > newEnd) newEnd = r.End;
            j++;
        }
        long added = ((long)end - begin + 1) - existing;
        _ranges.RemoveRange(i, j - i);
        _ranges.Insert(i, new IndexRange(newBegin, newEnd));
        _count += added;
        Version++;
        return (int)Math.Min(added, int.MaxValue);
    }

    /// <summary>移除 [begin, end]；返回移除的索引数。</summary>
    public int Remove(int begin, int end)
    {
        if (end < begin || _ranges.Count == 0) return 0;
        int i = LowerBound(begin);
        long removed = 0;
        var replacement = new List<IndexRange>(2);
        int j = i;
        while (j < _ranges.Count && _ranges[j].Begin <= end)
        {
            var r = _ranges[j];
            removed += (long)Math.Min(r.End, end) - Math.Max(r.Begin, begin) + 1;
            if (r.Begin < begin) replacement.Add(new IndexRange(r.Begin, begin - 1));
            if (r.End > end) replacement.Add(new IndexRange(end + 1, r.End));
            j++;
        }
        if (j == i) return 0;
        _ranges.RemoveRange(i, j - i);
        _ranges.InsertRange(i, replacement);
        _count -= removed;
        Version++;
        return (int)Math.Min(removed, int.MaxValue);
    }

    /// <summary>在 <paramref name="index"/> 处插入 <paramref name="count"/> 个未选中的项：其后的索引后移（跨越插入点的区间被拆开）。</summary>
    public void Insert(int index, int count)
    {
        if (count <= 0) return;
        Version++;
        int i = LowerBound(index);
        if (i < _ranges.Count && _ranges[i].Begin < index)
        {
            var r = _ranges[i];
            _ranges[i] = new IndexRange(r.Begin, index - 1);
            _ranges.Insert(i + 1, new IndexRange(index + count, r.End + count));
            i += 2;
        }
        for (; i < _ranges.Count; i++)
        {
            var r = _ranges[i];
            _ranges[i] = new IndexRange(r.Begin + count, r.End + count);
        }
    }

    /// <summary>删除 [index, index + count) 的项：其中选中的被移除（返回被移除的索引区间，按删除前编号），其后的索引前移。</summary>
    public List<IndexRange> RemoveItems(int index, int count)
    {
        var removed = new List<IndexRange>();
        if (count <= 0) return removed;
        Version++;
        int end = index + count - 1;
        int i = LowerBound(index);
        for (int k = i; k < _ranges.Count && _ranges[k].Begin <= end; k++)
        {
            var r = _ranges[k];
            removed.Add(new IndexRange(Math.Max(r.Begin, index), Math.Min(r.End, end)));
        }
        if (removed.Count > 0) Remove(index, end);
        i = LowerBound(index);
        for (int k = i; k < _ranges.Count; k++)
        {
            var r = _ranges[k];
            _ranges[k] = new IndexRange(r.Begin - count, r.End - count);
        }
        // 删除后两侧可能相邻：合并
        if (i > 0 && i < _ranges.Count && _ranges[i - 1].End + 1 == _ranges[i].Begin)
        {
            _ranges[i - 1] = new IndexRange(_ranges[i - 1].Begin, _ranges[i].End);
            _ranges.RemoveAt(i);
        }
        return removed;
    }

    /// <summary>只保留 [0, count) 内的索引；返回被剪掉的区间。</summary>
    public List<IndexRange> Trim(int count)
    {
        var removed = new List<IndexRange>();
        int i = LowerBound(count);
        for (int k = i; k < _ranges.Count; k++)
        {
            var r = _ranges[k];
            if (r.End >= count) removed.Add(new IndexRange(Math.Max(r.Begin, count), r.End));
        }
        foreach (var r in removed) Remove(r.Begin, r.End);
        return removed;
    }

    /// <summary>a − b（a、b 都按同一编号）。</summary>
    public static List<IndexRange> Except(IndexRangeList a, IndexRangeList b)
    {
        var result = new List<IndexRange>();
        int j = 0;
        var br = b._ranges;
        foreach (var r in a._ranges)
        {
            int cur = r.Begin;
            while (j < br.Count && br[j].End < cur) j++;
            int k = j;
            while (cur <= r.End)
            {
                if (k >= br.Count || br[k].Begin > r.End)
                {
                    result.Add(new IndexRange(cur, r.End));
                    break;
                }
                var x = br[k];
                if (x.Begin > cur) result.Add(new IndexRange(cur, x.Begin - 1));
                if (x.End >= r.End) break;
                cur = x.End + 1;
                k++;
            }
        }
        return result;
    }

    public override string ToString() => string.Join(",", _ranges);
}

/// <summary>区间列表上的只读索引视图（不展开：一千万项的全选不分配一千万个 int）。</summary>
internal sealed class RangeIndexList : IReadOnlyList<int>
{
    readonly IReadOnlyList<IndexRange> _ranges;
    readonly long[] _starts;   // 每个区间之前的索引数
    readonly int _count;

    public RangeIndexList(IReadOnlyList<IndexRange> ranges)
    {
        _ranges = ranges;
        _starts = new long[ranges.Count];
        long acc = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            _starts[i] = acc;
            acc += ranges[i].Count;
        }
        _count = (int)Math.Min(acc, int.MaxValue);
    }

    public static readonly RangeIndexList Empty = new(Array.Empty<IndexRange>());

    public int Count => _count;

    public int this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            int lo = 0, hi = _starts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_starts[mid] <= index) lo = mid;
                else hi = mid - 1;
            }
            return _ranges[lo].Begin + (int)(index - _starts[lo]);
        }
    }

    public IEnumerator<int> GetEnumerator()
    {
        foreach (var r in _ranges)
            for (int i = r.Begin; ; i++)
            {
                yield return i;
                if (i == r.End) break;
            }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
