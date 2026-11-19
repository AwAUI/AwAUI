using AwAUI.Graphics;
using AwAUI.Render.Gpu;
using AwAUI.Render.Rhi;
using AwAUI.RenderTests;
using Xunit.Abstractions;
using AwAUI.Rhi.Metal.Interop;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>GPU 后端在 Metal 上的行为：跨帧缓存、图集淘汰、局部重画、交换链、设备资源重建、回读格式。</summary>
public class GpuBackendTests
{
    readonly ITestOutputHelper _output;

    public GpuBackendTests(ITestOutputHelper output) => _output = output;

    static DisplayList Scene(Action<DrawingContext> draw)
    {
        var dc = new DrawingContext();
        draw(dc);
        return dc.Finish();
    }

    static void AssertClose(Bitmap expected, Bitmap actual, string name, double maxOver8Percent = 0.3)
    {
        var diff = ImageDiff.Compare(expected, actual);
        if (diff.Over8Percent > maxOver8Percent || diff.MaxDiff > 64)
        {
            MetalTestSupport.Save(actual, name + ".gpu");
            MetalTestSupport.Save(expected, name + ".software");
            diff.SaveDiff(name);
            Assert.Fail($"{name}: {diff}");
        }
    }

    static unsafe Bitmap Read(RenderTarget target)
    {
        var size = target.PixelSize;
        var bmp = new Bitmap(size);
        target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), new Span<byte>((void*)bmp.Address, bmp.RowBytes * size.Height), PixelFormat.Rgba8888);
        return bmp;
    }

    [MetalFact]
    public void Static_scene_reuses_glyph_and_path_caches_on_later_frames()
    {
        var dc = new DrawingContext();
        TextScenes.PaintScene(dc);
        Scenes.Strokes(dc);
        var list = dc.Finish();
        lock (MetalTestSupport.Lock)
        {
            using var target = (GpuRenderTarget)MetalTestSupport.Backend.CreateOffscreenTarget(new PixelSize(360, 220), 1);
            target.Render(list, new RenderOptions(Color.White));
            target.Render(list, new RenderOptions(Color.White));
            target.WaitForGpu();
            var stats = target.LastFrameStats!;
            _output.WriteLine(stats.ToString());
            Assert.Equal(0, stats.GlyphsRasterized);
            Assert.Equal(0, stats.PathMasksRasterized);
            Assert.True(stats.DrawCalls <= 12, $"{stats.DrawCalls} draw calls");
            Assert.NotNull(stats.GpuTime);
        }
    }

    [MetalFact]
    public void Tiny_atlas_evicts_pages_between_frames_without_corrupting_output()
    {
        var a = Scene(TextScenes.Sizes);
        var b = Scene(GpuScenes.ManyPaths);
        using var expectedA = MetalTestSupport.RenderSoftware(a, 560, 400);
        using var expectedB = MetalTestSupport.RenderSoftware(b, 560, 400);
        lock (MetalTestSupport.Lock)
        {
            // 256×256 的页、最多 2 页：每帧都要淘汰，单帧内还会临时超出上限
            using var backend = new GpuRenderBackend(MetalTestSupport.Backend.Device, ownsDevice: false, atlasPageSize: 256, atlasMaxPages: 2);
            using var target = backend.CreateOffscreenTarget(new PixelSize(560, 400), 1);
            for (int i = 0; i < 4; i++)
            {
                var (list, expected) = i % 2 == 0 ? (a, expectedA) : (b, expectedB);
                target.Render(list, new RenderOptions(Color.White));
                using var actual = Read(target);
                AssertClose(expected, actual, $"tiny-atlas-{i}");
            }
            Assert.True(backend.Statistics.AtlasEvictions > 0);
        }
    }

    [MetalFact]
    public void Damage_rect_only_redraws_that_area()
    {
        var a = Scene(Scenes.Shapes);
        var b = Scene(Scenes.Gradients);
        var damage = new PixelRect(30, 20, 120, 80);
        lock (MetalTestSupport.Lock)
        {
            using var software = new AwAUI.Render.SoftwareRenderBackend();
            using var swTarget = (AwAUI.Render.BitmapRenderTarget)software.CreateOffscreenTarget(new PixelSize(300, 160), 1);
            swTarget.Render(a, new RenderOptions(Color.White));
            swTarget.Render(b, new RenderOptions(new Color(0.9f, 0.9f, 1f), damage));

            using var target = MetalTestSupport.Backend.CreateOffscreenTarget(new PixelSize(300, 160), 1);
            target.Render(a, new RenderOptions(Color.White));
            target.Render(b, new RenderOptions(new Color(0.9f, 0.9f, 1f), damage));
            using var actual = Read(target);
            AssertClose(swTarget.Bitmap, actual, "damage");
        }
    }

    [MetalFact]
    public void Readback_converts_to_bgra()
    {
        var list = Scene(dc => dc.FillRect(new Rect(0, 0, 4, 4), new Color(1, 0, 0, 1)));
        lock (MetalTestSupport.Lock)
        {
            using var target = MetalTestSupport.Backend.CreateOffscreenTarget(new PixelSize(4, 4), 1);
            target.Render(list, new RenderOptions(Color.White));
            var px = new byte[2 * 1 * 4];
            target.ReadPixels(new PixelRect(1, 1, 2, 1), px, PixelFormat.Bgra8888);
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, px);
        }
    }

    [MetalFact]
    public void Invalidated_device_resources_are_rebuilt()
    {
        var dc = new DrawingContext();
        TextScenes.ClipAndLayers(dc);
        var list = dc.Finish();
        using var expected = MetalTestSupport.RenderSoftware(list, 360, 200);
        lock (MetalTestSupport.Lock)
        {
            using var backend = new GpuRenderBackend(MetalTestSupport.Backend.Device, ownsDevice: false);
            using var target = backend.CreateOffscreenTarget(new PixelSize(360, 200), 1);
            target.Render(list, new RenderOptions(Color.White));
            backend.InvalidateDeviceResources();
            target.Render(list, new RenderOptions(Color.White));
            using var actual = Read(target);
            AssertClose(expected, actual, "invalidate");
        }
    }

    [MetalFact]
    public void Masks_larger_than_an_atlas_page_use_transient_pages()
    {
        var list = Scene(dc =>
        {
            dc.FillCircle(new Point(1100, 300), 1000, Color.Parse("#3949ab"));
            dc.Save();
            dc.ClipPath(VectorPath.FromEllipse(new Rect(10, 10, 2380, 580)));
            dc.FillRect(new Rect(0, 0, 2400, 600), Color.Parse("#ff704380"));
            dc.Restore();
        });
        using var expected = MetalTestSupport.RenderSoftware(list, 2400, 600);
        using var actual = MetalTestSupport.RenderGpu(list, 2400, 600);
        AssertClose(expected, actual, "large-masks");
    }

    sealed class LayerSurface : NativeSurface, IMetalLayerSurface
    {
        public LayerSurface(IntPtr layer, PixelSize size) { MetalLayer = layer; Size = size; }
        public IntPtr MetalLayer { get; }
        public override PixelSize Size { get; }
        public override double Scale => 2;
    }

    public static IEnumerable<object[]> SwapchainScenes => new[] { new object[] { "demo" }, new object[] { "blend" } };

    /// <summary>交换链（BGRA8 可绘制纹理）：演示场景，以及读目标的混合模式（BGRA 的目标拷贝）。</summary>
    [MetalTheory]
    [MemberData(nameof(SwapchainScenes))]
    public void Renders_into_a_CAMetalLayer_swapchain(string sceneName)
    {
        DisplayList list;
        if (sceneName == "demo")
        {
            list = new RenderDemo.DemoScene { Time = 0.3 }.Build(new Size(320, 220));
        }
        else
        {
            var dc = new DrawingContext();
            TextScenes.PaintScene(dc);
            Scenes.BlendModes(dc);
            list = dc.Finish();
        }
        using var expected = MetalTestSupport.RenderSoftware(list, 320, 220, 2);
        lock (MetalTestSupport.Lock)
        {
            var backend = MetalTestSupport.Backend;
            var device = (MetalDevice)backend.Device;
            IntPtr layer;
            using (AutoreleasePool.Push())
                layer = ObjC.Retain(ObjC.Send(ObjC.Class("CAMetalLayer"), ObjC.Sel("layer")));
            try
            {
                // 1. 直接检查可绘制纹理（BGRA8）的内容：呈现前在同一个命令缓冲里回读
                using (var swapchain = device.CreateSwapchain(layer, new PixelSize(640, 440)))
                {
                    var texture = swapchain.AcquireNextTexture();
                    Assert.NotNull(texture);
                    Assert.Equal(GpuTextureFormat.Bgra8Unorm, texture!.Format);
                    GpuReadback readback;
                    using (var commands = device.BeginCommands())
                    {
                        backend.RenderFrame(commands, list, texture, 2, new PixelRect(0, 0, 640, 440), Color.White);
                        readback = commands.ReadTexture(texture, new PixelRect(0, 0, 640, 440));
                        commands.Present(swapchain);
                        commands.Submit();
                    }
                    using var actual = new Bitmap(new PixelSize(640, 440), PixelFormat.Bgra8888);
                    using (readback)
                        unsafe { readback.CopyTo(new Span<byte>((void*)actual.Address, actual.RowBytes * 440), actual.RowBytes); }
                    using var rgba = new Bitmap(actual.Size);
                    actual.CopyTo(rgba);
                    AssertClose(expected, rgba, "swapchain-drawable-" + sceneName);
                }

                // 2. 公开路径：NativeSurface 提供 IMetalLayerSurface → RenderTarget
                using var target = backend.CreateRenderTarget(new LayerSurface(layer, new PixelSize(640, 440)));
                Assert.Equal(new PixelSize(640, 440), target.PixelSize);
                for (int i = 0; i < 5; i++) target.Render(list, new RenderOptions(Color.White));
                ((GpuRenderTarget)target).WaitForGpu();
                using var shot = Read(target);
                AssertClose(expected, shot, "swapchain-target-" + sceneName);
            }
            finally
            {
                ObjC.Release(layer);
            }
        }
    }

    /// <summary>委托给真实设备，按需让图层纹理的创建失败（模拟显存不足）。</summary>
    sealed class FlakyDevice : GpuDevice
    {
        readonly GpuDevice _inner;
        public FlakyDevice(GpuDevice inner) => _inner = inner;
        public bool FailLayerTextures;
        public override string Name => _inner.Name;
        public override string Api => _inner.Api;
        public override GpuCapabilities Capabilities => _inner.Capabilities;
        public override GpuTexture CreateTexture(in GpuTextureDesc desc) =>
            FailLayerTextures && desc.Label == "AwAUI offscreen" ? throw new GpuException("Simulated out of GPU memory.") : _inner.CreateTexture(desc);
        public override GpuSwapchain CreateSwapchain(NativeSurface surface) => _inner.CreateSwapchain(surface);
        public override GpuCommandList BeginCommands() => _inner.BeginCommands();
        public override void WaitIdle() => _inner.WaitIdle();
        public override void Dispose() { }
    }

    /// <summary>
    /// 回归：编码到一半失败时（图层纹理创建失败），图集与查找表的上传已录进被丢弃的命令列表，但缓存认为内容已在 GPU 上：
    /// 之后的帧命中路径掩码缓存与查找表，画出的是从未上传的内容（圆与渐变消失）。
    /// </summary>
    [MetalFact]
    public void Failed_frame_does_not_leave_caches_pointing_at_unuploaded_content()
    {
        var list = Scene(dc =>
        {
            dc.FillCircle(new Point(60, 50), 40, Color.Parse("#3949ab"));
            dc.FillRect(new Rect(110, 10, 100, 80), new LinearGradientBrush(new Point(110, 0), new Point(210, 0),
                new[] { new GradientStop(0, Color.Parse("#ff5252")), new GradientStop(1, Color.Parse("#448aff")) }));
            dc.PushLayer(new LayerOptions(0.5f));
            dc.FillRect(new Rect(20, 60, 180, 30), Color.Parse("#2e7d32"));
            dc.PopLayer();
        });
        using var expected = MetalTestSupport.RenderSoftware(list, 220, 100);
        lock (MetalTestSupport.Lock)
        {
            var device = new FlakyDevice(MetalTestSupport.Backend.Device) { FailLayerTextures = true };
            using var backend = new GpuRenderBackend(device, ownsDevice: false);
            using var target = backend.CreateOffscreenTarget(new PixelSize(220, 100), 1);
            Assert.Throws<GpuException>(() => target.Render(list, new RenderOptions(Color.White)));
            device.FailLayerTextures = false;
            target.Render(list, new RenderOptions(Color.White));
            using var actual = Read(target);
            AssertClose(expected, actual, "failed-frame-recovery");
        }
    }
}
