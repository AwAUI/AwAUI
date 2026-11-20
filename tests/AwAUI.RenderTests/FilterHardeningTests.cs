using System.Diagnostics;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Render;

namespace AwAUI.RenderTests;

/// <summary>
/// 滤镜图层与背景滤镜的录制、格式与恶意输入的边界（第九批）：超长的滤镜链被截断、巨大的 σ 被钳制、巨大的图层被限制在目标附近、
/// 嵌套扇出受每帧工作量上限约束，全部在有界的时间与内存内完成且不抛异常。
/// </summary>
public class FilterHardeningTests
{
    static Color C(string hex) => Color.Parse(hex);

    static Bitmap Render(Action<DrawingContext> draw, int w, int h, List<string>? degraded = null, double scale = 1)
    {
        var dc = new DrawingContext();
        draw(dc);
        var list = dc.Finish();
        using var backend = new SoftwareRenderBackend();
        if (degraded is not null) backend.Degraded += degraded.Add;
        var size = new PixelSize((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale));
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(size, scale);
        target.Render(list, new RenderOptions(Color.White));
        var copy = new Bitmap(size);
        target.Bitmap.CopyTo(copy);
        return copy;
    }

    [Fact]
    public void Push_layer_encodes_the_filter_and_old_prefix_readers_still_balance()
    {
        var filter = new Filter(FilterOperation.Blur(2));
        var dc = new DrawingContext();
        dc.PushLayer(new LayerOptions(0.5f, BlendMode.SrcOver, new Rect(0, 0, 10, 10), filter));
        dc.FillRect(new Rect(0, 0, 10, 10), C("#ff0000"));
        dc.PopLayer();
        dc.DrawBackdrop(new RoundRect(new Rect(1, 1, 5, 5), 1), filter, 0.5f);
        var list = dc.Finish();
        var r = list.GetReader();
        Assert.True(r.MoveNext());
        Assert.Equal(DrawOp.PushLayer, r.Op);
        var c = r.Read<PushLayerCmd>();
        Assert.Equal(1, c.HasFilter);
        Assert.Same(filter, r.GetResource<Filter>(c.FilterRef));
        Assert.Equal(0.5f, c.Opacity);
        Assert.True(r.MoveNext());
        Assert.True(r.MoveNext());
        Assert.Equal(DrawOp.PopLayer, r.Op);
        Assert.True(r.MoveNext());
        Assert.Equal(DrawOp.DrawBackdrop, r.Op);
        var b = r.Read<DrawBackdropCmd>();
        Assert.Same(filter, r.GetResource<Filter>(b.FilterRef));
        Assert.Equal(0.5f, b.Opacity);
        Assert.Equal(38, (int)DrawOp.DrawBackdrop);
        // 旧前缀（第八批的 24 字节载荷）仍是新载荷的前缀：只认识旧字段的读取器把它当作普通图层
        Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<PushLayerCmd>() - 4);
        // 包围盒按滤镜外扩（模糊 3σ = 6）
        Assert.True(list.Bounds.Left <= -6 + 0.01f && list.Bounds.Right >= 16 - 0.01f, list.Bounds.ToString());
    }

    [Fact]
    public void Empty_filters_and_invisible_backdrops_are_not_recorded()
    {
        var dc = new DrawingContext();
        dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, null, new Filter()));
        dc.PopLayer();
        dc.DrawBackdrop(new RoundRect(new Rect(0, 0, 10, 10), 0), new Filter(), 1);
        dc.DrawBackdrop(new RoundRect(new Rect(0, 0, 10, 10), 0), new Filter(FilterOperation.Blur(1)), 0);
        dc.DrawBackdrop(new RoundRect(new Rect(0, 0, 10, 10), 0), new Filter(FilterOperation.Blur(1)), float.NaN);
        dc.DrawBackdrop(new RoundRect(new Rect(0, 0, float.PositiveInfinity, 10), 0), new Filter(FilterOperation.Blur(1)), 1);
        var list = dc.Finish();
        var r = list.GetReader();
        Assert.True(r.MoveNext());
        Assert.Equal(0, r.Read<PushLayerCmd>().HasFilter);
        Assert.True(r.MoveNext());
        Assert.Equal(DrawOp.PopLayer, r.Op);
        Assert.False(r.MoveNext());
    }

    [Fact]
    public void Filter_operation_factories_sanitize_their_input()
    {
        Assert.Equal(0, FilterOperation.Blur(float.NaN).StdDeviationX);
        Assert.Equal(0, FilterOperation.Blur(-3).StdDeviationY);
        Assert.Equal(float.MaxValue, FilterOperation.Blur(float.PositiveInfinity).StdDeviationX);
        Assert.Equal(0, FilterOperation.DropShadow(float.NaN, float.PositiveInfinity, 1, Color.Black).OffsetX);
        Assert.Equal(20, FilterOperation.Grayscale(5).Matrix.Count);
        Assert.Throws<ArgumentException>(() => FilterOperation.ColorMatrix(new float[3]));
        var m = FilterOperation.ColorMatrix(Enumerable.Repeat(float.NaN, 20).ToArray());
        Assert.All(m.Matrix, v => Assert.Equal(0, v));
        Assert.Equal(new Filter(FilterOperation.Sepia(0.5f)), new Filter(FilterOperation.Sepia(0.5f)));
        Assert.NotEqual(new Filter(FilterOperation.Sepia(0.5f)), new Filter(FilterOperation.Sepia(0.6f)));
    }

    [Fact]
    public void Long_filter_chains_are_truncated()
    {
        var degraded = new List<string>();
        var ops = Enumerable.Range(0, 50_000).Select(i => i % 2 == 0 ? FilterOperation.Blur(1) : FilterOperation.HueRotate(i)).ToArray();
        var sw = Stopwatch.StartNew();
        using var b = Render(dc =>
        {
            dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(10, 10, 40, 40), new Filter(ops)));
            dc.FillRect(new Rect(10, 10, 40, 40), C("#2196f3"));
            dc.PopLayer();
        }, 64, 64, degraded);
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Contains(degraded, d => d.Contains("truncated"));
        Assert.True(b.GetPixel(30, 30).A > 0.5f);
    }

    [Fact]
    public void Huge_sigma_and_offsets_are_bounded()
    {
        var degraded = new List<string>();
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var b = Render(dc =>
        {
            dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(-1e7f, -1e7f, 2e7f, 2e7f),
                new Filter(FilterOperation.Blur(1e30f), FilterOperation.DropShadow(1e9f, -1e9f, 1e20f, Color.Black), FilterOperation.Blur(float.PositiveInfinity))));
            dc.FillRect(new Rect(0, 0, 300, 200), C("#e91e63"));
            dc.PopLayer();
            dc.DrawBackdrop(new RoundRect(new Rect(-1e8f, -1e8f, 2e8f, 2e8f), 0), new Filter(FilterOperation.Blur(1e6f)));
        }, 300, 200, degraded);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.True(allocated < 400_000_000, $"allocated {allocated / 1e6:F0} MB");
        Assert.Contains(degraded, d => d.Contains("clamped"));
    }

    [Fact]
    public void Nested_filter_fan_out_hits_the_work_limit()
    {
        // 每层引用同一个子列表两次，子列表里是全幅的模糊图层：2^24 次重放的模糊不能执行完
        var degraded = new List<string>();
        var leaf = new DrawingContext();
        leaf.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(0, 0, 200, 150), new Filter(FilterOperation.Blur(3))));
        leaf.FillRect(new Rect(0, 0, 200, 150), C("#ff000010"));
        leaf.PopLayer();
        var list = leaf.Finish();
        for (int i = 0; i < 24; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var sw = Stopwatch.StartNew();
        using var backend = new SoftwareRenderBackend();
        backend.Degraded += degraded.Add;
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(200, 150), 1);
        target.Render(list, new RenderOptions(Color.White));
        Assert.True(sw.ElapsedMilliseconds < 60_000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Contains(degraded, d => d.Contains("work limit") || d.Contains("draw commands"));
    }

    [Fact]
    public void Deeply_nested_filter_layers_stop_at_the_layer_depth_limit()
    {
        var degraded = new List<string>();
        using var b = Render(dc =>
        {
            for (int i = 0; i < 200; i++) dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(0, 0, 40, 40), new Filter(FilterOperation.Invert(1))));
            dc.FillRect(new Rect(0, 0, 40, 40), C("#000000"));
            for (int i = 0; i < 200; i++) dc.PopLayer();
        }, 40, 40, degraded);
        Assert.Contains(degraded, d => d.Contains("nested deeper"));
    }

    [Fact]
    public void Blur_matches_a_reference_gaussian()
    {
        // 直接卷积路径（σ ≤ 4 设备像素）与按定义计算的高斯一致（8 位量化之内）
        using var b = Render(dc =>
        {
            dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(0, 0, 60, 20), new Filter(FilterOperation.Blur(3, 0))));
            dc.FillRect(new Rect(30, 0, 30, 20), C("#000000"));
            dc.PopLayer();
        }, 60, 20);
        for (int x = 20; x < 40; x++)
        {
            // 左半透明、右半黑：结果的 alpha 是高斯的累积分布
            double sum = 0, total = 0;
            for (int t = -9; t <= 9; t++)
            {
                double w = Math.Exp(-t * t / (2.0 * 9));
                total += w;
                if (x + t >= 30) sum += w;
            }
            double expected = 1 - sum / total;   // 白底上的灰度
            Assert.True(Math.Abs(b.GetPixel(x, 10).R - expected) < 2.5 / 255, $"x={x}: {b.GetPixel(x, 10).R} vs {expected}");
        }
    }

    [Fact]
    public void Color_matrices_follow_the_filter_effects_definitions()
    {
        using var b = Render(dc =>
        {
            void Swatch(float x, FilterOperation op)
            {
                dc.PushLayer(new LayerOptions(1, BlendMode.SrcOver, new Rect(x, 0, 10, 10), new Filter(op)));
                dc.FillRect(new Rect(x, 0, 10, 10), new Color(0.8f, 0.4f, 0.2f));
                dc.PopLayer();
            }
            Swatch(0, FilterOperation.Brightness(0.5f));
            Swatch(10, FilterOperation.Contrast(2));
            Swatch(20, FilterOperation.Invert(1));
            Swatch(30, FilterOperation.Opacity(0.5f));
            Swatch(40, FilterOperation.Grayscale(1));
            Swatch(50, FilterOperation.HueRotate(360));
        }, 60, 10);
        static void Near(Color e, Color a) => Assert.True(Math.Abs(e.R - a.R) < 0.01f && Math.Abs(e.G - a.G) < 0.01f && Math.Abs(e.B - a.B) < 0.01f, $"{e} vs {a}");
        Near(new Color(0.4f, 0.2f, 0.1f), b.GetPixel(5, 5));
        Near(new Color(1f, 0.3f, 0f), b.GetPixel(15, 5));
        Near(new Color(0.2f, 0.6f, 0.8f), b.GetPixel(25, 5));
        Near(new Color(0.9f, 0.7f, 0.6f), b.GetPixel(35, 5));        // 半透明叠在白底上
        float l = 0.2126f * 0.8f + 0.7152f * 0.4f + 0.0722f * 0.2f;
        Near(new Color(l, l, l), b.GetPixel(45, 5));
        Near(new Color(0.8f, 0.4f, 0.2f), b.GetPixel(55, 5));
    }

    [Fact]
    public void Backdrop_inside_a_rotated_transform_reads_the_device_space_backdrop()
    {
        using var b = Render(dc =>
        {
            dc.FillRect(new Rect(0, 0, 50, 100), C("#000000"));
            dc.Save();
            dc.Transform(Matrix3x2.CreateRotation(MathF.PI / 4, new Vector2(50, 50)));
            dc.DrawBackdrop(new RoundRect(new Rect(30, 30, 40, 40), 0), new Filter(FilterOperation.Invert(1)));
            dc.Restore();
        }, 100, 100);
        // 菱形中心左侧（原为黑）反相为白，右侧（原为白）反相为黑；菱形之外不变
        Assert.True(b.GetPixel(40, 50).R > 0.9f);
        Assert.True(b.GetPixel(60, 50).R < 0.1f);
        Assert.True(b.GetPixel(5, 5).R < 0.1f);
        Assert.True(b.GetPixel(95, 5).R > 0.9f);
    }
}
