using System;
using System.Collections.Generic;
using System.IO;
using AwAUI.Graphics;
using AwAUI.Imaging.Bmp;

namespace AwAUI.Imaging;

/// <summary>ICO / CUR 文件目录中的一项。</summary>
public sealed class IconEntry
{
    internal IconEntry(int index, int width, int height, int bitsPerPixel, bool isPng, int hotspotX, int hotspotY, int offset, int length)
    {
        Index = index;
        Width = width;
        Height = height;
        BitsPerPixel = bitsPerPixel;
        IsPng = isPng;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Offset = offset;
        Length = length;
    }

    public int Index { get; }
    /// <summary>目录中记录的宽度（0 表示 256）。</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>位深：BMP 项取自其信息头，PNG 项记为 32。</summary>
    public int BitsPerPixel { get; }
    public bool IsPng { get; }
    /// <summary>光标热点（只对 CUR 有意义，ICO 为 0）。</summary>
    public int HotspotX { get; }
    public int HotspotY { get; }
    internal int Offset { get; }
    internal int Length { get; }
}

/// <summary>
/// ICO / CUR 解码：BMP 项（含 AND 透明掩码，32 位项使用 alpha）与 PNG 项（交给 <see cref="PngCodec"/>）。
/// 默认选择面积最大的一项，同尺寸时取位深更高者；也可按期望尺寸选择。
/// 损坏的输入只抛出 <see cref="InvalidDataException"/> 或 <see cref="NotSupportedException"/>。
/// </summary>
public static class IcoCodec
{
    /// <summary>以 0, 0, 1|2, 0 及非零项数识别（需要 6 字节）。</summary>
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 6 && header[0] == 0 && header[1] == 0 && (header[2] == 1 || header[2] == 2) && header[3] == 0
        && (header[4] | header[5]) != 0;

    /// <summary>是否为光标（CUR）文件。</summary>
    public static bool IsCursor(ReadOnlySpan<byte> header) => CanDecode(header) && header[2] == 2;

    /// <summary>列出数据范围有效的各项。</summary>
    public static IReadOnlyList<IconEntry> ReadEntries(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (!CanDecode(data)) throw new InvalidDataException("Not an ICO/CUR file.");
        try
        {
            return ReadEntriesCore(data);
        }
        catch (Exception e) when (DecodeGuard.IsInternalFailure(e))
        {
            throw DecodeGuard.Wrap("ICO", e);
        }
    }

    static IReadOnlyList<IconEntry> ReadEntriesCore(byte[] data)
    {
        bool cursor = data[2] == 2;
        int count = data[4] | data[5] << 8;
        var list = new List<IconEntry>();
        for (int i = 0; i < count; i++)
        {
            int d = 6 + i * 16;
            if (d + 16 > data.Length) break;
            int w = data[d] == 0 ? 256 : data[d], h = data[d + 1] == 0 ? 256 : data[d + 1];
            int p1 = data[d + 4] | data[d + 5] << 8, p2 = data[d + 6] | data[d + 7] << 8;
            uint size = BmpDecoder.ReadU32(data, d + 8), offset = BmpDecoder.ReadU32(data, d + 12);
            if (size == 0 || offset >= (uint)data.Length || size > (uint)data.Length - offset) continue;
            bool png = PngCodec.CanDecode(data.AsSpan((int)offset, (int)size));
            int bpp = png ? 32 : (size >= 16 ? BmpDecoder.ReadU16(data, (int)offset + 14) : 0);
            if (!png && size >= 4 && BmpDecoder.ReadU32(data, (int)offset) == 12 && size >= 12) bpp = BmpDecoder.ReadU16(data, (int)offset + 10);
            list.Add(new IconEntry(list.Count, w, h, bpp, png, cursor ? p1 : 0, cursor ? p2 : 0, (int)offset, (int)size));
        }
        if (list.Count == 0) throw new InvalidDataException("ICO/CUR contains no valid entry.");
        return list;
    }

    /// <summary>解码面积最大（同尺寸取位深最高）的一项。</summary>
    public static Bitmap Decode(byte[] data) => Decode(data, Pick(ReadEntries(data), 0));

    /// <summary>解码不小于 <paramref name="preferredSize"/> 的最小一项；都比它小时取最大的一项。</summary>
    public static Bitmap Decode(byte[] data, int preferredSize) => Decode(data, Pick(ReadEntries(data), preferredSize));

    /// <summary>解码指定的一项（来自 <see cref="ReadEntries"/>）。</summary>
    public static Bitmap Decode(byte[] data, IconEntry entry)
    {
        if (entry != null && entry.IsPng)
        {
            Validate(data, entry);
            var png = new byte[entry.Length];
            Array.Copy(data, entry.Offset, png, 0, entry.Length);
            return PngCodec.Decode(png);
        }
        var rgba = DecodeRgba(data, entry!, out int w, out int h);
        return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(w, h));
    }

    static void Validate(byte[] data, IconEntry entry)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (entry.Offset < 0 || (long)entry.Offset + entry.Length > data.Length) throw new ArgumentException("Entry does not belong to this data.", nameof(entry));
    }

    internal static byte[] DecodeRgba(byte[] data, IconEntry entry, out int width, out int height)
    {
        Validate(data, entry);
        if (entry.IsPng)
        {
            var png = new byte[entry.Length];
            Array.Copy(data, entry.Offset, png, 0, entry.Length);
            using var bmp = PngCodec.Decode(png);
            width = bmp.Width;
            height = bmp.Height;
            return bmp.ToRgbaUnpremultiplied();
        }
        try
        {
            return BmpDecoder.DecodeDib(data, entry.Offset, entry.Offset + entry.Length, -1, icon: true, out width, out height);
        }
        catch (Exception e) when (DecodeGuard.IsInternalFailure(e))
        {
            throw DecodeGuard.Wrap("ICO", e);
        }
    }

    static IconEntry Pick(IReadOnlyList<IconEntry> entries, int preferred)
    {
        IconEntry? best = null;
        foreach (var e in entries)
        {
            if (best == null) { best = e; continue; }
            long area = (long)e.Width * e.Height, bestArea = (long)best.Width * best.Height;
            bool eFits = Math.Max(e.Width, e.Height) >= preferred, bestFits = Math.Max(best.Width, best.Height) >= preferred;
            bool better;
            if (preferred > 0 && eFits != bestFits) better = eFits;
            else if (area != bestArea) better = preferred > 0 && eFits ? area < bestArea : area > bestArea;
            else better = e.BitsPerPixel > best.BitsPerPixel;
            if (better) best = e;
        }
        return best!;
    }

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));
}
