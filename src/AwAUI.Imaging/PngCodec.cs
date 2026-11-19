using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AwAUI.Graphics;

namespace AwAUI.Imaging;

/// <summary>
/// 纯 C# 的 PNG 编解码。解码支持 8 位的灰度、灰度+α、RGB、RGBA、调色板（含 tRNS），以及 16 位（截断为 8 位）；
/// 隔行扫描（Adam7）尚未支持。编码输出 8 位 RGBA。
/// </summary>
public static class PngCodec
{
    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header.Slice(0, 8).SequenceEqual(Signature);

    // ---------------------------------------------------------------- 编码

    public static byte[] Encode(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height, stride = w * 4;
        var rgba = bitmap.ToRgbaUnpremultiplied();
        var raw = new byte[(stride + 1) * h];
        var prev = new byte[stride];
        var cur = new byte[stride];
        var candidate = new byte[stride];
        var best = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            Array.Copy(rgba, y * stride, cur, 0, stride);
            // 按「绝对值之和最小」启发式为每行选择滤波器
            long bestScore = long.MaxValue;
            byte bestFilter = 0;
            for (byte f = 0; f <= 4; f++)
            {
                long score = 0;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= 4 ? cur[i - 4] : 0, b = prev[i], c = i >= 4 ? prev[i - 4] : 0;
                    int pred = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                    byte v = (byte)(cur[i] - pred);
                    candidate[i] = v;
                    score += v < 128 ? v : 256 - v;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = f;
                    Array.Copy(candidate, best, stride);
                }
            }
            raw[y * (stride + 1)] = bestFilter;
            Array.Copy(best, 0, raw, y * (stride + 1) + 1, stride);
            (prev, cur) = (cur, prev);
        }

        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w);
        WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;   // 位深
        ihdr[9] = 6;   // RGBA
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", Zlib.Compress(raw));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    public static void Save(Bitmap bitmap, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
        File.WriteAllBytes(path, Encode(bitmap));
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var crcBytes = new byte[4];
        WriteBE(crcBytes, 0, crc);
        s.Write(crcBytes, 0, 4);
    }

    // ---------------------------------------------------------------- 解码

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>
    /// 解码 PNG。损坏或恶意的输入只会抛出 <see cref="InvalidDataException"/> 或 <see cref="NotSupportedException"/>，
    /// 分配量以输入大小为界（DEFLATE 的最大压缩比约 1032:1）。
    /// </summary>
    public static Bitmap Decode(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        try
        {
            return DecodeCore(data);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or EndOfStreamException)
        {
            throw new InvalidDataException("Corrupt PNG data.", e);
        }
    }

    static Bitmap DecodeCore(byte[] data)
    {
        if (!CanDecode(data)) throw new InvalidDataException("Not a PNG file.");
        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, transparency = null;
        var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = (int)ReadBE(data, pos);
            string type = Encoding.ASCII.GetString(data, pos + 4, 4);
            int body = pos + 8;
            if (len < 0 || body + len + 4 > data.Length) throw new InvalidDataException("Truncated PNG chunk.");
            switch (type)
            {
                case "IHDR":
                    width = (int)ReadBE(data, body);
                    height = (int)ReadBE(data, body + 4);
                    bitDepth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                    break;
                case "PLTE":
                    palette = new byte[len];
                    Array.Copy(data, body, palette, 0, len);
                    break;
                case "tRNS":
                    transparency = new byte[len];
                    Array.Copy(data, body, transparency, 0, len);
                    break;
                case "IDAT":
                    idat.Write(data, body, len);
                    break;
            }
            pos = body + len + 4;
            if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no valid IHDR.");
        // 与其他格式相同的头部检查（2 GB 上限、调用方的像素上限、每输入字节的像素预算），在分配解压缓冲之前
        DecodeLimits.CheckDimensions(width, height, "PNG");
        DecodeLimits.CheckBudget((long)width * height, data.Length, "PNG");
        if (interlace != 0) throw new NotSupportedException("Interlaced PNG is not supported yet.");
        if (bitDepth != 8 && bitDepth != 16) throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported yet.");
        // 尺寸上限：像素数据不能超过 2GB，解压后的数据量也不可能超过压缩数据的约 1032 倍
        long rawBytes = ((long)width * (colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 }) * bitDepth / 8 + 1) * height;
        if ((long)width * height * 4 > int.MaxValue || rawBytes > int.MaxValue)
            throw new NotSupportedException($"PNG of {width}x{height} pixels is too large.");
        if (rawBytes > (idat.Length + 1) * 1032L)
            throw new InvalidDataException("PNG image data is too short for its declared size.");

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Bad PNG color type {colorType}.") };
        int bpp = channels * bitDepth / 8;
        int stride = width * bpp;
        var raw = Zlib.Decompress(idat.ToArray(), (stride + 1) * height);
        Unfilter(raw, stride, height, bpp);

        var rgba = new byte[width * height * 4];
        int step = bitDepth / 8;
        for (int y = 0; y < height; y++)
        {
            int src = y * (stride + 1) + 1;
            for (int x = 0; x < width; x++)
            {
                int s = src + x * bpp, d = (y * width + x) * 4;
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0:
                        r = g = b = raw[s];
                        break;
                    case 4:
                        r = g = b = raw[s];
                        a = raw[s + step];
                        break;
                    case 2:
                        r = raw[s]; g = raw[s + step]; b = raw[s + 2 * step];
                        break;
                    case 3:
                    {
                        int idx = raw[s];
                        if (palette == null || idx * 3 + 2 >= palette.Length) throw new InvalidDataException("Bad palette index.");
                        r = palette[idx * 3]; g = palette[idx * 3 + 1]; b = palette[idx * 3 + 2];
                        if (transparency != null && idx < transparency.Length) a = transparency[idx];
                        break;
                    }
                    default:
                        r = raw[s]; g = raw[s + step]; b = raw[s + 2 * step]; a = raw[s + 3 * step];
                        break;
                }
                rgba[d] = r; rgba[d + 1] = g; rgba[d + 2] = b; rgba[d + 3] = a;
            }
        }
        return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(width, height));
    }

    static void Unfilter(byte[] raw, int stride, int height, int bpp)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            int filter = raw[row];
            int cur = row + 1, prev = row - stride;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? raw[cur + i - bpp] : 0;
                int b = y > 0 ? raw[prev + i] : 0;
                int c = y > 0 && i >= bpp ? raw[prev + i - bpp] : 0;
                int pred = filter switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Bad PNG filter {filter}."),
                };
                raw[cur + i] = (byte)(raw[cur + i] + pred);
            }
        }
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    static uint ReadBE(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    static class Crc32
    {
        static readonly uint[] Table = Build();

        static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Update(uint crc, IEnumerable<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
