using System.Diagnostics;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render.Gpu.Atlas;
using AwAUI.Render.Gpu.Frame;
using AwAUI.Render.Raster;
using AwAUI.RenderTests;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>
/// 恶意或极端的 DisplayList：极大 / 极小 / 退化的几何、奇异变换、超大线宽与模糊、超密虚线、深嵌套、大量图层与混合模式、
/// 无效字形。要求：不抛异常、不挂起（每帧有时间上限）、内存有界（图集每帧预算、图层深度上限）。
/// 录制部分（CPU 侧的全部逻辑）不需要 GPU，在任何系统上运行；完整的 Metal 渲染只在 macOS 上运行。
/// </summary>
public class GpuFuzzTests
{
    [Theory]
    [MemberData(nameof(DisplayListFuzzer.Seeds), MemberType = typeof(DisplayListFuzzer))]
    public void Recorder_survives_hostile_display_lists(int seed)
    {
        var fuzz = new DisplayListFuzzer(seed);
        var atlas = new MaskAtlas(null, 512, 2);
        var luts = new GradientLuts(null);
        var recorder = new FrameRecorder(atlas, luts, new GlyphCache(), new PathMaskCache(), null);
        var graph = new FrameGraph();
        for (int frame = 1; frame <= 15; frame++)
        {
            var list = fuzz.Build(60);
            var sw = Stopwatch.StartNew();
            double scale = frame % 3 == 0 ? 2 : 1;
            var full = new PixelRect(0, 0, (int)(160 * scale), (int)(120 * scale));
            atlas.BeginFrame(frame);
            luts.BeginFrame(frame);
            recorder.Record(list, graph, full, full, scale, frame, null);
            atlas.EndFrame();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"seed {seed} frame {frame} took {sw.Elapsed}");
            foreach (var q in graph.Instances.ToArray())
            {
                // 每个实例的四边形都在目标内且为整数
                Assert.InRange(q.Bounds.X, 0, full.Right);
                Assert.InRange(q.Bounds.Z, q.Bounds.X, full.Right);
                Assert.InRange(q.Bounds.Y, 0, full.Bottom);
                Assert.InRange(q.Bounds.W, q.Bounds.Y, full.Bottom);
                Assert.Equal(MathF.Floor(q.Bounds.X), q.Bounds.X);
            }
        }
    }

    [Fact]
    public void Absurd_dash_patterns_are_bounded()
    {
        var dc = new DrawingContext();
        dc.DrawLine(new Point(0, 10), new Point(1e6f, 10), Color.Black, new Stroke(2, dashes: new[] { 1e-5f, 1e-5f }));
        var atlas = new MaskAtlas(null);
        var degraded = new List<string>();
        var recorder = new FrameRecorder(atlas, new GradientLuts(null), new GlyphCache(), new PathMaskCache(), degraded.Add);
        var sw = Stopwatch.StartNew();
        var full = new PixelRect(0, 0, 200, 50);
        recorder.Record(dc.Finish(), new FrameGraph(), full, full, 1, 1, null);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(degraded, d => d.Contains("dash"));
    }

    [Fact]
    public void Huge_blur_under_rotation_is_bounded()
    {
        var dc = new DrawingContext();
        dc.Transform(Matrix3x2.CreateRotation(0.5f));
        dc.DrawBoxShadow(new RoundRect(new Rect(10, 10, 50, 50), 5), new BoxShadow(0, 0, 1e8f, 0, Color.Black));
        dc.DrawBoxShadow(new RoundRect(new Rect(10, 10, 50, 50), 5), new BoxShadow(0, 0, 1e8f, 0, Color.Black, inset: true));
        var degraded = new List<string>();
        var recorder = new FrameRecorder(new MaskAtlas(null), new GradientLuts(null), new GlyphCache(), new PathMaskCache(), degraded.Add);
        var sw = Stopwatch.StartNew();
        var full = new PixelRect(0, 0, 300, 300);
        recorder.Record(dc.Finish(), new FrameGraph(), full, full, 1, 1, null);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(degraded, d => d.Contains("sigma"));
    }

    [Fact]
    public void Mask_budget_limits_per_frame_memory()
    {
        // 互不相同的大路径：常规页用尽后计入每帧预算，超出的绘制被丢弃并上报
        var dc = new DrawingContext();
        for (int i = 0; i < 200; i++) dc.FillCircle(new Point(1000 + i * 0.01f, 1000), 990, Color.Black);
        var degraded = new List<string>();
        var atlas = new MaskAtlas(null, 2048, 1, frameOverflowBudget: 64L << 20);
        var recorder = new FrameRecorder(atlas, new GradientLuts(null), new GlyphCache(), new PathMaskCache(), degraded.Add);
        var full = new PixelRect(0, 0, 2000, 2000);
        atlas.BeginFrame(1);
        recorder.Record(dc.Finish(), new FrameGraph(), full, full, 1, 1, null);
        Assert.True(atlas.FrameOverflowBytes <= 64L << 20);
        Assert.Contains(degraded, d => d.Contains("budget"));
    }

    [MetalTheory]
    [MemberData(nameof(DisplayListFuzzer.Seeds), MemberType = typeof(DisplayListFuzzer))]
    public void Metal_backend_survives_hostile_display_lists(int seed)
    {
        var fuzz = new DisplayListFuzzer(1000 + seed);
        lock (MetalTestSupport.Lock)
        {
            using var target = MetalTestSupport.Backend.CreateOffscreenTarget(new PixelSize(160, 120), 1);
            for (int frame = 0; frame < 8; frame++)
            {
                var sw = Stopwatch.StartNew();
                target.Render(fuzz.Build(60), new RenderOptions(Color.White));
                ((AwAUI.Render.Gpu.GpuRenderTarget)target).WaitForGpu();
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"seed {seed} frame {frame} took {sw.Elapsed}");
            }
            var px = new byte[160 * 120 * 4];
            target.ReadPixels(new PixelRect(0, 0, 160, 120), px, PixelFormat.Rgba8888);
        }
    }
}
