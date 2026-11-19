using System;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Render.Gpu.Atlas;
using AwAUI.Render.Raster;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Frame;

// 位图绘制（DrawOp.DrawImage）：与软件后端（RasterContext.Images）相同的采样计划（ImagePlan）；
// 覆盖率在缩放 + 平移下是解析矩形（QuadShape.Rect），其他变换下是矩形路径的 A8 掩码（与 FillPath 相同）；
// 颜色由着色器按 QuadPaint.Image 采样，纹理来自 ImageTextures（绑定在渐变表槽位）。
internal sealed partial class FrameRecorder
{
    ImageTextures? _images;

    /// <summary>后端在构造后设置（测试可以不设置：没有纹理缓存时 DrawImage 被跳过并上报）。</summary>
    public ImageTextures? Images { get => _images; set => _images = value; }

    bool LevelFits(PixelRect crop, int level)
    {
        ImagePlan.LevelSize(crop, level, out int w, out int h);
        return _images!.FitsSize(w, h);
    }

    void DrawImage(in DrawImageCmd c, Bitmap image, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        if (image.IsDisposed)
        {
            Degrade("DrawImage with a disposed bitmap ignored.");
            return;
        }
        if (_images is null)
        {
            Degrade("DrawImage is not supported by this GPU backend instance; ignored.");
            return;
        }
        var ctm = _state.Ctm;
        if (!ImagePlan.TryCreate(c, image.Width, image.Height, ctm, 0, out var plan)) return;
        // 纹理区域：第 0 级优先上传整张位图（不同源矩形共用），放不下时只上传裁剪区；仍放不下（或本来就是 mip）时
        // 改用能放下的最低级别（缩小，上报降级）
        var full = new PixelRect(0, 0, image.Width, image.Height);
        PixelRect region;
        if (plan.Level == 0 && _images.FitsSize(full.Width, full.Height)) region = full;
        else if (plan.Level == 0 && _images.FitsSize(plan.Crop.Width, plan.Crop.Height)) region = plan.Crop;
        else
        {
            int planned = plan.Level;
            int level = Math.Max(1, planned);
            while (level <= ImagePlan.MaxLevel && !LevelFits(plan.Crop, level)) level++;
            if (level > ImagePlan.MaxLevel || !ImagePlan.TryCreate(c, image.Width, image.Height, ctm, level, out plan) || !LevelFits(plan.Crop, plan.Level))
            {
                Degrade("Image too large for a GPU texture; skipped.");
                return;
            }
            region = plan.Crop;
            if (plan.Level != planned)
                Degrade($"Image larger than the GPU texture limit ({_images.MaxTextureSize} px or {ImageTextures.MaxTextureBytes >> 20} MB) drawn from a downscaled level.");
        }

        bool scaleTranslate = ctm.IsScaleTranslate();
        Rect dev = default;
        PixelRect area;
        VectorPath? path = null;
        if (scaleTranslate)
        {
            dev = c.Dst.TransformBounds(ctm);
            if (!aa) dev = Rect.FromLTRB(MathF.Round(dev.Left), MathF.Round(dev.Top), MathF.Round(dev.Right), MathF.Round(dev.Bottom));
            area = PixelRect.RoundOut(dev).Intersect(clip.Bounds);
        }
        else
        {
            path = VectorPath.FromRect(c.Dst);
            area = PixelRect.RoundOut(path.Bounds.TransformBounds(ctm)).Intersect(clip.Bounds);
        }
        if (area.IsEmpty) return;

        long work = 0;
        var texture = _images.Get(image, plan, region, ref work, out var failure);
        _rasterizer.Work += work;
        if (texture is null)
        {
            Degrade(failure!);
            return;
        }

        // 第 0 级的纹理只含 region 时，纹素坐标与钳制范围平移到纹理原点
        var m = plan.DeviceToTexel;
        var r = plan.ClampRect;
        float ox = plan.Level == 0 ? region.X : 0, oy = plan.Level == 0 ? region.Y : 0;
        var q = new GpuQuadInstance
        {
            Paint0 = new Vector4(m.M11, m.M21, m.M31 - ox, 0),
            Paint1 = new Vector4(m.M12, m.M22, m.M32 - oy, c.Opacity),
            Extra = new Vector4(r.X - ox, r.Y - oy, r.Right - 1 - ox, r.Bottom - 1 - oy),
        };
        q.Kinds.Paint = QuadPaint.Image | ((plan.Sampling == ImageSampling.Nearest ? 0u : 1u) << QuadPaint.SpreadShift);
        if (scaleTranslate)
        {
            q.Bounds = Box(area);
            q.Shape = Box(dev);
            q.Kinds.Shape = QuadShape.Rect;
            Emit(ref q, null, texture, BlendMode.SrcOver, clip);
        }
        else if (PathMask(path!, null, ctm, area, aa, out var slot))
        {
            EmitMask(q, slot, area.X, area.Y, area, texture, BlendMode.SrcOver);
        }
    }
}
