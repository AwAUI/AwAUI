using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render.Gpu.Atlas;
using AwAUI.Render.Gpu.Frame;
using AwAUI.Render.Raster;
using AwAUI.RenderTests;
using AwAUI.Render.Rhi;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>DisplayList → 实例与批次（不需要 GPU：图集与查找表只用作批次键）。</summary>
public class BatchingTests
{
    sealed class Recording
    {
        public readonly FrameGraph Graph = new();
        public readonly List<string> Degradations = new();
        public FrameRecorder Recorder = null!;
    }

    static Recording Record(Action<DrawingContext> draw, int width = 400, int height = 300, double scale = 1, int atlasPage = 1024)
    {
        var dc = new DrawingContext();
        draw(dc);
        var r = new Recording();
        var atlas = new MaskAtlas(null, atlasPage, 4);
        var luts = new GradientLuts(null);
        r.Recorder = new FrameRecorder(atlas, luts, new GlyphCache(), new PathMaskCache(), r.Degradations.Add);
        atlas.BeginFrame(1);
        luts.BeginFrame(1);
        var full = new PixelRect(0, 0, (int)(width * scale), (int)(height * scale));
        r.Recorder.Record(dc.Finish(), r.Graph, full, full, scale, 1, null);
        return r;
    }

    static List<FrameOp> Draws(FrameTarget t) => t.Ops.Where(o => o.Kind == FrameOpKind.Draw).ToList();

    [Fact]
    public void Solid_shapes_merge_into_one_batch_in_order()
    {
        var r = Record(dc =>
        {
            for (int i = 0; i < 100; i++)
            {
                dc.FillRect(new Rect(i * 3, 10, 2, 50), Color.FromRgba((byte)i, 0, 0, 255));
                dc.FillRoundRect(new RoundRect(new Rect(i * 3, 70, 3, 20), 1), Color.FromRgba(0, (byte)i, 0, 255));
            }
            dc.StrokeRect(new Rect(10, 100, 50, 50), Color.Black, new Stroke(2));
            dc.DrawBoxShadow(new RoundRect(new Rect(100, 100, 50, 50), 8), new BoxShadow(0, 2, 8, 0, Color.Black));
        });
        Assert.Equal(202, r.Graph.InstanceCount);
        var op = Assert.Single(r.Graph.Main.Ops);
        Assert.Equal(FrameOpKind.Draw, op.Kind);
        Assert.Equal((0, 202), (op.First, op.Count));
        Assert.Equal(GpuBlendState.PremultipliedOver, op.Blend);
        // 顺序不变：矩形与圆角矩形交替
        Assert.Equal(QuadShape.Rect, r.Graph.Instances[0].Kinds.Shape);
        Assert.Equal(QuadShape.RoundRect, r.Graph.Instances[1].Kinds.Shape);
        Assert.Equal(QuadShape.Ring, r.Graph.Instances[200].Kinds.Shape);
        Assert.Equal(QuadShape.OuterShadow, r.Graph.Instances[201].Kinds.Shape);
    }

    [Fact]
    public void Paths_glyphs_and_gradients_share_batches_when_textures_match()
    {
        var r = Record(dc =>
        {
            var g = new LinearGradientBrush(new Point(0, 0), new Point(100, 0), new[] { new GradientStop(0, Color.Black), new GradientStop(1, Color.White) });
            dc.FillCircle(new Point(30, 30), 20, Color.Black);
            dc.FillRect(new Rect(0, 60, 100, 20), g);
            dc.DrawGlyphRun(TextScenes.Run("Batch", 16), new Point(10, 120), Color.Black);
            dc.FillCircle(new Point(80, 30), 20, g);
        });
        // 一页图集 + 一页查找表：全部并为一批（掩码、字形、解析矩形与渐变共用一个管线）
        var op = Assert.Single(r.Graph.Main.Ops);
        Assert.Equal(r.Graph.InstanceCount, op.Count);
        Assert.IsType<AtlasPage>(op.Coverage);
        Assert.IsType<GradientLuts.LutPage>(op.Lut);
    }

