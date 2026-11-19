using System;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Render.Raster;

// 位图绘制（DrawOp.DrawImage，design/03「图片」）。
//
// 覆盖率：目标矩形在缩放 + 平移下用与 FillRect 相同的解析面积，其他变换下按矩形路径光栅化（与 FillPath 相同）；
// 颜色由 ImageShader 按 ImagePlan 采样（最近 / 双线性，预乘，钳制到源矩形），乘以不透明度后以 SrcOver 合成。
// 缩小超过 √2 倍的双线性采样改用盒式预滤波的 mip 级别：按（位图，版本，裁剪区，级别）缓存在本上下文中（字节预算 LRU），
// 构造代价（裁剪区面积）计入每帧工作量；本帧新构造的 mip 字节数另有上限（MaxLevelBytesPerFrame），超出时改为直接采样第 0 级。
internal sealed partial class RasterContext
{
    /// <summary>软件后端 mip 级别缓存的字节预算。</summary>
    internal const long ImageCacheBudget = 64L << 20;

    /// <summary>
    /// 一帧新构造的 mip 字节数上限（与 GPU 后端每帧上传上限 <c>ImageTextures.MaxUploadBytesPerFrame</c> 相同）。
    /// 缓存只在帧开始时收缩到预算、本帧用到的条目不淘汰，没有这个上限时一帧可以按工作量上限（2^30）分配约 1 GB 的 mip
    /// （同一张大位图的许多不同源矩形各自缩小一半），而且因为超出预算，下一帧又全部重建。超出时这张图直接从第 0 级采样
    /// （不预滤波，缩小时可能走样）并上报降级。
    /// </summary>
    internal const long MaxLevelBytesPerFrame = 256L << 20;

    /// <summary>测试用：覆盖当前线程的 <see cref="MaxLevelBytesPerFrame"/>。</summary>
    [ThreadStatic] internal static long? LevelBytesPerFrameOverride;

    readonly ImageLruCache<ImageLevel> _images = new(ImageCacheBudget);
    long _frameNumber;
    long _levelBytesThisFrame;

    /// <summary>帧开始：mip 缓存丢弃旧版本 / 已释放 / 久未使用的条目并收缩到预算，清零本帧构造的字节数。</summary>
    void BeginImageFrame()
    {
        _images.BeginFrame(++_frameNumber);
        _levelBytesThisFrame = 0;
    }

    /// <summary>本上下文的 mip 缓存（测试与诊断用）。</summary>
    internal ImageLruCache<ImageLevel> ImageLevels => _images;

    void DrawImage(in DrawImageCmd c, Bitmap image, bool aa)
    {
        var clip = _state.Clip;
        if (clip.IsEmpty) return;
        if (image.IsDisposed)
        {
            _degraded?.Invoke("DrawImage with a disposed bitmap ignored.");
            return;
        }
        if (!ImagePlan.TryCreate(c, image.Width, image.Height, _state.Ctm, 0, out var plan)) return;

        bool scaleTranslate = _state.Ctm.IsScaleTranslate();
        Rect dev = default;
        PixelRect area = default;
        if (scaleTranslate && !RectArea(c.Dst, aa, out dev, out area)) return;
        // 其他变换：完全在裁剪区之外的图片不构造 mip（不然屏幕外、损伤矩形外的旋转图片每帧都要付构造代价与内存）
        if (!scaleTranslate && PixelRect.RoundOut(c.Dst.TransformBounds(_state.Ctm)).Intersect(clip.Bounds).IsEmpty) return;

        ImageLevel? level = null;
        if (plan.Level > 0 && !TryGetLevel(image, ref plan, out level, c)) return;

        _fillSink.Reset(_surface.Bitmap, _surface.OffsetX, _surface.OffsetY, _state.Clip, new ImageShader(plan, image, level, c.Opacity), BlendMode.SrcOver);
        if (scaleTranslate) RectCoverage(dev, area, _fillSink);
        else FillPath(VectorPath.FromRect(c.Dst), aa, _fillSink);
    }

    /// <summary>
    /// 取（或构造）第 L 级；构造代价计入每帧工作量，超出时跳过并上报。本帧新构造的字节数超出 <see cref="MaxLevelBytesPerFrame"/> 时
    /// 把 <paramref name="plan"/> 改成第 0 级（<paramref name="level"/> 为 null）并返回 true。
    /// </summary>
    bool TryGetLevel(Bitmap image, ref ImagePlan plan, out ImageLevel? level, in DrawImageCmd c)
    {
        var key = new ImageKey(image, image.Version, plan.Crop, plan.Level);
        if (_images.TryGet(key, out var hit))
        {
            level = hit;
            return true;
        }
        level = null;
        ImagePlan.LevelSize(plan.Crop, plan.Level, out int lw, out int lh);
        long bytes = (long)lw * lh * 4;
        long limit = LevelBytesPerFrameOverride ?? MaxLevelBytesPerFrame;
        if (_levelBytesThisFrame + bytes > limit)
        {
            _degraded?.Invoke($"More than {limit >> 20} MB of image mip levels built in one frame; the rest of the images were drawn without prefiltering.");
            return ImagePlan.TryCreate(c, image.Width, image.Height, _state.Ctm, 0, out plan, maxLevel: 0);
        }
        long cost = (long)plan.Crop.Width * plan.Crop.Height;
        if (FrameWork + cost > _workBudget)
        {
            _degraded?.Invoke($"Frame exceeded the work limit of {_workBudget} pixel operations while downscaling an image; the image was skipped.");
            return false;
        }
        _rasterizer.Work += cost;
        _levelBytesThisFrame += bytes;
        var pixels = ImagePlan.BuildLevel(image, plan.Crop, plan.Level, out int w, out int h);
        var built = new ImageLevel(pixels, w, h);
        _images.Add(key, built, pixels.Length);
        level = built;
        return true;
    }
}
