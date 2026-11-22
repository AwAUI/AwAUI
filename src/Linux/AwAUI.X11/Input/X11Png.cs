using System;
using System.IO;
using System.IO.Compression;
using AwAUI.Graphics;

namespace AwAUI.X11;

/// <summary>
/// 剪贴板 <c>image/png</c> 使用的自包含 PNG 编解码。平台层不允许引用 <c>AwAUI.Imaging</c>
/// （见依赖规则），因此这里只实现剪贴板需要的子集：编码 8 位 RGBA；解码 8/16 位灰度、灰度+α、RGB、RGBA
/// 与调色板（含 tRNS），不支持隔行扫描。所有尺寸与内存分配都有上限；损坏的输入抛出
/// <see cref="InvalidDataException"/> 或 <see cref="NotSupportedException"/>，由调用方转成 null。
/// </summary>
internal static class X11Png
{
    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>解码的宽高上限。</summary>
    public const int MaxDimension = 16384;

    /// <summary>解码的像素数上限（8M，16 位 RGBA 时原始扫描线约 64 MB）。</summary>
    public const int MaxPixels = 8 * 1024 * 1024;

    // ---------------------------------------------------------------- 编码

    /// <summary>把位图编码为 8 位 RGBA PNG。</summary>
    public static byte[] Encode(Bitmap bitmap)
    {
        if (bitmap is null) throw new ArgumentNullException(nameof(bitmap));
        int w = bitmap.Width, h = bitmap.Height;
        if (w <= 0 || h <= 0) throw new ArgumentException("Bitmap is empty.", nameof(bitmap));
        var rgba = bitmap.ToRgbaUnpremultiplied();
        int stride = w * 4;
        var raw = new byte[(stride + 1) * h];
        for (int y = 0; y < h; y++)
        {
            // 每行一个滤波器字节 0（None）：剪贴板图片通常很小，简单优先
            raw[y * (stride + 1)] = 0;
            Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78); // CMF：deflate，32K 窗口
            ms.WriteByte(0x9C); // FLG：默认压缩级别，校验位使 (CMF*256+FLG)%31==0
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(raw, 0, raw.Length);
            uint adler = Adler32(raw);
            ms.WriteByte((byte)(adler >> 24));
            ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8));
            ms.WriteByte((byte)adler);
            compressed = ms.ToArray();
        }

        using var output = new MemoryStream();
        output.Write(Signature, 0, Signature.Length);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w);
        WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;  // 位深
        ihdr[9] = 6;  // RGBA
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBE(length, 0, (uint)data.Length);
        s.Write(length, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBE(crcBytes, 0, crc);
        s.Write(crcBytes, 0, 4);
    }

    // ---------------------------------------------------------------- 解码

    /// <summary>解码 PNG 为 RGBA 位图（预乘 alpha）。</summary>
    public static Bitmap Decode(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (!CanDecode(data)) throw new InvalidDataException("Not a PNG file.");
        try
        {
            return DecodeCore(data);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or EndOfStreamException)
        {
            throw new InvalidDataException("Corrupt PNG data.", e);
        }
    }

    public static bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length < Signature.Length) return false;
        for (int i = 0; i < Signature.Length; i++)
            if (header[i] != Signature[i]) return false;
        return true;
    }

    static Bitmap DecodeCore(byte[] data)
    {
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        var idat = new MemoryStream();
        int pos = Signature.Length;
        bool sawHeader = false;
        while (pos + 8 <= data.Length)
        {
            int length = (int)ReadBE(data, pos);
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int body = pos + 8;
            if (length < 0 || body + length + 4 > data.Length) throw new InvalidDataException("Truncated PNG chunk.");
            switch (type)
            {
                case "IHDR":
                    if (length < 13) throw new InvalidDataException("Short IHDR.");
                    width = (int)ReadBE(data, body);
                    height = (int)ReadBE(data, body + 4);
                    bitDepth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                    sawHeader = true;
                    break;
                case "PLTE":
                    palette = new byte[length];
                    Array.Copy(data, body, palette, 0, length);
                    break;
                case "tRNS":
                    transparency = new byte[length];
                    Array.Copy(data, body, transparency, 0, length);
                    break;
                case "IDAT":
                    idat.Write(data, body, length);
                    break;
            }
            pos = body + length + 4;
            if (type == "IEND") break;
        }
        if (!sawHeader) throw new InvalidDataException("PNG has no IHDR.");
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no valid dimensions.");
        if (width > MaxDimension || height > MaxDimension) throw new NotSupportedException("PNG is too large.");
        if ((long)width * height > MaxPixels) throw new NotSupportedException("PNG has too many pixels.");
        if (interlace != 0) throw new NotSupportedException("Interlaced PNG is not supported.");
        if (bitDepth != 8 && bitDepth != 16) throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported.");

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) throw new NotSupportedException($"PNG color type {colorType} is not supported.");
        if (colorType == 3 && palette is null) throw new InvalidDataException("Palette PNG has no PLTE chunk.");
        int bytesPerSample = bitDepth / 8;
        int bpp = channels * bytesPerSample;
        long rawLength = ((long)bpp * width + 1) * height;
        if (rawLength > int.MaxValue) throw new NotSupportedException("PNG is too large.");

        byte[] raw = Inflate(idat.ToArray(), (int)rawLength);
        byte[] pixels = Unfilter(raw, (int)rawLength, width, height, bpp);

        var bitmap = new Bitmap(new PixelSize(width, height));
        byte[] row = new byte[width * 4];
        int sampleStride = bpp;
        int pixelStride = bpp * width; // Unfilter 已去掉每行的滤波字节
        for (int y = 0; y < height; y++)
        {
            int line = y * pixelStride;
            for (int x = 0; x < width; x++)
            {
                int i = line + x * sampleStride;
                byte r, g, b, a;
                switch (colorType)
                {
                    case 0:
                        r = g = b = Sample(pixels, i, bitDepth);
                        a = 255;
                        break;
                    case 2:
                        r = Sample(pixels, i, bitDepth);
                        g = Sample(pixels, i + bytesPerSample, bitDepth);
                        b = Sample(pixels, i + bytesPerSample * 2, bitDepth);
                        a = 255;
                        break;
                    case 3:
                    {
                        int index = pixels[i];
                        int pi = index * 3;
                        if (pi + 2 >= palette!.Length) { r = g = b = 0; }
                        else { r = palette[pi]; g = palette[pi + 1]; b = palette[pi + 2]; }
                        a = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                        break;
                    }
                    case 4:
                        r = g = b = Sample(pixels, i, bitDepth);
                        a = Sample(pixels, i + bytesPerSample, bitDepth);
                        break;
                    default:
                        r = Sample(pixels, i, bitDepth);
                        g = Sample(pixels, i + bytesPerSample, bitDepth);
                        b = Sample(pixels, i + bytesPerSample * 2, bitDepth);
                        a = Sample(pixels, i + bytesPerSample * 3, bitDepth);
                        break;
                }
                int o = x * 4;
                row[o] = Premultiply(r, a);
                row[o + 1] = Premultiply(g, a);
                row[o + 2] = Premultiply(b, a);
                row[o + 3] = a;
            }
            bitmap.GetRow(y).Clear();
            row.AsSpan().CopyTo(bitmap.GetRow(y));
        }
        bitmap.Invalidate();
        return bitmap;
    }

    static byte Sample(byte[] pixels, int offset, int bitDepth)
    {
        if (bitDepth == 8) return pixels[offset];
        // 16 位：取高字节（与 AwAUI.Imaging 的截断一致）
        return pixels[offset];
    }

    static byte Premultiply(byte channel, byte alpha)
    {
        if (alpha == 255) return channel;
        if (alpha == 0) return 0;
        return (byte)Math.Min(255, (channel * alpha + 127) / 255);
    }

    static byte[] Unfilter(byte[] raw, int rawLength, int width, int height, int bpp)
    {
        int stride = bpp * width;
        var output = new byte[stride * height];
        int src = 0;
        for (int y = 0; y < height; y++)
        {
            if (src >= rawLength) throw new InvalidDataException("Truncated PNG scanlines.");
            byte filter = raw[src++];
            int dst = y * stride;
            int prev = dst - stride;
            for (int x = 0; x < stride; x++)
            {
                int value = raw[src++];
                int left = x >= bpp ? output[dst + x - bpp] : 0;
                int up = y > 0 ? output[prev + x] : 0;
                int upLeft = y > 0 && x >= bpp ? output[prev + x - bpp] : 0;
                int result = filter switch
                {
                    0 => value,
                    1 => value + left,
                    2 => value + up,
                    3 => value + ((left + up) >> 1),
                    4 => value + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                };
                output[dst + x] = (byte)result;
            }
        }
        return output;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    static byte[] Inflate(byte[] zlib, int expected)
    {
        if (zlib.Length < 6) throw new InvalidDataException("Truncated zlib stream.");
        // 校验 zlib 头（CM=8，且头两字节满足 %31==0），跳过 Adler-32 尾部
        if ((zlib[0] & 0x0F) != 8 || ((zlib[0] << 8) + zlib[1]) % 31 != 0)
            throw new InvalidDataException("Invalid zlib header.");
        int payload = zlib.Length - 6;
        if (payload <= 0) throw new InvalidDataException("Empty zlib stream.");
        var output = new byte[expected];
        using var input = new MemoryStream(zlib, 2, payload);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        int total = 0;
        while (total < expected)
        {
            int n = inflate.Read(output, total, expected - total);
            if (n <= 0) break;
            total += n;
        }
        if (total < expected) throw new InvalidDataException("Not enough pixel data.");
        return output;
    }

    static uint Adler32(byte[] data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % Mod;
            b = (b + a) % Mod;
        }
        return (b << 16) | a;
    }

    static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in type) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    static uint ReadBE(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    static void WriteBE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
