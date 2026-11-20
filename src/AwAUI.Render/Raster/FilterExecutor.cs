using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 在 CPU 上执行 <see cref="FilterPlan"/>（软件后端的滤镜图层与背景滤镜）。中间图像是 8 位预乘 RGBA（通道顺序 R、G、B、A，与表面格式无关），
/// 每一步按浮点计算、用 <see cref="ScalarKernels.ToByte"/> 量化，与 GPU 后端写入 8 位纹理的结果只差舍入。
/// 一个实例同一时间只服务一次渲染。
/// </summary>
internal sealed class FilterExecutor
{
    readonly List<FilterImage> _images = new();
    readonly List<FilterPass> _passes = new();
    byte[]?[] _buffers = new byte[]?[8];

    /// <summary>
    /// 执行 <paramref name="filter"/>：输入图像是设备矩形 <paramref name="input"/>（与 <paramref name="limit"/> 求交），像素取自
    /// <paramref name="source"/>（预乘，表面格式，左上角在设备坐标 <paramref name="sourceRect"/> 处；之外为透明黑）。
    /// 返回结果位图（表面格式、预乘）及其设备矩形；工作量（像素 × 采样数）加到 <paramref name="work"/>，加上之后超出 <paramref name="budget"/> 时
    /// 不执行并返回 null。结果为空时也返回 null（<paramref name="resultRect"/> 为空）。
    /// </summary>
    public Bitmap? Run(Filter filter, in Matrix3x2 ctm, Bitmap source, PixelRect sourceRect, PixelRect input, PixelRect limit,
        Action<string>? degraded, ref long work, long budget, out PixelRect resultRect)
    {
        resultRect = default;
        int last = FilterPlan.Build(filter, ctm, input.Intersect(limit), limit, _images, _passes, degraded);
        long cost = PlanCost();
        if (work + cost > budget)
        {
            work += cost;
            return null;
        }
        work += cost;
        if (_buffers.Length < _images.Count) Array.Resize(ref _buffers, _images.Count);
        try
        {
            var src = _images[0].Rect;
            _buffers[0] = Load(source, sourceRect, src);
            // 每张中间图像最后一次被读取的步骤：之后立即归还
            var lastUse = new int[_images.Count];
            for (int i = 0; i < _passes.Count; i++)
            {
                lastUse[_passes[i].Input] = i;
                if (_passes[i].Input2 >= 0) lastUse[_passes[i].Input2] = i;
            }
            for (int i = 0; i < _passes.Count; i++)
            {
                var p = _passes[i];
                var outRect = _images[p.Output].Rect;
                var buffer = Rent(outRect);
                if (!outRect.IsEmpty) Execute(p, buffer, outRect);
                _buffers[p.Output] = buffer;
                if (lastUse[p.Input] == i && p.Input != last) Return(p.Input);
                if (p.Input2 >= 0 && lastUse[p.Input2] == i && p.Input2 != last) Return(p.Input2);
            }
            resultRect = _images[last].Rect;
            if (resultRect.IsEmpty) return null;
            return Store(_buffers[last]!, resultRect, source.Format);
        }
        finally
        {
            for (int i = 0; i < _images.Count; i++) Return(i);
        }
    }

    long Area(int image) => (long)_images[image].Rect.Width * _images[image].Rect.Height;

    /// <summary>
    /// 计划的总工作量：输出像素 × 每像素的读取次数（模糊为核宽度、降采样为 k²）× 4（每次读取是四个通道的浮点运算，
    /// 比光栅化的一个单元贵得多，按通道计入 <see cref="RasterContext.MaxWorkPerFrame"/>）。
    /// </summary>
    public long PlanCost()
    {
        long cost = 0;
        foreach (var p in _passes)
        {
            long area = Area(p.Output);
            long taps = p.Kind switch
            {
                FilterPassKind.BlurX or FilterPassKind.BlurY => 2 * p.Radius + 1,
                FilterPassKind.Downsample => (long)p.Scale * p.Scale,
                FilterPassKind.Upsample => 4,
                FilterPassKind.DropShadow => 2,
                _ => 1,
            };
            cost += area * taps * 4;
        }
        return cost;
    }

    byte[] Rent(PixelRect r)
    {
        long n = (long)Math.Max(0, r.Width) * Math.Max(0, r.Height) * 4;
        var b = ArrayPool<byte>.Shared.Rent((int)Math.Max(4, n));
        Array.Clear(b, 0, (int)n);
        return b;
    }

