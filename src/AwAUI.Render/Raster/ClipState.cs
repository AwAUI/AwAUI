using System;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 裁剪状态：一个像素矩形 + 可选的 alpha 蒙版。不可变，Save/Restore 时按引用保存。
/// 轴对齐、对齐到像素的矩形裁剪只收缩 <see cref="Bounds"/>，不生成蒙版。
/// </summary>
internal sealed class ClipState
{
    public ClipState(PixelRect bounds, float[]? mask = null, PixelRect maskRect = default)
    {
        Bounds = bounds;
        Mask = mask;
        MaskRect = maskRect;
    }

    public PixelRect Bounds { get; }
    public float[]? Mask { get; }
    public PixelRect MaskRect { get; }

    public bool IsEmpty => Bounds.IsEmpty;

    /// <summary>把蒙版乘到一行覆盖率上。</summary>
    public void Apply(int y, int x, Span<float> coverage)
    {
        if (Mask == null) return;
        int offset = (y - MaskRect.Y) * MaskRect.Width + (x - MaskRect.X);
        Kernels.Multiply(coverage, new ReadOnlySpan<float>(Mask, offset, coverage.Length));
    }

    public ClipState WithBounds(PixelRect bounds) => new(Bounds.Intersect(bounds), Mask, MaskRect);
}

/// <summary>把覆盖率写入浮点蒙版（可选乘上已有裁剪）。</summary>
internal sealed class MaskSink : CoverageSink
{
    readonly float[] _mask;
    readonly PixelRect _rect;
    readonly ClipState? _multiplyBy;

    public MaskSink(float[] mask, PixelRect rect, ClipState? multiplyBy)
    {
        _mask = mask; _rect = rect; _multiplyBy = multiplyBy;
    }

    public override void Row(int y, int x, Span<float> coverage)
    {
        _multiplyBy?.Apply(y, x, coverage);
        coverage.CopyTo(new Span<float>(_mask, (y - _rect.Y) * _rect.Width + (x - _rect.X), coverage.Length));
    }
}

/// <summary>
/// 把覆盖率按 <see cref="ScalarKernels.ToByte"/> 量化后写入 A8 缓冲（行距 <c>stride</c>，<c>offset</c> 处对应 <c>rect</c> 的左上角）。
/// GPU 后端用它把路径掩码直接光栅化进图集页的 CPU 影子副本。
/// </summary>
internal sealed class ByteMaskSink : CoverageSink
{
    byte[] _data = Array.Empty<byte>();
    int _offset, _stride;
    PixelRect _rect;

    public ByteMaskSink Reset(byte[] data, int offset, int stride, PixelRect rect)
    {
        _data = data; _offset = offset; _stride = stride; _rect = rect;
        return this;
    }

    public override void Row(int y, int x, Span<float> coverage)
    {
        int o = _offset + (y - _rect.Y) * _stride + (x - _rect.X);
        for (int i = 0; i < coverage.Length; i++) _data[o + i] = ScalarKernels.ToByte(coverage[i]);
    }
}

internal static class Blur
{
    /// <summary>
    /// 近似高斯模糊（就地）：三次盒式模糊，每像素开销与 σ 无关。
    /// 盒宽按 σ 选取（W. M. Wells, 1986；三次盒式卷积与高斯的偏差小于 3%）。边界按钳制处理。
    /// </summary>
    public static void Gaussian(float[] data, int width, int height, float sigma, int originX = 0, int originY = 0)
    {
        if (sigma < 0.3f || width <= 0 || height <= 0) return;
        // σ 较大时结果很平滑：降采样 k 倍后模糊（σ/k ≥ 2），再双线性放大，开销降为约 1/k²
        int k = DownsampleFactor(sigma);
        if (k >= 2)
        {
            Downsampled(data, width, height, sigma, k, originX, originY);
            return;
        }
        BoxBlur(data, width, height, sigma);
    }

    /// <summary><see cref="Gaussian"/> 的降采样倍数（σ/k ≥ 2，至多 8）；小于 2 时为 1（不降采样）。</summary>
    public static int DownsampleFactor(float sigma)
    {
        int k = Math.Min(8, (int)(sigma / 2));
        return k >= 2 ? k : 1;
    }