    [Fact]
    public void Rect_clips_fold_into_quads_and_rounded_clips_are_analytic()
    {
        var r = Record(dc =>
        {
            dc.Save();
            dc.ClipRect(new Rect(10, 10, 100, 100));                 // 对齐像素：只收缩范围
            dc.FillRect(new Rect(0, 0, 200, 200), Color.Black);
            dc.ClipRoundRect(new RoundRect(new Rect(20, 20, 60, 60), 10)); // 解析裁剪形状
            for (int i = 0; i < 5; i++) dc.FillRect(new Rect(20 + i * 10, 0, 5, 200), Color.White);
            dc.ClipRect(new Rect(30.5f, 30.5f, 40, 40));               // 再嵌套非对齐矩形：外层形状转为蒙版
            dc.FillRect(new Rect(0, 0, 200, 200), Color.Black);
            dc.Restore();
        });
        Assert.Equal(7, r.Graph.InstanceCount);
        var first = r.Graph.Instances[0];
        Assert.Equal(new Vector4(10, 10, 110, 110), first.Bounds);   // 裁剪矩形折进四边形
        Assert.Equal((0f, 0f), (first.Clip.Z, first.Clip.W));
        var shaped = r.Graph.Instances.Slice(1, 5).ToArray();
        Assert.All(shaped, q => Assert.Equal((0f, 1f), (q.Clip.Z, q.Clip.W)));
        var clipShape = Assert.Single(r.Graph.ClipShapes.ToArray());
        Assert.Equal(new Vector4(20, 20, 80, 80), clipShape.Rect);
        Assert.Equal(new Vector4(10), clipShape.RadiiX);
        var nested = r.Graph.Instances[6];
        Assert.Equal((1f, 0f), (nested.Clip.Z, nested.Clip.W));     // 蒙版
        // 不读裁剪槽位的实例与带裁剪的实例同批
        var op = Assert.Single(r.Graph.Main.Ops);
        Assert.IsType<AtlasPage>(op.Clip);
    }

    [Fact]
    public void Blend_modes_that_read_the_target_group_non_overlapping_instances()
    {
        var r = Record(dc =>
        {
            for (int i = 0; i < 4; i++) dc.FillRect(new Rect(i * 30, 0, 20, 20), new Paint(Color.Black, BlendMode.Multiply));   // 不重叠：一组
            for (int i = 0; i < 3; i++) dc.FillRect(new Rect(i * 5, 50, 20, 20), new Paint(Color.Black, BlendMode.Screen));     // 重叠：各自一组
            dc.FillRect(new Rect(0, 100, 10, 10), Color.Black);
        });
        var ops = r.Graph.Main.Ops;
        // 第一组：4 个 Multiply 与第一个 Screen 互不重叠（混合模式按实例记录，可以同组）
        Assert.Equal(FrameOpKind.DstCopy, ops[0].Kind);
        Assert.Equal(new PixelRect(0, 0, 110, 70), ops[0].CopyRect);
        Assert.Equal((5, GpuBlendState.Replace), (ops[1].Count, ops[1].Blend));
        Assert.Same(ops[0], ops[1].CopyOp);
        // 后两个 Screen 与前一个重叠：各自拷贝一次
        Assert.Equal(2, ops.Count(o => o.Kind == FrameOpKind.DstCopy && o.CopyRect.Y == 50));
        Assert.Equal(3, ops.Count(o => o.Kind == FrameOpKind.DstCopy));
        Assert.Equal(GpuBlendState.PremultipliedOver, ops[ops.Count - 1].Blend);
        Assert.Equal((uint)BlendMode.Multiply + 1, r.Graph.Instances[0].Kinds.Blend);
        Assert.Equal(0u, r.Graph.Instances[r.Graph.InstanceCount - 1].Kinds.Blend);
    }

