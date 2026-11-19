using System.Diagnostics;
using AwAUI.Graphics;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>WebP 解码与 libwebp 1.6（Pillow 的 WebPAnimDecoder，静态图另与 dwebp 核对）逐像素比较。</summary>
public class WebPTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files()
    {
        var d = new TheoryData<string>();
        foreach (var n in FormatTestImages.Files("webp", "*.webp").Where(n => !n.StartsWith("perf-", StringComparison.Ordinal))) d.Add(n);
        return d;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesLibwebp(string name)
    {
        var data = FormatTestImages.Read("webp", name);
        string still = FormatTestImages.PathOf("webp", name.Replace(".webp", ".ref.png"));
        var info = WebPCodec.ReadInfo(data);
        if (File.Exists(still))
        {
            var expected = FormatTestImages.ReadPngRgba(still, out int w, out int h);
            var actual = WebPCodec.DecodeRgba(data, out int aw, out int ah);
            Assert.Equal((w, h), (aw, ah));
            Assert.False(info.IsAnimated);
            FormatTestImages.AssertRgbaEqual(expected, actual, w, name);
            using var bmp = WebPCodec.Decode(data);
            Assert.Equal(FormatTestImages.Premultiply(expected), FormatTestImages.PremultipliedBytes(bmp));
            output.WriteLine($"{name}: {w}x{h} lossless={info.IsLossless} alpha={info.HasAlpha}");
        }
        else
        {
            var anim = WebPCodec.DecodeAnimation(data);
            var expected = FormatTestImages.ReadPngRgba(FormatTestImages.PathOf("webp", name.Replace(".webp", ".frames.png")), out int w, out int h);
            Assert.True(info.IsAnimated);
            Assert.Equal(w, anim.Width);
            Assert.Equal(h, anim.Height * anim.FrameCount);
            int frameBytes = anim.Width * anim.Height * 4;
            for (int i = 0; i < anim.FrameCount; i++)
                FormatTestImages.AssertRgbaEqual(expected.AsSpan(i * frameBytes, frameBytes).ToArray(), anim.RenderFrameRgba(i), w, $"{name} frame {i}");
            for (int i = anim.FrameCount - 1; i >= 0; i -= 2)
                FormatTestImages.AssertRgbaEqual(expected.AsSpan(i * frameBytes, frameBytes).ToArray(), anim.RenderFrameRgba(i), w, $"{name} frame {i} (backwards)");
            output.WriteLine($"{name}: {anim.Width}x{anim.Height}, {anim.FrameCount} frames, loop {anim.LoopCount}");
        }
    }

    [Fact]
    public void AnimationMetadata()
    {
        var anim = WebPCodec.DecodeAnimation(FormatTestImages.Read("webp", "anim-mux-dispose-blend.webp"));
        Assert.Equal(new PixelSize(56, 40), anim.Size);
        Assert.Equal(7, anim.LoopCount);
        Assert.Equal(new[] { 100, 70, 60, 50, 40 }, anim.Frames.Select(f => (int)f.Duration.TotalMilliseconds));
        Assert.Equal(new[] { FrameDisposal.None, FrameDisposal.Background, FrameDisposal.None, FrameDisposal.Background, FrameDisposal.None },
            anim.Frames.Select(f => f.Disposal));
        Assert.Equal(new[] { FrameBlending.Source, FrameBlending.Over, FrameBlending.Over, FrameBlending.Source, FrameBlending.Over },
            anim.Frames.Select(f => f.Blending));
        Assert.Equal(new PixelRect(4, 6, 20, 16), anim.Frames[1].Bounds);
        Assert.Equal(Color.FromRgba(10, 20, 30, 255), anim.BackgroundColor);
        Assert.Equal(3, WebPCodec.DecodeAnimation(FormatTestImages.Read("webp", "anim-lossless.webp")).LoopCount);

        var still = WebPCodec.DecodeAnimation(FormatTestImages.Read("webp", "ll-shapes.webp"));
        Assert.Equal(1, still.FrameCount);
        Assert.Equal(1, still.LoopCount);
        var info = WebPCodec.ReadInfo(FormatTestImages.Read("webp", "lossy-alpha-lossless.webp"));
        Assert.True(info.HasAlpha);
        Assert.False(info.IsLossless);
        Assert.Equal(240, info.Width);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void CorruptedFilesOnlyThrowDocumentedExceptions(string name)
    {
        var original = FormatTestImages.Read("webp", name);
        var sw = Stopwatch.StartNew();
        var (ok, failed) = FormatTestImages.Fuzz(original, 1500, name.GetHashCode(), data =>
        {
            var anim = WebPCodec.DecodeAnimation(data);
            for (int i = 0; i < anim.FrameCount; i++) anim.RenderFrameRgba(i);
        });
        output.WriteLine($"{name}: {ok} decoded, {failed} rejected in {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60));
    }
}
