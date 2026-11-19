using System;
using System.Collections.Generic;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Atlas;

/// <summary>
/// A8 覆盖率图集（design/03「图集」）：字形、路径掩码与裁剪蒙版共用。
/// 每页一张 R8 纹理 + CPU 端影子副本 + 天际线装箱；写入只改影子副本并扩大脏矩形，帧编码时每页上传一次脏区。
/// <para>
/// 淘汰按页进行（LRU）：页满时先开新页（不超过 <see cref="MaxPages"/>），否则清空最久未用、且本帧没有用到的一页，
/// 其上的全部条目随页的代数（<see cref="AtlasPage.Generation"/>）一起失效。本帧用到的条目永远不会被淘汰：
/// 所有页都在本帧用到时临时多开一页，之后的帧里优先回收。比页还大的条目放进只存活一帧的专用页。
/// </para>
/// </summary>
internal sealed class MaskAtlas : IDisposable
{
    public const int DefaultPageSize = 2048;
    public const int DefaultMaxPages = 4;

    /// <summary>
    /// 每帧在常规页之外（专用页 + 超出上限的临时页）最多分配的字节数。超出时分配失败，调用方跳过该绘制并上报降级：
    /// 一帧里本帧用到的掩码都不能被淘汰，没有这个上限，恶意构造的内容（大量互不相同的大路径）可以让显存无界增长。
    /// </summary>
    public const long DefaultFrameOverflowBudget = 256L << 20;

    readonly GpuDevice? _device;
    readonly List<AtlasPage> _pages = new();
    readonly List<AtlasPage> _transient = new();
    long _frame;

    /// <param name="device">为 null 时不创建纹理（单元测试只检验装箱与淘汰逻辑）。</param>
    public MaskAtlas(GpuDevice? device, int pageSize = DefaultPageSize, int maxPages = DefaultMaxPages, long frameOverflowBudget = DefaultFrameOverflowBudget)
    {
        if (pageSize < 16) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maxPages < 1) throw new ArgumentOutOfRangeException(nameof(maxPages));
        _device = device;
        PageSize = pageSize;
        MaxPages = maxPages;
        FrameOverflowBudget = frameOverflowBudget;
    }

    public int PageSize { get; }
    public int MaxPages { get; }
    public long FrameOverflowBudget { get; }

    /// <summary>本帧在常规页之外已分配的字节数。</summary>
    public long FrameOverflowBytes { get; private set; }

    public IReadOnlyList<AtlasPage> Pages => _pages;
    public int TransientPageCount => _transient.Count;
    public long Frame => _frame;

    /// <summary>累计被清空的页数（诊断用）。</summary>
    public long Evictions { get; private set; }

    public void BeginFrame(long frame)
    {
        _frame = frame;
        FrameOverflowBytes = 0;
        // 上一帧临时多开的页：超出上限的部分回收最久未用的
        while (_pages.Count > MaxPages)
        {
            int oldest = -1;
            for (int i = 0; i < _pages.Count; i++)
                if (_pages[i].LastUsed < frame && (oldest < 0 || _pages[i].LastUsed < _pages[oldest].LastUsed)) oldest = i;
            if (oldest < 0) break;
            _pages[oldest].Invalidate();
            _pages[oldest].Dispose();
            _pages.RemoveAt(oldest);
            Evictions++;
        }
    }

    /// <summary>
    /// 分配一块 width × height 的区域，内容由调用方经 <see cref="AtlasSlot.Page"/> 的影子副本写入。
    /// 超出本帧的额外预算（<see cref="FrameOverflowBudget"/>）时返回 false。
    /// </summary>
    public bool TryAllocate(int width, int height, out AtlasSlot slot)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (width > PageSize || height > PageSize) return TryAllocateTransient(width, height, out slot);

        for (int i = _pages.Count - 1; i >= 0; i--)
            if (TryPack(_pages[i], width, height, out slot)) return true;

        AtlasPage page;
        if (_pages.Count < MaxPages)
        {
            page = AddPage();
        }
        else if (OldestEvictable() is { } oldest)
        {
            page = oldest;
            page.Invalidate();
            Evictions++;
        }
        else
        {
            // 本帧用满全部页：临时超出上限（计入本帧预算）
            long bytes = (long)PageSize * PageSize;
            if (FrameOverflowBytes + bytes > FrameOverflowBudget)
            {
                slot = default;
                return false;
            }
            FrameOverflowBytes += bytes;
            page = AddPage();
        }
        if (!TryPack(page, width, height, out slot))
            throw new InvalidOperationException("A fresh atlas page could not hold the allocation.");
        return true;
    }

    /// <summary>
    /// 淘汰对象：本帧没有用到的页中，上一次使用时「被用到的字节」最少的一页（同样多时取最久未用的）。
    /// 动画内容每帧生成新掩码，旧掩码成为垃圾：按存活量而不是按时间挑选，能保住每帧都在用的静态条目所在的页。
    /// </summary>
    AtlasPage? OldestEvictable()
    {
        AtlasPage? best = null;
        long bestLive = 0;
        foreach (var p in _pages)
        {
            if (p.LastUsed >= _frame) continue;
            // 上一帧没有用到的页，存活量视为 0
            long live = p.LastUsed == _frame - 1 ? p.LiveBytes : 0;
            if (best is null || live < bestLive || live == bestLive && p.LastUsed < best.LastUsed)
            {
                best = p;
                bestLive = live;
            }
        }
        return best;
    }

    AtlasPage AddPage()
    {
        var page = new AtlasPage(_device, PageSize, PageSize, _pages.Count, transient: false);
        _pages.Add(page);
        return page;
    }

    bool TryPack(AtlasPage page, int width, int height, out AtlasSlot slot)
    {
        if (!page.Packer.TryPack(width, height, out int x, out int y))
        {
            slot = default;
            return false;
        }
        slot = new AtlasSlot(page, x, y, width, height);
        slot.Touch(_frame);
        return true;
    }

    bool TryAllocateTransient(int width, int height, out AtlasSlot slot)
    {
        int w = Align(width), h = Align(height);
        long bytes = (long)w * h;
        if (FrameOverflowBytes + bytes > FrameOverflowBudget || _device is not null && Math.Max(w, h) > _device.Capabilities.MaxTextureSize)
        {
            slot = default;
            return false;
        }
        FrameOverflowBytes += bytes;
        var page = new AtlasPage(_device, w, h, -1, transient: true);
        page.Touch(_frame, bytes);
        _transient.Add(page);
        page.Packer.TryPack(width, height, out _, out _);
        slot = new AtlasSlot(page, 0, 0, width, height);
        return true;
    }

    static int Align(int v) => (v + 63) & ~63;

    /// <summary>上传本帧的脏区（在任何渲染通道之前调用）。</summary>
    public void Upload(GpuCommandList commands, ref long uploadedBytes)
    {
        foreach (var p in _pages) p.Upload(commands, ref uploadedBytes);
        foreach (var p in _transient) p.Upload(commands, ref uploadedBytes);
    }

    /// <summary>帧结束：释放专用页（已提交的命令仍持有纹理，直到执行完毕）。</summary>
    public void EndFrame()
    {
        foreach (var p in _transient)
        {
            p.Invalidate();
            p.Dispose();
        }
        _transient.Clear();
    }

    /// <summary>设备丢失：丢弃全部页，条目随代数失效后按需重建。</summary>
    public void Clear()
    {
        foreach (var p in _pages) { p.Invalidate(); p.Dispose(); }
        _pages.Clear();
        EndFrame();
    }

    public void Dispose() => Clear();
}

