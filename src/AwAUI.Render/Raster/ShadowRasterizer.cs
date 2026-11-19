using System;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Render.Raster;

/// <summary>
/// 盒阴影（<see cref="DrawOp.DrawBoxShadow"/>）的覆盖率蒙版：形状覆盖率 → 高斯模糊 → 挖掉 / 乘上原始形状。
/// 软件后端直接用它合成；GPU 后端在解析阴影不适用（旋转、错切、翻转）时用它生成 A8 掩码。单一实现，两边结果一致。
/// <para>
/// 界限：σ 不超过 <see cref="MaxShadowSigma"/>；全分辨率工作区超过 <see cref="MaxShadowWorkPixels"/> 时改为在 1/k 分辨率上
/// 直接光栅化与模糊（内存与 σ 无关）。非有限的偏移、模糊或扩展量使阴影被忽略。
/// </para>
/// </summary>
internal sealed class ShadowRasterizer
{
    /// <summary>σ 的上限（设备像素）。更大的模糊在视觉上已与完全弥散无异；不设上限时外扩量 3σ 会让整数坐标溢出。</summary>
    public const float MaxShadowSigma = 4096;

    /// <summary>全分辨率工作区的像素上限；超过时（大 σ 的巨大外扩）改为直接在低分辨率上光栅化与模糊，内存与 σ 无关。</summary>
    public const long MaxShadowWorkPixels = 4_000_000;

    /// <summary>测试用：覆盖当前线程的工作区像素上限（比较低分辨率路径与全分辨率路径）。</summary>
    [ThreadStatic] internal static long? WorkPixelsOverride;

    readonly Rasterizer _rasterizer;
    readonly PolyList _scratch = new();

    public ShadowRasterizer(Rasterizer rasterizer) => _rasterizer = rasterizer;

    static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>参数是否都是有限值（否则阴影被忽略，调用方上报降级）。</summary>
    public static bool HasFiniteParameters(in BoxShadowCmd c) =>
        IsFinite(c.OffsetX) && IsFinite(c.OffsetY) && IsFinite(c.Blur) && IsFinite(c.Spread);

    /// <summary>设备像素中的高斯 σ（CSS 的模糊半径为 2σ），钳制到 [0, <see cref="MaxShadowSigma"/>]。</summary>
    public static float Sigma(in BoxShadowCmd c, in Matrix3x2 ctm) => Sigma(c, ctm, out _);

    /// <param name="clamped">σ 超过 <see cref="MaxShadowSigma"/> 被钳制（调用方上报降级）。</param>
    public static float Sigma(in BoxShadowCmd c, in Matrix3x2 ctm, out bool clamped)
    {
        float sigma = c.Blur * 0.5f * ctm.AverageScale();
        if (!(sigma >= 0)) sigma = 0;
        clamped = sigma > MaxShadowSigma;
        return MathF.Min(sigma, MaxShadowSigma);
    }

    /// <summary>σ 被钳制时上报的降级信息。</summary>
    public static string SigmaClampedMessage => $"Box shadow blur limited to sigma {MaxShadowSigma} device px.";

