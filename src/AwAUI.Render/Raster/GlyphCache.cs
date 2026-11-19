using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AwAUI.Graphics.Text;

namespace AwAUI.Render.Raster;

/// <summary>
/// 字形掩码缓存的键（design/03「渲染相关」）：字型（按对象引用）+ 轴取值 + 字形 + 设备字号桶 + 子像素 x 偏移 + 样式
/// （合成加粗时还有设备像素下的加宽量：它由逻辑字号决定，同一设备字号在不同缩放下加宽量不同）。
/// 只有「均匀正缩放 + 平移」的变换走缓存，缩放已折算进设备字号，因此变换类别只有这一种；
/// 旋转、错切与非均匀缩放直接按路径填充，不入缓存。y 方向对齐到整像素，不参与键。
/// </summary>
internal readonly struct GlyphKey : IEquatable<GlyphKey>
{
    /// <summary>设备字号的量化步长：1/64 px。</summary>
    public const int SizeScale = 64;

    /// <summary>子像素 x 偏移的量化级数：1/4 px。</summary>
    public const int SubpixelSteps = 4;

    /// <summary>合成加粗加宽量的量化步长：1/256 px。</summary>
    public const int BoldScale = 256;

    public GlyphKey(FontFace face, FontVariation variation, ushort glyph, int sizeBucket, byte subpixelX, GlyphStyle style, int boldBucket = 0)
    {
        Face = face;
        Variation = variation;
        Glyph = glyph;
        SizeBucket = sizeBucket;
        SubpixelX = subpixelX;
        Style = style;
        BoldBucket = (style & GlyphStyle.SyntheticBold) != 0 ? boldBucket : 0;
    }

    public FontFace Face { get; }
    public FontVariation Variation { get; }
    public ushort Glyph { get; }

    /// <summary>设备字号 × <see cref="SizeScale"/>（取整）。</summary>
    public int SizeBucket { get; }

    /// <summary>0 .. <see cref="SubpixelSteps"/> − 1。</summary>
    public byte SubpixelX { get; }

    public GlyphStyle Style { get; }

    /// <summary>合成加粗的加宽量（设备像素）× <see cref="BoldScale"/>（取整）；不加粗时为 0。</summary>
    public int BoldBucket { get; }

    public float DeviceSize => SizeBucket / (float)SizeScale;

    /// <summary>合成加粗的加宽量（设备像素）。</summary>
    public float BoldExtent => BoldBucket / (float)BoldScale;

    public bool Equals(GlyphKey other) =>
        ReferenceEquals(Face, other.Face) && Glyph == other.Glyph && SizeBucket == other.SizeBucket
        && SubpixelX == other.SubpixelX && Style == other.Style && BoldBucket == other.BoldBucket
        && Variation.Equals(other.Variation);

    public override bool Equals(object? obj) => obj is GlyphKey k && Equals(k);

    public override int GetHashCode()
    {
        // 每进程随机种子（KeyHash）：字形 id、字号来自绘制内容，固定的组合可被离线构造出大量碰撞
        int h = HashCodeCompat.Combine(HashCodeCompat.Combine(KeyHash.Seed, RuntimeHelpers.GetHashCode(Face)), Glyph, SizeBucket,
            SubpixelX | ((int)Style << 8) | (BoldBucket << 16));
        return Variation.IsDefault ? h : HashCodeCompat.Combine(h, Variation.GetHashCode());
    }
}

/// <summary>影响字形掩码的样式位。</summary>
[Flags]
internal enum GlyphStyle : byte
{
    None = 0,
    SyntheticBold = 1,
    SyntheticOblique = 2,
    /// <summary>不抗锯齿（<see cref="Graphics.Paint.AntiAlias"/> 为 false）。</summary>
    Aliased = 4,
}

/// <summary>
/// 一个字形的 A8 覆盖率掩码。<see cref="Left"/>、<see cref="Top"/> 是掩码左上角相对字形原点所在整像素的偏移；
/// 原点的小数部分（子像素 x）已经烘焙进掩码。
/// </summary>
internal sealed class GlyphMask
{
    /// <summary>每个条目在字节预算中额外计入的固定开销（对象头、字典槽等的粗略估计）。</summary>
    public const int Overhead = 96;