    /// <summary>
    /// 模糊结果在一个像素处依赖的输入范围（设备像素，单侧）：三次盒式模糊的半径之和；降采样 k 倍时再加上块平均与双线性放大用到的
    /// 两个低分辨率像素。工作区边界离输出区至少这么远时，边界处的钳制不影响输出。
    /// </summary>
    public static int Support(float sigma, int k)
    {
        if (sigma < 0.3f) return 0;
        Span<int> boxes = stackalloc int[3];
        BoxSizes(k > 1 ? sigma / k : sigma, boxes);
        int r = 0;
        foreach (var size in boxes) r += Math.Max(0, (size - 1) / 2);
        return k > 1 ? k * (r + 2) : r;
    }

    /// <summary>
    /// 降采样路径。<paramref name="originX"/>、<paramref name="originY"/> 是数据左上角的绝对设备坐标（调用方保证是 k 的倍数，宽高也是，
    /// 否则只是退化为与起点相关）：块网格与放大时的插值位置都由绝对坐标决定，所以结果与工作区从哪里开始无关。
    /// </summary>
    static void Downsampled(float[] data, int width, int height, float sigma, int k, int originX, int originY)
    {
        int lw = (width + k - 1) / k, lh = (height + k - 1) / k;
        var low = new float[lw * lh];
        // 每块按行优先顺序求和；完整的块 4 个一组同时累加（各块自己的加法顺序不变，只是隐藏加法延迟）
        int fullBlocks = width / k;
        for (int ly = 0; ly < lh; ly++)
        {
            int y0 = ly * k, y1 = Math.Min(height, y0 + k);
            int lx = 0;
            for (; lx + 4 <= fullBlocks; lx += 4)
            {
                float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int y = y0; y < y1; y++)
                {
                    int o = y * width + lx * k;
                    for (int x = 0; x < k; x++)
                    {
                        s0 += data[o + x];
                        s1 += data[o + k + x];
                        s2 += data[o + 2 * k + x];
                        s3 += data[o + 3 * k + x];
                    }
                }
                int count = (y1 - y0) * k;
                int lo = ly * lw + lx;
                low[lo] = s0 / count;
                low[lo + 1] = s1 / count;
                low[lo + 2] = s2 / count;
                low[lo + 3] = s3 / count;
            }
            for (; lx < lw; lx++)
            {
                float sum = 0;
                int x0 = lx * k, x1 = Math.Min(width, x0 + k);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) sum += data[y * width + x];
                low[ly * lw + lx] = sum / ((y1 - y0) * (x1 - x0));
            }
        }
        BoxBlur(low, lw, lh, sigma / k);
        UpsampleInto(low, lw, lh, k, ShadowRasterizer.FloorDiv(originX, k), ShadowRasterizer.FloorDiv(originY, k), originX, originY, data, width, height);
    }

    /// <summary>对低分辨率蒙版直接做三次盒式模糊（σ 以低分辨率像素计）。</summary>
    internal static void BoxBlurLow(float[] data, int width, int height, float sigma)
    {
        if (sigma < 0.3f || width <= 0 || height <= 0) return;
        BoxBlur(data, width, height, sigma);
    }

    /// <summary>
    /// 把低分辨率蒙版（每像素覆盖 <paramref name="k"/>×<paramref name="k"/> 个设备像素；低分辨率像素 i 覆盖绝对设备坐标 [i·k, (i+1)·k)，
    /// 数组左上角为低分辨率坐标 (<paramref name="lowX"/>, <paramref name="lowY"/>)）双线性放大到绝对设备坐标
    /// (<paramref name="destX"/>, <paramref name="destY"/>) 起的 <paramref name="width"/>×<paramref name="height"/> 区域。
    /// 插值位置 (X + 0.5)/k − 0.5 由绝对坐标 X 求得，结果与目标区域从哪里开始无关。
    /// </summary>
    internal static void UpsampleInto(float[] low, int lw, int lh, int k, int lowX, int lowY, int destX, int destY, float[] dest, int width, int height)
    {
        float inv = 1f / k;
        var xa = new int[width];
        var xb = new int[width];
        var tx = new float[width];
        for (int x = 0; x < width; x++)
        {
            float fx = (destX + x + 0.5f) * inv - 0.5f;
            float fl = MathF.Floor(fx);
            tx[x] = fx - fl;
            int x0 = (int)fl - lowX;
            xa[x] = Math.Max(0, Math.Min(lw - 1, x0));
            xb[x] = Math.Max(0, Math.Min(lw - 1, x0 + 1));
        }
        var rows = new UpsampleRows(low, lw, xa, xb, tx);
        for (int y = 0; y < height; y++)
        {
            float fy = (destY + y + 0.5f) * inv - 0.5f;
            float fl = MathF.Floor(fy);
            float ty = fy - fl;
            int y0 = (int)fl - lowY;
            int ya = Math.Max(0, Math.Min(lh - 1, y0)), yb = Math.Max(0, Math.Min(lh - 1, y0 + 1));
            var top = rows.Get(ya, keep: -1);
            var bottom = rows.Get(yb, keep: ya);
            Kernels.Lerp(top, bottom, ty, new Span<float>(dest, y * width, width));
        }
    }

    /// <summary>水平插值后的低分辨率行，缓存最近两行（放大时行号单调不减）。</summary>
    sealed class UpsampleRows
    {
        readonly float[] _low;
        readonly int _lw;
        readonly int[] _xa, _xb;
        readonly float[] _tx;
        readonly float[][] _rows;
        readonly int[] _index = { -1, -1 };

        public UpsampleRows(float[] low, int lw, int[] xa, int[] xb, float[] tx)
        {
            _low = low; _lw = lw; _xa = xa; _xb = xb; _tx = tx;
            _rows = new[] { new float[tx.Length], new float[tx.Length] };
        }

        public float[] Get(int ly, int keep)
        {
            if (_index[0] == ly) return _rows[0];
            if (_index[1] == ly) return _rows[1];
            int slot = _index[0] == keep ? 1 : 0;
            var row = _rows[slot];
            int o = ly * _lw;
            for (int x = 0; x < row.Length; x++)
            {
                float a = _low[o + _xa[x]];
                row[x] = a + (_low[o + _xb[x]] - a) * _tx[x];
            }
            _index[slot] = ly;
            return row;
        }
    }

    /// <summary>定点刻度：盒式模糊在 2^20 的整数刻度上求滑动和。整数加法满足结合律，每个输出只取决于它的窗口，与行从哪里开始无关。</summary>
    const float FixedScale = 1 << 20;

    static void BoxBlur(float[] data, int width, int height, float sigma)
    {
        Span<int> boxes = stackalloc int[3];
        BoxSizes(sigma, boxes);
        int n = width * height;
        var q = new int[n];
        for (int i = 0; i < n; i++)
        {
            float v = data[i];
            // 覆盖率在 [0, 1]；NaN 与越界值钳制（定点值不超过 2^20，求和不溢出）
            q[i] = !(v > 0) ? 0 : v >= 1 ? (int)FixedScale : (int)(v * FixedScale + 0.5f);
        }
        var tmp = new int[Math.Max(width, height)];
        var line = new int[Math.Max(width, height)];
        foreach (var size in boxes)
        {
            int r = (size - 1) / 2;
            // 水平
            for (int y = 0; y < height; y++)
            {
                Array.Copy(q, y * width, line, 0, width);
                BoxLine(line, tmp, width, r);
                Array.Copy(tmp, 0, q, y * width, width);
            }
            // 垂直
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++) line[y] = q[y * width + x];
                BoxLine(line, tmp, height, r);
                for (int y = 0; y < height; y++) q[y * width + x] = tmp[y];
            }
        }
        const float inv = 1 / FixedScale;
        for (int i = 0; i < n; i++) data[i] = q[i] * inv;
    }

    static void BoxSizes(float sigma, Span<int> sizes)
    {
        int n = sizes.Length;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        for (int i = 0; i < n; i++) sizes[i] = i < m ? wl : wu;
    }

    /// <summary>
    /// 半径为 r 的滑动平均（窗口 2r+1），边界钳制。整数滑动和是精确的；平均值按 ⌊和 / (2r+1) + 0.5⌋ 取整，只取决于和本身。
    /// </summary>
    static void BoxLine(int[] src, int[] dst, int n, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, n); return; }
        double inv = 1.0 / (2 * r + 1);
        long first = src[0], last = src[n - 1];
        long sum = (r + 1) * first;
        for (int i = 0; i < r; i++) sum += src[Math.Min(i, n - 1)];
        for (int i = 0; i < n; i++)
        {
            int add = i + r, sub = i - r - 1;
            sum += add < n ? src[add] : last;
            sum -= sub >= 0 ? src[sub] : first;
            dst[i] = (int)Math.Floor(sum * inv + 0.5);
        }
    }
}
