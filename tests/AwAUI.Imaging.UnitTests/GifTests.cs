using System.Diagnostics;
using AwAUI.Graphics;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>GIF 解码与 giflib（索引）+ 浏览器语义合成的参考结果逐像素比较。</summary>
public class GifTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files()
    {
        var d = new TheoryData<string>();
        foreach (var n in FormatTestImages.Files("gif", "*.gif").Where(n => !n.StartsWith("perf-", StringComparison.Ordinal))) d.Add(n);
        return d;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesReference(string name)
    {
        var data = FormatTestImages.Read("gif", name);
        var anim = GifCodec.DecodeAnimation(data);
        string still = FormatTestImages.PathOf("gif", name.Replace(".gif", ".ref.png"));
        string strip = FormatTestImages.PathOf("gif", name.Replace(".gif", ".frames.png"));
        if (File.Exists(still))
        {
            var expected = FormatTestImages.ReadPngRgba(still, out int w, out int h);
            Assert.Equal(new PixelSize(w, h), anim.Size);
            FormatTestImages.AssertRgbaEqual(expected, anim.RenderFrameRgba(0), w, name);
            using var bmp = GifCodec.Decode(data);
            Assert.Equal(FormatTestImages.Premultiply(expected), FormatTestImages.PremultipliedBytes(bmp));
        }
        else
        {
            var expected = FormatTestImages.ReadPngRgba(strip, out int w, out int h);
            int frameBytes = anim.Width * anim.Height * 4;
            Assert.Equal(w, anim.Width);
            Assert.Equal(h, anim.Height * anim.FrameCount);
            for (int i = 0; i < anim.FrameCount; i++)
                FormatTestImages.AssertRgbaEqual(expected.AsSpan(i * frameBytes, frameBytes).ToArray(), anim.RenderFrameRgba(i), w, $"{name} frame {i}");
            // 倒序访问（从头重新合成）结果相同
            for (int i = anim.FrameCount - 1; i >= 0; i--)
                FormatTestImages.AssertRgbaEqual(expected.AsSpan(i * frameBytes, frameBytes).ToArray(), anim.RenderFrameRgba(i), w, $"{name} frame {i} (backwards)");
        }
        output.WriteLine($"{name}: {anim.Width}x{anim.Height}, {anim.FrameCount} frames, loop {anim.LoopCount}");
    }

    [Fact]
    public void AnimationMetadata()
    {
        var anim = GifCodec.DecodeAnimation(FormatTestImages.Read("gif", "anim-disposal.gif"));
        Assert.Equal(new PixelSize(64, 48), anim.Size);
        Assert.Equal(3, anim.LoopCount);   // NETSCAPE 循环 2 次 = 共播放 3 次
        Assert.Equal(new[] { 200, 100, 50, 0, 70, 1000 }, anim.Frames.Select(f => (int)f.Duration.TotalMilliseconds));
        Assert.Equal(new[] { FrameDisposal.None, FrameDisposal.Background, FrameDisposal.Previous, FrameDisposal.None, FrameDisposal.Background, FrameDisposal.None },
            anim.Frames.Select(f => f.Disposal));
        Assert.Equal(new PixelRect(58, 42, 10, 10), anim.Frames[4].Bounds);
        Assert.Equal(TimeSpan.FromMilliseconds(1420), anim.TotalDuration);

        var pillow = GifCodec.DecodeAnimation(FormatTestImages.Read("gif", "pillow-anim.gif"));
        Assert.Equal(6, pillow.LoopCount);
        Assert.Equal(new[] { 30, 40, 50, 60, 70, 80 }, pillow.Frames.Select(f => (int)f.Duration.TotalMilliseconds));

        Assert.Equal(1, GifCodec.DecodeAnimation(FormatTestImages.Read("gif", "photo-256.gif")).LoopCount);
        Assert.Equal(0, GifCodec.DecodeAnimation(FormatTestImages.Read("gif", "anim-truncated-frame.gif")).LoopCount);
    }

    [Fact]
    public void RenderIntoBgraBitmap()
    {
        var anim = GifCodec.DecodeAnimation(FormatTestImages.Read("gif", "anim-disposal.gif"));
        using var rgba = anim.RenderFrame(2);
        using var bgra = new Bitmap(anim.Size, PixelFormat.Bgra8888);
        anim.RenderFrame(2, bgra);
        Assert.Equal(rgba.ToRgbaUnpremultiplied(), bgra.ToRgbaUnpremultiplied());
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.RenderFrame(6));
        using var wrong = new Bitmap(new PixelSize(3, 3));
        Assert.Throws<ArgumentException>(() => anim.RenderFrame(0, wrong));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void CorruptedFilesOnlyThrowDocumentedExceptions(string name)
    {
        var original = FormatTestImages.Read("gif", name);
        var sw = Stopwatch.StartNew();
        var (ok, failed) = FormatTestImages.Fuzz(original, 1500, name.GetHashCode(), data =>
        {
            var anim = GifCodec.DecodeAnimation(data);
            for (int i = 0; i < anim.FrameCount; i++) anim.RenderFrameRgba(i);
        });
        output.WriteLine($"{name}: {ok} decoded, {failed} rejected in {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    static byte[] Header(int w, int h) =>
        new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 0x80, 0, 0, 0, 0, 0, 255, 255, 255 };

    static byte[] Frame(int x, int y, int w, int h, byte[] lzw) =>
        new byte[] { 0x2C, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8), (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 0, 2 }
            .Concat(new[] { (byte)lzw.Length }).Concat(lzw).Concat(new byte[] { 0 }).ToArray();

    [Fact]
    public void HugeCanvasWithTinyDataIsRejectedWithoutAllocating()
    {
        // 65535×65535 的逻辑屏幕，只有一个 1×1 的帧
        var data = Header(65535, 65535).Concat(Frame(0, 0, 1, 1, new byte[] { 0x44, 0x01 })).Concat(new byte[] { 0x3B }).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<NotSupportedException>(() => GifCodec.DecodeAnimation(data));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void ManyLargeFramesWithTinyDataAreRejected()
    {
        // 1000×1000 画布、2000 个覆盖整张画布、数据只有 2 字节的帧：总工作量远超输入允许的上限
        var frames = Enumerable.Range(0, 2000).SelectMany(_ => Frame(0, 0, 1000, 1000, new byte[] { 0x44, 0x01 }));
        var data = Header(1000, 1000).Concat(frames).Concat(new byte[] { 0x3B }).ToArray();
        var sw = Stopwatch.StartNew();
        Assert.Throws<NotSupportedException>(() => GifCodec.DecodeAnimation(data));
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void CanvasGrowsToFitFirstFrame()
    {
        // 评审回归：逻辑屏幕 2×2，第一帧位于 (1,0)、3×2，全为索引 1（白色）。与 Chrome（Skia / Wuffs）及 Pillow 一样，
        // 画布扩大为 4×2；画布初始透明（Pillow 在此填背景色，浏览器不填）
        var data = Header(2, 2).Concat(Frame(1, 0, 3, 2, new byte[] { 76, 18, 17, 5 })).Concat(new byte[] { 0x3B }).ToArray();
        var anim = GifCodec.DecodeAnimation(data);
        Assert.Equal(new PixelSize(4, 2), anim.Size);
        byte[] row = { 0, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 };
        Assert.Equal(row.Concat(row).ToArray(), anim.RenderFrameRgba(0));

        // 逻辑屏幕为 0 时取第一帧的范围
        anim = GifCodec.DecodeAnimation(Header(0, 0).Concat(Frame(1, 0, 3, 2, new byte[] { 76, 18, 17, 5 })).Concat(new byte[] { 0x3B }).ToArray());
        Assert.Equal(new PixelSize(4, 2), anim.Size);
        // 之后的帧不再扩大画布
        anim = GifCodec.DecodeAnimation(Header(2, 2).Concat(Frame(0, 0, 2, 2, new byte[] { 0x44, 0x01 }))
            .Concat(Frame(1, 0, 3, 2, new byte[] { 76, 18, 17, 5 })).Concat(new byte[] { 0x3B }).ToArray());
        Assert.Equal(new PixelSize(2, 2), anim.Size);
    }

    [Fact]
    public void InvalidLzwCodeThrowsInvalidData()
    {
        // 最小码长 2：清表码 4 之后紧跟码字 7（表中还没有）
        var data = Header(4, 1).Concat(Frame(0, 0, 4, 1, new byte[] { 0x3C })).Concat(new byte[] { 0x3B }).ToArray();
        Assert.Throws<InvalidDataException>(() => GifCodec.Decode(data));
        Assert.Throws<InvalidDataException>(() => GifCodec.DecodeAnimation(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }));
        Assert.Throws<InvalidDataException>(() => GifCodec.DecodeAnimation(Header(4, 4).Concat(new byte[] { 0x3B }).ToArray()));
        Assert.False(GifCodec.CanDecode(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'8', (byte)'a' }));
    }
}