    public GlyphMask(GlyphKey key, byte[]? coverage, int left, int top, int width, int height, bool tooLarge = false)
    {
        Key = key;
        Coverage = coverage;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        TooLarge = tooLarge;
    }

    public GlyphKey Key { get; }

    /// <summary>行优先、<see cref="Width"/> × <see cref="Height"/>；空字形或 <see cref="TooLarge"/> 时为 null。</summary>
    public byte[]? Coverage { get; }

    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>掩码超出尺寸上限：调用方改为直接填充轮廓路径（记录下来避免每帧重复尝试）。</summary>
    public bool TooLarge { get; }

    public bool IsEmpty => Coverage is null;

    public long Bytes => (Coverage?.Length ?? 0) + Overhead;

    /// <summary>GPU 后端：掩码在图集中的位置；所在页被淘汰后失效，从 <see cref="Coverage"/> 重新上传。软件后端不用。</summary>
    internal Gpu.Atlas.AtlasSlot Slot;

    // LRU 双向链表（头部为最近使用）
    internal GlyphMask? Prev;
    internal GlyphMask? Next;
}

/// <summary>
/// 有界的 LRU 字形掩码缓存。条目数与字节数（掩码字节 + 固定开销）都有上限，超出时淘汰最久未用的条目。
/// 不是线程安全的：每个 <see cref="RasterContext"/>（软件后端的每个渲染目标）与每个 GPU 后端各持有自己的实例。
/// </summary>
internal sealed class GlyphCache
{
    public const int DefaultMaxEntries = 8192;
    public const long DefaultMaxBytes = 8L << 20;

    readonly Dictionary<GlyphKey, GlyphMask> _map = new();
    GlyphMask? _head;
    GlyphMask? _tail;

    public GlyphCache(int maxEntries = DefaultMaxEntries, long maxBytes = DefaultMaxBytes)
    {
        if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        MaxEntries = maxEntries;
        MaxBytes = maxBytes;
    }

    public int MaxEntries { get; }
    public long MaxBytes { get; }

    public int Count => _map.Count;
    public long Bytes { get; private set; }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    public bool TryGet(in GlyphKey key, out GlyphMask mask)
    {
        if (_map.TryGetValue(key, out var found))
        {
            Hits++;
            MoveToFront(found);
            mask = found;
            return true;
        }
        Misses++;
        mask = null!;
        return false;
    }

    /// <summary>加入（或替换）一个掩码并按预算淘汰；刚加入的条目本身不会被淘汰。</summary>
    public void Add(GlyphMask mask)
    {
        if (_map.TryGetValue(mask.Key, out var old)) Remove(old);
        _map.Add(mask.Key, mask);
        Bytes += mask.Bytes;
        mask.Prev = null;
        mask.Next = _head;
        if (_head is not null) _head.Prev = mask;
        _head = mask;
        _tail ??= mask;

        while ((_map.Count > MaxEntries || Bytes > MaxBytes) && _tail is not null && _tail != mask)
        {
            Remove(_tail);
            Evictions++;
        }
    }

    public void Clear()
    {
        _map.Clear();
        _head = _tail = null;
        Bytes = 0;
    }

    void Remove(GlyphMask m)
    {
        _map.Remove(m.Key);
        Bytes -= m.Bytes;
        Unlink(m);
    }

    void Unlink(GlyphMask m)
    {
        if (m.Prev is not null) m.Prev.Next = m.Next; else _head = m.Next;
        if (m.Next is not null) m.Next.Prev = m.Prev; else _tail = m.Prev;
        m.Prev = m.Next = null;
    }

    void MoveToFront(GlyphMask m)
    {
        if (_head == m) return;
        Unlink(m);
        m.Next = _head;
        if (_head is not null) _head.Prev = m;
        _head = m;
        _tail ??= m;
    }
}
