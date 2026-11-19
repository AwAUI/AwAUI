using System;
using System.IO;
using System.IO.Compression;

namespace AwAUI.Imaging;

/// <summary>
/// zlib 封装（RFC 1950）：2 字节头 + DEFLATE + Adler-32。
/// DeflateStream 在 netstandard2.0 中即可用，全运行时行为一致。
/// </summary>
internal static class Zlib
{
    public static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0xDA);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        uint adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    public static byte[] Decompress(byte[] data, int expectedLength)
    {
        if (data.Length < 6) throw new InvalidDataException("zlib stream too short.");
        int cmf = data[0], flg = data[1];
        if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0) throw new InvalidDataException("Invalid zlib header.");
        if ((flg & 0x20) != 0) throw new InvalidDataException("zlib preset dictionary is not supported.");
        var result = new byte[expectedLength];
        using var input = new MemoryStream(data, 2, data.Length - 2);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        int total = 0;
        while (total < expectedLength)
        {
            int n = deflate.Read(result, total, expectedLength - total);
            if (n == 0) throw new InvalidDataException("Unexpected end of compressed image data.");
            total += n;
        }
        return result;
    }

    static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
