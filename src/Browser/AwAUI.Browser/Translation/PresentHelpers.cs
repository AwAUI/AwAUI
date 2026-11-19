using System;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary><see cref="CursorShape"/> → CSS cursor 关键字。</summary>
internal static class CursorCss
{
    public static string ToCss(CursorShape cursor) => cursor switch
    {
        CursorShape.IBeam => "text",
        CursorShape.Hand => "pointer",
        CursorShape.Crosshair => "crosshair",
        CursorShape.ResizeHorizontal => "ew-resize",
        CursorShape.ResizeVertical => "ns-resize",
        CursorShape.Wait => "wait",
        CursorShape.NotAllowed => "not-allowed",
        CursorShape.None => "none",
        _ => "default",
    };
}

/// <summary>帧缓冲像素格式转换。</summary>
internal static class PixelConversion
{
    /// <summary>
    /// 预乘 RGBA → 非预乘 RGBA（ImageData 的格式）。只有透明 canvas 需要；不透明 canvas（alpha: false）
    /// 会忽略 alpha，预乘颜色恰好等于「叠在黑色上」的结果，可以直接呈现，省掉这一遍。
    /// <paramref name="source"/> 与 <paramref name="destination"/> 可以是同一块内存。
    /// </summary>
    public static void UnpremultiplyRgba(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length) throw new ArgumentException("Destination is too small.", nameof(destination));
        int n = source.Length & ~3;
        for (int i = 0; i < n; i += 4)
        {
            byte a = source[i + 3];
            if (a == 255)
            {
                destination[i] = source[i];
                destination[i + 1] = source[i + 1];
                destination[i + 2] = source[i + 2];
                destination[i + 3] = 255;
            }
            else if (a == 0)
            {
                destination[i] = 0;
                destination[i + 1] = 0;
                destination[i + 2] = 0;
                destination[i + 3] = 0;
            }
            else
            {
                int half = a >> 1;
                destination[i] = Unpremultiply(source[i], a, half);
                destination[i + 1] = Unpremultiply(source[i + 1], a, half);
                destination[i + 2] = Unpremultiply(source[i + 2], a, half);
                destination[i + 3] = a;
            }
        }
    }

    static byte Unpremultiply(byte c, byte a, int half)
    {
        int v = (c * 255 + half) / a;
        return v > 255 ? (byte)255 : (byte)v;
    }
}
