using System.Diagnostics;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>
/// 软件光栅器对恶意 / 退化输入的加固（模糊测试发现的问题）：每个测试复现原来的崩溃、死循环或无界的内存与时间。
/// </summary>
public class HardeningTests
{
    static Color C(string hex) => Color.Parse(hex);
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>渲染并收集降级信息。</summary>
    static Bitmap Render(Action<DrawingContext> draw, int w, int h, List<string>? degraded = null, Color? clear = null)
    {
        var dc = new DrawingContext();
        draw(dc);
        using var backend = new SoftwareRenderBackend();
        if (degraded != null) backend.Degraded += degraded.Add;
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(w, h), 1);
        target.Render(dc.Finish(), new RenderOptions(clear ?? Color.Transparent));
        var copy = new Bitmap(target.PixelSize);
        target.Bitmap.CopyTo(copy);
        return copy;
    }

    sealed class CollectingSink : CoverageSink
    {
        public readonly List<float> Values = new();
        public override void Row(int y, int x, Span<float> coverage)
        {
            foreach (var v in coverage) Values.Add(v);
        }
    }

    // ---------------------------------------------------------------- 光栅器

    [Fact]
    public void Nearly_horizontal_edge_with_infinite_slope_gives_finite_coverage()
    {
        // (0,0) → (100, 1e-39)：dy 是非规格化数，dx/dy = ∞；条带顶部 ys == y0，0·∞ = NaN。
        // 修复前 x 为 NaN：x64 上单元下标越界（崩溃），ARM64 上整行覆盖率变成 NaN。
        var poly = new PolyList();
        poly.Add(new Vector2(0, 0));
        poly.Add(new Vector2(100, 1e-39f));
        poly.Add(new Vector2(50, 10));
        var sink = new CollectingSink();
        new Rasterizer().Fill(poly, FillRule.NonZero, true, new PixelRect(0, 0, 128, 16), sink);
        Assert.NotEmpty(sink.Values);
        Assert.All(sink.Values, v => Assert.True(v >= 0 && v <= 1, $"coverage {v}"));
    }

    [Fact]
    public void Edges_overflowing_to_infinity_or_nan_are_not_rasterized()
    {
        // 变换溢出：∞ 与 ∞ − ∞ = NaN 的点。NaN 不参与包围盒比较，修复前包围盒仍是有限的，光栅化越界
        var poly = new PolyList();
        poly.Add(new Vector2(10, 10));
        poly.Add(new Vector2(float.NaN, 20));
        poly.Add(new Vector2(30, 30));
        Assert.False(poly.TryGetBounds(out _, out _, out _, out _));
        var sink = new CollectingSink();
        new Rasterizer().Fill(poly, FillRule.NonZero, true, new PixelRect(0, 0, 64, 64), sink);
        Assert.Empty(sink.Values);

        // 端到端：有限的命令经过变换后溢出
        using var b = Render(dc =>
        {
            dc.Transform(Matrix3x2.CreateScale(1e30f));
            var pb = new PathBuilder().MoveTo(new Point(0, 0)).LineTo(new Point(1e10f, -1e10f)).LineTo(new Point(-1e10f, 1e10f)).Close();
            dc.FillPath(pb.ToPath(), C("#ff0000"));
            dc.StrokePath(pb.ToPath(), C("#00ff00"), new Stroke(3));
        }, 64, 64);
    }

    // ---------------------------------------------------------------- 渐变

    [Fact]
    public void Single_gradient_stop_with_nan_offset_paints_that_color()
    {
        // 修复前：只有一个色标且偏移为 NaN 时 BuildLut 越界读第二个色标（ArgumentOutOfRangeException）
        var brush = new LinearGradientBrush(new Point(0, 0), new Point(10, 0), new[] { new GradientStop(float.NaN, C("#ff0000")) });
        using var b = Render(dc => dc.FillRect(new Rect(0, 0, 16, 16), brush), 16, 16);
        var p = b.GetPixel(8, 8);
        Assert.Equal(1f, p.R, 2);
        Assert.Equal(0f, p.G, 2);
        Assert.Equal(1f, p.A, 2);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.NegativeInfinity, float.PositiveInfinity)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, float.NaN)]
    [InlineData(float.MaxValue, float.MinValue)]
    public void Non_finite_stop_offsets_give_a_finite_lookup_table(float a, float b)
    {
        var stops = new[] { new GradientStop(a, C("#ff0000")), new GradientStop(0.5f, C("#00ff0080")), new GradientStop(b, C("#0000ff")) };
        var brush = new RadialGradientBrush(new Point(8, 8), 8, stops, SpreadMode.Reflect);
        var lut = new Vector4[GradientLut.Size];
        GradientLut.Build(brush.Stops, lut);
        foreach (var c in lut)
            Assert.True(Finite(c.X) && Finite(c.Y) && Finite(c.Z) && Finite(c.W), c.ToString());
        using var bmp = Render(dc => dc.FillRect(new Rect(0, 0, 16, 16), brush), 16, 16);
        Assert.True(bmp.GetPixel(8, 8).A > 0);
    }

    [Fact]
    public void Nan_offset_does_not_poison_later_stops()
    {
        // Math.Max 传播 NaN：修复前一个 NaN 偏移让其后全部色标变成 NaN
        var brush = new LinearGradientBrush(new Point(0, 0), new Point(1, 0),
            new[] { new GradientStop(0, C("#000000")), new GradientStop(float.NaN, C("#ff0000")), new GradientStop(1, C("#ffffff")) });
        Assert.Equal(new[] { 0f, 0f, 1f }, brush.Stops.Select(s => s.Offset).ToArray());
    }

    [Fact]
    public void Lookup_table_single_sweep_matches_per_entry_search()
    {
        // 参考实现：每项从头查找所在区间（原 BuildLut 的写法），结果必须逐位相同
        var rng = new Random(1234);
        for (int iter = 0; iter < 200; iter++)
        {
            int n = rng.Next(1, 12);
            var stops = new GradientStop[n];
            for (int i = 0; i < n; i++)
                stops[i] = new GradientStop((float)(rng.NextDouble() * 1.4 - 0.2), new Color((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()));
            if (rng.Next(4) == 0 && n > 2) stops[1] = new GradientStop(stops[0].Offset, stops[1].Color);   // 硬边
            var brush = new LinearGradientBrush(default, new Point(1, 0), stops);
            var s = brush.Stops;
            var lut = new Vector4[GradientLut.Size];
            GradientLut.Build(s, lut);
            for (int i = 0; i < lut.Length; i++)
            {
                float t = i / 255f;
                Vector4 expected;
                if (s.Count == 1 || t <= s[0].Offset) expected = Shader.Premultiply(s[0].Color);
                else if (t >= s[s.Count - 1].Offset) expected = Shader.Premultiply(s[s.Count - 1].Color);
                else
                {
                    int k = 1;
                    while (k < s.Count - 1 && s[k].Offset < t) k++;
                    float span = s[k].Offset - s[k - 1].Offset;
                    float u = span > 1e-6f ? (t - s[k - 1].Offset) / span : 1;
                    expected = Vector4.Lerp(Shader.Premultiply(s[k - 1].Color), Shader.Premultiply(s[k].Color), u);
                }
                Assert.Equal(expected, lut[i]);
            }
        }
    }

    [Fact]
    public void Lookup_tables_are_cached_per_brush_and_by_content()
    {
        var context = new RasterContext();
        using var target = new Bitmap(new PixelSize(32, 32));
        var stops = new[] { new GradientStop(0, C("#ff0000")), new GradientStop(1, C("#0000ff")) };
        var shared = new LinearGradientBrush(default, new Point(32, 0), stops);
        var dc = new DrawingContext();
        for (int i = 0; i < 50; i++) dc.FillRect(new Rect(0, i * 0.5f, 32, 1), shared);
        // 每帧重建的画刷对象：内容相同，命中同一张表
        for (int i = 0; i < 50; i++) dc.FillRect(new Rect(0, 0, 32, 1), new LinearGradientBrush(default, new Point(32, 0), stops.ToArray()));
        context.Render(dc.Finish(), target, 1, new RenderOptions(Color.White));
        Assert.Equal(1, context.GradientLuts.Builds);
        Assert.Equal(1, context.GradientLuts.Count);
    }

    [Fact]
    public void Nan_colored_stops_hit_the_cache_and_keep_it_bounded()
    {
        // 键用 float.Equals（NaN 等于 NaN）：== 比较时含 NaN 的键永远不命中，每次绘制都新建一项
        var context = new RasterContext();
        using var target = new Bitmap(new PixelSize(16, 16));
        for (int frame = 0; frame < 20; frame++)
        {
            var dc = new DrawingContext();
            for (int i = 0; i < 20; i++)
                dc.FillRect(new Rect(0, 0, 16, 16), new LinearGradientBrush(default, new Point(16, 0),
                    new[] { new GradientStop(0, new Color(float.NaN, 0, 0, 1)), new GradientStop(1, new Color(0, 0, 1, float.NaN)) }));
            // 互不相同的渐变：缓存条目数有上限
            for (int i = 0; i < 40; i++)
                dc.FillRect(new Rect(0, 0, 1, 1), new LinearGradientBrush(default, new Point(16, 0),
                    new[] { new GradientStop(0, new Color(frame * 40 + i, 0, 0, 1)), new GradientStop(1, C("#000000")) }));
            context.Render(dc.Finish(), target, 1, new RenderOptions(Color.White));
        }
        Assert.True(context.GradientLuts.Count <= GradientLutCache.DefaultMaxEntries);
        Assert.Equal(1 + 20 * 40, context.GradientLuts.Builds);
    }

    // ---------------------------------------------------------------- 虚线、展平与描边

    [Fact]
    public void Dense_dashes_beyond_the_piece_cap_fall_back_to_a_solid_stroke()
    {
        // 可见长度 4000 px、周期 0.002 px：约 4×10⁶ 段。修复前（上限之前）分配数 GB；之后截断为部分虚线
        var degraded = new List<string>();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using var b = Render(dc =>
        {
            var pb = new PathBuilder().MoveTo(new Point(0, 2)).LineTo(new Point(4000, 2));
            dc.StrokePath(pb.ToPath(), C("#000000"), new Stroke(2, dashes: new[] { 0.001f, 0.001f }));
        }, 4000, 4, degraded);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.True(allocated < 200_000_000, $"allocated {allocated / 1e6:F0} MB");
        Assert.Contains(degraded, d => d.Contains("solid"));
        // 实线：整条线完全覆盖
        Assert.Equal(1f, b.GetPixel(3990, 2).A, 2);
        Assert.Equal(1f, b.GetPixel(10, 1).A, 2);
    }

    [Fact]
    public void Flattening_has_a_global_point_budget()
    {
        // 10⁴ 条各自需要上限 1000 段的巨型曲线：修复前展平出 10⁷ 个点（约 80 MB，光栅化数秒）
        var pb = new PathBuilder().MoveTo(new Point(0, 0));
        for (int i = 0; i < 10_000; i++)
            pb.CubicTo(new Point(1e6f, -1e6f), new Point(-1e6f, 1e6f), new Point(i % 2 == 0 ? 10 : 0, 10));
        var path = pb.ToPath();
        var poly = new PolyList();
        Flattener.FlattenToPolygons(path, Matrix3x2.Identity, Flattener.DefaultTolerance, poly);
        Assert.True(poly.PointCount <= path.Verbs.Length + Flattener.MaxSubdivisionPoints, $"{poly.PointCount} points");
        var lines = Flattener.FlattenToPolylines(path, Flattener.DefaultTolerance);
        Assert.True(lines.Sum(l => l.Points.Count) <= path.Verbs.Length + Flattener.MaxSubdivisionPoints);
    }

    [Fact]
    public void Curves_outside_the_clip_are_replaced_by_chords_without_changing_visible_pixels()
    {
        // 大量控制点凸包完全在可见范围外的曲线：只生成弦（每条 1 个点），可见区域的结果与不剔除时相同
        var pb = new PathBuilder().MoveTo(new Point(-5000, 20));
        for (int i = 0; i < 2000; i++)
            pb.CubicTo(new Point(-5000 + i, -3000), new Point(-4000 + i, 3000), new Point(-4999 + i, 20));
        pb.LineTo(new Point(40, 20)).CubicTo(new Point(60, -10), new Point(80, 70), new Point(30, 50)).Close();
        var path = pb.ToPath();
        var culled = new PolyList();
        Flattener.FlattenToPolygons(path, Matrix3x2.Identity, Flattener.DefaultTolerance, culled, CullRect.FromPixels(new PixelRect(0, 0, 64, 64), 1));
        Assert.True(culled.PointCount < 2000 + 200, $"{culled.PointCount} points");

        var full = new PolyList();
        Flattener.FlattenToPolygons(path, Matrix3x2.Identity, Flattener.DefaultTolerance, full);
        Assert.True(full.PointCount > 20_000);
        var a = new float[64 * 64];
        var b = new float[64 * 64];
        var rect = new PixelRect(0, 0, 64, 64);
        new Rasterizer().Fill(culled, FillRule.NonZero, true, rect, new MaskSink(a, rect, null));
        new Rasterizer().Fill(full, FillRule.NonZero, true, rect, new MaskSink(b, rect, null));
        for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) < 1e-3f, $"pixel {i}: {a[i]} vs {b[i]}");
        Assert.Contains(a, v => v > 0.5f);
    }

    [Fact]
    public void Round_joins_on_huge_strokes_have_an_arc_point_budget()
    {
        // 线宽巨大的圆角连接每个至多 512 个点：10⁵ 个顶点修复前约 10⁸ 个点
        var pb = new PathBuilder().MoveTo(new Point(0, 0));
        for (int i = 1; i < 100_000; i++) pb.LineTo(new Point(i % 2 == 0 ? 0 : 1, i * 0.001f));
        var lines = Flattener.FlattenToPolylines(pb.ToPath(), 0.05f);
        var poly = new PolyList();
        Stroker.Stroke(lines, new Stroke(1e6f, StrokeCap.Round, StrokeJoin.Round), 0.05f, poly);
        Assert.True(poly.PointCount <= 8 * 100_000 + Stroker.MaxArcPoints, $"{poly.PointCount} points");
    }

    // ---------------------------------------------------------------- 嵌套与图层

    [Fact]
    public void Deeply_nested_display_lists_are_bounded()
    {
        // 修复前递归无深度上限：深嵌套耗尽线程栈（进程崩溃，无法捕获）
        var list = new DrawingContext().Let(dc => dc.FillRect(new Rect(0, 0, 8, 8), C("#ff0000")));
        for (int i = 0; i < 20_000; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var degraded = new List<string>();
        using var b = Render(dc => dc.DrawDisplayList(list), 8, 8, degraded);
        Assert.Contains(degraded, d => d.Contains("nesting"));
    }

    [Fact]
    public void Exponential_display_list_fan_out_is_bounded()
    {
        // 每层引用下一层两次，40 层就是 2⁴⁰ 次重放：每帧的命令数有上限
        var list = new DrawingContext().Let(dc => dc.FillRect(new Rect(0, 0, 1, 1), C("#ff0000")));
        for (int i = 0; i < 40; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var degraded = new List<string>();
        var sw = Stopwatch.StartNew();
        using var b = Render(dc => dc.DrawDisplayList(list), 4, 4, degraded);
        // 2²² 条 1 像素的命令：未优化构建单独运行约 10 s；只用来发现「不结束」，在高负载的机器上留足余量
        Assert.True(sw.ElapsedMilliseconds < 180_000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Contains(degraded, d => d.Contains("draw commands"));
        Assert.Equal(1f, b.GetPixel(0, 0).A, 2);
    }

    [Fact]
    public void Deeply_nested_offscreen_layers_are_bounded()
    {
        // 每层一块与裁剪范围等大的位图：1000 层 512×512 修复前约 1 GB
        var degraded = new List<string>();
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var b = Render(dc =>
        {
            for (int i = 0; i < 1000; i++) dc.PushLayer(new LayerOptions(0.999f));
            dc.FillRect(new Rect(0, 0, 512, 512), C("#ff0000"));
        }, 512, 512, degraded);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < (RasterContext.MaxLayerDepth + 8) * 512L * 512 * 4 + 50_000_000, $"allocated {allocated / 1e6:F0} MB");
        Assert.Contains(degraded, d => d.Contains("Layers nested"));
        Assert.True(b.GetPixel(100, 100).A > 0.9f);
    }

    // ---------------------------------------------------------------- 图层隔离

    static void HoleInLayer(DrawingContext dc, BlendMode mode, float opacity)
    {
        dc.FillRect(new Rect(0, 0, 40, 40), C("#ff0000"));          // 父表面上的内容
        dc.PushLayer(new LayerOptions(opacity));
        dc.FillRect(new Rect(5, 5, 30, 30), C("#0000ff"));
        dc.FillRect(new Rect(15, 15, 10, 10), new Paint(C("#000000"), mode));
        dc.PopLayer();
    }

    [Theory]
    [InlineData(BlendMode.DstOut)]
    [InlineData(BlendMode.Clear)]
    public void Destination_modes_inside_an_opaque_layer_act_on_the_layer_not_the_parent(BlendMode mode)
    {
        // 修复前：不透明度 1 的 SrcOver 图层跳过离屏，DstOut / Clear 把父表面上的红色也挖掉（洞里透明）
        using var b = Render(dc => HoleInLayer(dc, mode, 1f), 40, 40, clear: Color.White);
        var hole = b.GetPixel(20, 20);
        Assert.Equal(1f, hole.R, 2);
        Assert.Equal(0f, hole.B, 2);
        Assert.Equal(1f, hole.A, 2);
        Assert.Equal(1f, b.GetPixel(8, 8).B, 2);                  // 图层里其余的蓝色
        Assert.Equal(1f, b.GetPixel(2, 2).R, 2);                  // 图层外的红色
    }

    [Fact]
    public void Destination_in_inside_an_opaque_layer_keeps_the_parent()
    {
        // 半透明源的 DstIn 把图层内容减半；隔离时减半的蓝色再盖到父表面的红色上（不透明），
        // 修复前直接作用于父表面，把「蓝盖红」整体减半成半透明
        using var b = Render(dc =>
        {
            dc.FillRect(new Rect(0, 0, 40, 40), C("#ff0000"));
            dc.PushLayer(new LayerOptions(1f));
            dc.FillRect(new Rect(0, 0, 40, 40), C("#0000ff"));
            dc.FillRect(new Rect(10, 10, 20, 20), new Paint(C("#00000080"), BlendMode.DstIn));
            dc.PopLayer();
        }, 40, 40);
        var p = b.GetPixel(20, 20);
        Assert.Equal(1f, p.A, 2);
        Assert.Equal(0.5f, p.R, 1);
        Assert.Equal(0.5f, p.B, 1);
        Assert.Equal(1f, b.GetPixel(2, 2).B, 2);     // 源不覆盖处：DstIn 只作用于形状覆盖的像素（有界语义），图层内容不变
    }

    [Fact]
    public void Nested_display_list_with_destination_mode_isolates_the_enclosing_layer()
    {
        var child = new DrawingContext().Let(dc => dc.FillRect(new Rect(15, 15, 10, 10), new Paint(C("#000000"), BlendMode.DstOut)));
        using var b = Render(dc =>
        {
            dc.FillRect(new Rect(0, 0, 40, 40), C("#ff0000"));
            dc.PushLayer(new LayerOptions(1f));
            dc.FillRect(new Rect(5, 5, 30, 30), C("#0000ff"));
            dc.DrawDisplayList(child);
            dc.PopLayer();
        }, 40, 40, clear: Color.White);
        Assert.Equal(1f, b.GetPixel(20, 20).R, 2);
        Assert.Equal(1f, b.GetPixel(20, 20).A, 2);
    }

    [Fact]
    public void Opaque_layer_without_destination_modes_is_not_isolated()
    {
        // 判定本身：只有含读目标绘制的组需要离屏；子图层以非 SrcOver 合成也算
        var dc = new DrawingContext();
        dc.PushLayer(new LayerOptions(1f));                                     // 0：全是 SrcOver
        dc.FillRect(new Rect(0, 0, 4, 4), C("#ff0000"));
        dc.PushLayer(new LayerOptions(0.5f));                                   // 1：半透明，SrcOver 合成
        dc.FillRect(new Rect(0, 0, 4, 4), new Paint(C("#000000"), BlendMode.Multiply));
        dc.PopLayer();
        dc.PopLayer();
        dc.PushLayer(new LayerOptions(1f));                                     // 2：子图层以 Screen 合成
        dc.PushLayer(new LayerOptions(1f, BlendMode.Screen));                   // 3
        dc.FillRect(new Rect(0, 0, 4, 4), C("#ff0000"));
        dc.PopLayer();
        dc.PopLayer();
        dc.PushLayer(new LayerOptions(1f));                                     // 4：不支持的非可分离模式按 SrcOver 画
        dc.FillRect(new Rect(0, 0, 4, 4), new Paint(C("#000000"), BlendMode.Hue));
        dc.PopLayer();
        var summary = LayerIsolation.Analyze(dc.Finish());
        Assert.Equal(new[] { false, true, true, false, false }, summary.NeedsIsolation);
        Assert.False(summary.TopLevelReadsDestination);
    }
}

static class DrawingContextTestExtensions
{
    public static DisplayList Let(this DrawingContext dc, Action<DrawingContext> draw)
    {
        draw(dc);
        return dc.Finish();
    }
}

/// <summary>双点锥形渐变（带焦点的径向渐变）的参数求解。</summary>
public class TwoPointConicalTests
{
    static readonly GradientStop[] Stops = { new(0, Color.Black), new(1, Color.White) };

    static double T(RadialGradientBrush b, float x, float y)
    {
        GradientShader.ConicalParameters(b, out double fx, out double fy, out double norm, out double cdx, out double cdy, out double r0, out double dr, out double a);
        return GradientShader.TwoPointConical((x - fx) / norm, (y - fy) / norm, cdx, cdy, r0, dr, a);
    }

    [Fact]
    public void Concentric_circles_reduce_to_distance_over_radius()
    {
        // 焦点在圆心、起始半径为 0：t = |p − c| / r（与简单径向渐变相同）
        var b = new RadialGradientBrush(new Point(10, 10), 20, new Point(10, 10), 1e-9f, Stops);
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            float x = (float)(rng.NextDouble() * 80 - 30), y = (float)(rng.NextDouble() * 80 - 30);
            double expected = Math.Sqrt((x - 10.0) * (x - 10.0) + (y - 10.0) * (y - 10.0)) / 20;
            Assert.Equal(expected, T(b, x, y), 4);
        }
    }

    [Fact]
    public void Focus_inside_maps_the_focus_to_zero_and_the_circle_to_one()
    {
        var b = new RadialGradientBrush(new Point(0, 0), 10, new Point(4, -3), 0, Stops);
        Assert.Equal(0, T(b, 4, -3), 6);
        for (int i = 0; i < 16; i++)
        {
            double ang = i * Math.PI / 8;
            Assert.Equal(1, T(b, (float)(10 * Math.Cos(ang)), (float)(10 * Math.Sin(ang))), 4);
        }
        // 起始圆有半径：起始圆上 t = 0，其内取更大的根（仍在 [0, 1) 之外的部分按扩展模式处理）
        var withRadius = new RadialGradientBrush(new Point(0, 0), 10, new Point(2, 0), 3, Stops);
        Assert.Equal(0, T(withRadius, 5, 0), 5);
        Assert.Equal(1, T(withRadius, -10, 0), 5);
    }

    [Fact]
    public void Focus_outside_paints_only_the_cone()
    {
        // 焦点在结束圆外（锥形）：锥外的点没有满足 r(t) ≥ 0 的圆，返回 NaN（不绘制）
        var b = new RadialGradientBrush(new Point(20, 0), 4, new Point(0, 0), 0, Stops);
        // 圆 (20t, 0)、半径 4t：经过 (16, 0) 的是 t = 2/3 与 t = 1，取较大者；经过 (24, 0) 的是 t = 1 与 t = 1.5
        Assert.Equal(1, T(b, 16, 0), 4);
        Assert.Equal(1.5, T(b, 24, 0), 4);
        Assert.True(T(b, 10, 0) is >= 0 and < 1);
        Assert.True(double.IsNaN(T(b, 0, 10)));
        Assert.True(double.IsNaN(T(b, -5, 0)));
    }

    [Fact]
    public void Focus_on_the_circle_uses_the_linear_solution()
    {
        // 焦点恰在结束圆上：a = 0。圆上 t = 1，焦点处 t = 0，焦点切线背面无解
        var b = new RadialGradientBrush(new Point(0, 0), 10, new Point(-10, 0), 0, Stops);
        Assert.Equal(1, T(b, 10, 0), 4);
        Assert.Equal(1, T(b, 0, 10), 4);
        Assert.Equal(0.5, T(b, 0, 0), 4);
        Assert.True(double.IsNaN(T(b, -20, 0)));
    }

    [Fact]
    public void Identical_circles_paint_nothing()
    {
        var b = new RadialGradientBrush(new Point(3, 3), 5, new Point(3, 3), 5, Stops);
        Assert.True(double.IsNaN(T(b, 3, 3)));
        Assert.True(double.IsNaN(T(b, 20, 3)));
        using var bmp = Golden.Render(new DrawingContext().Let(dc => dc.FillRect(new Rect(0, 0, 8, 8), b)), 8, 8, 1, Color.Transparent);
        Assert.Equal(0f, bmp.GetPixel(4, 4).A);
    }

    [Fact]
    public void Simple_and_explicit_center_focus_render_identically()
    {
        var stops = new[] { new GradientStop(0, Color.Parse("#ff0000")), new GradientStop(1, Color.Parse("#0000ff")) };
        using var a = Golden.Render(new DrawingContext().Let(dc => dc.FillRect(new Rect(0, 0, 32, 32), new RadialGradientBrush(new Point(16, 16), 12, stops))), 32, 32);
        using var b = Golden.Render(new DrawingContext().Let(dc => dc.FillRect(new Rect(0, 0, 32, 32),
            new RadialGradientBrush(new Point(16, 16), 12, new Point(16, 16), 0, stops))), 32, 32);
        Assert.Equal(a.ToRgbaUnpremultiplied(), b.ToRgbaUnpremultiplied());
    }
}

