using System.Diagnostics;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>
/// 第五批模糊测试（SVG 与 GPU 后端）报告的原始复现输入，原样经公开 API 端到端重放：不抛异常、不挂起、内存有界。
/// 尺寸与变换取自报告（240×160 的 viewBox 画进 40×40 即 1/6 缩放）。
/// </summary>
public class FuzzReproTests
{
    static readonly Color Black = Color.Parse("#000000");

    /// <summary>渲染并断言耗时与分配有界。</summary>
    static Bitmap Run(Action<DrawingContext> draw, int w, int h, List<string>? degraded = null, int maxMs = 5000, long maxBytes = 256L << 20)
    {
        var dc = new DrawingContext();
        draw(dc);
        var list = dc.Finish();
        using var backend = new SoftwareRenderBackend();
        if (degraded != null) backend.Degraded += degraded.Add;
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(w, h), 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        target.Render(list, new RenderOptions(Color.Transparent));
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(sw.ElapsedMilliseconds < maxMs, $"took {sw.ElapsedMilliseconds} ms");
        Assert.True(allocated < maxBytes, $"allocated {allocated >> 20} MB");
        var copy = new Bitmap(target.PixelSize);
        target.Bitmap.CopyTo(copy);
        return copy;
    }

    [Fact]
    public void Dashed_circle_far_from_the_origin_terminates()
    {
        // SVG 模糊：<circle cx="1e38" r="12" stroke-dasharray="6 3"/>。舍入使展平后的线段约 1e31 长，float 的 pos += remaining 不再前进
        using var b = Run(dc =>
        {
            dc.Transform(Matrix3x2.CreateScale(1 / 6f));
            dc.StrokePath(new PathBuilder().AddCircle(new Point(1e38f, 80), 12).ToPath(), Black, new Stroke(1, dashes: new[] { 6f, 3f }));
        }, 40, 40);
    }

    [Fact]
    public void Huge_circle_with_absurd_stroke_width_does_not_index_out_of_range()
    {
        // SVG 模糊：circle (210,135) r=2e7、stroke-width 1e30：Rasterizer.DrawLine 的起始 x 未钳制而越界
        using var b = Run(dc =>
        {
            dc.Transform(Matrix3x2.CreateScale(1 / 6f));
            dc.StrokePath(new PathBuilder().AddCircle(new Point(210, 135), 2e7f).ToPath(), Black, new Stroke(1e30f));
            dc.StrokePath(new PathBuilder().AddCircle(new Point(210, 135), 2e7f).ToPath(), Black,
                new Stroke(1e30f, StrokeCap.Round, StrokeJoin.Round, dashes: new[] { 5f, 5f }));
        }, 40, 40);
    }

    [Fact]
    public void Line_with_subnormal_coordinate_does_not_index_out_of_range()
    {
        // SVG 模糊：20×160 的文档画进 40×40（1/4 缩放），线段 (-1e-38, 45) → (0, 0)，线宽 1
        using var b = Run(dc =>
        {
            dc.Transform(Matrix3x2.CreateScale(0.25f));
            dc.DrawLine(new Point(-1e-38f, 45), new Point(0, 0), Black, new Stroke(1));
            dc.StrokePath(new PathBuilder().MoveTo(new Point(-1e-38f, 45)).LineTo(new Point(0, 0)).ToPath(), Black, new Stroke(1, StrokeCap.Square));
        }, 40, 40);
    }

    [Fact]
    public void Fifty_thousand_huge_cubics_stay_within_the_point_budget()
    {
        // SVG 模糊：5 万条巨大的三次曲线，修复前展平分配约 1 GB
        var pb = new PathBuilder().MoveTo(new Point(0, 0));
        for (int i = 0; i < 50_000; i++)
            pb.CubicTo(new Point(3e5f, -3e5f), new Point(-3e5f, 3e5f), new Point(i % 7, i % 5));
        var path = pb.ToPath();
        using var b = Run(dc =>
        {
            dc.FillPath(path, Black);
            dc.StrokePath(path, Black, new Stroke(2));
        }, 40, 40, maxMs: 20_000, maxBytes: 600L << 20);
    }

