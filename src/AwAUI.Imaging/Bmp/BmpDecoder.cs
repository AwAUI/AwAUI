using System;
using System.IO;

namespace AwAUI.Imaging.Bmp;

/// <summary>
/// DIB（设备无关位图）解码，供 BMP 文件与 ICO/CUR 中的 BMP 项共用。
/// 支持 OS/2 1.x 核心头（12 字节）、BITMAPINFOHEADER 及 V2–V5 扩展头、OS/2 2.x 头（不含其专有压缩）；
/// 1/2/4/8 位调色板、16/24/32 位、BI_RLE8 / BI_RLE4、BI_BITFIELDS / BI_ALPHABITFIELDS、自上而下存储。
/// </summary>
internal static class BmpDecoder
{
    const int BI_RGB = 0, BI_RLE8 = 1, BI_RLE4 = 2, BI_BITFIELDS = 3, BI_JPEG = 4, BI_PNG = 5, BI_ALPHABITFIELDS = 6;

    /// <summary>
    /// 解码一个 DIB 为非预乘 RGBA。<paramref name="pixelOffset"/> 为 -1 时像素紧跟在调色板之后（ICO）。
    /// <paramref name="icon"/> 为 true 时高度是 XOR 与 AND 两张位图之和，并应用 AND 掩码。
    /// </summary>
    public static byte[] DecodeDib(byte[] data, int dibOffset, int dibEnd, int pixelOffset, bool icon, out int width, out int height)
    {
        if (dibOffset + 4 > dibEnd) throw new InvalidDataException("Truncated BMP header.");
        int headerSize = (int)ReadU32(data, dibOffset);
        int bpp, compression = BI_RGB, colorsUsed = 0;
        long w, h;
        bool topDown = false;
        bool core = headerSize == 12;
        uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
        bool hasMasks = false, hasAlphaMask = false;
        int afterHeader;
        if (core)
        {
            if (dibOffset + 12 > dibEnd) throw new InvalidDataException("Truncated BMP header.");
            w = ReadU16(data, dibOffset + 4);
            h = ReadU16(data, dibOffset + 6);
            bpp = ReadU16(data, dibOffset + 10);
            afterHeader = dibOffset + 12;
        }
        else
        {
            if (headerSize < 16 || headerSize > 1024) throw new NotSupportedException($"Unsupported BMP header size {headerSize}.");
            if (dibOffset + Math.Min(headerSize, 40) > dibEnd) throw new InvalidDataException("Truncated BMP header.");
            w = (int)ReadU32(data, dibOffset + 4);
            h = (int)ReadU32(data, dibOffset + 8);
            bpp = ReadU16(data, dibOffset + 14);
            if (headerSize >= 20) compression = (int)ReadU32(data, dibOffset + 16);
            if (headerSize >= 36) colorsUsed = (int)ReadU32(data, dibOffset + 32);
            afterHeader = dibOffset + headerSize;
            if (afterHeader > dibEnd) throw new InvalidDataException("Truncated BMP header.");
            if (headerSize == 64 && (compression == 3 || compression == 4))
                throw new NotSupportedException("OS/2 BMP Huffman / RLE24 compression is not supported.");
            if (compression == BI_BITFIELDS || compression == BI_ALPHABITFIELDS)
            {
                bool wantAlpha = compression == BI_ALPHABITFIELDS;
                int maskPos;
                if (headerSize >= 52)
                {
                    maskPos = dibOffset + 40;
                }
                else
                {
                    maskPos = afterHeader;
                    afterHeader += wantAlpha ? 16 : 12;
                    if (afterHeader > dibEnd) throw new InvalidDataException("Truncated BMP color masks.");
                }
                rMask = ReadU32(data, maskPos);
                gMask = ReadU32(data, maskPos + 4);
                bMask = ReadU32(data, maskPos + 8);
                int alphaPos = -1;
                if (headerSize >= 56) alphaPos = dibOffset + 52;
                else if (wantAlpha && headerSize < 52) alphaPos = maskPos + 12;
                else if (wantAlpha)
                {
                    alphaPos = afterHeader;
                    afterHeader += 4;
                    if (afterHeader > dibEnd) throw new InvalidDataException("Truncated BMP color masks.");
                }
                if (alphaPos >= 0)
                {
                    aMask = ReadU32(data, alphaPos);
                    hasAlphaMask = aMask != 0;
                }
                hasMasks = true;
            }
        }

        if (h < 0)
        {
            topDown = true;
            h = -h;
        }
        if (icon)
        {
            h /= 2;
        }
        DecodeLimits.CheckDimensions(w, h, "BMP");
        width = (int)w;
        height = (int)h;

        switch (compression)
        {
            case BI_RGB:
                if (bpp != 1 && bpp != 2 && bpp != 4 && bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32)
                    throw bpp == 64 ? new NotSupportedException("64-bit BMP is not supported.") : new InvalidDataException($"Invalid BMP bit depth {bpp}.");
                break;
            case BI_RLE8:
                if (bpp != 8) throw new InvalidDataException("BI_RLE8 requires 8 bits per pixel.");
                break;
            case BI_RLE4:
                if (bpp != 4) throw new InvalidDataException("BI_RLE4 requires 4 bits per pixel.");
                break;
            case BI_BITFIELDS:
            case BI_ALPHABITFIELDS:
                if (bpp != 16 && bpp != 32) throw new InvalidDataException("BI_BITFIELDS requires 16 or 32 bits per pixel.");
                break;
            case BI_JPEG:
            case BI_PNG:
                throw new NotSupportedException("BMP with embedded JPEG/PNG is not supported.");
            default:
                throw new NotSupportedException($"Unsupported BMP compression {compression}.");
        }

        // 调色板
        uint[]? palette = null;
        int paletteEnd = afterHeader;
        if (bpp <= 8)
        {
            int entrySize = core ? 3 : 4;
            int max = 1 << bpp;
            int count = colorsUsed > 0 && colorsUsed < max ? colorsUsed : max;
            int limit = pixelOffset >= 0 && pixelOffset > afterHeader ? pixelOffset : dibEnd;
            count = Math.Min(count, Math.Max(0, (limit - afterHeader) / entrySize));
            if (count == 0 && pixelOffset < 0) throw new InvalidDataException("Truncated BMP color table.");
            palette = new uint[256];
            for (int i = 0; i < palette.Length; i++) palette[i] = 0xFF000000;
            for (int i = 0; i < count; i++)
            {
                int o = afterHeader + i * entrySize;
                palette[i] = (uint)(data[o + 2] | data[o + 1] << 8 | data[o] << 16) | 0xFF000000;
            }
            paletteEnd = afterHeader + count * entrySize;
        }
        int start = pixelOffset >= 0 ? pixelOffset : paletteEnd;
        if (start > dibEnd || start < dibOffset) throw new InvalidDataException("Invalid BMP pixel data offset.");

        // 所有尺寸检查都在分配输出之前完成：很小的文件声明巨大尺寸时不能先分配再报错
        if (compression == BI_RLE8 || compression == BI_RLE4)
        {
            DecodeLimits.CheckBudget((long)width * height, data.Length, "BMP");
            var rle = new byte[checked(width * height * 4)];
            DecodeRle(data, start, dibEnd, compression == BI_RLE4, palette!, rle, width, height, topDown);
            return rle;
        }

        long stride = ((long)width * bpp + 31) / 32 * 4;
        long rowBytes = ((long)width * bpp + 7) / 8;
        if (start + stride * (height - 1) + rowBytes > dibEnd) throw new InvalidDataException("Truncated BMP pixel data.");
        var rgba = new byte[checked(width * height * 4)];
        bool usedAlpha;

        if (bpp <= 8)
        {
            DecodeIndexed(data, start, (int)stride, bpp, palette!, rgba, width, height, topDown);
            usedAlpha = false;
        }
        else if (bpp == 24)
        {
            for (int y = 0; y < height; y++)
            {
                int s = start + (int)(stride * (topDown ? y : height - 1 - y));
                int d = y * width * 4;
                for (int x = 0; x < width; x++, s += 3, d += 4)
                {
                    rgba[d] = data[s + 2];
                    rgba[d + 1] = data[s + 1];
                    rgba[d + 2] = data[s];
                    rgba[d + 3] = 255;
                }
            }
            usedAlpha = false;
        }
        else
        {
            if (!hasMasks)
            {
                if (bpp == 16)
                {
                    rMask = 0x7C00; gMask = 0x03E0; bMask = 0x001F;
                }
                else
                {
                    // 32 位 BI_RGB：第 4 字节按 alpha 尝试，全为 0 时视为不透明（与浏览器一致）
                    rMask = 0xFF0000; gMask = 0xFF00; bMask = 0xFF; aMask = 0xFF000000;
                    hasAlphaMask = true;
                }
            }
            usedAlpha = DecodeBitfields(data, start, (int)stride, bpp, rMask, gMask, bMask, hasAlphaMask ? aMask : 0, rgba, width, height, topDown);
        }

        if (icon)
        {
            // AND 掩码：1 位/像素，1 = 透明。32 位且带有效 alpha 的图标忽略掩码
            if (!usedAlpha)
            {
                long maskStride = ((long)width + 31) / 32 * 4;
                long maskStart = start + stride * height;
                if (maskStart + maskStride * height <= dibEnd)
                {
                    for (int y = 0; y < height; y++)
                    {
                        int s = (int)(maskStart + maskStride * (topDown ? y : height - 1 - y));
                        int d = y * width * 4 + 3;
                        for (int x = 0; x < width; x++, d += 4)
                            if ((data[s + (x >> 3)] & (0x80 >> (x & 7))) != 0) rgba[d] = 0;
                    }
                }
            }
        }
        return rgba;
    }

