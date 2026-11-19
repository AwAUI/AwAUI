using System;
using System.Collections.Generic;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Frame;

/// <summary>
/// 离屏纹理池（图层与目标拷贝）。按「能装下且面积不超过 4 倍」挑选空闲纹理，新建时边长向上取整到 256，
/// 让尺寸逐帧变化（动画）的图层也能复用。帧结束时全部归还：同一队列上的命令按序执行，下一帧复用是安全的。
/// 长时间未用的纹理被释放。
/// </summary>
internal sealed class TexturePool : IDisposable
{
    sealed class Entry
    {
        public GpuTexture Texture = null!;
        public long LastUsed;
        public bool InUse;
    }

    readonly GpuDevice _device;
    readonly List<Entry> _entries = new();

    public TexturePool(GpuDevice device) => _device = device;

    public int Count => _entries.Count;

    public GpuTexture Rent(int width, int height, GpuTextureFormat format, long frame)
    {
        Entry? best = null;
        long need = (long)width * height;
        foreach (var e in _entries)
        {
            var t = e.Texture;
            if (e.InUse || t.Format != format || t.Width < width || t.Height < height) continue;
            long area = (long)t.Width * t.Height;
            if (area > need * 4 && area > 256 * 256) continue;
            if (best is null || area < (long)best.Texture.Width * best.Texture.Height) best = e;
        }
        if (best is null)
        {
            int max = _device.Capabilities.MaxTextureSize;
            int w = Math.Min(max, Math.Max(width, Align(width))), h = Math.Min(max, Math.Max(height, Align(height)));
            best = new Entry
            {
                Texture = _device.CreateTexture(new GpuTextureDesc(w, h, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, "AwAUI offscreen")),
            };
            _entries.Add(best);
        }
        best.InUse = true;
        best.LastUsed = frame;
        return best.Texture;
    }

    static int Align(int v) => (v + 255) & ~255;

    /// <summary>归还一张租用的纹理（本帧之后录制的命令可以复用它）。</summary>
    public void Return(GpuTexture texture)
    {
        foreach (var e in _entries)
            if (ReferenceEquals(e.Texture, texture)) { e.InUse = false; return; }
    }

    public void ReturnAll()
    {
        foreach (var e in _entries) e.InUse = false;
    }

    /// <summary>释放 <paramref name="maxAge"/> 帧内没有用过的纹理。</summary>
    public void Trim(long frame, long maxAge)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (!e.InUse && frame - e.LastUsed > maxAge)
            {
                e.Texture.Dispose();
                _entries.RemoveAt(i);
            }
        }
    }

    public void Dispose()
    {
        foreach (var e in _entries) e.Texture.Dispose();
        _entries.Clear();
    }
}
