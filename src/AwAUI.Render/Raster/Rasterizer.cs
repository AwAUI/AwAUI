using System;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>接收一行覆盖率（0..1）。x 为该行第一个像素的设备坐标。</summary>
internal abstract class CoverageSink
{
    public abstract void Row(int y, int x, Span<float> coverage);
}

/// <summary>
/// 解析式抗锯齿的多边形光栅器：对每条边把有符号面积累积到单元格，再按行前缀求和得到覆盖率
/// （font-rs / tiny-skia 的思路）。结果与采样方式无关；支持非零与奇偶规则。
/// 按水平条带处理，内存占用与形状高度无关。
/// </summary>
internal sealed class Rasterizer
{
    const int BandHeight = 16;

    float[] _acc = Array.Empty<float>();
    float[] _row = Array.Empty<float>();
    int[] _rowMin = new int[BandHeight];
    int[] _rowMax = new int[BandHeight];
    int _stride;
    int _width;
    int _bandTop;

    /// <summary>
    /// 累计的工作量：每次填充加上 区域面积 + 点数 × 条带数（按条带扫描全部边、逐行累加）。调用方按帧清零，
    /// 用于每帧的工作量上限（见 <see cref="RasterContext.MaxWorkPerFrame"/>）。
    /// </summary>
    public long Work;

    /// <summary>在 <paramref name="clip"/> 内光栅化（窗口即裁剪区，见下面的重载）。</summary>
    public void Fill(PolyList poly, FillRule rule, bool antiAlias, PixelRect clip, CoverageSink sink) =>
        Fill(poly, rule, antiAlias, clip, clip, sink);

    /// <summary>
    /// 光栅化多边形，只把 <paramref name="clip"/> 内的覆盖率交给 <paramref name="sink"/>（每行一段，不超出 clip）。
    /// <para>
    /// 与裁剪无关：浮点累积的坐标系（列原点、在左右边界处切分边、条带的起点）只由「形状的设备包围盒 ∩ <paramref name="window"/>」决定，
    /// clip 只决定输出哪些行与列。所以只要 window 与几何相同，clip 内每个像素的覆盖率逐位相同，不论 clip 是整个目标还是一个损伤矩形
    /// （软件后端传整个根目标作为 window）。代价是每行要从窗口内形状的左边界开始累积（至多窗口宽度），而不是从 clip 的左边界开始。
    /// window 外的几何按贴边处理（对面积累积而言等价），所以 window 必须包含 clip 内像素依赖的全部几何（clip ⊆ window 即可）。
    /// </para>
    /// </summary>
    public void Fill(PolyList poly, FillRule rule, bool antiAlias, PixelRect clip, PixelRect window, CoverageSink sink)
    {
        if (!poly.TryGetBounds(out float l, out float t, out float r, out float b)) return;
        if (float.IsNaN(l) || float.IsNaN(r) || float.IsNaN(t) || float.IsNaN(b)) return;
        var win = PixelRect.RoundOut(Rect.FromLTRB(l, t, r, b)).Intersect(window);
        var area = win.Intersect(clip);
        if (area.IsEmpty) return;

        // 输出列（相对 win.X）：只需算到 clip 的右边界，左侧必须从窗口内形状的左边界开始累积
        int outFrom = area.X - win.X, outTo = area.Right - win.X;
        // 条带对齐到 win.Y + 16k（与 clip 无关）
        int firstBand = win.Y + (area.Y - win.Y) / BandHeight * BandHeight;
        int bands = (area.Bottom - firstBand + BandHeight - 1) / BandHeight;
        Work += (long)outTo * (area.Bottom - firstBand) + (long)poly.PointCount * bands;
        _width = win.Width;
        _stride = _width + 2;
        EnsureBuffers(_stride * BandHeight, _width);

        bool nonZero = rule == FillRule.NonZero;
        for (int bandTop = firstBand; bandTop < area.Bottom; bandTop += BandHeight)
        {
            int bandBottom = Math.Min(bandTop + BandHeight, win.Bottom);
            int rows = bandBottom - bandTop;
            _bandTop = bandTop;
            Array.Clear(_acc, 0, _stride * rows);
            for (int i = 0; i < rows; i++) { _rowMin[i] = int.MaxValue; _rowMax[i] = int.MinValue; }

            // 只处理与本条带相交的边
            for (int c = 0; c < poly.ContourCount; c++)
            {
                var contour = poly.Contour(c);
                int n = contour.Length;
                if (n < 2) continue;
                for (int i = 0; i < n; i++)
                {
                    var p0 = contour[i];
                    var p1 = contour[i + 1 == n ? 0 : i + 1];
                    if (Math.Max(p0.Y, p1.Y) <= bandTop || Math.Min(p0.Y, p1.Y) >= bandBottom) continue;
                    AddLine(p0.X - win.X, p0.Y, p1.X - win.X, p1.Y, bandTop, bandBottom);
                }
            }

            int rowFrom = Math.Max(bandTop, area.Y), rowTo = Math.Min(bandBottom, area.Bottom);
            for (int y = rowFrom; y < rowTo; y++)
            {
                int i = y - bandTop;
                if (_rowMin[i] > _rowMax[i]) continue;
                int from = Math.Max(0, _rowMin[i]);
                int to = Math.Min(Math.Min(_width, _rowMax[i] + 1), outTo);   // 最后一个被写入的单元之后累积值回到 0
                if (to <= outFrom || from >= to) continue;
                int baseIdx = i * _stride;
                var row = new Span<float>(_row, 0, to - from);
                bool any = Kernels.AccumulateCoverage(new ReadOnlySpan<float>(_acc, baseIdx + from, to - from), 0, row, nonZero, antiAlias);
                if (!any) continue;
                int start = Math.Max(from, outFrom);
                sink.Row(y, win.X + start, row.Slice(start - from));
            }
        }
    }

