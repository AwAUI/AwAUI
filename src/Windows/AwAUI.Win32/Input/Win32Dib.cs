using System;

namespace AwAUI.Win32;

// 纯逻辑：CF_DIB 的字节布局。不引用互操作，可由跨平台测试直接验证（见 Input/ 说明）。

/// <summary>
/// CF_DIB（BITMAPINFOHEADER + 像素 + AND 掩码）的构造与解析。写入固定为 32 位 BI_RGB、自下而上、
/// 直通 BGRA（未预乘）+ 1 位透明掩码；读取支持 24 位 BI_RGB 与 32 位 BI_RGB / BI_BITFIELDS（含 CF_DIBV5 头）。
/// 读取的尺寸、行距与偏移都对照实际字节数校验，绝不按对方声明的尺寸分配。
/// </summary>
internal static class Win32Dib
{
    /// <summary>像素总数上限（约 64M 像素 = 256MB 的 32 位数据），超过时拒绝解析/构造。</summary>
    public const long MaxPixels = 64L * 1024 * 1024;
    public const int HeaderSize = 40;

    /// <summary>
    /// 构造 CF_DIB 字节。<paramref name="rgba"/> 是自顶向下的直通（未预乘）RGBA。参数无效时抛出
    /// <see cref="ArgumentException"/>，尺寸过大时抛出 <see cref="OutOfMemoryException"/>。
    /// </summary>
    public static byte[] Build(int width, int height, byte[] rgba)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        long pixels = (long)width * height;
        if (pixels > MaxPixels) throw new OutOfMemoryException("Image is too large for the clipboard.");
        if (rgba is null || rgba.Length < pixels * 4) throw new ArgumentException("Pixel buffer is too small.", nameof(rgba));

        int pixelStride = checked(width * 4);
        int maskStride = ((width + 15) / 16) * 2;
        long total = HeaderSize + (long)pixelStride * height + (long)maskStride * height;
        if (total > int.MaxValue) throw new OutOfMemoryException("Image is too large for the clipboard.");

        var data = new byte[total];
        WriteInt(data, 0, HeaderSize);          // biSize
        WriteInt(data, 4, width);               // biWidth
        WriteInt(data, 8, height);              // biHeight（正数 = 自下而上）
        WriteShort(data, 12, 1);                // biPlanes
        WriteShort(data, 14, 32);               // biBitCount
        WriteInt(data, 16, 0);                  // biCompression = BI_RGB
        WriteInt(data, 20, pixelStride * height); // biSizeImage