    [Fact]
    public void Layers_render_before_their_composite_and_trivial_layers_are_skipped()
    {
        var r = Record(dc =>
        {
            dc.PushLayer(new LayerOptions(1f));          // 等价于直接绘制
            dc.FillRect(new Rect(0, 0, 10, 10), Color.Black);
            dc.PopLayer();
            dc.PushLayer(new LayerOptions(0f));          // 不可见
            dc.FillRect(new Rect(0, 0, 10, 10), Color.Black);
            dc.PopLayer();
            dc.PushLayer(new LayerOptions(0.5f, BlendMode.SrcOver, new Rect(20, 20, 50, 40)));
            dc.FillRect(new Rect(0, 0, 100, 100), Color.Black);
            dc.PopLayer();
        });
        var layer = Assert.Single(r.Graph.Layers);
        Assert.Equal(new PixelRect(20, 20, 50, 40), layer.Rect);
        var ops = r.Graph.Main.Ops;
        Assert.Equal(3, ops.Count);
        Assert.Equal(FrameOpKind.Draw, ops[0].Kind);
        Assert.Equal(FrameOpKind.RenderLayer, ops[1].Kind);
        Assert.Same(layer, ops[1].Layer);
        Assert.Same(layer, ops[2].Coverage);
        var composite = r.Graph.Instances[ops[2].First];
        Assert.Equal(QuadShape.Layer, composite.Kinds.Shape);
        Assert.Equal(0.5f, composite.Color.X);
        // 图层内的矩形被裁到图层范围
        Assert.Equal(new Vector4(20, 20, 70, 60), r.Graph.Instances[layer.Ops[0].First].Bounds);
    }

    [Theory]
    [InlineData(BlendMode.SrcOver, false)]
    [InlineData(BlendMode.DstOut, true)]
    [InlineData(BlendMode.DstIn, true)]
    [InlineData(BlendMode.Clear, true)]
    [InlineData(BlendMode.Multiply, true)]
    [InlineData(BlendMode.Hue, false)]   // 不支持的非可分离模式按 SrcOver 画，不需要隔离
    public void Opaque_layer_is_isolated_only_when_its_content_reads_the_destination(BlendMode mode, bool isolated)
    {
        // 与软件后端相同（LayerIsolation）：组内读目标的绘制必须作用于组自己的表面
        var r = Record(dc =>
        {
            dc.FillRect(new Rect(0, 0, 40, 40), Color.Parse("#ff0000"));
            dc.PushLayer(new LayerOptions(1f));
            dc.FillRect(new Rect(5, 5, 30, 30), Color.Parse("#0000ff"));
            dc.FillRect(new Rect(15, 15, 10, 10), new Paint(Color.Black, mode));
            dc.PopLayer();
        });
        Assert.Equal(isolated ? 1 : 0, r.Graph.Layers.Count);
    }

