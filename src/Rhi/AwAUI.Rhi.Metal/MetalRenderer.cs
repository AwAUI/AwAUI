using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Render.Gpu;
using AwAUI.Render.Rhi;

namespace AwAUI.Rhi.Metal;

/// <summary>
/// 组合根的渲染后端选择（design/01 <c>UseAwRenderer(o =&gt; o.PreferGpu = true)</c> 的现阶段形态）：应用只需一行
/// <code>
/// using var backend = MetalRenderer.Create(preferGpu: !args.Contains("--software"));
/// </code>
/// 有 Metal 时得到 <see cref="MetalRenderBackend"/>（GPU，设备丢失时自动回退到软件），否则得到 <see cref="SoftwareRenderBackend"/>。
/// 环境变量 <c>AWAUI_RENDERER=software</c> 强制软件后端（驱动问题的应急开关，类似浏览器的 --disable-gpu）。
/// 在任何系统上都可以调用：非 Apple 平台上 <see cref="IsSupported"/> 为 false，<see cref="Create"/> 返回软件后端。
/// </summary>
public static class MetalRenderer
{
    /// <summary>选择后端的环境变量名。值为 <c>software</c> 时 <see cref="Create"/> 总是返回软件后端；其他值不影响选择。</summary>
    public const string EnvironmentVariable = "AWAUI_RENDERER";

    /// <summary>当前进程能否使用 Metal（macOS 且有支持 Metal 的 GPU）。</summary>
    public static bool IsSupported => MetalDevice.IsSupported;

    /// <summary>创建 GPU（Metal）后端；不支持 Metal、或设备与着色器初始化失败时返回 null。</summary>
    public static MetalRenderBackend? TryCreate()
    {
        if (!MetalDevice.IsSupported) return null;
        MetalDevice device;
        try
        {
            device = MetalDevice.Create();
        }
        catch (GpuException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
        try
        {
            return new MetalRenderBackend(device);
        }
        catch (GpuException)
        {
            // GPU 后端初始化（图集、查找表纹理）失败：与设备创建失败同样处理，调用方改用软件
            device.Dispose();
            return null;
        }
    }

    /// <summary>
    /// GPU 可用且 <paramref name="preferGpu"/> 时返回 <see cref="MetalRenderBackend"/>，否则返回 <see cref="SoftwareRenderBackend"/>。
    /// 调用方用 <see cref="RenderBackend.Capabilities"/> 的 <see cref="BackendCapabilities.Gpu"/> 位（或 <see cref="RenderBackend.Name"/>）得知选择结果。
    /// </summary>
    public static RenderBackend Create(bool preferGpu = true)
    {
        if (preferGpu && !SoftwareForced(ReadEnvironment()) && TryCreate() is { } gpu) return gpu;
        return new SoftwareRenderBackend();
    }

    internal static bool SoftwareForced(string? value) =>
        value is not null && string.Equals(value.Trim(), "software", StringComparison.OrdinalIgnoreCase);

    static string? ReadEnvironment()
    {
        try { return Environment.GetEnvironmentVariable(EnvironmentVariable); }
        catch (System.Security.SecurityException) { return null; }
    }
}

/// <summary>
/// Metal GPU 后端 + 软件回退：内部是一个 <see cref="GpuRenderBackend"/>（Metal 设备）和按需创建的 <see cref="SoftwareRenderBackend"/>。
/// <list type="bullet">
/// <item>窗口表面提供 <see cref="IMetalLayerSurface"/> 时渲染到 CAMetalLayer 交换链；不提供时（例如无窗口平台的帧缓冲表面）该表面直接用软件后端，并上报一次降级。</item>
/// <item>设备丢失（命令缓冲出错、<see cref="GpuDevice.DeviceLost"/>）或渲染抛出 <see cref="GpuException"/> 后，整个后端永久切换到软件：
/// 已有的渲染目标在下一次 <see cref="RenderTarget.Render"/> 时为同一表面改建软件目标并重画这一帧（窗口表面须同时提供 <see cref="IFramebufferSurface"/>，
/// AwAUI.MacOS 的窗口即如此），离屏目标改为同尺寸的位图（原内容丢失，调用方本来就每帧重画）。切换经 <see cref="RenderBackend.Degraded"/> 上报。</item>
/// </list>
/// 线程模型与 <see cref="GpuRenderBackend"/> 相同：后端与它的渲染目标只在一个线程上使用。
/// </summary>
public sealed class MetalRenderBackend : RenderBackend
{
    readonly MetalDevice _device;
    readonly GpuRenderBackend _gpu;
    readonly List<FallbackRenderTarget> _targets = new();
    SoftwareRenderBackend? _software;
    string? _fallbackReason;
    bool _capture;
    bool _disposed;

    internal MetalRenderBackend(MetalDevice device)
    {
        _device = device;
        _gpu = new GpuRenderBackend(device, ownsDevice: true);
        _gpu.Degraded += ReportDegradation;
        device.DeviceLost += OnDeviceLost;
    }

    public override string Name => _fallbackReason is null ? _gpu.Name : "Software (Metal fallback)";

