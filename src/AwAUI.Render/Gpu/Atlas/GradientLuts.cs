using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render.Raster;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Atlas;

/// <summary>
/// 渐变查找表（design/03：「线性/径向/锥形渐变经渐变 LUT 纹理」）。每个渐变一行 256 个预乘 RGBA8 颜色，
/// 由与软件后端相同的 <see cref="GradientLut.Build"/> 构造后量化（CSS 规定的预乘空间插值），片元着色器读相邻两项线性插值。
/// 按色标内容缓存（每帧重建的画刷对象也能命中），行按 LRU 复用；本帧用满时开新页。
/// </summary>
internal sealed class GradientLuts : IDisposable
{
    public const int LutSize = 256;
    public const int RowsPerPage = 256;

    /// <summary>查找表页数上限（每页 256 个渐变、CPU 与 GPU 各 256 KB）。一帧里互不相同的渐变超过上限时，多出的按平均色画成纯色并上报降级。</summary>
    public const int MaxPages = 64;

    readonly GpuDevice? _device;
    readonly List<LutPage> _pages = new();
    readonly Dictionary<StopsKey, LutRow> _rows = new();
    // 已分配的行按最近使用排成双向链表（头部最新），淘汰取尾部：O(1)
    LutRow? _head, _tail;
    long _frame;

    public GradientLuts(GpuDevice? device) => _device = device;

    public int PageCount => _pages.Count;
    public int CachedCount => _rows.Count;

    public void BeginFrame(long frame) => _frame = frame;

    /// <summary>
    /// 取得渐变所在的页与行号（纹理在帧编码时经 <see cref="LutPage.Texture"/> 取得）。
    /// 页数达到 <see cref="MaxPages"/> 且全部行都在本帧使用时返回 false。
    /// </summary>
    public bool TryGet(GradientBrush brush, out LutPage page, out int row)
    {
        var probe = new StopsKey(brush.Stops);
        if (!_rows.TryGetValue(probe, out var r))
        {
            r = AllocateRow(probe.Freeze());
            if (r is null)
            {
                page = null!;
                row = 0;
                return false;
            }
            _rows[r.Key] = r;
            Fill(r, brush.Stops);
        }
        r.LastUsed = _frame;
        MoveToFront(r);
        page = r.Page;
        row = r.Index;
        return true;
    }

    LutRow? AllocateRow(StopsKey key)
    {
        if (_pages.Count > 0 && _pages[_pages.Count - 1].Free > 0) return _pages[_pages.Count - 1].Take(key);
        // LRU：链表尾部是最久未用的行，本帧没用到才能复用
        if (_tail is { } oldest && oldest.LastUsed < _frame)
        {
            _rows.Remove(oldest.Key);
            Unlink(oldest);
            var reused = new LutRow(oldest.Page, oldest.Index, key);
            oldest.Page.Owner[oldest.Index] = reused;
            return reused;
        }
        if (_pages.Count >= MaxPages) return null;
        var page = new LutPage(_device);
        _pages.Add(page);
        return page.Take(key);
    }

    void MoveToFront(LutRow r)
    {
        if (_head == r) return;
        Unlink(r);
        r.Next = _head;
        if (_head is not null) _head.Prev = r;
        _head = r;
        _tail ??= r;
    }

    void Unlink(LutRow r)
    {
        if (r.Prev is not null) r.Prev.Next = r.Next; else if (_head == r) _head = r.Next;
        if (r.Next is not null) r.Next.Prev = r.Prev; else if (_tail == r) _tail = r.Prev;
        r.Prev = r.Next = null;
    }

    static void Fill(LutRow row, IReadOnlyList<GradientStop> stops)
    {
        Span<Vector4> lut = stackalloc Vector4[LutSize];
        GradientLut.Build(stops, lut);
        var data = row.Page.Shadow;
        int o = row.Index * LutSize * 4;
        for (int i = 0; i < LutSize; i++)
        {
            var c = lut[i];
            data[o++] = ScalarKernels.ToByte(c.X);
            data[o++] = ScalarKernels.ToByte(c.Y);
            data[o++] = ScalarKernels.ToByte(c.Z);
            data[o++] = ScalarKernels.ToByte(c.W);
        }
        row.Page.MarkDirty(row.Index);
    }

    /// <summary>t 处的预乘颜色（与查找表同一规则；用于降级时的平均色）。</summary>
    internal static Vector4 Sample(IReadOnlyList<GradientStop> stops, float t) => GradientLut.Sample(stops, t);

    internal static Vector4 Premultiply(Color c) => GradientLut.Premultiply(c);

    public void Upload(GpuCommandList commands, ref long uploadedBytes)
    {
        foreach (var p in _pages) p.Upload(commands, ref uploadedBytes);
    }

    public void Clear()
    {
        foreach (var p in _pages) p.Dispose();
        _pages.Clear();
        _rows.Clear();
        _head = _tail = null;
    }

    public void Dispose() => Clear();

    internal sealed class LutRow
    {
        public LutRow(LutPage page, int index, StopsKey key) { Page = page; Index = index; Key = key; }
        public LutPage Page { get; }
        public int Index { get; }
        public StopsKey Key { get; }
        public long LastUsed;
        public LutRow? Prev, Next;
    }

    /// <summary>一页查找表：256 × 256 的 RGBA8 纹理 + CPU 影子副本。</summary>
    internal sealed class LutPage : IDisposable
    {
        readonly GpuDevice? _device;
        GpuTexture? _texture;
        int _dirtyMin = int.MaxValue, _dirtyMax = -1;

        public LutPage(GpuDevice? device) => _device = device;

        public readonly byte[] Shadow = new byte[LutSize * RowsPerPage * 4];
        public readonly LutRow?[] Owner = new LutRow?[RowsPerPage];
        public int Free = RowsPerPage;

        public GpuTexture? Texture => _device is null ? null
            : _texture ??= _device.CreateTexture(new GpuTextureDesc(LutSize, RowsPerPage, GpuTextureFormat.Rgba8Unorm, GpuTextureUsage.Sampled, "AwAUI gradient LUT"));

        public LutRow Take(StopsKey key)
        {
            int index = RowsPerPage - Free;
            Free--;
            var row = new LutRow(this, index, key);
            Owner[index] = row;
            return row;
        }

        public void MarkDirty(int row)
        {
            _dirtyMin = Math.Min(_dirtyMin, row);
            _dirtyMax = Math.Max(_dirtyMax, row);
        }

        public void Upload(GpuCommandList commands, ref long uploadedBytes)
        {
            if (_dirtyMax < _dirtyMin || Texture is null) return;
            var rect = new PixelRect(0, _dirtyMin, LutSize, _dirtyMax - _dirtyMin + 1);
            commands.UploadTexture(Texture, rect, new ReadOnlySpan<byte>(Shadow, _dirtyMin * LutSize * 4, rect.Height * LutSize * 4), LutSize * 4);
            uploadedBytes += rect.Height * LutSize * 4;
            _dirtyMin = int.MaxValue;
            _dirtyMax = -1;
        }

        public void Dispose()
        {
            _texture?.Dispose();
            _texture = null;
        }
    }
}
