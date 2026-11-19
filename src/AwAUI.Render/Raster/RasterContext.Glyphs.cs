using System;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Graphics.Text;

namespace AwAUI.Render.Raster;

// 字形串绘制（DrawOp.DrawGlyphRun）。
//
// 快速路径：变换为「均匀正缩放 + 平移」且设备字号不超过 GlyphRasterizer.MaxCachedGlyphSize 时，每个字形按
// （字型、字形、设备字号桶、1/4 px 子像素 x 偏移、样式）光栅化为 A8 掩码并放入 LRU 缓存；
// 绘制时把掩码逐行转换为覆盖率交给 FillSink，与其他图元共用裁剪、图层、混合与着色（渐变等）路径。
// y 方向对齐到整像素。不做 hinting，灰度抗锯齿。规则与掩码生成在 GlyphRasterizer（GPU 后端共用）。
//
// 回退路径：旋转、错切、非均匀缩放、翻转、超大字号或掩码超出上限时，把字形轮廓变换到局部坐标后
// 合并成一条路径直接填充（不缓存）。
//
// 彩色字形（COLR / sbix / CBDT）尚未支持：按普通轮廓绘制（没有轮廓的位图字形不绘制）。
internal sealed partial class RasterContext
{
    /// <summary>入缓存的最大设备字号（px）。</summary>
    internal const float MaxCachedGlyphSize = GlyphRasterizer.MaxCachedGlyphSize;

    /// <summary>单个字形掩码的最大像素数；超出时该字形改走路径填充。</summary>
    internal const int MaxGlyphMaskPixels = GlyphRasterizer.MaxGlyphMaskPixels;

    readonly GlyphCache _glyphCache;
    readonly GlyphRasterizer _glyphRasterizer;

    /// <summary>字节覆盖率到浮点的查找表：i / 255，且 255 精确映射为 1。</summary>
    static readonly float[] s_coverageLut = BuildCoverageLut();

    static float[] BuildCoverageLut()
    {
        var lut = new float[256];
        for (int i = 0; i < 255; i++) lut[i] = i / 255f;
        lut[255] = 1f;
        return lut;
    }

    /// <summary>本上下文的字形掩码缓存（测试与诊断用）。</summary>
    internal GlyphCache GlyphCache => _glyphCache;

    void DrawGlyphRun(GlyphRun run, Point origin, in PaintData paint, object? brush, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty || run.IsEmpty) return;
        if (run.Face.Metrics.UnitsPerEm <= 0) return;

        if (!GlyphRasterizer.TryPlan(run, _state.Ctm, aa, out var plan, out bool visible))
        {
            DrawGlyphsAsPath(run, origin, 0, run.Count, paint, brush, aa);
            return;
        }
        if (!visible) return;

        var sink = PrepareSink(paint, brush);
        if (sink == null) return;
        var bounds = clip.Bounds;
        for (int i = 0; i < run.Count; i++)
        {
            if (!GlyphRasterizer.TryPlace(plan, origin, i, out var key, out int ix, out int iy)) continue;
            if (!_glyphCache.TryGet(key, out var mask))
            {
                if (GlyphRasterizer.IsCulled(plan, ix, iy, bounds)) continue;
                mask = _glyphRasterizer.Rasterize(key);
                _glyphCache.Add(mask);
            }
            if (mask.TooLarge)
            {
                DrawGlyphsAsPath(run, origin, i, 1, paint, brush, aa);
                sink = PrepareSink(paint, brush)!;   // 路径填充重置了共享的 FillSink
                continue;
            }
            if (mask.IsEmpty) continue;
            CompositeGlyphMask(mask, ix + mask.Left, iy + mask.Top, bounds, sink);
        }
    }

    void CompositeGlyphMask(GlyphMask mask, int left, int top, PixelRect bounds, FillSink sink)
    {
        int y0 = Math.Max(top, bounds.Y), y1 = Math.Min(top + mask.Height, bounds.Bottom);
        if (y0 >= y1 || left >= bounds.Right || left + mask.Width <= bounds.X) return;
        var coverage = mask.Coverage!;
        int w = mask.Width;
        for (int y = y0; y < y1; y++)
            sink.RowA8(y, left, new ReadOnlySpan<byte>(coverage, (y - top) * w, w), s_coverageLut);
    }

    /// <summary>回退路径：把 [start, start + count) 的字形轮廓变换到局部坐标，合并为一条路径在当前变换下填充。</summary>
    void DrawGlyphsAsPath(GlyphRun run, Point origin, int start, int count, in PaintData paint, object? brush, bool aa)
    {
        var pb = new PathBuilder();
        GlyphRasterizer.AppendOutlines(run, origin, start, count, _state.Ctm, _window, pb);   // 按整个目标剔除，见 _window
        var path = pb.ToPath();
        if (!path.IsEmpty) FillPath(path, paint, brush, aa);
    }
}