    /// <summary>当前生效的能力：回退前是 GPU 后端的能力，回退后是软件后端的能力。</summary>
    public override BackendCapabilities Capabilities => _fallbackReason is null ? _gpu.Capabilities : Software.Capabilities;

    /// <summary>内部的 GPU 后端（统计、图集信息）。回退后仍可访问，但不再用于渲染。</summary>
    public GpuRenderBackend Gpu => _gpu;

    /// <summary>Metal 设备。</summary>
    public MetalDevice Device => _device;

    /// <summary>是否已因设备丢失或 GPU 错误回退到软件渲染。</summary>
    public bool IsSoftwareFallback => _fallbackReason is not null;

    /// <summary>回退的原因；没有回退时为 null。</summary>
    public string? FallbackReason => _fallbackReason;

    /// <summary>最近一次由 GPU 渲染的帧的统计（任一渲染目标；CPU 侧录制与编码时间、实例数等）。</summary>
    public GpuFrameStats? LastFrameStats { get; private set; }

    /// <summary>
    /// 最近一次窗口帧等待可绘制纹理（<c>nextDrawable</c>）的时间：Render 调用的墙钟时间里这部分是阻塞等待，不是 CPU 工作。
    /// 离屏帧不更新它。
    /// </summary>
    public TimeSpan LastDrawableWait { get; private set; }

    /// <summary>
    /// 诊断：窗口交换链每次呈现前复制一份可绘制纹理，供 <see cref="ReadPresentedFrame"/> 读回（每帧多一次整帧 blit）。
    /// 对已有与以后创建的窗口目标都生效。
    /// </summary>
    public bool CapturePresentedFrames
    {
        get => _capture;
        set
        {
            _capture = value;
            foreach (var t in _targets) t.SetCapture(value);
        }
    }

    /// <summary>
    /// 读回最近一次呈现到窗口（CAMetalLayer 可绘制纹理）的帧，RGBA8 预乘；需要先打开 <see cref="CapturePresentedFrames"/>。
    /// 多个窗口时取最近渲染的那个；还没有捕获到帧或已回退到软件时返回 null。
    /// </summary>
    public Bitmap? ReadPresentedFrame()
    {
        FallbackRenderTarget? latest = null;
        foreach (var t in _targets)
            if (t.Swapchain is not null && (latest is null || t.LastRenderedFrame > latest.LastRenderedFrame)) latest = t;
        return latest?.Swapchain?.ReadPresentedFrame();
    }

    /// <summary>
    /// 模拟设备丢失（与 WebGL 的 <c>WEBGL_lose_context.loseContext()</c> 同类的测试手段）：后端按真实的设备丢失处理，
    /// 下一帧起全部渲染目标改用软件后端。用来验证应用在 GPU 失效时仍能继续显示。
    /// </summary>
    public void SimulateDeviceLoss()
    {
        ThrowIfDisposed();
        _device.SimulateDeviceLoss();
    }

    internal SoftwareRenderBackend Software
    {
        get
        {
            if (_software is null)
            {
                _software = new SoftwareRenderBackend();
                _software.Degraded += ReportDegradation;
            }
            return _software;
        }
    }

    long _frameCounter;
    internal long NextFrameNumber() => ++_frameCounter;

    GpuRenderTarget? _lastGpuTarget;

    /// <summary>等待最近一次由 GPU 渲染的帧执行完毕，并填入 <see cref="LastFrameStats"/> 的 <see cref="GpuFrameStats.GpuTime"/>。</summary>
    public void WaitForGpu() => _lastGpuTarget?.WaitForGpu();

    internal void SetLastFrameStats(GpuRenderTarget target, MetalSwapchain? swapchain)
    {
        _lastGpuTarget = target;
        LastFrameStats = target.LastFrameStats;
        if (swapchain is not null) LastDrawableWait = swapchain.LastAcquireTime;
    }

    void OnDeviceLost() => FallBack("the Metal device was lost");

    /// <summary>永久切换到软件后端（幂等）。</summary>
    internal void FallBack(string reason)
    {
        if (_fallbackReason is not null) return;
        _fallbackReason = reason;
        ReportDegradation("GPU rendering disabled, falling back to the software backend: " + reason);
    }

    public override RenderTarget CreateRenderTarget(NativeSurface surface)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        ThrowIfDisposed();
        if (_fallbackReason is not null) return Software.CreateRenderTarget(surface);
        if (surface.GetFeature<IMetalLayerSurface>() is not { } layer)
        {
            ReportDegradation("The surface does not provide a CAMetalLayer; rendering it with the software backend.");
            return Software.CreateRenderTarget(surface);
        }
        // 读取 MetalLayer 时平台窗口切换到 CAMetalLayer 呈现（见 AwAUI.MacOS.MacWindow）
        var swapchain = _device.CreateSwapchain(layer.MetalLayer, surface.Size);
        GpuRenderTarget gpu;
        try
        {
            gpu = _gpu.CreateRenderTarget(swapchain, surface.Scale);
        }
        catch
        {
            swapchain.Dispose();
            throw;
        }
        var target = new FallbackRenderTarget(this, surface, gpu, swapchain);
        target.SetCapture(_capture);
        _targets.Add(target);
        return target;
    }

