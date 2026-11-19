using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render.Raster;

namespace AwAUI.Render.Gpu.Atlas;

/// <summary>
/// 路径覆盖率掩码的跨帧缓存（design/03：「带缓存键（路径哈希 + 缩放）」）。
/// 键按内容比较：路径的动词与点、填充规则、完整变换、抗锯齿、描边参数，以及掩码区域（裁剪会截掉掩码的一部分）。
/// 因此每帧重建的同形路径（例如每帧重新录制的 DisplayList）也能命中。
/// 条目数有上限；超出时清掉最久未用的一半。页被淘汰后条目自动失效（<see cref="AtlasSlot.IsValid"/>）。
/// </summary>
internal sealed class PathMaskCache
{
    public const int DefaultMaxEntries = 2048;

    /// <summary>点数超过此值的路径不缓存（哈希与比较的开销接近重新光栅化，且多为每帧变化的数据）。</summary>
    public const int MaxCachedPoints = 4096;

    readonly Dictionary<PathMaskKey, Entry> _map = new();

    public PathMaskCache(int maxEntries = DefaultMaxEntries) => MaxEntries = Math.Max(16, maxEntries);

    public int MaxEntries { get; }
    public int Count => _map.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public bool TryGet(in PathMaskKey key, long frame, out AtlasSlot slot)
    {
        if (_map.TryGetValue(key, out var e))
        {
            if (e.Slot.IsValid)
            {
                e.LastUsed = frame;
                slot = e.Slot;
                Hits++;
                return true;
            }
            _map.Remove(key);
        }
        Misses++;
        slot = default;
        return false;
    }

    long _lastTrimFrame = -1;

    /// <summary>
    /// 加入一个条目。满了时每帧最多整理一次（清掉失效与最久未用的一半）；整理后仍满（全是本帧在用的条目）就不缓存，
    /// 保证单帧里大量互不相同的路径不会让每次加入都触发一次整理。
    /// </summary>
    public void Add(in PathMaskKey key, AtlasSlot slot, long frame)
    {
        if (_map.Count >= MaxEntries && _lastTrimFrame != frame)
        {
            _lastTrimFrame = frame;
            Trim(frame);
        }
        if (_map.Count >= MaxEntries) return;
        _map[key] = new Entry { Slot = slot, LastUsed = frame };
    }

    void Trim(long frame)
    {
        var used = new List<long>(_map.Count);
        foreach (var e in _map.Values) used.Add(e.LastUsed);
        used.Sort();
        long cutoff = used[used.Count / 2];
        var remove = new List<PathMaskKey>();
        foreach (var kv in _map)
            if (!kv.Value.Slot.IsValid || kv.Value.LastUsed <= cutoff && kv.Value.LastUsed < frame) remove.Add(kv.Key);
        foreach (var k in remove) _map.Remove(k);
    }

    public void Clear() => _map.Clear();

    sealed class Entry
    {
        public AtlasSlot Slot;
        public long LastUsed;
    }
}

/// <summary>路径掩码的内容键。浮点按 <see cref="KeyHash.Same(float, float)"/> 比较（含 NaN 的键也等于自己），哈希带每进程随机种子。</summary>
internal readonly struct PathMaskKey : IEquatable<PathMaskKey>
{
    readonly VectorPath _path;
    readonly Stroke? _stroke;
    readonly Matrix3x2 _matrix;
    readonly PixelRect _area;
    readonly bool _antiAlias;
    readonly int _hash;

    public PathMaskKey(VectorPath path, Stroke? stroke, in Matrix3x2 matrix, PixelRect area, bool antiAlias)
    {
        _path = path;
        _stroke = stroke;
        _matrix = matrix;
        _area = area;
        _antiAlias = antiAlias;
        int h = HashCodeCompat.Combine(KeyHash.Seed, (int)path.FillRule, area.GetHashCode(), antiAlias ? 1 : 0);
        h = HashCodeCompat.Combine(h, matrix.M11.GetHashCode(), matrix.M12.GetHashCode(), matrix.M21.GetHashCode());
        h = HashCodeCompat.Combine(h, matrix.M22.GetHashCode(), matrix.M31.GetHashCode(), matrix.M32.GetHashCode());
        foreach (var v in path.Verbs) h = h * 31 + (int)v;
        foreach (var p in path.Points) h = HashCodeCompat.Combine(h, p.X.GetHashCode(), p.Y.GetHashCode());
        if (stroke is not null) h = HashCodeCompat.Combine(h, StrokeHash(stroke));
        _hash = h;
    }

    public static bool IsCacheable(VectorPath path) => path.Points.Length <= PathMaskCache.MaxCachedPoints;

    static int StrokeHash(Stroke s)
    {
        int h = HashCodeCompat.Combine(s.Width.GetHashCode(), (int)s.Cap | (int)s.Join << 8, s.MiterLimit.GetHashCode(), s.DashOffset.GetHashCode());
        if (s.Dashes is { } d)
            for (int i = 0; i < d.Count; i++) h = HashCodeCompat.Combine(h, d[i].GetHashCode());
        return h;
    }

    static bool StrokeEquals(Stroke? a, Stroke? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (!KeyHash.Same(a.Width, b.Width) || a.Cap != b.Cap || a.Join != b.Join || !KeyHash.Same(a.MiterLimit, b.MiterLimit)
            || !KeyHash.Same(a.DashOffset, b.DashOffset)) return false;
        var da = a.Dashes;
        var db = b.Dashes;
        if (da is null || db is null) return da is null && db is null;
        if (da.Count != db.Count) return false;
        for (int i = 0; i < da.Count; i++)
            if (!KeyHash.Same(da[i], db[i])) return false;
        return true;
    }

    public bool Equals(PathMaskKey o)
    {
        if (_hash != o._hash || _area != o._area || _antiAlias != o._antiAlias || !SameMatrix(_matrix, o._matrix)) return false;
        if (!StrokeEquals(_stroke, o._stroke)) return false;
        if (ReferenceEquals(_path, o._path)) return true;
        if (_path.FillRule != o._path.FillRule) return false;
        var va = _path.Verbs;
        var vb = o._path.Verbs;
        if (va.Length != vb.Length) return false;
        for (int i = 0; i < va.Length; i++)
            if (va[i] != vb[i]) return false;
        var pa = _path.Points;
        var pb = o._path.Points;
        if (pa.Length != pb.Length) return false;
        for (int i = 0; i < pa.Length; i++)
            if (!KeyHash.Same(pa[i].X, pb[i].X) || !KeyHash.Same(pa[i].Y, pb[i].Y)) return false;
        return true;
    }

    /// <summary>逐元素比较（netstandard2.0 的 Matrix3x2.Equals 用 ==，NaN 不等于自己）。</summary>
    static bool SameMatrix(in Matrix3x2 a, in Matrix3x2 b) =>
        KeyHash.Same(a.M11, b.M11) && KeyHash.Same(a.M12, b.M12) && KeyHash.Same(a.M21, b.M21)
        && KeyHash.Same(a.M22, b.M22) && KeyHash.Same(a.M31, b.M31) && KeyHash.Same(a.M32, b.M32);

    public override bool Equals(object? obj) => obj is PathMaskKey k && Equals(k);
    public override int GetHashCode() => _hash;
}