    void Return(int index)
    {
        if (index >= _buffers.Length) return;
        var b = _buffers[index];
        if (b is null) return;
        _buffers[index] = null;
        ArrayPool<byte>.Shared.Return(b);
    }

    /// <summary>把表面位图中 <paramref name="rect"/> 的像素复制为 RGBA 顺序（源之外为 0）。</summary>
    byte[] Load(Bitmap source, PixelRect sourceRect, PixelRect rect)
    {
        var buf = Rent(rect);
        bool bgra = source.Format == PixelFormat.Bgra8888;
        var overlap = rect.Intersect(sourceRect);
        for (int y = overlap.Y; y < overlap.Bottom; y++)
        {
            var row = source.GetRow(y - sourceRect.Y);
            int so = (overlap.X - sourceRect.X) * 4;
            int d = ((y - rect.Y) * rect.Width + (overlap.X - rect.X)) * 4;
            for (int x = 0; x < overlap.Width; x++, so += 4, d += 4)
            {
                if (bgra)
                {
                    buf[d] = row[so + 2]; buf[d + 1] = row[so + 1]; buf[d + 2] = row[so]; buf[d + 3] = row[so + 3];
                }
                else
                {
                    buf[d] = row[so]; buf[d + 1] = row[so + 1]; buf[d + 2] = row[so + 2]; buf[d + 3] = row[so + 3];
                }
            }
        }
        return buf;
    }

    static Bitmap Store(byte[] buf, PixelRect rect, PixelFormat format)
    {
        var bmp = new Bitmap(rect.Size, format);
        bool bgra = format == PixelFormat.Bgra8888;
        for (int y = 0; y < rect.Height; y++)
        {
            var row = bmp.GetRow(y);
            int s = y * rect.Width * 4;
            for (int x = 0; x < rect.Width; x++, s += 4)
            {
                int d = x * 4;
                if (bgra)
                {
                    row[d] = buf[s + 2]; row[d + 1] = buf[s + 1]; row[d + 2] = buf[s]; row[d + 3] = buf[s + 3];
                }
                else
                {
                    row[d] = buf[s]; row[d + 1] = buf[s + 1]; row[d + 2] = buf[s + 2]; row[d + 3] = buf[s + 3];
                }
            }
        }
        return bmp;
    }

    // ---------------------------------------------------------------- 各步

    void Execute(in FilterPass p, byte[] dst, PixelRect outRect)
    {
        var inRect = _images[p.Input].Rect;
        var src = _buffers[p.Input]!;
        switch (p.Kind)
        {
            case FilterPassKind.ColorMatrix: ColorMatrix(src, inRect, dst, outRect, p.Matrix!); break;
            case FilterPassKind.Downsample: Downsample(src, inRect, dst, outRect, p.Scale); break;
            case FilterPassKind.BlurX: BlurX(src, inRect, dst, outRect, p.Sigma, p.Radius); break;
            case FilterPassKind.BlurY: BlurY(src, inRect, dst, outRect, p.Sigma, p.Radius); break;
            case FilterPassKind.Upsample: Upsample(src, inRect, dst, outRect, p.Scale); break;
            case FilterPassKind.DropShadow:
                DropShadow(src, inRect, _buffers[p.Input2]!, _images[p.Input2].Rect, dst, outRect, p.Color, p.Dx, p.Dy);
                break;
        }
    }

    const float K = 1 / 255f;

    Vector4[] _rowA = new Vector4[256];
    Vector4[] _rowB = new Vector4[256];
    Vector4[] _acc = new Vector4[256];

    static Vector4[] Ensure(ref Vector4[] row, int n)
    {
        if (row.Length < n) row = new Vector4[Math.Max(n, row.Length * 2)];
        return row;
    }

    /// <summary>
    /// 把图像第 <paramref name="y"/> 行中 [x0, x0 + n) 的像素转成浮点（范围外为透明黑）写入 <paramref name="dst"/>。
    /// 与逐像素读取的结果相同（每个通道 = 字节 × 1/255），只是不再逐像素检查范围。
    /// </summary>
    static void LoadRow(byte[] buf, PixelRect r, int y, int x0, int n, Vector4[] dst)
    {
        var span = new Span<Vector4>(dst, 0, n);
        if ((uint)(y - r.Y) >= (uint)r.Height)
        {
            span.Clear();
            return;
        }
        int a = Math.Max(x0, r.X), b = Math.Min(x0 + n, r.Right);
        if (b <= a)
        {
            span.Clear();
            return;
        }
        span.Slice(0, a - x0).Clear();
        span.Slice(b - x0).Clear();
        int o = ((y - r.Y) * r.Width + (a - r.X)) * 4;
        for (int x = a - x0, end = b - x0; x < end; x++, o += 4)
            dst[x] = new Vector4(buf[o] * K, buf[o + 1] * K, buf[o + 2] * K, buf[o + 3] * K);
    }