    /// <summary>
    /// 计算阴影的最终覆盖率（未乘颜色）。<paramref name="mask"/> 覆盖 <paramref name="maskRect"/>，需要绘制的部分是其中的
    /// <paramref name="outRect"/>（已与 <paramref name="clipBounds"/> 求交）。不可见时返回 false。
    /// <para>
    /// 与裁剪无关：<paramref name="window"/>（软件后端为整个根目标）之内，<paramref name="outRect"/> 中每个像素的值只取决于形状与 σ，
    /// 不取决于 <paramref name="clipBounds"/>。为此：工作区按降采样倍数 k 对齐到绝对设备坐标的网格；外扩量覆盖模糊的全部支撑
    /// （<see cref="Blur.Support"/>，含块平均与双线性放大）；盒式模糊用定点整数求滑动和（与起点无关）；放大时由绝对坐标求插值位置；
    /// 形状覆盖率按扩大后的窗口光栅化；是否走低分辨率路径及其倍数按整个阴影（与窗口求交）而不是裁剪后的工作区决定。
    /// </para>
    /// </summary>
    public bool Compute(in BoxShadowCmd c, in Matrix3x2 ctm, PixelRect clipBounds, PixelRect window, out float[] mask, out PixelRect maskRect,
        out PixelRect outRect, Action<string>? degraded = null)
    {
        mask = Array.Empty<float>();
        maskRect = outRect = default;
        if (clipBounds.IsEmpty || !HasFiniteParameters(c)) return false;
        float sigma = Sigma(c, ctm, out bool clamped);
        if (clamped) degraded?.Invoke(SigmaClampedMessage);
        int margin = (int)MathF.Ceiling(sigma * 3) + 1;
        var offsetShape = new RoundRect(c.Shape.Rect.Offset(c.OffsetX, c.OffsetY),
            c.Shape.TopLeft, c.Shape.TopRight, c.Shape.BottomRight, c.Shape.BottomLeft);
        var box = VectorPath.FromRoundRect(c.Shape);

        VectorPath blurred;
        PixelRect extentBase;
        bool inset = c.Inset != 0;
        if (!inset)
        {
            blurred = VectorPath.FromRoundRect(c.Spread != 0 ? offsetShape.Inflate(c.Spread) : offsetShape);
            extentBase = Inflate(DeviceBounds(blurred, ctm), margin);
        }
        else
        {
            blurred = VectorPath.FromRoundRect(offsetShape.Inflate(-c.Spread));
            extentBase = DeviceBounds(box, ctm);
        }
        outRect = extentBase.Intersect(clipBounds);
        if (outRect.IsEmpty) return false;

        // 降采样倍数与外扩量只取决于 σ（以及整个阴影的大小），与裁剪无关
        int k = Blur.DownsampleFactor(sigma);
        int workMargin = Math.Max(margin, Blur.Support(sigma, k)) + k;
        var extent = Inflate(extentBase, workMargin).Intersect(Inflate(window, workMargin + k));
        bool lowRes = LowResShadow(extent, sigma, out int kl);
        if (lowRes)
        {
            k = kl;
            workMargin = Math.Max(margin, Blur.Support(sigma, k)) + k;
        }
        var rasterWindow = Inflate(window, workMargin + k);
        var work = AlignOut(Inflate(outRect, workMargin), k);
        float[] shapeMask;
        if (lowRes)
        {
            mask = BlurredLowRes(blurred, ctm, work, outRect, sigma, k, inset, rasterWindow);
            maskRect = outRect;
            shapeMask = Coverage(box, ctm, outRect, rasterWindow);
        }
        else
        {
            mask = Coverage(blurred, ctm, work, rasterWindow);
            if (inset)
                for (int i = 0; i < mask.Length; i++) mask[i] = 1 - mask[i];
            Blur.Gaussian(mask, work.Width, work.Height, sigma, work.X, work.Y);
            maskRect = work;
            shapeMask = Coverage(box, ctm, work, rasterWindow);
        }
        if (inset) Kernels.Multiply(mask, shapeMask);
        else Kernels.MultiplyByComplement(mask, shapeMask);
        return true;
    }

