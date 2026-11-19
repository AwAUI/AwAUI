using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.Imaging.UnitTests;

/// <summary>
/// 头部声明的尺寸在分配之前检查：<see cref="ImageDecoder.Decode(byte[], long)"/> 的像素上限对每种格式都在分配像素缓冲之前生效，
/// PNG 也按与其他格式相同的规则（2 GB 上限、每输入字节的像素预算）检查头部。
/// </summary>
public class DecodePixelLimitTests
{
    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "baseline-420-q90.jpg", 240 * 160 },
        new object[] { "gif/anim-disposal.gif", 64 * 48 },
        new object[] { "webp/lossy-33x17.webp", 33 * 17 },
        new object[] { "webp/ll-gradient-200x3.webp", 200 * 3 },
        new object[] { "webp/anim-lossy.webp", -1 },
        new object[] { "bmp/rgb24.bmp", 63 * 41 },
        new object[] { "ico/png-entries.ico", -1 },
        new object[] { "ico/bmp-entries.ico", -1 },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Pixel_limit_rejects_every_format_and_allows_images_within_it(string name, int pixels)
    {
        var data = TestImages.Read(name);
        using (var full = ImageDecoder.Decode(data))
        {
            if (pixels > 0) Assert.Equal(pixels, full.Width * full.Height);
            using var within = ImageDecoder.Decode(data, (long)full.Width * full.Height);
            Assert.Equal(full.Size, within.Size);
        }
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(data, 16));
    }

    [Fact]
    public void Png_over_the_limit_is_rejected_before_any_pixel_buffer_is_allocated()
    {
        // 4096×4096 的单色 PNG 只有几十 KB，完整解码要分配约 64 MB（RGBA）+ 64 MB（解压数据）
        byte[] png;
        using (var big = new Bitmap(new PixelSize(4096, 4096)))
        {
            big.Clear(Color.Parse("#808080"));
            png = PngCodec.Encode(big);
        }
        Assert.True(png.Length < 1024 * 1024, png.Length.ToString());
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(png, 1_000_000));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // 只允许与输入大小成正比的分配（IDAT 拼接）
        Assert.True(allocated < png.Length * 4L + 256 * 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public void Limit_is_scoped_to_the_call()
    {
        var data = TestImages.Read("baseline-420-q90.jpg");
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(data, 100));
        // 之前的上限不残留在线程上
        using var ok = ImageDecoder.Decode(data);
        Assert.Equal(240, ok.Width);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageDecoder.Decode(data, 0));
    }

    [Fact]
    public void Png_header_is_checked_against_the_per_input_byte_budget_before_decompressing()
    {
        // 合法的 IHDR 声明 30000×30000，IDAT 只有几个字节：在解压（按声明大小分配）之前被拒绝
        var png = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        void Chunk(string type, byte[] body)
        {
            png.AddRange(new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length });
            png.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            png.AddRange(body);
            png.AddRange(new byte[4]); // CRC 不校验
        }
        Chunk("IHDR", new byte[] { 0, 0, 0x75, 0x30, 0, 0, 0x75, 0x30, 8, 0, 0, 0, 0 });
        Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00 });
        Chunk("IEND", Array.Empty<byte>());
        var data = png.ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Record.Exception(() => ImageDecoder.Decode(data));
        Assert.True(ex is NotSupportedException or InvalidDataException, ex?.GetType().Name);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }
}