/// <summary>
/// 软件后端的模糊测试（与 GPU 后端共用 <see cref="DisplayListFuzzer"/>）：不抛异常、不挂起、内存有界。
/// 修复前这里会复现 NaN 点的越界、单个 NaN 色标的越界、巨大模糊的数百 MB 分配与超密虚线。
/// </summary>
public class SoftwareFuzzTests
{
    [Theory]
    [MemberData(nameof(DisplayListFuzzer.Seeds), MemberType = typeof(DisplayListFuzzer))]
    public void Software_backend_survives_hostile_display_lists(int seed)
    {
        var fuzz = new DisplayListFuzzer(5000 + seed);
        using var backend = new SoftwareRenderBackend();
        using var small = backend.CreateOffscreenTarget(new PixelSize(160, 120), 1);
        using var large = backend.CreateOffscreenTarget(new PixelSize(320, 240), 2);
        for (int frame = 1; frame <= 15; frame++)
        {
            var list = fuzz.Build(60);
            var target = frame % 3 == 0 ? large : small;
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            target.Render(list, new RenderOptions(Color.White, frame % 5 == 0 ? new PixelRect(10, 10, 50, 40) : null));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"seed {seed} frame {frame} took {sw.Elapsed}");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 512L << 20, $"seed {seed} frame {frame} allocated {allocated >> 20} MB");
        }
    }
}
