#if NET8_0_OR_GREATER
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace AwAUI.Render.Raster;

/// <summary>
/// Vector128 硬件内建档内核（net8.0+，对应 SSE / NEON / WASM SIMD）。
/// 每个元素的浮点运算顺序与 <see cref="ScalarKernels"/> 相同，输出逐字节一致。
/// </summary>
internal static unsafe class Vector128Kernels
{
    const float ToFloat = ScalarKernels.ToFloat;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<float> Channel(Vector128<uint> u, int shift) =>
        Vector128.ConvertToSingle((Vector128.ShiftRightLogical(u, shift) & Vector128.Create(0xFFu)).AsInt32());

    /// <summary>与 <see cref="ScalarKernels.ToByte"/> 相同：v·255 + 0.5，钳制后截断。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<uint> ToByte(Vector128<float> v)
    {
        v = v * Vector128.Create(255f) + Vector128.Create(0.5f);
        v = Vector128.Min(Vector128.Max(v, Vector128<float>.Zero), Vector128.Create(255f));
        return Vector128.ConvertToInt32(v).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<uint> Pack(Vector128<uint> c0, Vector128<uint> c1, Vector128<uint> c2, Vector128<uint> c3) =>
        c0 | Vector128.ShiftLeft(c1, 8) | Vector128.ShiftLeft(c2, 16) | Vector128.ShiftLeft(c3, 24);

    public static void FillPixels(Span<byte> row, uint pixel)
    {
        int n = row.Length / 4;
        fixed (byte* p = row)
        {
            uint* u = (uint*)p;
            var v = Vector128.Create(pixel);
            int i = 0;
            for (; i <= n - 8; i += 8)
            {
                v.Store(u + i);
                v.Store(u + i + 4);
            }
            for (; i <= n - 4; i += 4) v.Store(u + i);
            for (; i < n; i++) u[i] = pixel;
        }
    }

    /// <summary>纯色 SrcOver。SoA：一次 4 个像素，每个通道一个向量。</summary>
    public static void SrcOverSolid(Span<byte> dst, bool bgra, Vector4 color, ReadOnlySpan<float> coverage)
    {
        int n = coverage.Length;
        float r = bgra ? color.Z : color.X, g = color.Y, b = bgra ? color.X : color.Z, a = color.W;
        bool opaque = a >= 1f;
        fixed (byte* pd = dst)
        fixed (float* pc = coverage)
        {
            uint* pu = (uint*)pd;
            int i = 0;
            if (n >= 4)
            {
                var vr = Vector128.Create(r);
                var vg = Vector128.Create(g);
                var vb = Vector128.Create(b);
                var va = Vector128.Create(a);
                var one = Vector128<float>.One;
                var zero = Vector128<float>.Zero;
                var k = Vector128.Create(ToFloat);
                var solid = Vector128.Create(opaque
                    ? ScalarKernels.Pack(ScalarKernels.ToByte(r), ScalarKernels.ToByte(g), ScalarKernels.ToByte(b), 255)
                    : 0u);
                for (; i <= n - 4; i += 4)
                {
                    var c = Vector128.Load(pc + i);
                    if (Vector128.LessThanOrEqualAll(c, zero)) continue;
                    // 不透明纯色的全覆盖段：直接写内存
                    if (opaque && Vector128.GreaterThanOrEqualAll(c, one))
                    {
                        solid.Store(pu + i);
                        continue;
                    }
                    var u = Vector128.Load(pu + i);
                    var inv = one - va * c;
                    var f0 = vr * c + Channel(u, 0) * k * inv;
                    var f1 = vg * c + Channel(u, 8) * k * inv;
                    var f2 = vb * c + Channel(u, 16) * k * inv;
                    var f3 = va * c + Channel(u, 24) * k * inv;
                    if (opaque)
                    {
                        var full = Vector128.GreaterThanOrEqual(c, one);
                        f0 = Vector128.ConditionalSelect(full, vr, f0);
                        f1 = Vector128.ConditionalSelect(full, vg, f1);
                        f2 = Vector128.ConditionalSelect(full, vb, f2);
                        f3 = Vector128.ConditionalSelect(full, va, f3);
                    }
                    var p = Pack(ToByte(f0), ToByte(f1), ToByte(f2), ToByte(f3));
                    var skip = Vector128.LessThanOrEqual(c, zero).AsUInt32();
                    Vector128.ConditionalSelect(skip, u, p).Store(pu + i);
                }
            }
            for (; i < n; i++) ScalarKernels.SrcOverPixel(pd + i * 4, r, g, b, a, pc[i]);
        }
    }

    /// <summary>
    /// 逐像素颜色的 SrcOver，一次 4 个像素。全覆盖且源不透明的组直接按 AoS（每像素一个向量）转换源颜色；
    /// 其余组把颜色转置为每通道一个向量（SoA）后混合。
    /// </summary>
    public static void SrcOver(Span<byte> dst, bool bgra, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> coverage)
    {
        int n = coverage.Length;
        fixed (byte* pd = dst)
        fixed (float* pc = coverage)
        fixed (Vector4* ps = src)
        {
            uint* pu = (uint*)pd;
            int i = 0;
            var one = Vector128<float>.One;
            var zero = Vector128<float>.Zero;
            var k = Vector128.Create(ToFloat);
            for (; i <= n - 4; i += 4)
            {
                var c = Vector128.Load(pc + i);
                if (Vector128.LessThanOrEqualAll(c, zero)) continue;
                var s = ps + i;
                if (Vector128.GreaterThanOrEqualAll(c, one) && s[0].W >= 1f && s[1].W >= 1f && s[2].W >= 1f && s[3].W >= 1f)
                {
                    // 全覆盖且源不透明（与标量的快速分支一致）：结果就是源颜色，不读目标
                    var q0 = s[0].AsVector128();
                    var q1 = s[1].AsVector128();
                    var q2 = s[2].AsVector128();
                    var q3 = s[3].AsVector128();
                    if (bgra)
                    {
                        q0 = Vector128.Shuffle(q0, Vector128.Create(2, 1, 0, 3));
                        q1 = Vector128.Shuffle(q1, Vector128.Create(2, 1, 0, 3));
                        q2 = Vector128.Shuffle(q2, Vector128.Create(2, 1, 0, 3));
                        q3 = Vector128.Shuffle(q3, Vector128.Create(2, 1, 0, 3));
                    }
                    Vector128.Narrow(Vector128.Narrow(ToByte(q0), ToByte(q1)), Vector128.Narrow(ToByte(q2), ToByte(q3))).Store(pd + i * 4);
                    continue;
                }
                var sx = Vector128.Create(s[0].X, s[1].X, s[2].X, s[3].X);
                var sg = Vector128.Create(s[0].Y, s[1].Y, s[2].Y, s[3].Y);
                var sz = Vector128.Create(s[0].Z, s[1].Z, s[2].Z, s[3].Z);
                var sa = Vector128.Create(s[0].W, s[1].W, s[2].W, s[3].W);
                var sr = bgra ? sz : sx;
                var sb = bgra ? sx : sz;
                var u = Vector128.Load(pu + i);
                var inv = one - sa * c;
                var full = Vector128.GreaterThanOrEqual(c, one) & Vector128.GreaterThanOrEqual(sa, one);
                var f0 = Vector128.ConditionalSelect(full, sr, sr * c + Channel(u, 0) * k * inv);
                var f1 = Vector128.ConditionalSelect(full, sg, sg * c + Channel(u, 8) * k * inv);
                var f2 = Vector128.ConditionalSelect(full, sb, sb * c + Channel(u, 16) * k * inv);
                var f3 = Vector128.ConditionalSelect(full, sa, sa * c + Channel(u, 24) * k * inv);
                var p = Pack(ToByte(f0), ToByte(f1), ToByte(f2), ToByte(f3));
                var skip = Vector128.LessThanOrEqual(c, zero).AsUInt32();
                Vector128.ConditionalSelect(skip, u, p).Store(pu + i);
            }
            for (; i < n; i++)
            {
                var s = ps[i];
                ScalarKernels.SrcOverPixel(pd + i * 4, bgra ? s.Z : s.X, s.Y, bgra ? s.X : s.Z, s.W, pc[i]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<float> Coverage(Vector128<float> v, bool nonZero, bool antiAlias)
    {
        var one = Vector128<float>.One;
        var half = Vector128.Create(0.5f);
        v = Vector128.Abs(v);
        if (nonZero) v = Vector128.Min(v, one);
        else
        {
            // cov % 2 == cov - floor(cov / 2)·2（cov ≥ 0，各步均无舍入）
            var two = Vector128.Create(2f);
            v -= Vector128.Floor(v * half) * two;
            v = Vector128.ConditionalSelect(Vector128.GreaterThan(v, one), two - v, v);
        }
        if (!antiAlias) return Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(v, half), one, Vector128<float>.Zero);
        return Vector128.ConditionalSelect(Vector128.LessThan(v, Vector128.Create(1f / 512f)), Vector128<float>.Zero, v);
    }

    /// <summary>
    /// 行内前缀和 + 覆盖率换算：row[x] = Coverage(acc + cells[0..x])。前缀和逐个相加（与标量舍入一致）；
    /// 连续 4 个单元为 0 时累积值不变（x + 0 == x，仅可能改变零的符号，不影响 |acc|），直接写常量覆盖率。
    /// 返回是否存在非零覆盖。
    /// </summary>
    public static bool AccumulateCoverage(ReadOnlySpan<float> cells, float acc, Span<float> row, bool nonZero, bool antiAlias)
    {
        int n = row.Length;
        fixed (float* pc = cells)
        fixed (float* pr = row)
        {
            int i = 0;
            var anyMask = Vector128<float>.Zero;
            var flat = Coverage(Vector128.Create(acc), nonZero, antiAlias);
            for (; i <= n - 4; i += 4)
            {
                var cell = Vector128.Load(pc + i);
                if (cell == Vector128<float>.Zero)
                {
                    flat.Store(pr + i);
                    anyMask |= flat;
                    continue;
                }
                float a0 = acc + pc[i];
                float a1 = a0 + pc[i + 1];
                float a2 = a1 + pc[i + 2];
                acc = a2 + pc[i + 3];
                var v = Coverage(Vector128.Create(a0, a1, a2, acc), nonZero, antiAlias);
                anyMask |= v;
                v.Store(pr + i);
                flat = Coverage(Vector128.Create(acc), nonZero, antiAlias);
            }
            // 覆盖率非负，按位或之后不为零即存在正值
            bool any = anyMask.AsUInt32() != Vector128<uint>.Zero;
            for (; i < n; i++)
            {
                acc += pc[i];
                float cov = ScalarKernels.Coverage(acc, nonZero, antiAlias);
                pr[i] = cov;
                any |= cov > 0;
            }
            return any;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<float> Clamp01(Vector128<float> t) => Vector128.Min(Vector128.Max(t, Vector128<float>.Zero), Vector128<float>.One);

    public static void Ramp(Span<float> destination, float t0, float dt, bool clamp, int first)
    {
        int n = destination.Length;
        fixed (float* d = destination)
        {
            int i = 0;
            var vt0 = Vector128.Create(t0);
            var vdt = Vector128.Create(dt);
            var index = Vector128.Create(0f, 1f, 2f, 3f) + Vector128.Create((float)first);
            var stride = Vector128.Create(4f);
            for (; i <= n - 4; i += 4, index += stride)
            {
                var t = vt0 + index * vdt;
                (clamp ? Clamp01(t) : t).Store(d + i);
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
        fixed (float* d = destination)
        {
            int i = 0;
            var vx = Vector128.Create(dx0);
            var vy = Vector128.Create(dy0);
            var vsx = Vector128.Create(sx);
            var vsy = Vector128.Create(sy);
            var vr = Vector128.Create(invRadius);
            var index = Vector128.Create(0f, 1f, 2f, 3f) + Vector128.Create((float)first);
            var stride = Vector128.Create(4f);
            for (; i <= n - 4; i += 4, index += stride)
            {
                var dx = vx + index * vsx;
                var dy = vy + index * vsy;
                var t = Vector128.Sqrt(dx * dx + dy * dy) * vr;
                (clamp ? Clamp01(t) : t).Store(d + i);
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
        fixed (float* s = source)
        fixed (float* d = destination)
        {
            var f = Vector128.Create(factor);
            int i = 0;
            for (; i <= n - 4; i += 4) (Vector128.Load(s + i) * f).Store(d + i);
            for (; i < n; i++) d[i] = s[i] * factor;
        }
    }

    public static void Multiply(Span<float> values, ReadOnlySpan<float> factors)
    {
        int n = values.Length;
        fixed (float* v = values)
        fixed (float* f = factors)
        {
            int i = 0;
            for (; i <= n - 4; i += 4) (Vector128.Load(v + i) * Vector128.Load(f + i)).Store(v + i);
            for (; i < n; i++) v[i] *= f[i];
        }
    }

    public static void MultiplyByComplement(Span<float> values, ReadOnlySpan<float> factors)
    {
        int n = values.Length;
        fixed (float* v = values)
        fixed (float* f = factors)
        {
            int i = 0;
            var one = Vector128<float>.One;
            for (; i <= n - 4; i += 4) (Vector128.Load(v + i) * (one - Vector128.Load(f + i))).Store(v + i);
            for (; i < n; i++) v[i] *= 1 - f[i];
        }
    }

    public static void Lerp(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float t, Span<float> destination)
    {
        int n = destination.Length;
        fixed (float* pa = a)
        fixed (float* pb = b)
        fixed (float* d = destination)
        {
            var vt = Vector128.Create(t);
            int i = 0;
            for (; i <= n - 4; i += 4)
            {
                var x = Vector128.Load(pa + i);
                (x + (Vector128.Load(pb + i) - x) * vt).Store(d + i);
            }
            for (; i < n; i++) d[i] = pa[i] + (pb[i] - pa[i]) * t;
        }
    }
}
#endif