    static void DecodeIndexed(byte[] data, int start, int stride, int bpp, uint[] palette, byte[] rgba, int width, int height, bool topDown)
    {
        int perByte = 8 / bpp, mask = (1 << bpp) - 1;
        for (int y = 0; y < height; y++)
        {
            int s = start + stride * (topDown ? y : height - 1 - y);
            int d = y * width * 4;
            for (int x = 0; x < width; x++, d += 4)
            {
                int idx;
                if (bpp == 8) idx = data[s + x];
                else
                {
                    int shift = 8 - bpp * (x % perByte + 1);
                    idx = (data[s + x / perByte] >> shift) & mask;
                }
                Put(rgba, d, palette[idx]);
            }
        }
    }

    static void Put(byte[] rgba, int d, uint c)
    {
        rgba[d] = (byte)c;
        rgba[d + 1] = (byte)(c >> 8);
        rgba[d + 2] = (byte)(c >> 16);
        rgba[d + 3] = (byte)(c >> 24);
    }

    /// <summary>按位掩码解 16/32 位像素。返回是否使用了 alpha（存在非零 alpha）。</summary>
    static bool DecodeBitfields(byte[] data, int start, int stride, int bpp, uint rMask, uint gMask, uint bMask, uint aMask,
        byte[] rgba, int width, int height, bool topDown)
    {
        var r = new Channel(rMask);
        var g = new Channel(gMask);
        var b = new Channel(bMask);
        var a = new Channel(aMask);
        bool anyAlpha = false;
        for (int y = 0; y < height; y++)
        {
            int s = start + stride * (topDown ? y : height - 1 - y);
            int d = y * width * 4;
            for (int x = 0; x < width; x++, d += 4)
            {
                uint v;
                if (bpp == 16)
                {
                    v = (uint)(data[s] | data[s + 1] << 8);
                    s += 2;
                }
                else
                {
                    v = ReadU32(data, s);
                    s += 4;
                }
                rgba[d] = r.Get(v);
                rgba[d + 1] = g.Get(v);
                rgba[d + 2] = b.Get(v);
                if (aMask != 0)
                {
                    byte av = a.Get(v);
                    rgba[d + 3] = av;
                    anyAlpha |= av != 0;
                }
                else rgba[d + 3] = 255;
            }
        }
        if (aMask != 0 && !anyAlpha)
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        return anyAlpha;
    }