    static void Write(byte[] buf, int o, Vector4 v)
    {
        buf[o] = ScalarKernels.ToByte(v.X);
        buf[o + 1] = ScalarKernels.ToByte(v.Y);
        buf[o + 2] = ScalarKernels.ToByte(v.Z);
        buf[o + 3] = ScalarKernels.ToByte(v.W);
    }

    /// <summary>颜色矩阵：反预乘 → 矩阵 → 钳制 → 预乘（与着色器 filterPass 的操作 0 相同）。</summary>
    internal static Vector4 ApplyMatrix(Vector4 c, float[] m)
    {
        float a = c.W;
        float r = 0, g = 0, b = 0;
        if (a > 0)
        {
            r = c.X / a; g = c.Y / a; b = c.Z / a;
        }
        float nr = Clamp01(m[0] * r + m[1] * g + m[2] * b + m[3] * a + m[4]);
        float ng = Clamp01(m[5] * r + m[6] * g + m[7] * b + m[8] * a + m[9]);
        float nb = Clamp01(m[10] * r + m[11] * g + m[12] * b + m[13] * a + m[14]);
        float na = Clamp01(m[15] * r + m[16] * g + m[17] * b + m[18] * a + m[19]);
        return new Vector4(nr * na, ng * na, nb * na, na);
    }

    static float Clamp01(float v) => v > 0 ? (v < 1 ? v : 1) : 0;

    void ColorMatrix(byte[] src, PixelRect inRect, byte[] dst, PixelRect outRect, float[] m)
    {
        var row = Ensure(ref _rowA, outRect.Width);
        for (int y = outRect.Y; y < outRect.Bottom; y++)
        {
            LoadRow(src, inRect, y, outRect.X, outRect.Width, row);
            int o = (y - outRect.Y) * outRect.Width * 4;
            for (int x = 0; x < outRect.Width; x++, o += 4) Write(dst, o, ApplyMatrix(row[x], m));
        }
    }

    void Downsample(byte[] src, PixelRect inRect, byte[] dst, PixelRect outRect, int k)
    {
        float inv = 1f / (k * k);
        int span = outRect.Width * k;
        var row = Ensure(ref _rowA, span);
        var acc = Ensure(ref _acc, outRect.Width);
        for (int j = outRect.Y; j < outRect.Bottom; j++)
        {
            Array.Clear(acc, 0, outRect.Width);
            // 与逐纹素求和的顺序相同：先按行（b），再按列（a）
            for (int b = 0; b < k; b++)
            {
                LoadRow(src, inRect, j * k + b, outRect.X * k, span, row);
                for (int i = 0; i < outRect.Width; i++)
                {
                    var sum = acc[i];
                    int baseX = i * k;
                    for (int a = 0; a < k; a++) sum += row[baseX + a];
                    acc[i] = sum;
                }
            }
            int o = (j - outRect.Y) * outRect.Width * 4;
            for (int i = 0; i < outRect.Width; i++, o += 4) Write(dst, o, acc[i] * inv);
        }
    }

    void BlurX(byte[] src, PixelRect inRect, byte[] dst, PixelRect outRect, float sigma, int radius)
    {
        var w = FilterPlan.Weights(sigma, radius, out float sum);
        float inv = 1 / sum;
        // 每行先展开成浮点（含两侧 radius 个零），再卷积
        int span = outRect.Width + 2 * radius;
        var row = Ensure(ref _rowA, span);
        for (int y = outRect.Y; y < outRect.Bottom; y++)
        {
            LoadRow(src, inRect, y, outRect.X - radius, span, row);
            int o = (y - outRect.Y) * outRect.Width * 4;
            for (int x = 0; x < outRect.Width; x++, o += 4)
            {
                var acc = Vector4.Zero;
                int c = x + radius;
                for (int t = radius; t >= 1; t--) acc += w[t] * (row[c - t] + row[c + t]);
                acc += w[0] * row[c];
                Write(dst, o, acc * inv);
            }
        }
    }

