using AwAUI.Imaging;

namespace AwAUI.Imaging.UnitTests;

/// <summary>
/// 手工构造的恶意头部：几十个字节的文件声明 3000² 或 16383² 的尺寸（GIF 的逻辑屏幕与帧、JPEG 的 SOF、WebP 的 VP8 / VP8L / VP8X 画布与
/// 动画帧、BMP、PNG，以及 ICO 里的 PNG 条目）。<see cref="ImageDecoder.Decode(byte[])"/>、<see cref="ImageDecoder.Decode(byte[], long)"/>
/// 与 <see cref="ImageDecoder.DecodeAnimation"/> 都必须在分配与尺寸成正比的缓冲之前拒绝它们；给出像素上限时一律按上限拒绝。
/// </summary>
public class HostileHeaderTests
{
    static byte[] U16(int v) => new[] { (byte)v, (byte)(v >> 8) };

    static byte[] U32(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    static byte[] BE32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    static byte[] Cat(params object[] parts)
    {
        var l = new List<byte>();
        foreach (var p in parts)
        {
            if (p is byte[] b) l.AddRange(b);
            else if (p is string s) l.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
            else l.Add(Convert.ToByte(p));
        }
        return l.ToArray();
    }

    static byte[] Chunk(string fourcc, byte[] data) => Cat(fourcc, U32(data.Length), data, data.Length % 2 == 1 ? new byte[1] : Array.Empty<byte>());

    static byte[] Riff(params byte[][] chunks)
    {
        var body = new List<byte>(System.Text.Encoding.ASCII.GetBytes("WEBP"));
        foreach (var c in chunks) body.AddRange(c);
        return Cat("RIFF", U32(body.Count), body.ToArray());
    }

    /// <summary>逻辑屏幕 w×h（带 2 色全局调色板），一帧 fw×fh，LZW 数据只有两个字节。</summary>
    static byte[] Gif(int w, int h, int fw, int fh) => Cat("GIF89a", U16(w), U16(h), 0x80, 0, 0, new byte[] { 0, 0, 0, 255, 255, 255 },
        0x2C, U16(0), U16(0), U16(fw), U16(fh), 0, 2, 2, new byte[] { 0x44, 0x01 }, 0, 0x3B);

    /// <summary>基线 SOF0（单分量），之后没有任何扫描数据。</summary>
    static byte[] Jpeg(int w, int h) => Cat(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 1, 1, 0x11, 0 },
        new byte[64], 0xFF, 0xD9);

    static byte[] Vp8(int w, int h) => Riff(Chunk("VP8 ", Cat(new byte[] { 0x10, 0x02, 0x00, 0x9d, 0x01, 0x2a }, U16(w), U16(h), new byte[64])));

    static byte[] Vp8LBody(int w, int h) => Cat(0x2f, U32((int)((uint)(w - 1) | ((uint)(h - 1) << 14))), new byte[64]);

    static byte[] Vp8X(int cw, int ch, bool animated, byte[] frame)
    {
        var x = Cat((byte)(animated ? 0x02 : 0), 0, 0, 0,
            (byte)(cw - 1), (byte)((cw - 1) >> 8), (byte)((cw - 1) >> 16), (byte)(ch - 1), (byte)((ch - 1) >> 8), (byte)((ch - 1) >> 16));
        if (!animated) return Riff(Chunk("VP8X", x), frame);
        // ANMF：偏移 0、帧尺寸 1×1（w-1 = h-1 = 0）、时长 100 ms
        var anmf = Cat(new byte[6], 0, 0, 0, 0, 0, 0, 100, 0, 0, 0, frame);
        return Riff(Chunk("VP8X", x), Chunk("ANIM", new byte[6]), Chunk("ANMF", anmf));
    }

    static byte[] Bmp(int w, int h) =>
        Cat("BM", U32(0), U32(0), U32(54), U32(40), U32(w), U32(h), U16(1), U16(24), U32(0), U32(0), U32(0), U32(0), U32(0), U32(0), new byte[64]);

    static byte[] Png(int w, int h) => Cat(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A },
        BE32(13), "IHDR", BE32(w), BE32(h), 8, 6, 0, 0, 0, new byte[4],
        BE32(4), "IDAT", new byte[] { 0x78, 0x9C, 3, 0 }, new byte[4],
        BE32(0), "IEND", new byte[4]);

    static byte[] Ico(byte[] entry) => Cat(U16(0), U16(1), U16(1), 0, 0, 0, 0, U16(1), U16(32), U32(entry.Length), U32(22), entry);

    public static IEnumerable<object[]> Cases()
    {
        foreach (int side in new[] { 3000, 16383 })
        {
            var tinyVp8L = Chunk("VP8L", Cat(0x2f, U32(0), new byte[8]));
            yield return new object[] { "gif", side, Gif(side, side, side, side) };
            yield return new object[] { "gif-screen", side, Gif(side, side, 1, 1) };
            yield return new object[] { "jpeg", side, Jpeg(side, side) };
            yield return new object[] { "vp8", side, Vp8(side, side) };
            yield return new object[] { "vp8l", side, Riff(Chunk("VP8L", Vp8LBody(side, side))) };
            yield return new object[] { "vp8x-still", side, Vp8X(side, side, false, tinyVp8L) };
            yield return new object[] { "vp8x-animated", side, Vp8X(side, side, true, tinyVp8L) };
            yield return new object[] { "vp8x-animated-frame", side, Vp8X(side, side, true, Chunk("VP8L", Vp8LBody(side, side))) };
            yield return new object[] { "bmp", side, Bmp(side, side) };
            yield return new object[] { "png", side, Png(side, side) };
            yield return new object[] { "ico-png", side, Ico(Png(side, side)) };
        }
    }

    /// <summary>拒绝时允许的分配量：与几十字节的输入相比很大，与声明尺寸（至少 9M 像素 × 4 字节）相比很小。</summary>
    const long AllowedBytes = 256 * 1024;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Huge_declared_sizes_are_rejected_before_allocating(string format, int side, byte[] data)
    {
        Assert.True(data.Length < 200, $"{format}: {data.Length} bytes");
        Assert.NotEqual(ImageFormat.Unknown, ImageDecoder.DetectFormat(data));

        _ = Rejects(() => ImageDecoder.Decode(data).Dispose());
        _ = Rejects(() => ImageDecoder.DecodeAnimation(data));
        // 调用方的像素上限：总是按上限拒绝（NotSupportedException），即使数据随后才会被发现截断
        var e = Rejects(() => ImageDecoder.Decode(data, 1_000_000).Dispose());
        Assert.IsType<NotSupportedException>(e);
        Assert.Contains($"{side}x{side}", e.Message);

        Exception Rejects(Action decode)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var ex = Record.Exception(decode);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(ex is NotSupportedException or InvalidDataException, $"{format} {side}: {ex?.GetType().Name ?? "decoded"}");
            Assert.True(allocated < AllowedBytes, $"{format} {side}: allocated {allocated} bytes before rejecting ({ex!.Message})");
            return ex;
        }
    }
}