    /// <summary>
    /// 一个颜色掩码。位数小于 8 时用位复制扩展到 8 位（如 5 位 v → v&lt;&lt;3 | v&gt;&gt;2，与 Firefox 一致；按 v×255/max 取整的解码器个别值差 1），
    /// 多于 8 位时取最高 8 位。不连续的掩码按其最低位到最高位之间的连续范围处理。
    /// </summary>
    readonly struct Channel
    {
        readonly int _shift, _bits;
        readonly uint _max;
        readonly byte[]? _lut;

        public Channel(uint mask)
        {
            _shift = 0;
            _bits = 0;
            _max = 0;
            _lut = null;
            if (mask == 0) return;
            while ((mask & 1) == 0)
            {
                mask >>= 1;
                _shift++;
            }
            while (mask != 0)
            {
                mask >>= 1;
                _bits++;
            }
            if (_bits > 8)
            {
                _shift += _bits - 8;
                _bits = 8;
            }
            _max = (1u << _bits) - 1;
            if (_bits < 8)
            {
                _lut = new byte[1 << _bits];
                for (int v = 0; v < _lut.Length; v++)
                {
                    int r = 0;
                    for (int s = 8 - _bits; s > -_bits; s -= _bits) r |= s >= 0 ? v << s : v >> -s;
                    _lut[v] = (byte)r;
                }
            }
        }

        public byte Get(uint v)
        {
            if (_bits == 0) return 0;
            uint c = (v >> _shift) & _max;
            return _lut != null ? _lut[c] : (byte)c;
        }
    }