    void BlurY(byte[] src, PixelRect inRect, byte[] dst, PixelRect outRect, float sigma, int radius)
    {
        var w = FilterPlan.Weights(sigma, radius, out float sum);
        float inv = 1 / sum;
        int width = outRect.Width;
        // 输入行的环形缓冲：输出第 y 行读输入第 y − r … y + r 行
        int window = 2 * radius + 1;
        if (_rows is null || _rows.Length < window) _rows = new Vector4[window][];
        for (int i = 0; i < window; i++)
            if (_rows[i] is null || _rows[i].Length < width) _rows[i] = new Vector4[Math.Max(width, 16)];
        for (int i = 0; i < window - 1; i++) LoadRow(src, inRect, outRect.Y - radius + i, outRect.X, width, _rows[(outRect.Y - radius + i - (outRect.Y - radius)) % window]);
        for (int y = outRect.Y; y < outRect.Bottom; y++)
        {
            int newest = y + radius;
            LoadRow(src, inRect, newest, outRect.X, width, _rows[(newest - (outRect.Y - radius)) % window]);
            int baseIndex = y - radius - (outRect.Y - radius);
            int o = (y - outRect.Y) * width * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                var acc = Vector4.Zero;
                for (int t = radius; t >= 1; t--)
                    acc += w[t] * (_rows[(baseIndex + radius - t) % window][x] + _rows[(baseIndex + radius + t) % window][x]);
                acc += w[0] * _rows[(baseIndex + radius) % window][x];
                Write(dst, o, acc * inv);
            }
        }
    }

    Vector4[][]? _rows;

    void Upsample(byte[] src, PixelRect inRect, byte[] dst, PixelRect outRect, int k)
    {
        float invK = 1f / k;
        // 输出行覆盖的输入列：[i0(left), i0(right) + 1]
        int lo = (int)MathF.Floor((outRect.X + 0.5f) * invK - 0.5f);
        int hi = (int)MathF.Floor((outRect.Right - 0.5f) * invK - 0.5f) + 1;
        int n = hi - lo + 1;
        var r0 = Ensure(ref _rowA, n);
        var r1 = Ensure(ref _rowB, n);
        int loaded0 = int.MinValue, loaded1 = int.MinValue;
        for (int y = outRect.Y; y < outRect.Bottom; y++)
        {
            float v = (y + 0.5f) * invK - 0.5f;
            float fy = MathF.Floor(v);
            int j0 = (int)fy;
            float ty = v - fy;
            if (loaded0 != j0)
            {
                if (loaded1 == j0)
                {
                    (r0, r1) = (r1, r0);
                    (_rowA, _rowB) = (_rowB, _rowA);
                    loaded0 = j0;
                    loaded1 = int.MinValue;
                }
                else
                {
                    LoadRow(src, inRect, j0, lo, n, r0);
                    loaded0 = j0;
                }
            }
            if (loaded1 != j0 + 1)
            {
                LoadRow(src, inRect, j0 + 1, lo, n, r1);
                loaded1 = j0 + 1;
            }
            int o = (y - outRect.Y) * outRect.Width * 4;
            for (int x = outRect.X; x < outRect.Right; x++, o += 4)
            {
                float u = (x + 0.5f) * invK - 0.5f;
                float fx = MathF.Floor(u);
                int i0 = (int)fx - lo;
                float tx = u - fx;
                var c00 = r0[i0];
                var c10 = r0[i0 + 1];
                var c01 = r1[i0];
                var c11 = r1[i0 + 1];
                var top = c00 + (c10 - c00) * tx;
                var bottom = c01 + (c11 - c01) * tx;
                Write(dst, o, top + (bottom - top) * ty);
            }
        }
    }

    void DropShadow(byte[] src, PixelRect inRect, byte[] shadow, PixelRect shadowRect, byte[] dst, PixelRect outRect,
        Vector4 color, int dx, int dy)
    {
        var a = Ensure(ref _rowA, outRect.Width);
        var s = Ensure(ref _rowB, outRect.Width);
        for (int y = outRect.Y; y < outRect.Bottom; y++)
        {
            LoadRow(src, inRect, y, outRect.X, outRect.Width, a);
            LoadRow(shadow, shadowRect, y - dy, outRect.X - dx, outRect.Width, s);
            int o = (y - outRect.Y) * outRect.Width * 4;
            for (int x = 0; x < outRect.Width; x++, o += 4)
            {
                var c = a[x];
                Write(dst, o, c + color * (s[x].W * (1 - c.W)));
            }
        }
    }
}