    void EnsureBuffers(int accSize, int width)
    {
        if (_acc.Length < accSize) _acc = new float[accSize];
        if (_row.Length < width + 2) _row = new float[width + 2];
    }

    /// <summary>在 x = 0 与 x = width 处切分，区域外的部分贴到边界上（对面积累积而言等价）。</summary>
    void AddLine(float x0, float y0, float x1, float y1, int bandTop, int bandBottom)
    {
        if (y0 == y1) return;
        float w = _width;
        Span<float> ts = stackalloc float[4];
        int count = 0;
        ts[count++] = 0;
        if (x0 != x1)
        {
            float tA = (0 - x0) / (x1 - x0);
            float tB = (w - x0) / (x1 - x0);
            if (tA > 0 && tA < 1) ts[count++] = tA;
            if (tB > 0 && tB < 1) ts[count++] = tB;
        }
        ts[count++] = 1;
        if (count == 4 && ts[1] > ts[2]) (ts[1], ts[2]) = (ts[2], ts[1]);

        for (int i = 0; i + 1 < count; i++)
        {
            float ta = ts[i], tb = ts[i + 1];
            float ax = x0 + (x1 - x0) * ta, ay = y0 + (y1 - y0) * ta;
            float bx = x0 + (x1 - x0) * tb, by = y0 + (y1 - y0) * tb;
            if (i == 0) { ax = x0; ay = y0; }
            if (i + 1 == count - 1) { bx = x1; by = y1; }
            DrawLine(Clamp(ax, w), ay, Clamp(bx, w), by, bandTop, bandBottom);
        }
    }

    static float Clamp(float v, float max) => v < 0 ? 0 : v > max ? max : v;

    void DrawLine(float x0, float y0, float x1, float y1, int bandTop, int bandBottom)
    {
        if (y0 == y1) return;
        float dir = 1;
        if (y0 > y1)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            dir = -1;
        }
        float ys = Math.Max(y0, bandTop), ye = Math.Min(y1, bandBottom);
        if (ys >= ye) return;

        float dxdy = (x1 - x0) / (y1 - y0);
        float x = x0 + (ys - y0) * dxdy;
        float maxX = _width;
        // 钳制到 [0, 宽度]，NaN 也落到 0：几乎水平的边 dxdy 可以是无穷大，ys == y0 时 0·∞ = NaN；
        // 正常输入的舍入也可能给出略小于 0 或略大于宽度的 x。两者都会让下面的单元下标越界。
        if (!(x >= 0)) x = 0; else if (x > maxX) x = maxX;
        int yStart = (int)MathF.Floor(ys);
        int yEnd = (int)MathF.Ceiling(ye);

        for (int y = yStart; y < yEnd; y++)
        {
            float top = Math.Max(y, ys);
            float bottom = Math.Min(y + 1, ye);
            float dy = bottom - top;
            if (dy <= 0) continue;
            float xnext = x + dxdy * dy;
            if (!(xnext >= 0)) xnext = 0; else if (xnext > maxX) xnext = maxX;
            float d = dy * dir;
            int ri = y - _bandTop;
            int line = ri * _stride;

            float xa = Math.Min(x, xnext), xb = Math.Max(x, xnext);
            float xaFloor = MathF.Floor(xa);
            int xai = (int)xaFloor;
            int xbi = (int)MathF.Ceiling(xb);

            if (xbi <= xai + 1)
            {
                // 整段落在一个像素列内：按梯形中点分配
                float xmf = 0.5f * (x + xnext) - xaFloor;
                _acc[line + xai] += d - d * xmf;
                _acc[line + xai + 1] += d * xmf;
                Touch(ri, xai, xai + 1);
            }
            else
            {
                float s = 1 / (xb - xa);
                float xaf = xa - xaFloor;
                float a0 = 0.5f * s * (1 - xaf) * (1 - xaf);
                float xbf = xb - xbi + 1;
                float am = 0.5f * s * xbf * xbf;
                _acc[line + xai] += d * a0;
                if (xbi == xai + 2)
                    _acc[line + xai + 1] += d * (1 - a0 - am);
                else
                {
                    float a1 = s * (1.5f - xaf);
                    _acc[line + xai + 1] += d * (a1 - a0);
                    for (int xi = xai + 2; xi < xbi - 1; xi++) _acc[line + xi] += d * s;
                    float a2 = a1 + (xbi - xai - 3) * s;
                    _acc[line + xbi - 1] += d * (1 - a2 - am);
                }
                _acc[line + xbi] += d * am;
                Touch(ri, xai, xbi);
            }
            x = xnext;
        }
    }

    void Touch(int row, int min, int max)
    {
        if (min < _rowMin[row]) _rowMin[row] = min;
        if (max > _rowMax[row]) _rowMax[row] = max;
    }
}
