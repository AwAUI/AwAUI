using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Rhi.Metal;
using Xunit.Abstractions;

namespace AwAUI.Samples.Tests;

/// <summary>只在有 Metal 的 macOS 上运行；其他系统以明确的原因跳过。</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "GPU (Metal) tests run only on macOS.";
        else if (!MetalRenderer.IsSupported) Skip = "No Metal device is available on this Mac.";
    }
}

/// <summary>
/// 计数器示例在 GPU 与软件后端上渲染一致（design/07 M2「软件与 GPU 输出差异在阈值内」，应用级）：
/// 无窗口平台上跑真实的计数器（软件后端，2x），每一步取宿主交给后端的 DisplayList，再用 <see cref="MetalRenderer"/> 创建的 GPU 后端离屏渲染，
/// 与软件后端整帧渲染同一 DisplayList 的结果比较（并确认它与窗口帧缓冲一致）。容差与 tests/AwAUI.Rhi.Metal.Tests 的 GpuParityTests 默认容差相同。
/// </summary>
public class GpuCounterTests
{
    readonly ITestOutputHelper _output;

    public GpuCounterTests(ITestOutputHelper output) => _output = output;

    static unsafe Bitmap RenderGpu(RenderBackend backend, DisplayList frame, PixelSize size, double scale, Color clear)
    {
        using var target = backend.CreateOffscreenTarget(size, scale);
        target.Render(frame, new RenderOptions(clear));
        var bmp = new Bitmap(size);
        target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), new Span<byte>((void*)bmp.Address, bmp.RowBytes * size.Height), PixelFormat.Rgba8888);
        return bmp;
    }

    static (int Max, double Over2, double Over8, double Over24) Diff(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        int max = 0;
        long over2 = 0, over8 = 0, over24 = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            var e = expected.GetRow(y);
            var a = actual.GetRow(y);
            for (int i = 0; i < e.Length; i += 4)
            {
                int d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(e[i + c] - a[i + c]));
                max = Math.Max(max, d);
                if (d > 2) over2++;
                if (d > 8) over8++;
                if (d > 24) over24++;
            }
        }
        double n = expected.Width * (double)expected.Height / 100;
        return (max, over2 / n, over8 / n, over24 / n);
    }

    static int CountDifferent(Bitmap a, Bitmap b)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y++)
        {
            var ra = a.GetRow(y);
            var rb = b.GetRow(y);
            for (int i = 0; i < ra.Length; i += 4)
                if (ra[i] != rb[i] || ra[i + 1] != rb[i + 1] || ra[i + 2] != rb[i + 2] || ra[i + 3] != rb[i + 3]) n++;
        }
        return n;
    }

    [GpuFact]
    public void Counter_frames_render_the_same_on_the_gpu_and_in_software()
    {
        using var backend = MetalRenderer.TryCreate();
        Assert.NotNull(backend);
        var degraded = new List<string>();
        backend!.Degraded += degraded.Add;

        foreach (bool markup in new[] { false, true })
        {
            var app = new CounterTests.App(markup);
            void Check(string step)
            {
                var fb = app.Window.Framebuffer!;
                var frame = app.Host.PresentedFrame;
                Assert.True(frame.CommandCount > 0, step + ": empty frame");
                // 参照：整帧软件渲染。窗口帧缓冲是局部光栅化逐帧累积的结果，应与它一致（确认比较的是屏幕上的那一帧）；
                // 受损区域边缘的抗锯齿像素允许 1/255 的差（局部重画与整帧重画的覆盖率累加顺序不同）
                using var sw = new SoftwareRenderBackend();
                using var full = (BitmapRenderTarget)sw.CreateOffscreenTarget(fb.Size, app.Window.RenderScale);
                full.Render(frame, new RenderOptions(app.Host.ClearColor));
                var self = Diff(full.Bitmap, fb);
                _output.WriteLine(FormattableString.Invariant($"{step}: window framebuffer vs full software render: max {self.Max}, >0 px: {CountDifferent(full.Bitmap, fb)}"));
                Assert.True(self.Max <= 1, $"{step}: framebuffer differs from a full software render (max {self.Max})");
                using var gpu = RenderGpu(backend, frame, fb.Size, app.Window.RenderScale, app.Host.ClearColor);
                var d = Diff(full.Bitmap, gpu);
                string name = (markup ? "awaui " : "c# ") + step;
                _output.WriteLine(FormattableString.Invariant($"{name}: {fb.Width}x{fb.Height}, max {d.Max}, >2 {d.Over2:F3}%, >8 {d.Over8:F3}%, >24 {d.Over24:F4}%"));
                bool ok = d.Max <= 64 && d.Over8 <= 0.3 && d.Over24 <= 0.02 && d.Over2 <= 3;
                if (!ok && Environment.GetEnvironmentVariable("AWAUI_SAVE_DIFF") == "1")
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "gpu-counter");
                    Directory.CreateDirectory(dir);
                    AwAUI.Imaging.PngCodec.Save(full.Bitmap, Path.Combine(dir, name.Replace(' ', '-') + ".software.png"));
                    AwAUI.Imaging.PngCodec.Save(gpu, Path.Combine(dir, name.Replace(' ', '-') + ".gpu.png"));
                }
                Assert.True(ok, $"{name}: GPU frame differs from software beyond the GPU parity tolerance (max {d.Max}, >2 {d.Over2:F3}%, >8 {d.Over8:F3}%, >24 {d.Over24:F4}%)");
            }

            Check("mount");
            for (int i = 0; i < 3; i++) app.Click(app.ById("increment"));
            app.Frames(2);
            Check("after clicks");
            app.Click(app.ById("name"));
            app.Type("Ada");
            app.Frames(2);
            Check("focused input with text");
            app.Key(PhysicalKey.Enter);
            app.Pointer(RawPointerEventType.Move, app.ById("increment").BoundingRect.Center, PointerButtons.None, PointerButtons.None);
            app.Pointer(RawPointerEventType.Down, app.ById("increment").BoundingRect.Center, PointerButtons.Left, PointerButtons.Left);
            app.Frames(2);
            Check("greeting, pressed button");
            Assert.Equal("Hello, Ada!", app.ById("greeting").TextContent);
        }
        Assert.False(backend.IsSoftwareFallback);
        Assert.Empty(degraded);
    }
}
