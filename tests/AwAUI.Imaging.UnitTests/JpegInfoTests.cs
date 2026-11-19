namespace AwAUI.Imaging.UnitTests;

public class JpegInfoTests
{
    [Theory]
    [InlineData("baseline-420-q90.jpg", 240, 160, 3, false, JpegColorSpace.YCbCr)]
    [InlineData("baseline-444-q85.jpg", 300, 160, 3, false, JpegColorSpace.YCbCr)]
    [InlineData("baseline-rgb.jpg", 240, 120, 3, false, JpegColorSpace.Rgb)]
    [InlineData("gray.jpg", 240, 120, 1, false, JpegColorSpace.Grayscale)]
    [InlineData("gray-progressive.jpg", 300, 160, 1, true, JpegColorSpace.Grayscale)]
    [InlineData("progressive-420.jpg", 320, 160, 3, true, JpegColorSpace.YCbCr)]
    [InlineData("odd-420.jpg", 237, 153, 3, false, JpegColorSpace.YCbCr)]
    [InlineData("cmyk.jpg", 300, 160, 4, false, JpegColorSpace.Cmyk)]
    [InlineData("ycck.jpg", 240, 160, 4, false, JpegColorSpace.Ycck)]
    [InlineData("perf-1280x880-progressive.jpg", 1280, 880, 3, true, JpegColorSpace.YCbCr)]
    public void ReadInfoReportsHeader(string name, int width, int height, int components, bool progressive, JpegColorSpace colorSpace)
    {
        var info = JpegCodec.ReadInfo(TestImages.Read(name));
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(components, info.ComponentCount);
        Assert.Equal(progressive, info.IsProgressive);
        Assert.Equal(colorSpace, info.ColorSpace);
        Assert.Equal(8, info.BitsPerSample);
        Assert.Equal(1, info.Orientation);
        Assert.True(info.IsDecodable);
    }

    [Fact]
    public void DecodeReturnsSameInfo()
    {
        using var bmp = JpegCodec.Decode(TestImages.Read("progressive-422-q50.jpg"), out var info);
        Assert.Equal(bmp.Width, info.Width);
        Assert.Equal(bmp.Height, info.Height);
        Assert.True(info.IsProgressive);
    }

    [Fact]
    public void ReadsExifOrientationLittleEndian()
    {
        var info = JpegCodec.ReadInfo(TestImages.Read("exif-orientation-6.jpg"));
        Assert.Equal(6, info.Orientation);
        Assert.True(info.OrientationSwapsDimensions);
        // 不旋转像素：尺寸保持存储方向
        using var bmp = JpegCodec.Decode(TestImages.Read("exif-orientation-6.jpg"));
        Assert.Equal(240, bmp.Width);
        Assert.Equal(120, bmp.Height);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(3, 3)]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    public void ReadsExifOrientationBigEndian(int stored, int expected)
    {
        var data = InsertApp1(TestImages.Read("gray.jpg"), BigEndianExif(stored));
        Assert.Equal(expected, JpegCodec.ReadInfo(data).Orientation);
        using var bmp = JpegCodec.Decode(data);
        Assert.Equal(240, bmp.Width);
    }

    [Fact]
    public void MalformedExifIsIgnored()
    {
        var exif = BigEndianExif(6);
        exif[10] = 0x7F; // IFD0 偏移指向段外
        var data = InsertApp1(TestImages.Read("gray.jpg"), exif);
        Assert.Equal(1, JpegCodec.ReadInfo(data).Orientation);

        var truncated = BigEndianExif(6).Take(20).ToArray();
        Assert.Equal(1, JpegCodec.ReadInfo(InsertApp1(TestImages.Read("gray.jpg"), truncated)).Orientation);
    }

    [Theory]
    [InlineData("unsupported-arithmetic.jpg")]
    [InlineData("unsupported-12bit.jpg")]
    [InlineData("unsupported-lossless.jpg")]
    public void UnsupportedCodingThrowsNotSupported(string name)
    {
        var data = TestImages.Read(name);
        var info = JpegCodec.ReadInfo(data);
        Assert.False(info.IsDecodable);
        Assert.Equal(48, info.Width);
        Assert.Equal(32, info.Height);
        Assert.Throws<NotSupportedException>(() => JpegCodec.Decode(data));
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(data));
    }

    [Fact]
    public void UnsupportedFlagsAreReported()
    {
        Assert.True(JpegCodec.ReadInfo(TestImages.Read("unsupported-arithmetic.jpg")).IsArithmeticCoded);
        Assert.Equal(12, JpegCodec.ReadInfo(TestImages.Read("unsupported-12bit.jpg")).BitsPerSample);
        Assert.True(JpegCodec.ReadInfo(TestImages.Read("unsupported-lossless.jpg")).IsLossless);
    }

    /// <summary>"Exif\0\0" + 大端 TIFF，IFD0 含一个 Orientation 条目。</summary>
    static byte[] BigEndianExif(int orientation)
    {
        var b = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
        b.AddRange(new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8 });
        b.AddRange(new byte[] { 0, 2 });                                   // 2 个条目
        b.AddRange(new byte[] { 0x01, 0x0F, 0, 2, 0, 0, 0, 4, 0, 0, 0, 0 }); // Make（无关条目）
        b.AddRange(new byte[] { 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0 });
        b.AddRange(new byte[] { 0, 0, 0, 0 });
        return b.ToArray();
    }

    static byte[] InsertApp1(byte[] jpeg, byte[] payload)
    {
        int len = payload.Length + 2;
        var seg = new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(payload);
        return jpeg.Take(2).Concat(seg).Concat(jpeg.Skip(2)).ToArray();
    }
}
