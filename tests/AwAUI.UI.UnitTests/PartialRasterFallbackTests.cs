using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// UIHost.PartialRaster 的默认值跟随后端当前能力：GPU 后端运行中回退到软件（如 MetalRenderBackend 设备丢失）后，
/// 应恢复局部光栅化，而不是一直整窗软件光栅化。
/// </summary>
public class PartialRasterFallbackTests
{
    /// <summary>委托给软件后端、但可以声明 GPU 能力的后端，模拟回退前后。</summary>
    sealed class SwitchableBackend : RenderBackend
    {
        readonly SoftwareRenderBackend _inner = new();
        public bool ReportGpu { get; set; } = true;
        public override string Name => ReportGpu ? "fake-gpu" : _inner.Name;
        public override BackendCapabilities Capabilities => ReportGpu ? _inner.Capabilities | BackendCapabilities.Gpu : _inner.Capabilities;
        public override RenderTarget CreateRenderTarget(NativeSurface surface) => _inner.CreateRenderTarget(surface);
        public override RenderTarget CreateOffscreenTarget(PixelSize size, double scale) => _inner.CreateOffscreenTarget(size, scale);
        public override void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void Partial_raster_default_follows_backend_capabilities()
    {
        var platform = new HeadlessPlatform();
        using var backend = new SwitchableBackend();
        var host = AwApp.CreateHost(platform, new WindowOptions { Size = new Size(100, 80) }, backend, new TestTextEngine(), new Div { Class = "box" });
        Assert.False(host.PartialRaster);
        Assert.Equal("fake-gpu", host.BackendName);

        backend.ReportGpu = false;     // 回退到软件
        Assert.True(host.PartialRaster);
        Assert.Equal(new SoftwareRenderBackend().Name, host.BackendName);

        host.PartialRaster = false;    // 显式赋值后固定
        backend.ReportGpu = true;
        Assert.False(host.PartialRaster);
        backend.ReportGpu = false;
        Assert.False(host.PartialRaster);
    }
}
