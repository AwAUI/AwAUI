using System;
using AwAUI.Graphics;
using AwAUI.Render.Raster;

namespace AwAUI.Render;

/// <summary>
/// 纯 C# 软件渲染后端：CPU 光栅化，输出到平台帧缓冲或离屏位图。
/// 没有 GPU、驱动被列入黑名单或在 Headless 测试中都可以使用。
/// </summary>
public sealed class SoftwareRenderBackend : RenderBackend
{
    public override string Name => "Software";

    public override BackendCapabilities Capabilities => BackendCapabilities.BlurFilter | BackendCapabilities.AdvancedBlendModes;

    public override RenderTarget CreateRenderTarget(NativeSurface surface)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        var fb = surface.GetFeature<IFramebufferSurface>()
                 ?? throw new NotSupportedException("The software backend requires a surface with IFramebufferSurface.");
        return new FramebufferRenderTarget(this, surface, fb);
    }

    public override RenderTarget CreateOffscreenTarget(PixelSize size, double scale) => new BitmapRenderTarget(this, size, scale);

    internal RasterContext CreateContext() => new(ReportDegradation);
}

/// <summary>软件后端一帧的工作量统计（确定性，可用于测量滚动快路径的效果）。</summary>
public readonly struct SoftwareFrameStats
{
    public SoftwareFrameStats(long rasterizedPixels, long rasterWork, long commandCount, int blitCalls, long blitPixels)
    {
        RasterizedPixels = rasterizedPixels;
        RasterWork = rasterWork;
        CommandCount = commandCount;
        BlitCalls = blitCalls;
        BlitPixels = blitPixels;
    }

    /// <summary>实际着色的像素数（已按损伤区域裁剪；帧间位块搬移不计入）。</summary>
    public long RasterizedPixels { get; }

    /// <summary>光栅器与填充的抽象工作量（与 <see cref="RasterContext.MaxWorkPerFrame"/> 同单位）。</summary>
    public long RasterWork { get; }

    /// <summary>重放的 DisplayList 命令数（含嵌套列表展开）。</summary>
    public long CommandCount { get; }

    /// <summary>帧间位块搬移的次数（纯滚动快路径为 1，其余为 0）。</summary>
    public int BlitCalls { get; }

    /// <summary>帧间位块搬移复制的像素数。</summary>
    public long BlitPixels { get; }
}

/// <summary>渲染到自有位图的离屏目标。</summary>
public sealed class BitmapRenderTarget : RenderTarget
{
    readonly RasterContext _context;
    Bitmap _bitmap;
    double _scale;

    internal BitmapRenderTarget(SoftwareRenderBackend backend, PixelSize size, double scale)
    {
        _context = backend.CreateContext();
        _bitmap = new Bitmap(size);
        _scale = scale;
    }

    public Bitmap Bitmap => _bitmap;
    public override PixelSize PixelSize => _bitmap.Size;
    public override double Scale => _scale;

    /// <summary>最近一次 <see cref="Render"/> 的工作量统计。</summary>
    public SoftwareFrameStats LastFrameStats => new(_context.LastFrameRasterizedPixels, _context.LastFrameWork,
        _context.LastFrameCommands, _context.LastFrameBlitCalls, _context.LastFrameBlitPixels);

    public override void Resize(PixelSize size, double scale)
    {
        _scale = scale;
        if (size == _bitmap.Size) return;
        _bitmap.Dispose();
        _bitmap = new Bitmap(size);
    }

    public override void Render(DisplayList content, in RenderOptions options) =>
        _context.Render(content, _bitmap, _scale, options);

    public override void ReadPixels(PixelRect rect, Span<byte> destination, PixelFormat format) =>
        PixelCopy.Copy(_bitmap, rect, destination, format);

    public override void Dispose() => _bitmap.Dispose();
}

/// <summary>渲染到平台帧缓冲（锁定 → 绘制 → 提交）。</summary>
internal sealed class FramebufferRenderTarget : RenderTarget
{
    readonly RasterContext _context;
    readonly NativeSurface _surface;
    readonly IFramebufferSurface _framebuffer;

    public FramebufferRenderTarget(SoftwareRenderBackend backend, NativeSurface surface, IFramebufferSurface framebuffer)
    {
        _context = backend.CreateContext();
        _surface = surface;
        _framebuffer = framebuffer;
    }

    public override PixelSize PixelSize => _surface.Size;
    public override double Scale => _surface.Scale;

    public override void Resize(PixelSize size, double scale) { }

    public override void Render(DisplayList content, in RenderOptions options)
    {
        using var locked = _framebuffer.Lock();
        var bitmap = Bitmap.Wrap(locked.Address, locked.Size, locked.RowBytes, locked.Format);
        _context.Render(content, bitmap, _surface.Scale, options);
    }

    public override void ReadPixels(PixelRect rect, Span<byte> destination, PixelFormat format)
    {
        using var locked = _framebuffer.Lock();
        var bitmap = Bitmap.Wrap(locked.Address, locked.Size, locked.RowBytes, locked.Format);
        PixelCopy.Copy(bitmap, rect, destination, format);
    }
}

internal static class PixelCopy
{
    public static void Copy(Bitmap source, PixelRect rect, Span<byte> destination, PixelFormat format)
    {
        rect = rect.Intersect(new PixelRect(0, 0, source.Width, source.Height));
        int o = 0;
        bool swap = format != source.Format;
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            var row = source.GetRow(y).Slice(rect.X * 4, rect.Width * 4);
            if (!swap) row.CopyTo(destination.Slice(o));
            else
                for (int i = 0; i < row.Length; i += 4)
                {
                    destination[o + i] = row[i + 2]; destination[o + i + 1] = row[i + 1];
                    destination[o + i + 2] = row[i]; destination[o + i + 3] = row[i + 3];
                }
            o += row.Length;
        }
    }
}