/// <summary>图集中的一页。</summary>
internal sealed class AtlasPage : IDisposable
{
    readonly GpuDevice? _device;
    GpuTexture? _texture;
    PixelRect _dirty;

    public AtlasPage(GpuDevice? device, int width, int height, int index, bool transient)
    {
        _device = device;
        Width = width;
        Height = height;
        Index = index;
        IsTransient = transient;
        Shadow = new byte[width * height];
        Packer = new SkylinePacker(width, height);
    }

    public int Width { get; }
    public int Height { get; }
    public int Index { get; }
    public bool IsTransient { get; }

    /// <summary>CPU 端的完整副本（行距 = <see cref="Width"/>）。</summary>
    public byte[] Shadow { get; }

    public SkylinePacker Packer { get; }

    /// <summary>每次清空加一；条目记录分配时的代数，不相等即失效。</summary>
    public int Generation { get; private set; }

    public long LastUsed { get; private set; } = -1;

    long _touchedBytes;

    /// <summary>最近一次被使用的那一帧里，本页被用到的字节数（同一区域重复使用会重复计数，只作为淘汰的启发值）。</summary>
    public long LiveBytes => _touchedBytes;

    /// <summary>记录本页在 <paramref name="frame"/> 被使用了 <paramref name="bytes"/> 字节。</summary>
    public void Touch(long frame, long bytes)
    {
        if (LastUsed != frame)
        {
            LastUsed = frame;
            _touchedBytes = 0;
        }
        _touchedBytes += bytes;
    }

    public PixelRect Dirty => _dirty;

    public GpuTexture Texture => _texture ??= _device?.CreateTexture(new GpuTextureDesc(Width, Height, GpuTextureFormat.R8Unorm, GpuTextureUsage.Sampled, "AwAUI mask atlas"))
                                             ?? throw new InvalidOperationException("The atlas has no device.");

    public void MarkDirty(PixelRect r) => _dirty = _dirty.Union(r);

    public void Invalidate()
    {
        Generation++;
        Packer.Reset();
        _touchedBytes = 0;
    }

    public void Upload(GpuCommandList commands, ref long uploadedBytes)
    {
        if (_dirty.IsEmpty) return;
        var d = _dirty;
        _dirty = default;
        commands.UploadTexture(Texture, d, new ReadOnlySpan<byte>(Shadow, d.Y * Width + d.X, (d.Height - 1) * Width + d.Width), Width);
        uploadedBytes += (long)d.Width * d.Height;
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
    }
}

/// <summary>图集中的一块区域。页被清空后失效（<see cref="IsValid"/>）。</summary>
internal readonly struct AtlasSlot
{
    public AtlasSlot(AtlasPage page, int x, int y, int width, int height)
    {
        Page = page;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Generation = page.Generation;
    }

    public AtlasPage Page { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int Generation { get; }

    public bool IsValid => Page is not null && Page.Generation == Generation;

    /// <summary>影子副本中本区域左上角的下标；行距为 <see cref="AtlasPage.Width"/>。</summary>
    public int Offset => Y * Page.Width + X;

    /// <summary>本帧用到了这块区域：所在页本帧不会被淘汰。</summary>
    public void Touch(long frame) => Page.Touch(frame, (long)Width * Height);

    /// <summary>写入完成，标记待上传。</summary>
    public void Commit() => Page.MarkDirty(new PixelRect(X, Y, Width, Height));

    /// <summary>清零本区域（光栅器只写有覆盖的行）。</summary>
    public void Clear()
    {
        var s = Page.Shadow;
        for (int y = 0; y < Height; y++) Array.Clear(s, Offset + y * Page.Width, Width);
    }
}
