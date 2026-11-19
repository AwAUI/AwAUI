using System;
using System.Diagnostics;
using AwAUI.Graphics;
using AwAUI.Render.Gpu.Atlas;
using AwAUI.Render.Gpu.Frame;
using AwAUI.Render.Raster;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu;

/// <summary>
/// GPU 渲染后端（design/03「GPU 渲染管线」）：在任意 <see cref="GpuDevice"/> 上实现 <see cref="RenderBackend"/>。
/// 矩形、圆角矩形、矩形描边与盒阴影在片元着色器里解析求覆盖率；路径、字形与裁剪蒙版在 CPU 上用与软件后端相同的光栅器生成 A8 掩码，
/// 放进共享图集；渐变经查找表纹理；图层渲染到离屏纹理后合成；读目标的混合模式先拷贝目标区域。输出与软件后端在容差内一致。
/// <para>
/// 线程模型：后端及其全部渲染目标只在一个线程上使用（渲染线程）。图集、字形缓存、路径掩码缓存与纹理池在渲染目标之间共享。
/// 位图（DrawImage）上传为纹理，按（位图，内容版本）缓存，缩小用 CPU 构造的 mip 级别，与软件后端采样相同的纹素。
/// 尚未实现：保留层（<see cref="BackendCapabilities.RetainedLayers"/>，等 Scene 合成器）、滤镜、彩色字形。
/// </para>
/// </summary>
public sealed class GpuRenderBackend : RenderBackend
{
    readonly GpuDevice _device;
    readonly bool _ownsDevice;
    readonly MaskAtlas _atlas;
    readonly GradientLuts _luts;
    readonly GlyphCache _glyphs = new();
    readonly PathMaskCache _paths = new();
    readonly TexturePool _pool;
    readonly ImageTextures _images;
    readonly FrameGraph _graph = new();
    readonly FrameRecorder _recorder;
    readonly FrameEncoder _encoder;
    long _frame;
    bool _disposed;

    /// <param name="device">GPU 设备。</param>
    /// <param name="ownsDevice">为 true 时后端释放时一并释放设备。</param>
    public GpuRenderBackend(GpuDevice device, bool ownsDevice = true)
        : this(device, ownsDevice, MaskAtlas.DefaultPageSize, MaskAtlas.DefaultMaxPages) { }

    /// <summary>测试用：指定图集页的大小与页数上限（小图集用来检验淘汰与跨页）。</summary>
    internal GpuRenderBackend(GpuDevice device, bool ownsDevice, int atlasPageSize, int atlasMaxPages)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _ownsDevice = ownsDevice;
        _atlas = new MaskAtlas(device, atlasPageSize, atlasMaxPages);
        _luts = new GradientLuts(device);
        _pool = new TexturePool(device);
        _images = new ImageTextures(device);
        _recorder = new FrameRecorder(_atlas, _luts, _glyphs, _paths, ReportDegradation) { Images = _images };
        _encoder = new FrameEncoder(device, _pool);
        device.DeviceLost += InvalidateDeviceResources;
    }

    public override string Name => "GPU (" + _device.Api + ")";

    public override BackendCapabilities Capabilities => BackendCapabilities.Gpu | BackendCapabilities.AdvancedBlendModes;

    public GpuDevice Device => _device;

    public override RenderTarget CreateRenderTarget(NativeSurface surface)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        ThrowIfDisposed();
        return new GpuSurfaceRenderTarget(this, _device.CreateSwapchain(surface), surface.Scale);
    }

    /// <summary>直接用已创建的交换链创建渲染目标（平台窗口尚未提供本设备能识别的 <see cref="NativeSurface"/> 特性时使用）。</summary>
    public GpuRenderTarget CreateRenderTarget(GpuSwapchain swapchain, double scale)
    {
        ThrowIfDisposed();
        return new GpuSurfaceRenderTarget(this, swapchain, scale);
    }

    public override RenderTarget CreateOffscreenTarget(PixelSize size, double scale)
    {
        ThrowIfDisposed();
        return new GpuOffscreenRenderTarget(this, size, scale);
    }

    /// <summary>丢弃全部设备资源；图集条目、查找表与离屏纹理在下一帧按需重建。</summary>
    public override void InvalidateDeviceResources()
    {
        _atlas.Clear();
        _luts.Clear();
        _paths.Clear();
        _glyphs.Clear();
        _images.Clear();
        _pool.Dispose();
        _encoder.Dispose();
    }

    /// <summary>诊断：图集页数、缓存条目数等。</summary>
    public GpuBackendStatistics Statistics => new(_atlas.Pages.Count, _atlas.Evictions, _glyphs.Count, _paths.Count, _luts.PageCount, _pool.Count)
    {
        ImageTextures = _images.Count,
        ImageTextureBytes = _images.Bytes,
        ImageTexturesCreated = _images.Builds,
    };

    /// <summary>录制并编码一帧到 <paramref name="texture"/>。<paramref name="area"/> 小于整个目标时只重画该区域（目标原有内容保留）。</summary>
    internal GpuFrameStats RenderFrame(GpuCommandList commands, DisplayList list, GpuTexture texture, double scale, PixelRect area, Color clear)
    {
        ThrowIfDisposed();
        if (list is null) throw new ArgumentNullException(nameof(list));
        _frame++;
        var sw = Stopwatch.StartNew();
        _atlas.BeginFrame(_frame);
        _luts.BeginFrame(_frame);
        _images.BeginFrame(_frame);
        var full = new PixelRect(0, 0, texture.Width, texture.Height);
        area = area.Intersect(full);
        bool partial = area != full;
        TimeSpan record;
        try
        {
            _recorder.Record(list, _graph, full, area, scale, _frame, partial ? clear : null);
            record = sw.Elapsed;
            _encoder.Encode(commands, _graph, texture, partial ? GpuLoadAction.Load : GpuLoadAction.Clear, clear, _atlas, _luts, _frame, _images);
        }
        catch
        {
            // 编码中途失败（例如显存不足时创建图层纹理）：图集与查找表的脏区已经录进了调用方将要丢弃的命令列表，
            // CPU 端却认为它们已在 GPU 上。丢弃这些缓存的设备内容，下一帧重新光栅化并上传（字形掩码保留 CPU 字节，随页代数失效后重传）。
            _atlas.Clear();
            _luts.Clear();
            _paths.Clear();
            _images.Clear();
            _pool.ReturnAll();
            throw;
        }
        _atlas.EndFrame();
        _pool.ReturnAll();
        _pool.Trim(_frame, 600);
        var total = sw.Elapsed;
        return new GpuFrameStats
        {
            RecordTime = record,
            EncodeTime = total - record,
            Instances = _graph.InstanceCount,
            Layers = _graph.Layers.Count,
            DrawCalls = _encoder.DrawCalls,
            RenderPasses = _encoder.RenderPasses,
            DstCopies = _encoder.DstCopies,
            UploadBytes = _encoder.UploadBytes,
            PathMasksRasterized = _recorder.PathMasksRasterized,
            GlyphsRasterized = _recorder.GlyphsRasterized,
        };
    }

    void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GpuRenderBackend));
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.DeviceLost -= InvalidateDeviceResources;
        _device.WaitIdle();
        InvalidateDeviceResources();
        if (_ownsDevice) _device.Dispose();
    }
}

