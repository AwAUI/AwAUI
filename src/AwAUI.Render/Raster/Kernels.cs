using System;
using System.Numerics;

namespace AwAUI.Render.Raster;

/// <summary>按 <see cref="Active"/> 分派到三档内核。调用方只看到这里的入口。</summary>
internal static partial class Kernels
{
    /// <summary>把一行像素填成同一个值（4 字节，内存通道顺序 c0..c3）。</summary>
    public static void FillPixels(Span<byte> row, byte c0, byte c1, byte c2, byte c3)
    {
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.FillPixels(row, ScalarKernels.Pack(c0, c1, c2, c3)); return;
#endif
            case SimdTier.Vector: VectorKernels.FillPixels(row, ScalarKernels.Pack(c0, c1, c2, c3)); return;
            default: ScalarKernels.FillPixels(row, c0, c1, c2, c3); return;
        }
    }

    /// <summary>按组透明度混合两个具有相同通道顺序的预乘位图行。</summary>
    public static void SrcOverBitmapRow(Span<byte> destination, ReadOnlySpan<byte> source, float opacity)
    {
        if (source.Length % 4 != 0 || destination.Length < source.Length)
            throw new ArgumentException("Malformed pixel row.");
#if NET8_0_OR_GREATER
        if (s_active == SimdTier.Vector128)
        {
            Vector128Kernels.SrcOverBitmapRow(destination, source, opacity);
            return;
        }
#endif
        ScalarKernels.SrcOverBitmapRow(destination, source, opacity);
    }

    /// <summary>纯色（预乘 RGBA）按覆盖率 SrcOver 到目标行。</summary>
    public static void SrcOverSolid(Span<byte> dst, bool bgra, Vector4 color, ReadOnlySpan<float> coverage)
    {
        if (dst.Length < coverage.Length * 4) throw new ArgumentException("Destination too short.", nameof(dst));
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.SrcOverSolid(dst, bgra, color, coverage); return;
#endif
            case SimdTier.Vector: VectorKernels.SrcOverSolid(dst, bgra, color, coverage); return;
            default: ScalarKernels.SrcOverSolid(dst, bgra, color, coverage); return;
        }
    }

    /// <summary>逐像素颜色按覆盖率 SrcOver 到目标行；标量档返回 false，由调用方走参考实现。</summary>
    public static bool TrySrcOver(Span<byte> dst, bool bgra, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> coverage)
    {
        if (dst.Length < coverage.Length * 4) throw new ArgumentException("Destination too short.", nameof(dst));
        if (src.Length < coverage.Length) throw new ArgumentException("Source too short.", nameof(src));
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.SrcOver(dst, bgra, src, coverage); return true;
#endif
            case SimdTier.Vector: VectorKernels.SrcOver(dst, bgra, src, coverage); return true;
            default: return false;
        }
    }

    /// <summary>
    /// 光栅器的行内前缀和与覆盖率换算：row[x] = Coverage(acc + Σ cells[0..x])。返回是否有非零覆盖。
    /// </summary>
    public static bool AccumulateCoverage(ReadOnlySpan<float> cells, float acc, Span<float> row, bool nonZero, bool antiAlias)
    {
        if (cells.Length < row.Length) throw new ArgumentException("Cells too short.", nameof(cells));
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: return Vector128Kernels.AccumulateCoverage(cells, acc, row, nonZero, antiAlias);
#endif
            case SimdTier.Vector: return VectorKernels.AccumulateCoverage(cells, acc, row, nonZero, antiAlias);
            default: return ScalarKernels.AccumulateCoverage(cells, acc, row, nonZero, antiAlias);
        }
    }

    /// <summary>
    /// destination[i] = t0 + (first + i)·dt（线性渐变参数），可选钳制到 0..1。<paramref name="first"/> 是第一个元素的绝对下标
    /// （渐变按像素的绝对列号求值，结果与行从哪一列开始无关；须小于 2^24 才能精确转为 float）。
    /// </summary>
    public static void Ramp(Span<float> destination, float t0, float dt, bool clamp, int first = 0)
    {
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.Ramp(destination, t0, dt, clamp, first); return;
#endif
            case SimdTier.Vector: VectorKernels.Ramp(destination, t0, dt, clamp, first); return;
            default: ScalarKernels.Ramp(destination, t0, dt, clamp, first); return;
        }
    }

    /// <summary>
    /// destination[i] = √(dx² + dy²)·invRadius，其中 dx = dx0 + (first + i)·sx、dy = dy0 + (first + i)·sy（径向渐变参数），可选钳制到 0..1。
    /// </summary>
    public static void RadialDistance(Span<float> destination, float dx0, float dy0, float sx, float sy, float invRadius, bool clamp, int first = 0)
    {
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.RadialDistance(destination, dx0, dy0, sx, sy, invRadius, clamp, first); return;
#endif
            case SimdTier.Vector: VectorKernels.RadialDistance(destination, dx0, dy0, sx, sy, invRadius, clamp, first); return;
            default: ScalarKernels.RadialDistance(destination, dx0, dy0, sx, sy, invRadius, clamp, first); return;
        }
    }

    /// <summary>destination[i] = source[i] · factor。</summary>
    public static void Scale(ReadOnlySpan<float> source, float factor, Span<float> destination)
    {
        if (destination.Length < source.Length) throw new ArgumentException("Destination too short.", nameof(destination));
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.Scale(source, factor, destination); return;
#endif
            case SimdTier.Vector: VectorKernels.Scale(source, factor, destination); return;
            default: ScalarKernels.Scale(source, factor, destination); return;
        }
    }

    /// <summary>values[i] ·= factors[i]。</summary>
    public static void Multiply(Span<float> values, ReadOnlySpan<float> factors)
    {
        if (factors.Length < values.Length) throw new ArgumentException("Factors too short.", nameof(factors));
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.Multiply(values, factors); return;
#endif
            case SimdTier.Vector: VectorKernels.Multiply(values, factors); return;
            default: ScalarKernels.Multiply(values, factors); return;
        }
    }

    /// <summary>values[i] ·= 1 − factors[i]。</summary>
    public static void MultiplyByComplement(Span<float> values, ReadOnlySpan<float> factors)
    {
        if (factors.Length < values.Length) throw new ArgumentException("Factors too short.", nameof(factors));
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.MultiplyByComplement(values, factors); return;
#endif
            case SimdTier.Vector: VectorKernels.MultiplyByComplement(values, factors); return;
            default: ScalarKernels.MultiplyByComplement(values, factors); return;
        }
    }

    /// <summary>destination[i] = a[i] + (b[i] − a[i]) · t。</summary>
    public static void Lerp(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float t, Span<float> destination)
    {
        if (a.Length < destination.Length || b.Length < destination.Length) throw new ArgumentException("Inputs too short.");
        switch (s_active)
        {
#if NET8_0_OR_GREATER
            case SimdTier.Vector128: Vector128Kernels.Lerp(a, b, t, destination); return;
#endif
            case SimdTier.Vector: VectorKernels.Lerp(a, b, t, destination); return;
            default: ScalarKernels.Lerp(a, b, t, destination); return;
        }
    }
}
