using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AwAUI.Render.Raster;

/// <summary>
/// <see cref="Vector{T}"/> 档内核（netstandard2.0 / .NET Framework 也可用）。只用 netstandard2.0 中已有的
/// Vector API：没有移位与混洗，通道拆分靠按位与 + 2 的幂缩放（精确，见 <see cref="Channel1"/>）。
/// 每个元素的浮点运算顺序与 <see cref="ScalarKernels"/> 相同，输出逐字节一致。
/// </summary>
internal static unsafe class VectorKernels
{
    const float ToFloat = ScalarKernels.ToFloat;

    // 像素按 uint（小端）处理，通道 k 位于第 8k 位。通道 k 取出后的浮点值是 v·2^(8k)，
    // 乘以 ToFloat·2^(-8k) 与先取出 v 再乘 ToFloat 的结果完全相同：两个因子只差 2 的幂，
    // 实数乘积相同，舍入也就相同（这些量远离非规格化数与溢出）。
    const float K0 = ToFloat;
    const float K1 = ToFloat / 256f;
    const float K2 = ToFloat / 65536f;
    const float K3 = ToFloat / 16777216f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> Load(float* p) => Unsafe.ReadUnaligned<Vector<float>>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<uint> Load(uint* p) => Unsafe.ReadUnaligned<Vector<uint>>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Store(float* p, Vector<float> v) => Unsafe.WriteUnaligned(p, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Store(uint* p, Vector<uint> v) => Unsafe.WriteUnaligned(p, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> Channel0(Vector<uint> u) => Vector.ConvertToSingle(Vector.AsVectorInt32(u & new Vector<uint>(0xFFu)));

    /// <summary>通道 1 的值 ×256（精确整数）。与 <see cref="K1"/> 相乘后等于通道值 × ToFloat。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> Channel1(Vector<uint> u) => Vector.ConvertToSingle(Vector.AsVectorInt32(u & new Vector<uint>(0xFF00u)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> Channel2(Vector<uint> u) => Vector.ConvertToSingle(Vector.AsVectorInt32(u & new Vector<uint>(0xFF0000u)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> Channel3(Vector<uint> u) => Vector.ConvertToSingle(u & new Vector<uint>(0xFF000000u));

    /// <summary>与 <see cref="ScalarKernels.ToByte"/> 相同：v·255 + 0.5，钳制后截断。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<uint> ToByte(Vector<float> v)
    {
        v = v * new Vector<float>(255f) + new Vector<float>(0.5f);
        v = Vector.Min(Vector.Max(v, Vector<float>.Zero), new Vector<float>(255f));
        return Vector.AsVectorUInt32(Vector.ConvertToInt32(v));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<uint> Pack(Vector<uint> c0, Vector<uint> c1, Vector<uint> c2, Vector<uint> c3) =>
        c0 | c1 * new Vector<uint>(0x100u) | c2 * new Vector<uint>(0x10000u) | c3 * new Vector<uint>(0x1000000u);

    public static void FillPixels(Span<byte> row, uint pixel)
    {
        int n = row.Length / 4;
        int w = Vector<uint>.Count;
        fixed (byte* p = row)
        {
            uint* u = (uint*)p;
            var v = new Vector<uint>(pixel);
            int i = 0;
            for (; i <= n - w; i += w) Store(u + i, v);
            for (; i < n; i++) u[i] = pixel;
        }
    }

    /// <summary>纯色 SrcOver。按 SoA 处理：一次 W 个像素，每个通道一个向量。</summary>
    public static void SrcOverSolid(Span<byte> dst, bool bgra, Vector4 color, ReadOnlySpan<float> coverage)
    {
        int n = coverage.Length;
        int w = Vector<float>.Count;
        float r = bgra ? color.Z : color.X, g = color.Y, b = bgra ? color.X : color.Z, a = color.W;
        bool opaque = a >= 1f;
        fixed (byte* pd = dst)
        fixed (float* pc = coverage)
        {
            uint* pu = (uint*)pd;
            int i = 0;
            if (n >= w)
            {
                var vr = new Vector<float>(r);
                var vg = new Vector<float>(g);
                var vb = new Vector<float>(b);
                var va = new Vector<float>(a);
                var one = Vector<float>.One;
                var zero = Vector<float>.Zero;
                var solid = new Vector<uint>(opaque
                    ? ScalarKernels.Pack(ScalarKernels.ToByte(r), ScalarKernels.ToByte(g), ScalarKernels.ToByte(b), 255)
                    : 0u);
                for (; i <= n - w; i += w)
                {
                    var c = Load(pc + i);
                    if (Vector.LessThanOrEqualAll(c, zero)) continue;
                    // 不透明纯色的全覆盖段：直接写内存
                    if (opaque && Vector.GreaterThanOrEqualAll(c, one))
                    {
                        Store(pu + i, solid);
                        continue;
                    }
                    var u = Load(pu + i);
                    var inv = one - va * c;
                    var f0 = vr * c + Channel0(u) * new Vector<float>(K0) * inv;
                    var f1 = vg * c + Channel1(u) * new Vector<float>(K1) * inv;
                    var f2 = vb * c + Channel2(u) * new Vector<float>(K2) * inv;
                    var f3 = va * c + Channel3(u) * new Vector<float>(K3) * inv;
                    if (opaque)
                    {
                        var full = Vector.GreaterThanOrEqual(c, one);
                        f0 = Vector.ConditionalSelect(full, vr, f0);
                        f1 = Vector.ConditionalSelect(full, vg, f1);
                        f2 = Vector.ConditionalSelect(full, vb, f2);
                        f3 = Vector.ConditionalSelect(full, va, f3);
                    }
                    var p = Pack(ToByte(f0), ToByte(f1), ToByte(f2), ToByte(f3));
                    var skip = Vector.AsVectorUInt32(Vector.LessThanOrEqual(c, zero));
                    Store(pu + i, Vector.ConditionalSelect(skip, u, p));
                }
            }
            for (; i < n; i++) ScalarKernels.SrcOverPixel(pd + i * 4, r, g, b, a, pc[i]);
        }
    }

    /// <summary>逐像素颜色的 SrcOver。先把一组像素的颜色转置为 SoA，再按通道向量计算。</summary>
    public static void SrcOver(Span<byte> dst, bool bgra, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> coverage)
    {
        int n = coverage.Length;
        int w = Vector<float>.Count;
        float* t = stackalloc float[4 * w];
        fixed (byte* pd = dst)
        fixed (float* pc = coverage)
        fixed (Vector4* ps = src)
        {
            uint* pu = (uint*)pd;
            int i = 0;
            var one = Vector<float>.One;
            var zero = Vector<float>.Zero;
            for (; i <= n - w; i += w)
            {
                var c = Load(pc + i);
                if (Vector.LessThanOrEqualAll(c, zero)) continue;
                var s = ps + i;
                if (!bgra && Vector.GreaterThanOrEqualAll(c, one) && AllOpaque(s, w))
                {
                    // 全覆盖且源不透明（与标量的快速分支一致）：结果就是源颜色。RGBA 目标与 Vector4 通道顺序相同，
                    // 直接按 AoS 转换，不读目标
                    var f = (float*)s;
                    var b01 = Vector.Narrow(ToByte(Load(f)), ToByte(Load(f + w)));
                    var b23 = Vector.Narrow(ToByte(Load(f + 2 * w)), ToByte(Load(f + 3 * w)));
                    Unsafe.WriteUnaligned(pd + i * 4, Vector.Narrow(b01, b23));
                    continue;
                }
                if (bgra)
                    for (int j = 0; j < w; j++) { t[j] = s[j].Z; t[w + j] = s[j].Y; t[2 * w + j] = s[j].X; t[3 * w + j] = s[j].W; }
                else
                    for (int j = 0; j < w; j++) { t[j] = s[j].X; t[w + j] = s[j].Y; t[2 * w + j] = s[j].Z; t[3 * w + j] = s[j].W; }
                var sr = Load(t);
                var sg = Load(t + w);
                var sb = Load(t + 2 * w);
                var sa = Load(t + 3 * w);

                var u = Load(pu + i);
                var inv = one - sa * c;
                var full = Vector.GreaterThanOrEqual(c, one) & Vector.GreaterThanOrEqual(sa, one);
                var f0 = Vector.ConditionalSelect(full, sr, sr * c + Channel0(u) * new Vector<float>(K0) * inv);
                var f1 = Vector.ConditionalSelect(full, sg, sg * c + Channel1(u) * new Vector<float>(K1) * inv);
                var f2 = Vector.ConditionalSelect(full, sb, sb * c + Channel2(u) * new Vector<float>(K2) * inv);
                var f3 = Vector.ConditionalSelect(full, sa, sa * c + Channel3(u) * new Vector<float>(K3) * inv);
                var p = Pack(ToByte(f0), ToByte(f1), ToByte(f2), ToByte(f3));
                var skip = Vector.AsVectorUInt32(Vector.LessThanOrEqual(c, zero));
                Store(pu + i, Vector.ConditionalSelect(skip, u, p));
            }
            for (; i < n; i++)
            {
                var s = ps[i];
                ScalarKernels.SrcOverPixel(pd + i * 4, bgra ? s.Z : s.X, s.Y, bgra ? s.X : s.Z, s.W, pc[i]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool AllOpaque(Vector4* s, int count)
    {
        for (int j = 0; j < count; j++)
            if (!(s[j].W >= 1f)) return false;
        return true;
    }

    /// <summary>
    /// 覆盖率换算（见 <see cref="ScalarKernels.Coverage(float, bool, bool)"/>）。奇偶规则需要 floor，
    /// netstandard2.0 的 Vector 没有，用整数截断代替：只在 |v| &lt; 2^24 时精确，否则返回 false 由调用方退回标量。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryCoverage(ref Vector<float> v, bool nonZero, bool antiAlias)
    {
        var one = Vector<float>.One;
        var half = new Vector<float>(0.5f);
        var x = Vector.Abs(v);
        if (nonZero) x = Vector.Min(x, one);
        else
        {
            if (!Vector.LessThanAll(x, new Vector<float>(16777216f))) return false;
            // cov % 2 == cov - floor(cov / 2)·2（cov ≥ 0，各步均无舍入）
            var two = new Vector<float>(2f);
            x -= Vector.ConvertToSingle(Vector.ConvertToInt32(x * half)) * two;
            x = Vector.ConditionalSelect(Vector.GreaterThan(x, one), two - x, x);
        }
        if (!antiAlias) x = Vector.ConditionalSelect(Vector.GreaterThanOrEqual(x, half), one, Vector<float>.Zero);
        else x = Vector.ConditionalSelect(Vector.LessThan(x, new Vector<float>(1f / 512f)), Vector<float>.Zero, x);
        v = x;
        return true;
    }

    /// <summary>
    /// 行内前缀和 + 覆盖率换算：row[x] = Coverage(acc + cells[0..x])。前缀和逐个相加（与标量舍入一致）；
    /// 整组单元为 0 时累积值不变（x + 0 == x，仅可能改变零的符号，不影响 |acc|），直接写常量覆盖率。
    /// 返回是否存在非零覆盖。
    /// </summary>
    public static bool AccumulateCoverage(ReadOnlySpan<float> cells, float acc, Span<float> row, bool nonZero, bool antiAlias)
    {
        int n = row.Length;
        int w = Vector<float>.Count;
        bool any = false;
        fixed (float* pc = cells)
        fixed (float* pr = row)
        {
            int i = 0;
            var anyMask = Vector<int>.Zero;
            var flat = new Vector<float>(ScalarKernels.Coverage(acc, nonZero, antiAlias));
            for (; i <= n - w; i += w)
            {
                if (Vector.EqualsAll(Load(pc + i), Vector<float>.Zero))
                {
                    Store(pr + i, flat);
                    anyMask |= Vector.AsVectorInt32(flat);
                    continue;
                }
                for (int j = 0; j < w; j++)
                {
                    acc += pc[i + j];
                    pr[i + j] = acc;
                }
                var v = Load(pr + i);
                if (TryCoverage(ref v, nonZero, antiAlias))
                {
                    Store(pr + i, v);
                    anyMask |= Vector.AsVectorInt32(v);
                }
                else
                {
                    for (int j = 0; j < w; j++)
                    {
                        float cov = ScalarKernels.Coverage(pr[i + j], nonZero, antiAlias);
                        pr[i + j] = cov;
                        any |= cov > 0;
                    }
                }
                flat = new Vector<float>(ScalarKernels.Coverage(acc, nonZero, antiAlias));
            }
            for (; i < n; i++)
            {
                acc += pc[i];
                float cov = ScalarKernels.Coverage(acc, nonZero, antiAlias);
                pr[i] = cov;
                any |= cov > 0;
            }
            // 覆盖率非负，按位或之后不为零即存在正值
            return any || !Vector.EqualsAll(anyMask, Vector<int>.Zero);
        }
    }

    /// <summary>0, 1, 2, … 的向量（索引转浮点，2^24 以内精确）。</summary>
    static Vector<float> Iota()
    {
        int w = Vector<float>.Count;
        float* tmp = stackalloc float[w];
        for (int i = 0; i < w; i++) tmp[i] = i;
        return Load(tmp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector<float> Clamp01(Vector<float> t) => Vector.Min(Vector.Max(t, Vector<float>.Zero), Vector<float>.One);

    public static void Ramp(Span<float> destination, float t0, float dt, bool clamp, int first)
    {
        int n = destination.Length;
        int w = Vector<float>.Count;
        fixed (float* d = destination)
        {
            int i = 0;
            var vt0 = new Vector<float>(t0);
            var vdt = new Vector<float>(dt);
            var index = Iota() + new Vector<float>((float)first);
            var stride = new Vector<float>(w);
            for (; i <= n - w; i += w, index += stride)
            {
                var t = vt0 + index * vdt;
                Store(d + i, clamp ? Clamp01(t) : t);
            }
            for (; i < n; i++)
            {
                float t = t0 + (float)(first + i) * dt;
                d[i] = clamp ? ScalarKernels.Clamp01(t) : t;
            }
        }
    }

    public static void RadialDistance(Span<float> destination, float dx0, float dy0, float sx, float sy, float invRadius, bool clamp, int first)
    {
        int n = destination.Length;
        int w = Vector<float>.Count;
        fixed (float* d = destination)
        {
            int i = 0;
            var vx = new Vector<float>(dx0);
            var vy = new Vector<float>(dy0);
            var vsx = new Vector<float>(sx);
            var vsy = new Vector<float>(sy);
            var vr = new Vector<float>(invRadius);
            var index = Iota() + new Vector<float>((float)first);
            var stride = new Vector<float>(w);
            for (; i <= n - w; i += w, index += stride)
            {
                var dx = vx + index * vsx;
                var dy = vy + index * vsy;
                var t = Vector.SquareRoot(dx * dx + dy * dy) * vr;
                Store(d + i, clamp ? Clamp01(t) : t);
            }
            for (; i < n; i++)
            {
                float k = first + i;
                float dx = dx0 + k * sx, dy = dy0 + k * sy;
                float t = MathF.Sqrt(dx * dx + dy * dy) * invRadius;
                d[i] = clamp ? ScalarKernels.Clamp01(t) : t;
            }
        }
    }

    public static void Scale(ReadOnlySpan<float> source, float factor, Span<float> destination)
    {
        int n = source.Length;
        int w = Vector<float>.Count;
        fixed (float* s = source)
        fixed (float* d = destination)
        {
            var f = new Vector<float>(factor);
            int i = 0;
            for (; i <= n - w; i += w) Store(d + i, Load(s + i) * f);
            for (; i < n; i++) d[i] = s[i] * factor;
        }
    }

    public static void Multiply(Span<float> values, ReadOnlySpan<float> factors)
    {
        int n = values.Length;
        int w = Vector<float>.Count;
        fixed (float* v = values)
        fixed (float* f = factors)
        {
            int i = 0;
            for (; i <= n - w; i += w) Store(v + i, Load(v + i) * Load(f + i));
            for (; i < n; i++) v[i] *= f[i];
        }
    }

    public static void MultiplyByComplement(Span<float> values, ReadOnlySpan<float> factors)
    {
        int n = values.Length;
        int w = Vector<float>.Count;
        fixed (float* v = values)
        fixed (float* f = factors)
        {
            int i = 0;
            var one = Vector<float>.One;
            for (; i <= n - w; i += w) Store(v + i, Load(v + i) * (one - Load(f + i)));
            for (; i < n; i++) v[i] *= 1 - f[i];
        }
    }

    public static void Lerp(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float t, Span<float> destination)
    {
        int n = destination.Length;
        int w = Vector<float>.Count;
        fixed (float* pa = a)
        fixed (float* pb = b)
        fixed (float* d = destination)
        {
            var vt = new Vector<float>(t);
            int i = 0;
            for (; i <= n - w; i += w)
            {
                var x = Load(pa + i);
                Store(d + i, x + (Load(pb + i) - x) * vt);
            }
            for (; i < n; i++) d[i] = pa[i] + (pb[i] - pa[i]) * t;
        }
    }
}