    public override RenderTarget CreateOffscreenTarget(PixelSize size, double scale)
    {
        ThrowIfDisposed();
        if (_fallbackReason is not null) return Software.CreateOffscreenTarget(size, scale);
        var target = new FallbackRenderTarget(this, null, (GpuRenderTarget)_gpu.CreateOffscreenTarget(size, scale), null);
        _targets.Add(target);
        return target;
    }

    internal void Remove(FallbackRenderTarget target) => _targets.Remove(target);

    public override void InvalidateDeviceResources() => _gpu.InvalidateDeviceResources();

    public override T? GetFeature<T>() where T : class => this as T ?? _gpu.GetFeature<T>();

    void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetalRenderBackend));
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.DeviceLost -= OnDeviceLost;
        _targets.Clear();
        _lastGpuTarget = null;
        _gpu.Dispose();
        _software?.Dispose();
    }
}

/// <summary>
/// <see cref="MetalRenderBackend"/> 的渲染目标：平时转发给 GPU 目标；后端回退后为同一表面（或同尺寸的离屏位图）改建软件目标。
/// </summary>
internal sealed class FallbackRenderTarget : RenderTarget
{
    readonly MetalRenderBackend _owner;
    readonly NativeSurface? _surface;
    RenderTarget? _active;
    bool _isGpu = true;
    PixelSize _size;
    double _scale;
    bool _disposed;

    public FallbackRenderTarget(MetalRenderBackend owner, NativeSurface? surface, GpuRenderTarget gpu, MetalSwapchain? swapchain)
    {
        _owner = owner;
        _surface = surface;
        _active = gpu;
        Swapchain = swapchain;
        _size = gpu.PixelSize;
        _scale = gpu.Scale;
    }

    /// <summary>窗口目标的交换链（离屏目标或回退后为 null）。</summary>
    public MetalSwapchain? Swapchain { get; private set; }

    /// <summary>最近一次渲染的序号（选出最近渲染的窗口）。</summary>
    public long LastRenderedFrame { get; private set; }

    /// <summary>当前是否由 GPU 渲染。</summary>
    public bool IsGpu => _isGpu;

    public override PixelSize PixelSize => _active?.PixelSize ?? _size;
    public override double Scale => _active?.Scale ?? _scale;

    /// <summary>转发当前目标的丢帧状态；切到软件之后恒为 true（软件目标不会丢帧）。</summary>
    public override bool LastFramePresented => _active?.LastFramePresented ?? true;

    public void SetCapture(bool capture)
    {
        if (_isGpu && Swapchain is not null) Swapchain.CapturePresentedFrames = capture;
    }

    public override void Resize(PixelSize size, double scale)
    {
        _size = size;
        _scale = scale;
        if (_active is null) return;
        try
        {
            _active.Resize(size, scale);
        }
        catch (GpuException e) when (_isGpu)
        {
            _owner.FallBack(e.Message);
        }
    }

    public override void Render(DisplayList content, in RenderOptions options)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FallbackRenderTarget));
        if (_isGpu && !_owner.IsSoftwareFallback)
        {
            try
            {
                _active!.Render(content, options);
                _owner.SetLastFrameStats((GpuRenderTarget)_active, Swapchain);
                LastRenderedFrame = _owner.NextFrameNumber();
            }
            catch (Exception e) when (e is GpuException || _owner.IsSoftwareFallback)
            {
                // GpuException：命令缓冲 / 资源创建失败。其他异常只在本帧内刚发生设备丢失时吞掉（失效的资源可能以别的方式失败）
                _owner.FallBack(e.Message);
            }
            if (!_owner.IsSoftwareFallback) return;
            // 设备在这一帧里丢失：本帧改用软件重画，避免丢帧
        }
        bool switched = _isGpu;
        if (switched) SwitchToSoftware();
        if (_active is null) return;
        // 软件目标保留上一帧像素才能局部重画；刚切换时没有上一帧，忽略受损区域整帧重画
        if (switched) _active.Render(content, new RenderOptions(options.ClearColor));
        else _active.Render(content, options);
    }

    void SwitchToSoftware()
    {
        _isGpu = false;
        var gpu = _active;
        _active = null;
        Swapchain = null;
        try
        {
            _owner.Gpu.Device.WaitIdle();
        }
        catch (Exception)
        {
            // 设备已丢失：等待可能失败，资源照常释放
        }
        gpu?.Dispose();
        if (_surface is null)
        {
            _active = _owner.Software.CreateOffscreenTarget(_size.IsEmpty ? new PixelSize(1, 1) : _size, _scale);
            return;
        }
        if (_surface.GetFeature<IFramebufferSurface>() is null)
        {
            // 表面只能用 GPU 呈现：之后的帧只能丢弃
            return;
        }
        _active = _owner.Software.CreateRenderTarget(_surface);
    }

    public override void ReadPixels(PixelRect rect, Span<byte> destination, PixelFormat format) =>
        _active?.ReadPixels(rect, destination, format);

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Remove(this);
        _active?.Dispose();
        _active = null;
    }
}
