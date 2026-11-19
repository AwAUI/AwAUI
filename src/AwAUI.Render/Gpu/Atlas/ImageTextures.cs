using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Render.Raster;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Atlas;

/// <summary>
/// <see cref="DrawOp.DrawImage"/> 的 GPU 纹理缓存（design/03「图片」）。键为（位图对象，<see cref="Bitmap.Version"/>，区域，级别）：
/// 第 0 级是整张位图（同一位图的不同源矩形共用），第 L ≥ 1 级是源矩形裁剪区的盒式预滤波 mip（与软件后端由同一函数在 CPU 上构造，
/// 所以两个后端采样的是相同的纹素）。动画图片每帧换版本，旧版本的纹理在下一帧开始时释放。
/// <para>
/// 界限：缓存按字节预算 LRU（本帧用到的纹理不淘汰）；单张纹理不超过设备的 <see cref="GpuCapabilities.MaxTextureSize"/> 与
/// <see cref="MaxTextureBytes"/>，否则改用更高的级别（缩小，上报降级）；每帧新上传的字节不超过 <see cref="MaxUploadBytesPerFrame"/>，
/// 超出的图片跳过并上报。纹理在录制时创建、像素在编码开始时上传（与图集相同，按命令顺序生效）。
/// </para>
/// </summary>
internal sealed class ImageTextures : IDisposable
{
    /// <summary>缓存的字节预算。</summary>
    public const long BudgetBytes = 256L << 20;

    /// <summary>单张纹理的字节上限（约 5800² 的 RGBA）；更大的位图改用缩小的级别。</summary>
    public const long MaxTextureBytes = 128L << 20;

    /// <summary>每帧新上传的字节上限。</summary>
    public const long MaxUploadBytesPerFrame = 256L << 20;

    /// <summary>一张纹理与它的待上传像素。</summary>
    public sealed class Entry
    {
        public GpuTexture Texture = null!;
        /// <summary>待上传：mip 级别的像素（RGBA），或第 0 级时直接从位图读取。</summary>
        public byte[]? PendingPixels;
        public Bitmap? PendingBitmap;
        /// <summary>第 0 级：位图中要上传的区域。</summary>
        public PixelRect PendingRegion;
    }

    readonly GpuDevice _device;
    readonly ImageLruCache<Entry> _cache;
    readonly List<Entry> _pending = new();
    long _uploadedThisFrame;

    public ImageTextures(GpuDevice device)
    {
        _device = device;
        _cache = new ImageLruCache<Entry>(BudgetBytes, e => e.Texture.Dispose());
    }

    public int Count => _cache.Count;
    public long Bytes => _cache.Bytes;

    /// <summary>新建过的纹理数（诊断与测试：命中缓存时不增加）。</summary>
    public long Builds => _cache.Builds;

    public int MaxTextureSize => _device.Capabilities.MaxTextureSize;

    public void BeginFrame(long frame)
    {
        _cache.BeginFrame(frame);
        _uploadedThisFrame = 0;
    }

    /// <summary>这么大的纹理是否放得下（设备尺寸上限与 <see cref="MaxTextureBytes"/>）。</summary>
    public bool FitsSize(int width, int height)
    {
        int max = MaxTextureSize;
        return width <= max && height <= max && (long)width * height * 4 <= MaxTextureBytes;
    }

    /// <summary>
    /// 取（或创建并排队上传）纹理。第 0 级的纹理是位图的 <paramref name="region"/>（整张位图；整张放不下时为源矩形的裁剪区），
    /// 第 L ≥ 1 级是 <see cref="ImagePlan.Crop"/> 的 mip（<paramref name="region"/> 必须等于它）。<paramref name="work"/> 累加 CPU 构造 mip 的代价
    /// （裁剪区面积）。超出每帧上传上限时返回 null 并给出原因。
    /// </summary>
    public Entry? Get(Bitmap image, in ImagePlan plan, PixelRect region, ref long work, out string? failure)
    {
        failure = null;
        var key = new ImageKey(image, image.Version, region, plan.Level);
        if (_cache.TryGet(key, out var hit)) return hit;

        int w, h;
        if (plan.Level == 0) { w = region.Width; h = region.Height; }
        else ImagePlan.LevelSize(plan.Crop, plan.Level, out w, out h);
        long bytes = (long)w * h * 4;
        if (_uploadedThisFrame + bytes > MaxUploadBytesPerFrame)
        {
            failure = $"More than {MaxUploadBytesPerFrame >> 20} MB of image data uploaded in one frame; the rest of the images were skipped.";
            return null;
        }
        _uploadedThisFrame += bytes;
        var entry = new Entry();
        GpuTextureFormat format;
        if (plan.Level == 0)
        {
            format = image.Format == PixelFormat.Bgra8888 ? GpuTextureFormat.Bgra8Unorm : GpuTextureFormat.Rgba8Unorm;
            entry.PendingBitmap = image;
            entry.PendingRegion = region;
        }
        else
        {
            work += (long)plan.Crop.Width * plan.Crop.Height;
            entry.PendingPixels = ImagePlan.BuildLevel(image, plan.Crop, plan.Level, out _, out _);
            format = GpuTextureFormat.Rgba8Unorm;
        }
        entry.Texture = _device.CreateTexture(new GpuTextureDesc(w, h, format, GpuTextureUsage.Sampled, "AwAUI image"));
        _cache.Add(key, entry, bytes);
        _pending.Add(entry);
        return entry;
    }

    /// <summary>编码开始时上传本帧新建的纹理。</summary>
    public unsafe void Upload(GpuCommandList commands, ref long uploadedBytes)
    {
        foreach (var e in _pending)
        {
            var t = e.Texture;
            var region = new PixelRect(0, 0, t.Width, t.Height);
            if (e.PendingPixels is { } pixels)
            {
                commands.UploadTexture(t, region, pixels, t.Width * 4);
                uploadedBytes += pixels.Length;
            }
            else if (e.PendingBitmap is { IsDisposed: false } bmp)
            {
                var r = e.PendingRegion;
                byte* start = (byte*)bmp.Address + (long)r.Y * bmp.RowBytes + (long)r.X * 4;
                long length = (long)bmp.RowBytes * (r.Height - 1) + (long)r.Width * 4;
                commands.UploadTexture(t, region, new ReadOnlySpan<byte>(start, checked((int)length)), bmp.RowBytes);
                uploadedBytes += (long)r.Width * r.Height * 4;
                GC.KeepAlive(bmp);
            }
            e.PendingPixels = null;
            e.PendingBitmap = null;
        }
        _pending.Clear();
    }

    /// <summary>编码失败或设备丢失：丢弃全部纹理（下一帧按需重建）。</summary>
    public void Clear()
    {
        _pending.Clear();
        _cache.Clear();
    }

    public void Dispose() => Clear();
}
