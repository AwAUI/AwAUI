using System;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 把预乘颜色按覆盖率与混合模式写入 8 位目标行。SrcOver 按 <see cref="Kernels.Active"/> 分派到三档内核
/// （标量 / Vector&lt;T&gt; / Vector128，输出逐字节一致）；其他混合模式只有标量实现。
/// </summary>
internal static class Blender
{
    const float ToFloat = 1 / 255f;

    public static bool IsSupported(BlendMode mode) => mode is not (BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity);

    /// <param name="dst">目标行中从第一个像素开始的字节（每像素 4 字节）。</param>
    public static void BlendRow(Span<byte> dst, bool bgra, ReadOnlySpan<Vector4> src, ReadOnlySpan<float> coverage, BlendMode mode)
    {
        if (mode == BlendMode.SrcOver && Kernels.TrySrcOver(dst, bgra, src, coverage)) return;
        int ir = bgra ? 2 : 0, ib = bgra ? 0 : 2;
        for (int i = 0; i < coverage.Length; i++)
        {
            float c = coverage[i];
            if (c <= 0) continue;
            int o = i * 4;
            var s = src[i];

            if (mode == BlendMode.SrcOver)
            {
                if (c >= 1f && s.W >= 1f)
                {
                    dst[o + ir] = ToByte(s.X); dst[o + 1] = ToByte(s.Y); dst[o + ib] = ToByte(s.Z); dst[o + 3] = 255;
                    continue;
                }
                s *= c;
                float inv = 1 - s.W;
                dst[o + ir] = ToByte(s.X + dst[o + ir] * ToFloat * inv);
                dst[o + 1] = ToByte(s.Y + dst[o + 1] * ToFloat * inv);
                dst[o + ib] = ToByte(s.Z + dst[o + ib] * ToFloat * inv);
                dst[o + 3] = ToByte(s.W + dst[o + 3] * ToFloat * inv);
                continue;
            }

            var d = new Vector4(dst[o + ir], dst[o + 1], dst[o + ib], dst[o + 3]) * ToFloat;
            var result = Blend(s, d, mode);
            // 覆盖率在源与结果之间线性插值（等价于先按覆盖率裁剪再混合）
            if (c < 1f) result = Vector4.Lerp(d, result, c);
            dst[o + ir] = ToByte(result.X);
            dst[o + 1] = ToByte(result.Y);
            dst[o + ib] = ToByte(result.Z);
            dst[o + 3] = ToByte(result.W);
        }
    }

    static Vector4 Blend(Vector4 s, Vector4 d, BlendMode mode)
    {
        switch (mode)
        {
            case BlendMode.Src: return s;
            case BlendMode.Clear: return Vector4.Zero;
            case BlendMode.DstIn: return d * s.W;
            case BlendMode.DstOut: return d * (1 - s.W);
            case BlendMode.Plus: return Vector4.Min(Vector4.One, s + d);
            case BlendMode.SrcOver: return s + d * (1 - s.W);
        }

        // 可分离混合（W3C Compositing 1）：co = cs(1-ab) + cb(1-as) + as·ab·B(Cs, Cb)
        float sa = s.W, da = d.W;
        float ao = sa + da - sa * da;
        return new Vector4(
            Channel(s.X, d.X, sa, da, mode),
            Channel(s.Y, d.Y, sa, da, mode),
            Channel(s.Z, d.Z, sa, da, mode),
            ao);
    }

    static float Channel(float cs, float cb, float sa, float da, BlendMode mode)
    {
        float Cs = sa > 0 ? cs / sa : 0, Cb = da > 0 ? cb / da : 0;
        float b = mode switch
        {
            BlendMode.Multiply => Cs * Cb,
            BlendMode.Screen => Cs + Cb - Cs * Cb,
            BlendMode.Overlay => HardLight(Cb, Cs),
            BlendMode.Darken => MathF.Min(Cs, Cb),
            BlendMode.Lighten => MathF.Max(Cs, Cb),
            BlendMode.ColorDodge => Cb <= 0 ? 0 : Cs >= 1 ? 1 : MathF.Min(1, Cb / (1 - Cs)),
            BlendMode.ColorBurn => Cb >= 1 ? 1 : Cs <= 0 ? 0 : 1 - MathF.Min(1, (1 - Cb) / Cs),
            BlendMode.HardLight => HardLight(Cs, Cb),
            BlendMode.SoftLight => SoftLight(Cs, Cb),
            BlendMode.Difference => MathF.Abs(Cs - Cb),
            BlendMode.Exclusion => Cs + Cb - 2 * Cs * Cb,
            _ => Cs, // 不支持的非可分离模式按 Normal 处理（后端会上报降级）
        };
        return cs * (1 - da) + cb * (1 - sa) + sa * da * b;
    }

    static float HardLight(float cs, float cb) =>
        cs <= 0.5f ? cb * 2 * cs : Screen(cb, 2 * cs - 1);

    static float Screen(float a, float b) => a + b - a * b;

    static float SoftLight(float cs, float cb)
    {
        if (cs <= 0.5f) return cb - (1 - 2 * cs) * cb * (1 - cb);
        float d = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : MathF.Sqrt(cb);
        return cb + (2 * cs - 1) * (d - cb);
    }

    /// <summary>纯色（预乘）按覆盖率混合；不需要逐像素着色。</summary>
    public static void BlendSolidRow(Span<byte> dst, bool bgra, Vector4 color, ReadOnlySpan<float> coverage, BlendMode mode, Span<Vector4> scratch)
    {
        if (mode == BlendMode.SrcOver)
        {
            Kernels.SrcOverSolid(dst, bgra, color, coverage);
            return;
        }
        var src = scratch.Slice(0, coverage.Length);
        src.Fill(color);
        BlendRow(dst, bgra, src, coverage, mode);
    }

    static byte ToByte(float v) => ScalarKernels.ToByte(v);
}
