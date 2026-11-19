namespace AwAUI.Imaging.UnitTests;

public class ImageDecoderTests
{
    [Fact]
    public void DetectsFormatsBySignature()
    {
        var png = File.ReadAllBytes(System.IO.Path.Combine(TestImages.GoldenDir, "shapes.png"));
        var jpg = TestImages.Read("baseline-420-q90.jpg");
        Assert.Equal(ImageFormat.Png, ImageDecoder.DetectFormat(png));
        Assert.Equal(ImageFormat.Jpeg, ImageDecoder.DetectFormat(jpg));
        Assert.Equal(ImageFormat.Unknown, ImageDecoder.DetectFormat(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(ImageFormat.Unknown, ImageDecoder.DetectFormat(ReadOnlySpan<byte>.Empty));
        Assert.True(JpegCodec.CanDecode(jpg));
        Assert.False(JpegCodec.CanDecode(png));
        Assert.False(PngCodec.CanDecode(jpg));
    }

    [Fact]
    public void DecodesBothFormats()
    {
        using var png = ImageDecoder.Load(System.IO.Path.Combine(TestImages.GoldenDir, "shapes.png"));
        using var pngDirect = PngCodec.Load(System.IO.Path.Combine(TestImages.GoldenDir, "shapes.png"));
        Assert.Equal(pngDirect.ToRgbaUnpremultiplied(), png.ToRgbaUnpremultiplied());

        using var jpg = ImageDecoder.Load(TestImages.PathOf("progressive-420.jpg"));
        using var jpgDirect = JpegCodec.Load(TestImages.PathOf("progressive-420.jpg"));
        Assert.Equal(jpgDirect.ToRgbaUnpremultiplied(), jpg.ToRgbaUnpremultiplied());
    }

    [Fact]
    public void UnknownFormatThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8' }));
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(Array.Empty<byte>()));
    }

    [Fact]
    public void CorruptPngThrowsOnlyDocumentedExceptions()
    {
        // PngCodec 本身在部分损坏数据上会抛出 OverflowException 等；ImageDecoder 统一转换为 InvalidDataException
        var original = File.ReadAllBytes(System.IO.Path.Combine(TestImages.GoldenDir, "shapes.png"));
        var rng = new Random(2024);
        for (int iter = 0; iter < 3000; iter++)
        {
            byte[] data;
            if (iter % 3 == 0)
            {
                data = original.AsSpan(0, 8 + rng.Next(original.Length - 8)).ToArray();
            }
            else
            {
                data = (byte[])original.Clone();
                for (int f = 1 + rng.Next(4); f > 0; f--)
                {
                    int pos = 8 + (iter % 2 == 0 ? rng.Next(Math.Min(64, data.Length - 8)) : rng.Next(data.Length - 8));
                    data[pos] ^= (byte)(1 << rng.Next(8));
                }
            }
            try
            {
                using var bmp = ImageDecoder.Decode(data);
            }
            catch (InvalidDataException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }

    [Fact]
    public void PngRoundTripStillWorks()
    {
        using var jpg = JpegCodec.Load(TestImages.PathOf("baseline-444-q85.jpg"));
        var encoded = PngCodec.Encode(jpg);
        using var back = ImageDecoder.Decode(encoded);
        Assert.Equal(jpg.ToRgbaUnpremultiplied(), back.ToRgbaUnpremultiplied());
    }
}
