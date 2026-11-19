using System;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Graphics.Text;
using AwAUI.Render.Raster;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Frame;

// 字形串（DrawOp.DrawGlyphRun）。规则与掩码生成用软件后端的同一实现（GlyphRasterizer）：
// 「均匀正缩放 + 平移」且设备字号不超过 128px 时，每个字形按（字型、字形、设备字号桶、1/4 px 子像素 x、样式）光栅化为 A8 掩码，
// 掩码放进 GPU 图集，每个字形一个实例；y 对齐到整像素。其他变换、超大字号或超大掩码时整串（或单个字形）按路径填充。
internal sealed partial class FrameRecorder
{
    internal const float MaxCachedGlyphSize = GlyphRasterizer.MaxCachedGlyphSize;
    internal const int MaxGlyphMaskPixels = GlyphRasterizer.MaxGlyphMaskPixels;

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

        var q = new GpuQuadInstance();
        if (!SetPaint(ref q, paint, brush, out var lut, out var mode)) return;
        var bounds = clip.Bounds;
        for (int i = 0; i < run.Count; i++)
        {
            if (!GlyphRasterizer.TryPlace(plan, origin, i, out var key, out int ix, out int iy)) continue;
            if (!_glyphCache.TryGet(key, out var mask))
            {
                if (GlyphRasterizer.IsCulled(plan, ix, iy, bounds)) continue;
                mask = _glyphRasterizer.Rasterize(key);
                _glyphCache.Add(mask);
                GlyphsRasterized++;
            }
            if (mask.TooLarge)
            {
                DrawGlyphsAsPath(run, origin, i, 1, paint, brush, aa);
                continue;
            }
            if (mask.IsEmpty) continue;
            int left = ix + mask.Left, top = iy + mask.Top;
            var rect = new PixelRect(left, top, mask.Width, mask.Height).Intersect(bounds);
            if (rect.IsEmpty) continue;
            if (GlyphSlot(mask, out var slot)) EmitMask(q, slot, left, top, rect, lut, mode);
        }
    }

    /// <summary>字形掩码在图集中的位置；所在页被淘汰后从缓存的字节重新上传。</summary>
    bool GlyphSlot(GlyphMask mask, out Atlas.AtlasSlot slot)
    {
        if (mask.Slot.IsValid)
        {
            slot = mask.Slot;
            return true;
        }
        if (!Allocate(mask.Width, mask.Height, out slot)) return false;
        var src = mask.Coverage!;
        var dst = slot.Page.Shadow;
        for (int y = 0; y < mask.Height; y++)
            Buffer.BlockCopy(src, y * mask.Width, dst, slot.Offset + y * slot.Page.Width, mask.Width);
        slot.Commit();
        mask.Slot = slot;
        return true;
    }

    void DrawGlyphsAsPath(GlyphRun run, Point origin, int start, int count, in PaintData paint, object? brush, bool aa)
    {
        var pb = new PathBuilder();
        GlyphRasterizer.AppendOutlines(run, origin, start, count, _state.Ctm, _state.Clip.Bounds, pb);
        var path = pb.ToPath();
        if (!path.IsEmpty) FillPath(path, paint, brush, aa);
    }
}