    /// <summary>
    /// RLE8 / RLE4。未写到的像素（行尾跳过、增量跳转、提前结束）为全透明，与浏览器一致；
    /// 数据截断或缺少结束标记时保留已解出的像素。每个指令最多处理 255 个像素，耗时与数据量成正比。
    /// </summary>
    static void DecodeRle(byte[] data, int pos, int end, bool rle4, uint[] palette, byte[] rgba, int width, int height, bool topDown)
    {
        int x = 0, y = 0;
        while (pos + 1 < end && y < height)
        {
            int n = data[pos], c = data[pos + 1];
            pos += 2;
            int row = topDown ? y : height - 1 - y;
            if (n > 0)
            {
                int count = Math.Min(n, width - x);
                int d = (row * width + x) * 4;
                for (int i = 0; i < count; i++, d += 4)
                {
                    int idx = rle4 ? ((i & 1) == 0 ? c >> 4 : c & 15) : c;
                    Put(rgba, d, palette[idx]);
                }
                x = Math.Min(width, x + n);
                continue;
            }
            switch (c)
            {
                case 0:
                    x = 0;
                    y++;
                    break;
                case 1:
                    return;
                case 2:
                    if (pos + 1 >= end) return;
                    x = Math.Min(width, x + data[pos]);
                    y += data[pos + 1];
                    pos += 2;
                    break;
                default:
                {
                    int bytes = rle4 ? (c + 1) / 2 : c;
                    if (pos + bytes > end) return;
                    int count = Math.Min(c, width - x);
                    int d = (row * width + x) * 4;
                    for (int i = 0; i < count; i++, d += 4)
                    {
                        int idx = rle4 ? ((i & 1) == 0 ? data[pos + i / 2] >> 4 : data[pos + i / 2] & 15) : data[pos + i];
                        Put(rgba, d, palette[idx]);
                    }
                    x = Math.Min(width, x + c);
                    pos += bytes + (bytes & 1);
                    break;
                }
            }
        }
    }

    public static uint ReadU32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);

    public static int ReadU16(byte[] d, int o) => d[o] | d[o + 1] << 8;
}