    [Fact]
    public void Exponential_display_list_fan_out_is_bounded()
    {
        var leaf = new DrawingContext();
        leaf.FillRect(new Rect(0, 0, 1, 1), Color.Black);
        var list = leaf.Finish();
        for (int i = 0; i < 40; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Record(dc => dc.DrawDisplayList(list), 4, 4);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        // 实例数（与实例缓冲）在命令数上限之前就被每帧实例数上限截住
        Assert.Contains(r.Degradations, d => d.Contains("work limit"));
        Assert.Equal(FrameRecorder.MaxInstancesPerFrame, r.Graph.InstanceCount);
        Assert.Single(r.Degradations);
    }

    [Fact]
    public void Fan_out_of_full_screen_fills_is_bounded_by_the_work_limit()
    {
        // 与软件后端相同：几百字节的输入重放出数百万次全屏绘制，GPU 上同样是无界的填充量；按实例覆盖的像素计入工作量
        var leaf = new DrawingContext();
        leaf.FillRect(new Rect(0, 0, 400, 300), Color.Parse("#ff000080"));
        var list = leaf.Finish();
        for (int i = 0; i < 40; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var r = Record(dc => dc.DrawDisplayList(list));
        Assert.Contains(r.Degradations, d => d.Contains("GPU fill"));
        Assert.InRange(r.Graph.InstanceCount, 1, FrameRecorder.MaxInstancePixelsPerFrame / (400 * 300) + 2);
        Assert.InRange(r.Recorder.InstancePixels, FrameRecorder.MaxInstancePixelsPerFrame, FrameRecorder.MaxInstancePixelsPerFrame + 400 * 300 * 2);
    }

    [Fact]
    public void Fan_out_of_rasterized_paths_is_bounded_by_the_cpu_work_limit()
    {
        // 旋转下的填充走 CPU 掩码；点数超过 PathMaskCache.MaxCachedPoints 的路径不缓存，每次重放都重新光栅化。
        // 工作量上限调低到 2²⁴ 以保持测试快速
        var pb = new PathBuilder().MoveTo(new Point(0, 0));
        for (int i = 0; i < PathMaskCache.MaxCachedPoints + 1000; i++) pb.LineTo(new Point(i % 2 == 0 ? 300 : 0, i * 0.05f));
        var path = pb.Close().ToPath();
        var leaf = new DrawingContext();
        for (int i = 0; i < 8; i++)
        {
            leaf.Save();
            leaf.Transform(Matrix3x2.CreateRotation(0.01f * (i + 1)));
            leaf.FillPath(path, Color.Parse("#0000ff40"));
            leaf.Restore();
        }
        var list = leaf.Finish();
        for (int i = 0; i < 40; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        RasterContext.WorkBudgetOverride = 1 << 24;
        Recording r;
        try
        {
            r = Record(dc => dc.DrawDisplayList(list));
        }
        finally
        {
            RasterContext.WorkBudgetOverride = null;
        }
        Assert.Contains(r.Degradations, d => d.Contains("rasterized"));
        Assert.InRange(r.Recorder.FrameWork, 1 << 24, (1 << 24) + 2_000_000);
    }

    [Fact]
    public void Deep_layer_nesting_degrades_instead_of_allocating_unbounded_targets()
    {
        var r = Record(dc =>
        {
            for (int i = 0; i < 100; i++) dc.PushLayer(new LayerOptions(0.9f));
            dc.FillRect(new Rect(0, 0, 10, 10), Color.Black);
            for (int i = 0; i < 100; i++) dc.PopLayer();
        });
        Assert.Equal(FrameRecorder.MaxLayerDepth, r.Graph.Layers.Count);
        Assert.Contains(r.Degradations, d => d.Contains("nested deeper"));
    }

    [Fact]
    public void Deep_display_list_nesting_is_bounded()
    {
        var inner = new DrawingContext();
        inner.FillRect(new Rect(0, 0, 5, 5), Color.Black);
        var list = inner.Finish();
        for (int i = 0; i < 1000; i++)
        {
            var dc = new DrawingContext();
            dc.Translate(0.01f, 0);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var r = Record(dc => dc.DrawDisplayList(list));
        Assert.Equal(0, r.Graph.InstanceCount);
        Assert.Contains(r.Degradations, d => d.Contains("nesting"));
    }

    [Fact]
    public void Repeated_paths_hit_the_mask_cache_across_frames()
    {
        var atlas = new MaskAtlas(null, 512, 2);
        var luts = new GradientLuts(null);
        var cache = new PathMaskCache();
        var recorder = new FrameRecorder(atlas, luts, new GlyphCache(), cache, null);
        var graph = new FrameGraph();
        var full = new PixelRect(0, 0, 200, 200);
        for (int frame = 1; frame <= 3; frame++)
        {
            // 每帧新建同形路径对象：缓存按内容命中
            var dc = new DrawingContext();
            dc.FillCircle(new Point(50, 50), 30, Color.Black);
            dc.StrokePath(new PathBuilder().MoveTo(10, 10).LineTo(150, 120).ToPath(), Color.Black, new Stroke(3));
            atlas.BeginFrame(frame);
            luts.BeginFrame(frame);
            recorder.Record(dc.Finish(), graph, full, full, 1, frame, null);
            atlas.EndFrame();
            Assert.Equal(frame == 1 ? 2 : 0, recorder.PathMasksRasterized);
        }
        Assert.Equal(4, cache.Hits);
    }

    [Fact]
    public void Partial_redraw_clears_the_damage_rect_first()
    {
        var dc = new DrawingContext();
        dc.FillRect(new Rect(0, 0, 100, 100), Color.Black);
        var atlas = new MaskAtlas(null);
        var luts = new GradientLuts(null);
        var recorder = new FrameRecorder(atlas, luts, new GlyphCache(), new PathMaskCache(), null);
        var graph = new FrameGraph();
        recorder.Record(dc.Finish(), graph, new PixelRect(0, 0, 100, 100), new PixelRect(10, 20, 30, 40), 1, 1, Color.White);
        Assert.Equal(GpuBlendState.Replace, graph.Main.Ops[0].Blend);
        Assert.Equal(new Vector4(10, 20, 40, 60), graph.Instances[0].Bounds);
        Assert.Equal(new Vector4(10, 20, 40, 60), graph.Instances[1].Bounds);   // 绘制也只在受损区域内
    }
}
