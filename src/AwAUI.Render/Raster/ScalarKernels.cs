using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AwAUI.Render.Raster;

/// <summary>
/// 标量基线内核。它们是另外两档的参考：向量档必须与这里的浮点运算顺序、舍入方式完全一致，
/// 向量内核处理不满一个向量宽度的尾部时也直接调用这里的单元素函数。
/// </summary>
internal static unsafe class ScalarKernels
{
    public const float ToFloat = 1 / 255f;

    /// <summary>0..1 浮点转 8 位：v·255 + 0.5 截断，并钳制到 0..255。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(float v)
    {
        v = v * 255f + 0.5f;
        return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
    }

    /// <summary>按小端字节序把 4 个通道（内存顺序）打包成一个 uint。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(byte c0, byte c1, byte c2, byte c3) => c0 | (uint)c1 << 8 | (uint)c2 << 16 | (uint)c3 << 24;

    /// <summary>
    /// 单个像素的 SrcOver。<paramref name="r"/>、<paramref name="g"/>、<paramref name="b"/> 已按目标的
    /// 内存通道顺序排列（BGRA 目标时 r 实为蓝色），颜色为预乘值。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SrcOverPixel(byte* d, float r, float g, float b, float a, float c)
    {
        if (c <= 0) return;
        if (c >= 1f && a >= 1f)
        {
            d[0] = ToByte(r); d[1] = ToByte(g); d[2] = ToByte(b); d[3] = 255;
            return;
        }
        r *= c; g *= c; b *= c; a *= c;
        float inv = 1 - a;
        d[0] = ToByte(r + d[0] * ToFloat * inv);
        d[1] = ToByte(g + d[1] * ToFloat * inv);
        d[2] = ToByte(b + d[2] * ToFloat * inv);
        d[3] = ToByte(a + d[3] * ToFloat * inv);
    }

    public static void SrcOverBitmapRow(Span<byte> destination, ReadOnlySpan<byte> source, float opacity)
    {
        fixed (byte* target = destination)
        fixed (byte* pixels = source)
        {
            for (int i = 0; i < source.Length; i += 4)
                SrcOverPixel(target + i, pixels[i] * ToFloat, pixels[i + 1] * ToFloat,
                    pixels[i + 2] * ToFloat, pixels[i + 3] * ToFloat, opacity);
        }
    }

    public static void SrcOverSolid(Span<byte> dst, bool bgra, Vector4 color, ReadOnlySpan<float> coverage)
    {
        float r = bgra ? color.Z : color.X, g = color.Y, b = bgra ? color.X : color.Z, a = color.W;
        fixed (byte* pd = dst)
        fixed (float* pc = coverage)
        {
            for (int i = 0; i < coverage.Length; i++) SrcOverPixel(pd + i * 4, r, g, b, a, pc[i]);
        }
    }

    public static void FillPixels(Span<byte> row, byte c0, byte c1, byte c2, byte c3)
    {
        for (int i = 0; i + 3 < row.Length; i += 4) { row[i] = c0; row[i + 1] = c1; row[i + 2] = c2; row[i + 3] = c3; }
    }

    /// <summary>有符号面积累积值 → 覆盖率（非零 / 奇偶规则，可选关闭抗锯齿）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Coverage(float acc, bool nonZero, bool antiAlias)
    {
        float cov = MathF.Abs(acc);
        if (nonZero) cov = MathF.Min(cov, 1f);
        else
        {
            cov %= 2f;
            if (cov > 1f) cov = 2f - cov;
        }
        if (!antiAlias) cov = cov >= 0.5f ? 1f : 0f;
        else if (cov < 1f / 512f) cov = 0;
        return cov;
    }

    public static bool AccumulateCoverage(ReadOnlySpan<float> cells, float acc, Span<float> row, bool nonZero, bool antiAlias)
    {
        bool any = false;
        for (int x = 0; x < row.Length; x++)
        {
            acc += cells[x];
            float cov = Coverage(acc, nonZero, antiAlias);
            row[x] = cov;
            any |= cov > 0;
        }
        return any;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp01(float t) => t < 0 ? 0 : t > 1 ? 1 : t;

    public static void Ramp(Span<float> destination, float t0, float dt, bool clamp, int first = 0)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            float t = t0 + (float)(first + i) * dt;
            destination[i] = clamp ? Clamp01(t) : t;
        }
    }

    public static void RadialDistance(Span<float> destination, float dx0, float dy0, float sx, float sy, float invRadius, bool clamp, int first = 0)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            float k = first + i;
            float dx = dx0 + k * sx, dy = dy0 + k * sy;
            float t = MathF.Sqrt(dx * dx + dy * dy) * invRadius;
            destination[i] = clamp ? Clamp01(t) : t;
        }
    }

    public static void Scale(ReadOnlySpan<float> source, float factor, Span<float> destination)
    {
        for (int i = 0; i < source.Length; i++) destination[i] = source[i] * factor;
    }

    public static void Multiply(Span<float> values, ReadOnlySpan<float> factors)
    {
        for (int i = 0; i < values.Length; i++) values[i] *= factors[i];
    }

    public static void MultiplyByComplement(Span<float> values, ReadOnlySpan<float> factors)
    {
        for (int i = 0; i < values.Length; i++) values[i] *= 1 - factors[i];
    }

    public static void Lerp(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float t, Span<float> destination)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] = a[i] + (b[i] - a[i]) * t;
    }
}