    [Fact]
    public void Huge_box_shadow_blur_is_bounded()
    {
        // GPU 后端报告：160×120 的目标上 blur 2000，修复前工作区 290 MB
        var degraded = new List<string>();
        using var b = Run(dc =>
        {
            dc.DrawBoxShadow(new RoundRect(new Rect(20, 20, 100, 60), 8), new BoxShadow(3, 3, 2000, 10, Black));
            dc.Transform(Matrix3x2.CreateRotation(0.3f));
            dc.DrawBoxShadow(new RoundRect(new Rect(20, 20, 100, 60), 8), new BoxShadow(3, 3, 1e9f, 1e9f, Black));
        }, 160, 120, degraded);
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f, 10f)]
    [InlineData(0f, float.PositiveInfinity, 0f, 10f)]
    [InlineData(5f, 5f, float.NaN, 10f)]
    [InlineData(5f, 5f, 2f, float.NaN)]
    [InlineData(5f, 5f, 1e30f, 10f)]
    [InlineData(1e30f, -1e30f, 3f, 1e-30f)]
    [InlineData(20f, 20f, 0f, 0f)]
    [InlineData(20f, 20f, 3f, float.PositiveInfinity)]
    public void Hostile_focal_gradients_render_without_throwing(float fx, float fy, float fr, float r)
    {
        var stops = new[] { new GradientStop(0, Color.Parse("#ff0000")), new GradientStop(1, Color.Parse("#0000ff")) };
        foreach (var spread in new[] { SpreadMode.Pad, SpreadMode.Repeat, SpreadMode.Reflect })
        {
            var brush = new RadialGradientBrush(new Point(20, 20), r, new Point(fx, fy), fr, stops, spread);
            using var b = Run(dc => dc.FillRect(new Rect(0, 0, 40, 40), brush), 40, 40);
            for (int y = 0; y < 40; y += 7)
            for (int x = 0; x < 40; x += 7)
            {
                var p = b.GetPixel(x, y);
                Assert.True(p.A is >= 0 and <= 1 && !float.IsNaN(p.R), $"({x},{y}) = {p}");
            }
        }
    }

    // ---------------------------------------------------------------- 每帧工作量

    static DisplayList FanOut(DisplayList leaf, int levels)
    {
        var list = leaf;
        for (int i = 0; i < levels; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        return list;
    }

    [Fact]
    public void Fan_out_of_expensive_commands_is_bounded_by_the_work_limit()
    {
        // 几百字节的输入：每层引用两次、40 层，叶子是一次全屏半透明填充加一条全屏的复杂路径。
        // 命令数上限（2²²）之内仍是数百万次全屏绘制（实测 2¹⁴ 次 640×440 的填充在未优化构建中超过 80 s CPU），
        // 每帧工作量上限让它在上限对应的时间内结束。这里把上限调低到 2²⁴ 以保持测试快速
        var pb = new PathBuilder().MoveTo(new Point(0, 0));
        for (int i = 0; i < 200; i++) pb.LineTo(new Point(i % 2 == 0 ? 256 : 0, i * 1.28f));
        var leaf = new DrawingContext().Let(dc =>
        {
            dc.FillRect(new Rect(0, 0, 256, 256), Color.Parse("#ff000080"));
            dc.FillPath(pb.Close().ToPath(), Color.Parse("#0000ff40"));
        });
        var list = FanOut(leaf, 40);
        var degraded = new List<string>();
        RasterContext.WorkBudgetOverride = 1 << 24;
        try
        {
            using var b = Run(dc => dc.DrawDisplayList(list), 256, 256, degraded, maxMs: 20_000);
            Assert.True(b.GetPixel(128, 128).A > 0.9f);
        }
        finally
        {
            RasterContext.WorkBudgetOverride = null;
        }
        Assert.Contains(degraded, d => d.Contains("work limit"));
        Assert.DoesNotContain(degraded, d => d.Contains("draw commands"));
    }

    [Fact]
    public void Reference_scenes_use_a_small_fraction_of_the_work_limit()
    {
        // 上限不能影响正常内容：全部黄金场景在 2x 下的工作量远小于上限
        var context = new RasterContext();
        foreach (var (name, w, h, draw) in Scenes.All)
        {
            var dc = new DrawingContext();
            draw(dc);
            using var bmp = new Bitmap(new PixelSize(w * 2, h * 2));
            context.Render(dc.Finish(), bmp, 2, new RenderOptions(Color.White));
            Assert.True(context.FrameWork > 0 && context.FrameWork < RasterContext.MaxWorkPerFrame / 1000, $"{name}: {context.FrameWork}");
        }
    }

    [Fact]
    public void Repeated_degradations_are_reported_once_per_frame()
    {
        // 恶意输入可以让同一种降级在一帧里出现数百万次（这里 2¹⁶ 次不支持的混合模式），每帧只上报一次
        var leaf = new DrawingContext().Let(dc => dc.FillRect(new Rect(0, 0, 2, 2), new Paint(Color.Parse("#ff0000"), BlendMode.Hue)));
        var list = FanOut(leaf, 16);
        var degraded = new List<string>();
        using var backend = new SoftwareRenderBackend();
        backend.Degraded += degraded.Add;
        using var target = backend.CreateOffscreenTarget(new PixelSize(4, 4), 1);
        target.Render(list, new RenderOptions(Color.White));
        Assert.Single(degraded);
        target.Render(list, new RenderOptions(Color.White));
        Assert.Equal(2, degraded.Count);
    }
}