/// <summary>一帧的统计（诊断与性能测量）。</summary>
public sealed class GpuFrameStats
{
    /// <summary>解释 DisplayList、准备图集与合批的 CPU 时间。</summary>
    public TimeSpan RecordTime { get; internal set; }

    /// <summary>上传与命令编码的 CPU 时间（不含提交）。</summary>
    public TimeSpan EncodeTime { get; internal set; }

    /// <summary>GPU 执行时间；命令尚未执行完毕或设备不支持时为 null（见 <see cref="GpuRenderTarget.WaitForGpu"/>）。</summary>
    public TimeSpan? GpuTime { get; internal set; }

    public int Instances { get; internal set; }
    public int DrawCalls { get; internal set; }
    public int RenderPasses { get; internal set; }
    public int Layers { get; internal set; }
    public int DstCopies { get; internal set; }
    public long UploadBytes { get; internal set; }
    public int PathMasksRasterized { get; internal set; }
    public int GlyphsRasterized { get; internal set; }

    public override string ToString() =>
        $"record {RecordTime.TotalMilliseconds:F2} ms, encode {EncodeTime.TotalMilliseconds:F2} ms, gpu {(GpuTime is { } g ? g.TotalMilliseconds.ToString("F2") + " ms" : "n/a")}, " +
        $"{Instances} instances, {DrawCalls} draws, {RenderPasses} passes, {Layers} layers, {DstCopies} dst copies, {UploadBytes / 1024} KB uploaded, " +
        $"{PathMasksRasterized} path masks, {GlyphsRasterized} glyphs rasterized";
}

/// <summary>后端级的资源统计。</summary>
public readonly struct GpuBackendStatistics
{
    public GpuBackendStatistics(int atlasPages, long atlasEvictions, int glyphs, int pathMasks, int lutPages, int pooledTextures)
    {
        AtlasPages = atlasPages;
        AtlasEvictions = atlasEvictions;
        CachedGlyphs = glyphs;
        CachedPathMasks = pathMasks;
        GradientLutPages = lutPages;
        PooledTextures = pooledTextures;
    }

    public int AtlasPages { get; }
    public long AtlasEvictions { get; }
    public int CachedGlyphs { get; }
    public int CachedPathMasks { get; }
    public int GradientLutPages { get; }
    public int PooledTextures { get; }

    /// <summary>缓存中的位图纹理数（DrawImage）。</summary>
    public int ImageTextures { get; init; }

    /// <summary>位图纹理占用的字节数。</summary>
    public long ImageTextureBytes { get; init; }

    /// <summary>累计新建的位图纹理数（命中缓存时不增加）。</summary>
    public long ImageTexturesCreated { get; init; }
}
