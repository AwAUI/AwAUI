using System;

namespace AwAUI.Imaging.Jpeg;

/// <summary>从 Exif（TIFF 结构）中只读取 IFD0 的 Orientation（0x0112）标签。任何格式问题都按「无方向」处理。</summary>
internal static class ExifReader
{
    public static int ReadOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return 1;
        if (U16(tiff, 2, little) != 42) return 1;
        long ifd = U32(tiff, 4, little);
        if (ifd < 8 || ifd + 2 > tiff.Length) return 1;
        int count = U16(tiff, (int)ifd, little);
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2 + 12L * i;
            if (entry + 12 > tiff.Length) return 1;
            int e = (int)entry;
            if (U16(tiff, e, little) != 0x0112) continue;
            int type = U16(tiff, e + 2, little);
            int value = type switch
            {
                3 => U16(tiff, e + 8, little),          // SHORT
                4 => (int)Math.Min(U32(tiff, e + 8, little), int.MaxValue), // LONG（不规范但偶见）
                _ => 0,
            };
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }

    static int U16(ReadOnlySpan<byte> b, int o, bool little) =>
        little ? b[o] | b[o + 1] << 8 : b[o] << 8 | b[o + 1];

    static long U32(ReadOnlySpan<byte> b, int o, bool little) =>
        little
            ? (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24)
            : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
}
