using System;
using System.IO;
using AwAUI.Graphics;
using AwAUI.Imaging.Bmp;

namespace AwAUI.Imaging;

/// <summary>
/// 纯 C# 的 BMP 解码：1/2/4/8 位调色板、16（默认 5-5-5）/24/32 位、BI_RLE8 / BI_RLE4、BI_BITFIELDS /
/// BI_ALPHABITFIELDS（任意掩码，不足 8 位按位复制扩展）、自上而下存储、OS/2 1.x 核心头与 V2–V5 信息头。
/// 32 位图片的 alpha 全为 0 时视为不透明；RLE 中未写到的像素为透明（均与浏览器一致）。
/// 损坏的输入只抛出 <see cref="InvalidDataException"/> 或 <see cref="NotSupportedException"/>（内嵌 JPEG/PNG、64 位等）。
/// </summary>
public static class BmpCodec
{
    /// <summary>以 "BM" 识别；至少 18 字节时还检查信息头大小。</summary>
    public static bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2 || header[0] != 'B' || header[1] != 'M') return false;
        if (header.Length < 18) return true;
        uint size = (uint)(header[14] | header[15] << 8 | header[16] << 16 | header[17] << 24);
        return size == 12 || (size >= 16 && size <= 1024);
    }

    /// <summary>解码为预乘 RGBA 位图。</summary>
    public static Bitmap Decode(byte[] data)
    {
        var rgba = DecodeRgba(data, out int width, out int height);
        return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(width, height));
    }

    /// <summary>解码为非预乘 RGBA（行紧密排列）。</summary>
    internal static byte[] DecodeRgba(byte[] data, out int width, out int height)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (!CanDecode(data) || data.Length < 18) throw new InvalidDataException("Not a BMP file.");
        uint offset = BmpDecoder.ReadU32(data, 10);
        if (offset >= (uint)data.Length) throw new InvalidDataException("Invalid BMP pixel data offset.");
        try
        {
            return BmpDecoder.DecodeDib(data, 14, data.Length, (int)offset, icon: false, out width, out height);
        }
        catch (Exception e) when (DecodeGuard.IsInternalFailure(e))
        {
            throw DecodeGuard.Wrap("BMP", e);
        }
    }

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));
}
