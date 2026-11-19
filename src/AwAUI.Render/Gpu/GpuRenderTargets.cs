using System;
using AwAUI.Graphics;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu;

/// <summary>GPU 后端的渲染目标基类：提供每帧统计与等待 GPU 完成。</summary>
public abstract class GpuRenderTarget : RenderTarget
{
    private protected GpuRenderTarget(GpuRenderBackend backend) => Backend = backend;

    internal GpuRenderBackend Backend { get; }

    private protected GpuSubmission? LastSubmission;

    /// <summary>最近一帧的统计；<see cref="GpuFrameStats.GpuTime"/> 在 <see cref="WaitForGpu"/> 之后才有值。</summary>
    public GpuFrameStats? LastFrameStats { get; private protected set; }

    /// <summary>等待最近一帧在 GPU 上执行完毕，并填入它的 GPU 时间。</summary>
    public void WaitForGpu()
    {
        if (LastSubmission is null) return;
        LastSubmission.Wait();
        if (LastFrameStats is not null) LastFrameStats.GpuTime = LastSubmission.GpuTime;
    }

    private protected void Submit(GpuCommandList commands, GpuFrameStats stats)
    {
        LastSubmission = commands.Submit();
        LastFrameStats = stats;
    }

    private protected static void ReadBack(GpuDevice device, GpuTexture texture, PixelRect rect, Span<byte> destination, PixelFormat format)
    {
        rect = rect.Intersect(new PixelRect(0, 0, texture.Width, texture.Height));
        if (rect.IsEmpty) return;
        GpuReadback readback;
        using (var commands = device.BeginCommands())
        {
            readback = commands.ReadTexture(texture, rect);
            commands.Submit();
        }
        using (readback)
        {
            int rowBytes = rect.Width * 4;
            readback.CopyTo(destination, rowBytes);
            bool sourceBgra = texture.Format == GpuTextureFormat.Bgra8Unorm;
            if (sourceBgra != (format == PixelFormat.Bgra8888))
            {
                var data = destination.Slice(0, rowBytes * rect.Height);
                for (int i = 0; i + 3 < data.Length; i += 4) (data[i], data[i + 2]) = (data[i + 2], data[i]);
            }
        }
    }
}

/// <summary>渲染到 GPU 离屏纹理（RGBA8，预乘）的目标；<see cref="ReadPixels"/> 经 blit 回读。</summary>
public sealed class GpuOffscreenRenderTarget : GpuRenderTarget
{
    GpuTexture _texture;
    double _scale;

    internal GpuOffscreenRenderTarget(GpuRenderBackend backend, PixelSize size, double scale) : base(backend)
    {
        if (size.IsEmpty) throw new ArgumentOutOfRangeException(nameof(size));
        _texture = Create(size);
        _scale = scale;
    }

    GpuTexture Create(PixelSize size) =>
        Backend.Device.CreateTexture(new GpuTextureDesc(size.Width, size.Height, GpuTextureFormat.Rgba8Unorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, "AwAUI offscreen target"));

    /// <summary>目标纹理（给合成器把它作为图层贴到其他目标上）。</summary>
    public GpuTexture Texture => _texture;

    public override PixelSize PixelSize => _texture.Size;
    public override double Scale => _scale;

    public override void Resize(PixelSize size, double scale)
    {
        if (size.IsEmpty) throw new ArgumentOutOfRangeException(nameof(size));
        _scale = scale;
        if (size == _texture.Size) return;
        _texture.Dispose();
        _texture = Create(size);
    }

    public override void Render(DisplayList content, in RenderOptions options)
    {
        var full = new PixelRect(0, 0, _texture.Width, _texture.Height);
        var area = options.Damage is { } d ? d.Intersect(full) : full;
        if (area.IsEmpty) return;
        var commands = Backend.Device.BeginCommands();
        try
        {
            var stats = Backend.RenderFrame(commands, content, _texture, _scale, area, options.ClearColor);
            Submit(commands, stats);
        }
        catch
        {
            commands.Dispose();
            throw;
        }
    }

    public override void ReadPixels(PixelRect rect, Span<byte> destination, PixelFormat format) =>
        ReadBack(Backend.Device, _texture, rect, destination, format);

    public override void Dispose() => _texture.Dispose();
}

/// <summary>
/// 渲染到窗口交换链的目标。交换链图像每帧轮换且内容未定义，因此受损区域被忽略、总是整帧重画。
/// <see cref="ReadPixels"/> 把最近一帧的内容重画到离屏纹理后回读（截图与测试用）。
/// </summary>
internal sealed class GpuSurfaceRenderTarget : GpuRenderTarget
{
    readonly GpuSwapchain _swapchain;
    double _scale;
    DisplayList? _last;
    Color _lastClear;

    public GpuSurfaceRenderTarget(GpuRenderBackend backend, GpuSwapchain swapchain, double scale) : base(backend)
    {
        _swapchain = swapchain;
        _scale = scale;
    }

    public override PixelSize PixelSize => _swapchain.Size;
    public override double Scale => _scale;

    public override void Resize(PixelSize size, double scale)
    {
        _scale = scale;
        _swapchain.Resize(size);
    }

    /// <summary>见 <see cref="RenderTarget.LastFramePresented"/>：交换链没有给出可绘制纹理时这一帧被丢弃，返回 false。</summary>
    public override bool LastFramePresented => _presented;

    bool _presented = true;

    public override void Render(DisplayList content, in RenderOptions options)
    {
        _last = content;
        _lastClear = options.ClearColor;
        var texture = _swapchain.AcquireNextTexture();
        if (texture is null)
        {
            // 取不到可绘制纹理（窗口不可见或超时）：丢帧，由调用方在下一次机会重画
            _presented = false;
            return;
        }
        var commands = Backend.Device.BeginCommands();
        try
        {
            var stats = Backend.RenderFrame(commands, content, texture, _scale, new PixelRect(0, 0, texture.Width, texture.Height), options.ClearColor);
            commands.Present(_swapchain);
            Submit(commands, stats);
            _presented = true;
        }
        catch
        {
            commands.Dispose();
            throw;
        }
    }

    public override void ReadPixels(PixelRect rect, Span<byte> destination, PixelFormat format)
    {
        var size = _swapchain.Size;
        if (_last is null || size.IsEmpty) return;
        using var offscreen = (GpuOffscreenRenderTarget)Backend.CreateOffscreenTarget(size, _scale);
        offscreen.Render(_last, new RenderOptions(_lastClear));
        offscreen.ReadPixels(rect, destination, format);
    }

    public override void Dispose() => _swapchain.Dispose();
}
