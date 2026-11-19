using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 渐变查找表：256 项预乘颜色（CSS 规定在预乘空间插值）。软件后端的 <see cref="GradientShader"/> 直接用浮点表，
/// GPU 后端把同一张表量化为 RGBA8 放进查找表纹理，两边取样一致。
/// </summary>
internal static class GradientLut
{
    public const int Size = 256;

    /// <summary>
    /// 偏移的有限范围。无穷大的偏移会让插值参数变成 ∞/∞ = NaN；钳制到这个范围后结果等于极限值（误差远小于 1/255）。
    /// </summary>
    const float MaxOffset = 1e6f;

    /// <summary>
    /// 构造查找表：一次前向扫描（t 单调递增，所在的色标区间只会向后移动），O(色标数 + 表长)。
    /// 第 i 项取 t = i / 255 处的颜色：t 不超过第一个色标时取第一个色标，不小于最后一个色标时取最后一个，
    /// 否则在包含 t 的区间内线性插值（区间长度小于 1e-6 时取区间终点，即硬边）。只有一个色标时整张表为该颜色。
    /// 偏移中的 NaN 视为前一个色标的偏移（第一个为 0），±∞ 钳制到 ±1e6；颜色分量中的 NaN 按 0 处理，所以表中总是有限值。
    /// </summary>
    public static void Build(IReadOnlyList<GradientStop> stops, Span<Vector4> lut)
    {
        if (stops.Count == 0)
        {
            lut.Clear();
            return;
        }
        Prepare(stops, out var offsets, out var colors);
        int k = 1;
        for (int i = 0; i < lut.Length; i++) lut[i] = Eval(offsets, colors, i / (float)(lut.Length - 1), ref k);
    }

    /// <summary>t 处的颜色（规则同 <see cref="Build"/>；t = i / 255 时与表的第 i 项相同）。</summary>
    public static Vector4 Sample(IReadOnlyList<GradientStop> stops, float t)
    {
        if (stops.Count == 0) return default;
        Prepare(stops, out var offsets, out var colors);
        int k = 1;
        return Eval(offsets, colors, t, ref k);
    }

    static void Prepare(IReadOnlyList<GradientStop> stops, out float[] offsets, out Vector4[] colors)
    {
        int n = stops.Count;
        offsets = new float[n];
        colors = new Vector4[n];
        float prev = 0;
        for (int i = 0; i < n; i++)
        {
            float o = stops[i].Offset;
            if (float.IsNaN(o)) o = prev;
            o = o < -MaxOffset ? -MaxOffset : o > MaxOffset ? MaxOffset : o;
            offsets[i] = o;
            prev = o;
            colors[i] = Premultiply(stops[i].Color);
        }
    }

    /// <summary>
    /// 一个 t 的取值。<paramref name="k"/> 是上一次所在区间的终点下标：t 单调递增时只需向后移动，
    /// 结果与每次从 1 开始查找相同（「第一个偏移 ≥ t 的色标」随 t 单调不减）。
    /// </summary>
    static Vector4 Eval(float[] offsets, Vector4[] colors, float t, ref int k)
    {
        int n = offsets.Length;
        if (n == 1 || t <= offsets[0]) return colors[0];
        if (t >= offsets[n - 1]) return colors[n - 1];
        while (k < n - 1 && offsets[k] < t) k++;
        float a = offsets[k - 1];
        float span = offsets[k] - a;
        float u = span > 1e-6f ? (t - a) / span : 1;
        return Vector4.Lerp(colors[k - 1], colors[k], u);
    }

    /// <summary>非预乘颜色 → 预乘；NaN 分量按 0 处理（与 <see cref="Shader.Premultiply"/> 对有限值相同）。</summary>
    public static Vector4 Premultiply(Color c)
    {
        float a = Finite(c.A);
        return new Vector4(Finite(c.R) * a, Finite(c.G) * a, Finite(c.B) * a, a);
    }

    static float Finite(float v) => float.IsNaN(v) ? 0 : v;
}

