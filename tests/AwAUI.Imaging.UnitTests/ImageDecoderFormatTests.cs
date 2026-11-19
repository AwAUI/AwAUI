using AwAUI.Graphics;

namespace AwAUI.Imaging.UnitTests;

/// <summary>ImageDecoder 对 GIF / WebP / BMP / ICO 的识别与分派。</summary>
public class ImageDecoderFormatTests
{
    [Theory]
    [InlineData("gif", "photo-256.gif", ImageFormat.Gif)]
    [InlineData("gif", "anim-disposal.gif", ImageFormat.Gif)]
    [InlineData("webp", "ll-shapes.webp", ImageFormat.WebP)]
    [InlineData("webp", "lossy-q75.webp", ImageFormat.WebP)]
    [InlineData("webp", "anim-lossy.webp", ImageFormat.WebP)]
    [InlineData("bmp", "rgb24.bmp", ImageFormat.Bmp)]
    [InlineData("bmp", "core-pal4.bmp", ImageFormat.Bmp)]
    [InlineData("ico", "bmp-entries.ico", ImageFormat.Ico)]
    [InlineData("ico", "pointer.cur", ImageFormat.Ico)]
    public void DetectsAndDecodes(string sub, string name, ImageFormat format)
    {
        var data = FormatTestImages.Read(sub, name);
        Assert.Equal(format, ImageDecoder.DetectFormat(data));
        Assert.True(ImageDecoder.CanDecode(data));
        using var bmp = ImageDecoder.Decode(data);
        Assert.True(bmp.Width > 0);
        // Decode 与 DecodeAnimation 的第一帧一致
        var anim = ImageDecoder.DecodeAnimation(data);
        using var first = anim.RenderFrame(0);
        Assert.Equal(FormatTestImages.PremultipliedBytes(bmp), FormatTestImages.PremultipliedBytes(first));
        Assert.Equal(name.StartsWith("anim", StringComparison.Ordinal), anim.FrameCount > 1);
    }

    [Fact]
    public void StillFormatsBecomeSingleFrameAnimations()
    {
        var png = File.ReadAllBytes(System.IO.Path.Combine(TestImages.GoldenDir, "shapes.png"));
        var anim = ImageDecoder.DecodeAnimation(png);
        Assert.Equal(1, anim.FrameCount);
        Assert.Equal(1, anim.LoopCount);
        using var expected = PngCodec.Decode(png);
        using var frame = anim.RenderFrame(0);
        Assert.Equal(FormatTestImages.PremultipliedBytes(expected), FormatTestImages.PremultipliedBytes(frame));
        using var bgra = new Bitmap(anim.Size, PixelFormat.Bgra8888);
        anim.RenderFrame(0, bgra);
        Assert.Equal(expected.ToRgbaUnpremultiplied(), bgra.ToRgbaUnpremultiplied());
        Assert.Equal(expected.ToRgbaUnpremultiplied(), anim.RenderFrameRgba(0));
    }

    [Fact]
    public void ShortHeadersAreNotMisdetected()
    {
        Assert.Equal(ImageFormat.Unknown, ImageDecoder.DetectFormat(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8' }));
        Assert.Equal(ImageFormat.Unknown, ImageDecoder.DetectFormat(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0 }));
        Assert.Equal(ImageFormat.Unknown, ImageDecoder.DetectFormat(new byte[] { 0, 0, 1, 0, 0, 0 }));
        Assert.Throws<NotSupportedException>(() => ImageDecoder.DecodeAnimation(new byte[] { 1, 2, 3 }));
    }
}
