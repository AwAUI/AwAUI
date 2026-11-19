using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Render.Gpu;
using AwAUI.RenderTests;
using AwAUI.Rhi.Metal.Interop;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>
/// 组合根助手 <see cref="MetalRenderer"/> 与 <see cref="MetalRenderBackend"/>：后端选择、窗口交换链的呈现与捕获
/// （含 presentsWithTransaction 的呈现路径）、没有 CAMetalLayer 的表面、设备丢失后回退到软件。
/// </summary>
public class MetalRendererTests
{
    const int W = 320, H = 220;
    const double Scale = 2;
    static readonly PixelSize Device = new(640, 440);

    static DisplayList Demo(double time = 0.4) =>
        new RenderDemo.DemoScene { Time = time, Clicks = 3, Pointer = new Point(200, 150) }.Build(new Size(W, H));

    static Bitmap Software(DisplayList list) => MetalTestSupport.RenderSoftware(list, W, H, Scale);

    static unsafe Bitmap Read(RenderTarget target)
    {
        var size = target.PixelSize;
        var bmp = new Bitmap(size);
        target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), new Span<byte>((void*)bmp.Address, bmp.RowBytes * size.Height), PixelFormat.Rgba8888);
        return bmp;
    }

    static void AssertParity(Bitmap expected, Bitmap actual, string name)
    {
        var diff = ImageDiff.Compare(expected, actual);
        if (diff.MaxDiff > 64 || diff.Over8Percent > 0.3 || diff.Over24Percent > 0.02 || diff.Over2Percent > 3)
        {
            MetalTestSupport.Save(actual, name + ".gpu");
            MetalTestSupport.Save(expected, name + ".software");
            diff.SaveDiff(name);
            Assert.Fail($"{name}: {diff}");
        }
    }

    static void AssertIdentical(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        for (int y = 0; y < expected.Height; y++)
            Assert.True(expected.GetRow(y).SequenceEqual(actual.GetRow(y)), $"row {y} differs");
    }

    [Fact]
    public void Software_is_chosen_when_gpu_is_not_preferred_or_forced_off()
    {
        using (var b = MetalRenderer.Create(preferGpu: false)) Assert.IsType<SoftwareRenderBackend>(b);
        Assert.True(MetalRenderer.SoftwareForced("software"));
        Assert.True(MetalRenderer.SoftwareForced(" Software "));
        Assert.False(MetalRenderer.SoftwareForced("gpu"));
        Assert.False(MetalRenderer.SoftwareForced(""));
        Assert.False(MetalRenderer.SoftwareForced(null));
        if (!MetalRenderer.IsSupported) Assert.Null(MetalRenderer.TryCreate());
    }

    [MetalFact]
    public void Gpu_is_chosen_when_available()
    {
        if (MetalRenderer.SoftwareForced(Environment.GetEnvironmentVariable(MetalRenderer.EnvironmentVariable))) return;
        using var backend = MetalRenderer.Create();
        var metal = Assert.IsType<MetalRenderBackend>(backend);
        Assert.True((backend.Capabilities & BackendCapabilities.Gpu) != 0);
        Assert.Equal("GPU (Metal)", backend.Name);
        Assert.False(metal.IsSoftwareFallback);
        Assert.Null(metal.FallbackReason);
    }

    [MetalFact]
    public void Offscreen_target_renders_on_the_gpu_then_falls_back_to_software_after_device_loss()
    {
        using var backend = MetalRenderer.TryCreate()!;
        var degraded = new List<string>();
        backend.Degraded += degraded.Add;
        var list = Demo();
        using var expected = Software(list);
        using var target = backend.CreateOffscreenTarget(Device, Scale);
        target.Render(list, new RenderOptions(Color.White));
        Assert.NotNull(backend.LastFrameStats);
        using (var gpu = Read(target)) AssertParity(expected, gpu, "renderer-offscreen");

        backend.SimulateDeviceLoss();
        Assert.True(backend.IsSoftwareFallback);
        Assert.Equal("Software (Metal fallback)", backend.Name);
        Assert.Equal(0, (int)(backend.Capabilities & BackendCapabilities.Gpu));
        Assert.Single(degraded, m => m.Contains("falling back to the software backend", StringComparison.Ordinal));

        // 已有目标：下一帧起由软件渲染，与软件后端逐字节相同；尺寸不变
        target.Render(list, new RenderOptions(Color.White));
        Assert.Equal(Device, target.PixelSize);
        using (var sw = Read(target)) AssertIdentical(expected, sw);
        // 回退后的局部重画（受损区域）在软件目标上照常工作
        var list2 = Demo(0.9);
        using var expected2 = Software(list2);
        target.Render(list2, new RenderOptions(Color.White));
        using (var sw2 = Read(target)) AssertIdentical(expected2, sw2);

        // 新目标直接是软件目标；再次模拟丢失不重复上报
        using var fresh = backend.CreateOffscreenTarget(Device, Scale);
        Assert.IsType<BitmapRenderTarget>(fresh);
        backend.SimulateDeviceLoss();
        Assert.Single(degraded, m => m.Contains("falling back", StringComparison.Ordinal));
    }

    [MetalFact]
    public void Window_surface_presents_to_the_layer_captures_the_drawable_and_falls_back_to_the_framebuffer()
    {
        using var backend = MetalRenderer.TryCreate()!;
        IntPtr layer;
        using (AutoreleasePool.Push())
            layer = ObjC.Retain(ObjC.Send(ObjC.Class("CAMetalLayer"), ObjC.Sel("layer")));
        try
        {
            var surface = new DualSurface(layer, Device);
            backend.CapturePresentedFrames = true;
            using var target = backend.CreateRenderTarget(surface);
            Assert.Equal(1, surface.LayerRequests);
            Assert.Equal(0, surface.Locks);
            Assert.Null(backend.ReadPresentedFrame()); // 还没有呈现过

            // 常规呈现（presentDrawable:）：读回的可绘制纹理与软件渲染一致
            var list = Demo();
            using var expected = Software(list);
            for (int i = 0; i < 3; i++) target.Render(list, new RenderOptions(Color.White));
            using (var presented = backend.ReadPresentedFrame())
            {
                Assert.NotNull(presented);
                AssertParity(expected, presented!, "renderer-presented");
            }

            // 层要求随 CA 事务呈现（平台在拖动缩放时设置）：提交 → 等待调度 → [drawable present]
            ObjC.SendBool(layer, ObjC.Sel("setPresentsWithTransaction:"), 1);
            var list2 = Demo(1.3);
            using var expected2 = Software(list2);
            target.Render(list2, new RenderOptions(Color.White));
            using (var presented2 = backend.ReadPresentedFrame()) AssertParity(expected2, presented2!, "renderer-presented-transaction");
            ObjC.SendBool(layer, ObjC.Sel("setPresentsWithTransaction:"), 0);

            // 尺寸变化：可绘制尺寸跟随
            target.Resize(new PixelSize(400, 300), Scale);
            surface.CurrentSize = new PixelSize(400, 300);
            target.Render(list, new RenderOptions(Color.White));
            using (var resized = backend.ReadPresentedFrame()) Assert.Equal(new PixelSize(400, 300), resized!.Size);

            // 设备丢失：同一表面改用帧缓冲（平台据此切回软件呈现），本帧即由软件画出
            backend.SimulateDeviceLoss();
            target.Render(list, new RenderOptions(Color.White));
            Assert.True(surface.Locks > 0);
            Assert.Null(backend.ReadPresentedFrame());
            using var expected3 = MetalTestSupport.RenderSoftware(list, 200, 150, Scale);
            AssertIdentical(expected3, surface.Framebuffer);
        }
        finally
        {
            ObjC.Release(layer);
        }
    }

    [MetalFact]
    public void Swapchain_drops_an_unpresented_drawable_when_resized()
    {
        // 一帧在取得可绘制对象之后、呈现之前失败时，交换链留着这张可绘制对象给下一帧复用；
        // 期间窗口改变了尺寸，就必须丢掉它按新尺寸重新取得，否则新一帧画进旧尺寸的纹理
        using var device = MetalDevice.Create();
        IntPtr layer;
        using (AutoreleasePool.Push())
            layer = ObjC.Retain(ObjC.Send(ObjC.Class("CAMetalLayer"), ObjC.Sel("layer")));
        try
        {
            using var swapchain = device.CreateSwapchain(layer, new PixelSize(64, 48));
            var first = swapchain.AcquireNextTexture();
            Assert.NotNull(first);
            Assert.Equal((64, 48), (first!.Width, first.Height));
            Assert.Same(first, swapchain.AcquireNextTexture()); // 尺寸不变：复用未呈现的那张
            swapchain.Resize(new PixelSize(64, 48));
            Assert.Same(first, swapchain.AcquireNextTexture());

            swapchain.Resize(new PixelSize(100, 80));
            var second = swapchain.AcquireNextTexture();
            Assert.NotNull(second);
            Assert.Equal((100, 80), (second!.Width, second.Height));
        }
        finally
        {
            ObjC.Release(layer);
        }
    }

    [MetalFact]
    public void Window_target_reports_frames_dropped_when_no_drawable_is_available()
    {
        // 交换链取不到可绘制纹理（这里用尺寸 0 模拟窗口不可见）时 Render 丢帧并经 LastFramePresented 告知调用方；
        // 恢复后下一帧呈现。设备丢失回退到软件之后恒为 true。
        using var backend = MetalRenderer.TryCreate()!;
        IntPtr layer;
        using (AutoreleasePool.Push())
            layer = ObjC.Retain(ObjC.Send(ObjC.Class("CAMetalLayer"), ObjC.Sel("layer")));
        try
        {
            var surface = new DualSurface(layer, Device);
            using var target = backend.CreateRenderTarget(surface);
            Assert.True(target.LastFramePresented);   // 第一次渲染之前
            var list = Demo();
            target.Render(list, new RenderOptions(Color.White));
            Assert.True(target.LastFramePresented);

            target.Resize(new PixelSize(0, 0), Scale);
            target.Render(list, new RenderOptions(Color.White));
            Assert.False(target.LastFramePresented);

            target.Resize(Device, Scale);
            target.Render(list, new RenderOptions(Color.White));
            Assert.True(target.LastFramePresented);

            backend.SimulateDeviceLoss();
            target.Resize(new PixelSize(0, 0), Scale);
            surface.CurrentSize = new PixelSize(8, 8);
            target.Render(list, new RenderOptions(Color.White));
            Assert.True(target.LastFramePresented);
        }
        finally
        {
            ObjC.Release(layer);
        }
    }

    [Fact]
    public void Software_and_offscreen_targets_always_report_presented_frames()
    {
        using var backend = new SoftwareRenderBackend();
        using var target = backend.CreateOffscreenTarget(new PixelSize(4, 4), 1);
        target.Render(DisplayList.Empty, new RenderOptions(Color.White));
        Assert.True(target.LastFramePresented);
    }

    [MetalFact]
    public void Surface_without_a_metal_layer_is_rendered_by_the_software_backend()
    {
        using var backend = MetalRenderer.TryCreate()!;
        var degraded = new List<string>();
        backend.Degraded += degraded.Add;
        var surface = new FramebufferOnlySurface(Device);
        using var target = backend.CreateRenderTarget(surface);
        Assert.Single(degraded);
        Assert.False(backend.IsSoftwareFallback); // 只影响这个表面
        var list = Demo();
        target.Render(list, new RenderOptions(Color.White));
        using var expected = Software(list);
        AssertIdentical(expected, surface.Framebuffer);
    }

    /// <summary>同时提供 CAMetalLayer 与内存帧缓冲的表面（与 AwAUI.MacOS 的窗口表面相同的能力组合）。</summary>
    sealed class DualSurface : NativeSurface, IMetalLayerSurface, IFramebufferSurface
    {
        readonly IntPtr _layer;
        public DualSurface(IntPtr layer, PixelSize size) { _layer = layer; CurrentSize = size; Framebuffer = new Bitmap(size); }
        public PixelSize CurrentSize { get; set; }
        public override PixelSize Size => CurrentSize;
        public override double Scale => 2;
        public Bitmap Framebuffer { get; private set; }
        public int LayerRequests { get; private set; }
        public int Locks { get; private set; }

        public IntPtr MetalLayer
        {
            get
            {
                LayerRequests++;
                return _layer;
            }
        }

        public ILockedFramebuffer Lock()
        {
            Locks++;
            if (Framebuffer.Size != Size)
            {
                Framebuffer.Dispose();
                Framebuffer = new Bitmap(Size);
            }
            return new Locked(Framebuffer);
        }
    }

    sealed class FramebufferOnlySurface : NativeSurface, IFramebufferSurface
    {
        public FramebufferOnlySurface(PixelSize size) { Size = size; Framebuffer = new Bitmap(size); }
        public override PixelSize Size { get; }
        public override double Scale => 2;
        public Bitmap Framebuffer { get; }
        public ILockedFramebuffer Lock() => new Locked(Framebuffer);
    }

    sealed class Locked : ILockedFramebuffer
    {
        readonly Bitmap _bitmap;
        public Locked(Bitmap bitmap) => _bitmap = bitmap;
        public IntPtr Address => _bitmap.Address;
        public PixelSize Size => _bitmap.Size;
        public int RowBytes => _bitmap.RowBytes;
        public PixelFormat Format => _bitmap.Format;
        public void Dispose() { }
    }
}
