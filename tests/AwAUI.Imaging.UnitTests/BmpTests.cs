using System.Diagnostics;
using AwAUI.Graphics;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>BMP 解码与 Pillow（16 位与 2 位、RLE 跳转由生成脚本直接给出期望值）逐像素比较。</summary>
public class BmpTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files()
    {
        var d = new TheoryData<string>();
        foreach (var n in FormatTestImages.Files("bmp", "*.bmp")) d.Add(n);
        return d;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesReference(string name)
    {
        var data = FormatTestImages.Read("bmp", name);
        string refName = name == "rgba32-v4-swapped.bmp" ? "rgba32-v5.ref.png" : name.Replace(".bmp", ".ref.png");
        var expected = FormatTestImages.ReadPngRgba(FormatTestImages.PathOf("bmp", refName), out int w, out int h);
        var actual = BmpCodec.DecodeRgba(data, out int aw, out int ah);
        Assert.Equal((w, h), (aw, ah));
        FormatTestImages.AssertRgbaEqual(expected, actual, w, name);
        using var bmp = BmpCodec.Decode(data);
        Assert.Equal(FormatTestImages.Premultiply(expected), FormatTestImages.PremultipliedBytes(bmp));
        output.WriteLine($"{name}: {w}x{h}");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void CorruptedFilesOnlyThrowDocumentedExceptions(string name)
    {
        var original = FormatTestImages.Read("bmp", name);
        var sw = Stopwatch.StartNew();
        var (ok, failed) = FormatTestImages.Fuzz(original, 1500, name.GetHashCode(), data => BmpCodec.DecodeRgba(data, out _, out _), 160);
        output.WriteLine($"{name}: {ok} decoded, {failed} rejected in {sw.ElapsedMilliseconds} ms");
    }

    static byte[] Bmp(int width, int height, int bpp, int compression, byte[] tail)
    {
        var d = new byte[54 + tail.Length];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BitConverter.GetBytes(d.Length).CopyTo(d, 2);
        BitConverter.GetBytes(54 + (bpp <= 8 ? 4 << bpp : 0)).CopyTo(d, 10);
        BitConverter.GetBytes(40).CopyTo(d, 14);
        BitConverter.GetBytes(width).CopyTo(d, 18);
        BitConverter.GetBytes(height).CopyTo(d, 22);
        d[26] = 1;
        d[28] = (byte)bpp;
        BitConverter.GetBytes(compression).CopyTo(d, 30);
        tail.CopyTo(d, 54);
        return d;
    }

    [Fact]
    public void SizeGuards()
    {
        // 未压缩：数据不足
        Assert.Throws<InvalidDataException>(() => BmpCodec.Decode(Bmp(1000, 1000, 24, 0, new byte[16])));
        // RLE：40000×40000 只有一个结束标记，像素预算不足
        long before = GC.GetAllocatedBytesForCurrentThread();
        var rle = new byte[1024 + 2];
        rle[rle.Length - 1] = 1;
        Assert.Throws<NotSupportedException>(() => BmpCodec.Decode(Bmp(40000, 40000, 8, 1, rle)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
        // 宽高非法
        Assert.Throws<InvalidDataException>(() => BmpCodec.Decode(Bmp(0, 10, 24, 0, new byte[64])));
        Assert.Throws<InvalidDataException>(() => BmpCodec.Decode(Bmp(-5, 10, 24, 0, new byte[64])));
        Assert.Throws<NotSupportedException>(() => BmpCodec.Decode(Bmp(1, 1, 64, 0, new byte[64])));
        Assert.Throws<NotSupportedException>(() => BmpCodec.Decode(Bmp(1, 1, 24, 5, new byte[64])));
        Assert.Throws<InvalidDataException>(() => BmpCodec.Decode(Bmp(1, 1, 24, 1, new byte[64])));
        Assert.False(BmpCodec.CanDecode(new byte[] { (byte)'B', (byte)'A' }));
    }

    [Fact]
    public void HugeDeclaredSizeIsRejectedBeforeAllocating()
    {
        // 评审回归：尺寸在 CheckDimensions 之内（16384×32767 ≈ 5.4 亿像素）但数据只有几十字节，
        // 以前先分配约 2 GB 的 RGBA 缓冲再报「截断」（或直接 OutOfMemoryException）
        var tiny = Bmp(16384, 32767, 1, 0, new byte[8 + 16]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => BmpCodec.Decode(tiny));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);

        // RLE8：16384×16384 只有一个结束标记，超出像素预算（以前先分配 1 GB）
        var rle = new byte[1024 + 4];
        rle[rle.Length - 1] = 1;
        var rleFile = Bmp(16384, 16384, 8, 1, rle);
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<NotSupportedException>(() => BmpCodec.Decode(rleFile));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);

        // ICO 中的 BMP 项走同一条路径（高度为 XOR + AND 两张位图之和）
        var dib = Bmp(16384, 2 * 32767, 1, 0, new byte[8 + 16]).AsSpan(14).ToArray();
        var ico = new byte[6 + 16 + dib.Length];
        ico[2] = 1; ico[4] = 1;
        BitConverter.GetBytes(dib.Length).CopyTo(ico, 6 + 8);
        BitConverter.GetBytes(6 + 16).CopyTo(ico, 6 + 12);
        dib.CopyTo(ico, 22);
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => IcoCodec.Decode(ico));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void AllZeroAlphaIn32BitIsOpaque()
    {
        // 2×1，32 位 BI_RGB，第 4 字节为 0：按不透明处理
        using var bmp = BmpCodec.Decode(Bmp(2, 1, 32, 0, new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 }));
        Assert.Equal(new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, bmp.ToRgbaUnpremultiplied());
        // 有非零 alpha 时使用之
        using var bmp2 = BmpCodec.Decode(Bmp(2, 1, 32, 0, new byte[] { 10, 20, 30, 255, 40, 50, 60, 0 }));
        Assert.Equal(new byte[] { 30, 20, 10, 255, 0, 0, 0, 0 }, bmp2.ToRgbaUnpremultiplied());
    }
}