    /// <summary>
    /// 整个阴影（<paramref name="extent"/>，与裁剪无关）超过像素上限且 σ 足够大（可降采样）时走低分辨率路径；<paramref name="k"/> 为降采样倍数。
    /// </summary>
    static bool LowResShadow(PixelRect extent, float sigma, out int k)
    {
        // 与 Blur.Gaussian 的降采样倍数相同（σ/k ≥ 2，至多 8），工作区仍太大时再加大倍数，使低分辨率蒙版不超过上限
        k = Blur.DownsampleFactor(sigma);
        long area = extent.IsEmpty ? 0 : (long)extent.Width * extent.Height;
        long budget = WorkPixelsOverride ?? MaxShadowWorkPixels;
        if (k < 2 || area <= budget) return false;
        if (budget > 0) k = Math.Max(k, (int)Math.Ceiling(Math.Sqrt((double)area / budget)));
        // 模糊后的信号在 σ 的尺度上变化：块平均（宽 k 的盒）加双线性放大（宽 k 的三角）只让等效 σ 变为 √(σ² + k²/4)，
        // k ≤ σ/8 时不到 0.2%。大 σ 时按此加大倍数，低分辨率蒙版只有约 (6·8)² 个像素，耗时与内存只与输出区成正比
        k = Math.Max(k, (int)(sigma / 8));
        return true;
    }

    /// <summary>
    /// 在 1/k 分辨率上直接求形状覆盖率（解析覆盖率即块平均，与先全分辨率再降采样相同）、模糊，再只把 <paramref name="outRect"/>
    /// 部分放大回全分辨率。<paramref name="work"/> 对齐到 k 的倍数，低分辨率像素 i 覆盖绝对设备坐标 [i·k, (i+1)·k)。
    /// 内存约为 (工作区/k)² + 输出区。
    /// </summary>
    float[] BlurredLowRes(VectorPath path, in Matrix3x2 ctm, PixelRect work, PixelRect outRect, float sigma, int k, bool invert, PixelRect window)
    {
        var lowRect = new PixelRect(FloorDiv(work.X, k), FloorDiv(work.Y, k), work.Width / k, work.Height / k);
        int lw = lowRect.Width, lh = lowRect.Height;
        var low = new float[lw * lh];
        var toLow = ctm * Matrix3x2.CreateScale(1f / k);
        var lowWindow = PixelRect.FromLTRB(FloorDiv(window.X, k), FloorDiv(window.Y, k), CeilDiv(window.Right, k), CeilDiv(window.Bottom, k));
        _scratch.Clear();
        Flattener.FlattenToPolygons(path, toLow, Flattener.DefaultTolerance / k, _scratch, CullRect.FromPixels(lowWindow, 1));
        _rasterizer.Fill(_scratch, path.FillRule, true, lowRect, lowWindow, new MaskSink(low, lowRect, null));
        if (invert)
            for (int i = 0; i < low.Length; i++) low[i] = 1 - low[i];
        Blur.BoxBlurLow(low, lw, lh, sigma / k);
        var mask = new float[outRect.Width * outRect.Height];
        Blur.UpsampleInto(low, lw, lh, k, lowRect.X, lowRect.Y, outRect.X, outRect.Y, mask, outRect.Width, outRect.Height);
        return mask;
    }

    static PixelRect DeviceBounds(VectorPath path, in Matrix3x2 ctm) => PixelRect.RoundOut(path.Bounds.TransformBounds(ctm));

    static PixelRect Inflate(PixelRect r, int d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    /// <summary>向外对齐到 k 的倍数（绝对设备坐标）。</summary>
    static PixelRect AlignOut(PixelRect r, int k) =>
        k <= 1 ? r : PixelRect.FromLTRB(FloorDiv(r.X, k) * k, FloorDiv(r.Y, k) * k, CeilDiv(r.Right, k) * k, CeilDiv(r.Bottom, k) * k);

    internal static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(int)(((long)-a + b - 1) / b);
    static int CeilDiv(int a, int b) => -FloorDiv(-a, b);

    float[] Coverage(VectorPath path, in Matrix3x2 ctm, PixelRect rect, PixelRect window)
    {
        var mask = new float[rect.Width * rect.Height];
        _scratch.Clear();
        Flattener.FlattenToPolygons(path, ctm, Flattener.DefaultTolerance, _scratch, CullRect.FromPixels(window, 1));
        _rasterizer.Fill(_scratch, path.FillRule, true, rect, window, new MaskSink(mask, rect, null));
        return mask;
    }
}