/// <summary>
/// 按色标内容比较的键（软件后端的查找表缓存与 GPU 后端的查找表行共用）。查找时引用画刷的色标，入表前复制一份（避免持有画刷）。
/// 浮点按 <see cref="KeyHash.Same(float, float)"/> 比较（NaN 也等于自己），哈希带每进程随机种子。
/// </summary>
internal readonly struct StopsKey : IEquatable<StopsKey>
{
    readonly IReadOnlyList<GradientStop> _stops;
    readonly int _hash;

    public StopsKey(IReadOnlyList<GradientStop> stops)
    {
        _stops = stops;
        int h = HashCodeCompat.Combine(KeyHash.Seed, stops.Count);
        for (int i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            h = HashCodeCompat.Combine(h, s.Offset.GetHashCode(), KeyHash.Hash(s.Color));
        }
        _hash = h;
    }

    public IReadOnlyList<GradientStop> Stops => _stops;

    public StopsKey Freeze()
    {
        var copy = new GradientStop[_stops.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = _stops[i];
        return new StopsKey(copy);
    }

    public bool Equals(StopsKey other)
    {
        if (_hash != other._hash || _stops.Count != other._stops.Count) return false;
        for (int i = 0; i < _stops.Count; i++)
            if (!KeyHash.Same(_stops[i].Offset, other._stops[i].Offset) || !KeyHash.Same(_stops[i].Color, other._stops[i].Color)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is StopsKey k && Equals(k);
    public override int GetHashCode() => _hash;
}

/// <summary>
/// 软件后端的查找表缓存：按色标内容（每帧重建的画刷也能命中），LRU，条目数有上限（每项 4 KB）。
/// 同一个画刷对象连续绘制时直接复用上一次的表，不再计算哈希。不是线程安全的：每个 <see cref="RasterContext"/> 一份。
/// </summary>
internal sealed class GradientLutCache
{
    public const int DefaultMaxEntries = 256;

    readonly Dictionary<StopsKey, Entry> _map = new();
    readonly int _maxEntries;
    Entry? _head, _tail;
    GradientBrush? _lastBrush;
    Vector4[]? _lastLut;

    public GradientLutCache(int maxEntries = DefaultMaxEntries) => _maxEntries = Math.Max(1, maxEntries);

    public int Count => _map.Count;

    /// <summary>构造过的表数（诊断与测试：命中缓存时不增加）。</summary>
    public long Builds { get; private set; }

    /// <summary>画刷的查找表。返回的数组不可修改（可能被其他画刷共享）。</summary>
    public Vector4[] Get(GradientBrush brush)
    {
        if (ReferenceEquals(brush, _lastBrush)) return _lastLut!;
        var probe = new StopsKey(brush.Stops);
        if (_map.TryGetValue(probe, out var e))
        {
            MoveToFront(e);
        }
        else
        {
            if (_map.Count >= _maxEntries && _tail is { } oldest)
            {
                Unlink(oldest);
                _map.Remove(oldest.Key);
            }
            var lut = new Vector4[GradientLut.Size];
            GradientLut.Build(brush.Stops, lut);
            Builds++;
            e = new Entry(probe.Freeze(), lut);
            _map[e.Key] = e;
            MoveToFront(e);
        }
        _lastBrush = brush;
        _lastLut = e.Lut;
        return e.Lut;
    }

    void MoveToFront(Entry e)
    {
        if (_head == e) return;
        Unlink(e);
        e.Next = _head;
        if (_head is not null) _head.Prev = e;
        _head = e;
        _tail ??= e;
    }

    void Unlink(Entry e)
    {
        if (e.Prev is not null) e.Prev.Next = e.Next; else if (_head == e) _head = e.Next;
        if (e.Next is not null) e.Next.Prev = e.Prev; else if (_tail == e) _tail = e.Prev;
        e.Prev = e.Next = null;
    }

    sealed class Entry
    {
        public Entry(StopsKey key, Vector4[] lut) { Key = key; Lut = lut; }
        public StopsKey Key { get; }
        public Vector4[] Lut { get; }
        public Entry? Prev, Next;
    }
}