        int pixelBase = HeaderSize;
        for (int row = 0; row < height; row++)
        {
            int srcRow = (height - 1 - row) * width * 4; // DIB 自下而上
            int dst = pixelBase + row * pixelStride;
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + x * 4;
                data[dst] = rgba[s + 2];     // B
                data[dst + 1] = rgba[s + 1]; // G
                data[dst + 2] = rgba[s];     // R
                data[dst + 3] = rgba[s + 3]; // A
                dst += 4;
            }
        }

        int maskBase = pixelBase + pixelStride * height;
        for (int row = 0; row < height; row++)
        {
            int srcRow = (height - 1 - row) * width * 4;
            int dst = maskBase + row * maskStride;
            for (int x = 0; x < width; x++)
            {
                if (rgba[srcRow + x * 4 + 3] != 0) continue;
                // AND 掩码：置位表示透明
                data[dst + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }
        return data;
    }

    /// <summary>解析 CF_DIB 字节为自顶向下、未预乘的 RGBA。数据无效/不支持时返回 false。</summary>
    public static bool TryParse(byte[] data, out int width, out int height, out byte[] rgba)
    {
        width = 0;
        height = 0;
        rgba = Array.Empty<byte>();
        if (data is null || data.Length < HeaderSize) return false;

        int headerSize = ReadInt(data, 0);
        if (headerSize < HeaderSize || headerSize > data.Length) return false;
        int w = ReadInt(data, 4);
        int h = ReadInt(data, 8);
        int planes = ReadShort(data, 12);
        int bitCount = ReadShort(data, 14);
        uint compression = (uint)ReadInt(data, 16);
        if (w <= 0 || h == 0 || planes != 1) return false;
        long pixels = (long)w * Math.Abs((long)h);
        if (pixels <= 0 || pixels > MaxPixels) return false;

        bool topDown = h < 0;
        int rows = Math.Abs(h);
        if (bitCount != 24 && bitCount != 32) return false;
        if (compression != 0 && compression != 3 /* BI_BITFIELDS */) return false;
        if (compression == 3 && bitCount != 32) return false;

        long pixelOffset = headerSize;
        if (compression == 3 && headerSize == HeaderSize) pixelOffset += 12; // 三色掩码紧随 40 字节头
        uint redMask = 0, greenMask = 0, blueMask = 0, alphaMask = 0;
        if (compression == 3)
        {
            int maskAt = headerSize == HeaderSize ? HeaderSize : 40;
            if (maskAt + 12 > data.Length) return false;
            redMask = (uint)ReadInt(data, maskAt);
            greenMask = (uint)ReadInt(data, maskAt + 4);
            blueMask = (uint)ReadInt(data, maskAt + 8);
            if (headerSize >= 56 && maskAt + 16 <= data.Length) alphaMask = (uint)ReadInt(data, maskAt + 12);
            if (redMask == 0 || greenMask == 0 || blueMask == 0) return false;
        }

        long strideLong = ((long)w * bitCount + 31) / 32 * 4;
        long needed = pixelOffset + strideLong * rows;
        if (needed > data.Length) return false;

        int stride = (int)strideLong;
        var result = new byte[pixels * 4];
        bool alphaAllZero = true;
        if (bitCount == 32 && compression == 0)
        {
            for (int row = 0; row < rows && alphaAllZero; row++)
            {
                int at = (int)(pixelOffset + (long)row * stride);
                for (int x = 0; x < w; x++)
                    if (data[at + x * 4 + 3] != 0) { alphaAllZero = false; break; }
            }
        }

        for (int row = 0; row < rows; row++)
        {
            int srcRow = topDown ? row : rows - 1 - row;
            int src = (int)(pixelOffset + (long)row * stride);
            int dst = srcRow * w * 4;
            for (int x = 0; x < w; x++)
            {
                int b, g, r, a = 255;
                if (bitCount == 24)
                {
                    b = data[src]; g = data[src + 1]; r = data[src + 2];
                    src += 3;
                }
                else if (compression == 0)
                {
                    b = data[src]; g = data[src + 1]; r = data[src + 2]; a = data[src + 3];
                    if (alphaAllZero) a = 255;
                    src += 4;
                }
                else
                {
                    uint pixel = (uint)ReadInt(data, src);
                    r = Channel(pixel, redMask);
                    g = Channel(pixel, greenMask);
                    b = Channel(pixel, blueMask);
                    a = alphaMask != 0 ? Channel(pixel, alphaMask) : 255;
                    src += 4;
                }
                result[dst] = (byte)r;
                result[dst + 1] = (byte)g;
                result[dst + 2] = (byte)b;
                result[dst + 3] = (byte)a;
                dst += 4;
            }
        }

        width = w;
        height = rows;
        rgba = result;
        return true;
    }

    static int Channel(uint pixel, uint mask)
    {
        if (mask == 0) return 0;
        int shift = 0;
        while (shift < 32 && ((mask >> shift) & 1u) == 0) shift++;
        uint shifted = mask >> shift;
        int bits = 0;
        while (bits < 32 && ((shifted >> bits) & 1u) != 0) bits++;
        if (bits == 0) return 0;
        uint value = (pixel & mask) >> shift;
        if (bits == 8) return (int)value;
        long max = (1L << bits) - 1;
        return (int)((value * 255L + max / 2) / max);
    }

    static int ReadInt(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    static int ReadShort(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);

    static void WriteInt(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    static void WriteShort(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }
}
