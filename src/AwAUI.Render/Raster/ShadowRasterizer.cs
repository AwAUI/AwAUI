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

    // ---------------------------------------------------------------- 结果缓存

    /// <summary>
    /// 缓存的蒙版总量上限（浮点数个数，约 4 MB）。超过上限的单个蒙版不缓存；放不下时淘汰最久未用的项。
    /// 缓存的蒙版由多次 <see cref="Compute"/> 共享，调用方只读。
    /// </summary>
    internal const int MaxCachedFloats = 1 << 20;
    const int MaxCacheEntries = 16;

    readonly struct CacheKey : IEquatable<CacheKey>
    {
        readonly RoundRect _shape;
        readonly float _offsetX, _offsetY, _blur, _spread;
        readonly int _inset;
        readonly Matrix3x2 _ctm;
        readonly PixelRect _window;
        readonly long _workPixels;

        public CacheKey(in BoxShadowCmd c, in Matrix3x2 ctm, PixelRect window, long workPixels)
        {
            _shape = c.Shape;
            _offsetX = c.OffsetX; _offsetY = c.OffsetY; _blur = c.Blur; _spread = c.Spread;
            _inset = c.Inset;
            _ctm = ctm;
            _window = window;
            _workPixels = workPixels;
        }

        public bool Equals(CacheKey o) =>
            _shape.Equals(o._shape) && _offsetX == o._offsetX && _offsetY == o._offsetY && _blur == o._blur && _spread == o._spread
            && _inset == o._inset && _ctm == o._ctm && _window.Equals(o._window) && _workPixels == o._workPixels;

        public override bool Equals(object? obj) => obj is CacheKey o && Equals(o);
        public override int GetHashCode() => _shape.GetHashCode() ^ _blur.GetHashCode() ^ _ctm.GetHashCode();
    }

    sealed class CacheEntry
    {
        public CacheKey Key;
        public float[] Mask = Array.Empty<float>();
        public PixelRect MaskRect, OutRect;
        public long LastUsed;
    }

    readonly System.Collections.Generic.List<CacheEntry> _cache = new();
    long _cacheClock;
    int _cachedFloats;

    /// <summary>缓存命中次数（测试与诊断）。</summary>
    internal long CacheHits { get; private set; }

    /// <summary>当前缓存的蒙版总量（浮点数个数；测试与诊断）。</summary>
    internal int CachedFloats => _cachedFloats;

    bool TryGetCached(in CacheKey key, PixelRect outRect, out float[] mask, out PixelRect maskRect)
    {
        foreach (var e in _cache)
        {
            if (!e.Key.Equals(key)) continue;
            var o = e.OutRect;
            if (outRect.X < o.X || outRect.Y < o.Y || outRect.Right > o.Right || outRect.Bottom > o.Bottom) break;
            e.LastUsed = ++_cacheClock;
            CacheHits++;
            mask = e.Mask;
            maskRect = e.MaskRect;
            return true;
        }
        mask = Array.Empty<float>();
        maskRect = default;
        return false;
    }

    void Store(in CacheKey key, float[] mask, PixelRect maskRect, PixelRect outRect)
    {
        // 同一个阴影的旧结果（输出区较小，例如上次只重画了一部分）被新结果取代
        for (int i = _cache.Count - 1; i >= 0; i--)
            if (_cache[i].Key.Equals(key)) Evict(i);
        if (mask.Length > MaxCachedFloats) return;
        while (_cache.Count > 0 && (_cachedFloats + mask.Length > MaxCachedFloats || _cache.Count >= MaxCacheEntries))
        {
            int oldest = 0;
            for (int i = 1; i < _cache.Count; i++)
                if (_cache[i].LastUsed < _cache[oldest].LastUsed) oldest = i;
            Evict(oldest);
        }
        _cache.Add(new CacheEntry { Key = key, Mask = mask, MaskRect = maskRect, OutRect = outRect, LastUsed = ++_cacheClock });
        _cachedFloats += mask.Length;
    }

    void Evict(int index)
    {
        _cachedFloats -= _cache[index].Mask.Length;
        _cache.RemoveAt(index);
    }

    /// <summary>把形状覆盖率的补（1 − 覆盖率）逐行乘进蒙版。</summary>
    sealed class ComplementSink : CoverageSink
    {
        float[] _mask = Array.Empty<float>();
        PixelRect _rect;

        public ComplementSink Reset(float[] mask, PixelRect rect)
        {
            _mask = mask; _rect = rect;
            return this;
        }

        public override void Row(int y, int x, Span<float> coverage) =>
            Kernels.MultiplyByComplement(_mask.AsSpan((y - _rect.Y) * _rect.Width + (x - _rect.X), coverage.Length), coverage);
    }

    readonly ComplementSink _complement = new();

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
        // 相同几何的阴影（界面的大多数重绘：文字、悬停状态变了，卡片没动）直接复用上次的结果。
        // 蒙版与裁剪无关，所以裁剪后的输出区只要落在缓存项的输出区之内就可以用。
        var key = new CacheKey(c, ctm, window, WorkPixelsOverride ?? MaxShadowWorkPixels);
        if (TryGetCached(key, outRect, out mask, out maskRect)) return true;

        var rasterWindow = Inflate(window, workMargin + k);
        var work = AlignOut(Inflate(outRect, workMargin), k);
        if (lowRes)
        {
            mask = BlurredLowRes(blurred, ctm, work, outRect, sigma, k, inset, rasterWindow);
            maskRect = outRect;
        }
        else
        {
            mask = Coverage(blurred, ctm, work, rasterWindow);
            if (inset)
                for (int i = 0; i < mask.Length; i++) mask[i] = 1 - mask[i];
            Blur.Gaussian(mask, work.Width, work.Height, sigma, work.X, work.Y);
            maskRect = work;
        }
        if (inset)
        {
            // 内阴影：乘上形状覆盖率，形状之外必须变成 0，需要完整的形状蒙版
            Kernels.Multiply(mask, Coverage(box, ctm, maskRect, rasterWindow, scratch: true));
            if (_shape.Length > MaxRetainedScratch) _shape = Array.Empty<float>();
        }
        else
        {
            // 外阴影：挖掉形状。光栅器没有送来的像素覆盖率为 0，乘 (1 - 0) 不变，
            // 所以逐行直接乘进蒙版即可，不必再分配一份与工作区等大的形状蒙版
            _scratch.Clear();
            Flattener.FlattenToPolygons(box, ctm, Flattener.DefaultTolerance, _scratch, CullRect.FromPixels(rasterWindow, 1));
            _rasterizer.Fill(_scratch, box.FillRule, true, maskRect, rasterWindow, _complement.Reset(mask, maskRect));
        }
        Store(key, mask, maskRect, outRect);
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

    // 形状自身的覆盖率只用来与模糊结果相乘，用完即弃：复用同一块缓冲，避免每个阴影多分配一份与工作区等大的数组。
    // 超过上限（约 1 MB）的缓冲不保留，免得一次大阴影让进程长期多占内存。
    float[] _shape = Array.Empty<float>();
    const int MaxRetainedScratch = 1 << 18;

    /// <summary>
    /// 路径在 <paramref name="rect"/> 内的覆盖率。<paramref name="scratch"/> 为 true 时返回复用的缓冲
    /// （长度可能大于 <c>rect</c> 的像素数，只有前面的部分有效，下次调用前有效）。
    /// </summary>
    float[] Coverage(VectorPath path, in Matrix3x2 ctm, PixelRect rect, PixelRect window, bool scratch = false)
    {
        int n = rect.Width * rect.Height;
        float[] mask;
        if (!scratch) mask = new float[n];
        else if (_shape.Length < n) mask = _shape = new float[n];
        else
        {
            mask = _shape;
            Array.Clear(mask, 0, n);
        }
        _scratch.Clear();
        Flattener.FlattenToPolygons(path, ctm, Flattener.DefaultTolerance, _scratch, CullRect.FromPixels(window, 1));
        _rasterizer.Fill(_scratch, path.FillRule, true, rect, window, new MaskSink(mask, rect, null));
        return mask;
    }
}
