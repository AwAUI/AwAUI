using AwAUI.Tests.Shared;
using System.Diagnostics;
using AwAUI.Imaging.WebP;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>WebP 位流解码器的直接模糊测试与针对性的恶意输入。</summary>
public class WebPRobustnessTests(ITestOutputHelper output)
{
    /// <summary>VP8L 位写入（低位在前）。</summary>
    sealed class BitWriter
    {
        readonly List<byte> _bytes = new();
        ulong _acc;
        int _n;

        public BitWriter Put(uint value, int bits)
        {
            _acc |= (ulong)value << _n;
            _n += bits;
            while (_n >= 8)
            {
                _bytes.Add((byte)_acc);
                _acc >>= 8;
                _n -= 8;
            }
            return this;
        }

        public byte[] ToArray()
        {
            var r = new List<byte>(_bytes);
            if (_n > 0) r.Add((byte)_acc);
            return r.ToArray();
        }
    }

    /// <summary>纯色 VP8L：5 棵只有一个符号的简单码，像素不消耗任何位。</summary>
    static byte[] SolidVp8L(int width, int height, byte r, byte g, byte b, byte a)
    {
        var w = new BitWriter();
        w.Put(0x2F, 8).Put((uint)(width - 1), 14).Put((uint)(height - 1), 14).Put(1, 1).Put(0, 3);
        w.Put(0, 1).Put(0, 1).Put(0, 1);   // 无变换、无颜色缓存、无元码
        foreach (var s in new[] { g, r, b, a, (byte)0 }) w.Put(1, 1).Put(0, 1).Put(1, 1).Put(s, 8);
        return w.ToArray();
    }

    static byte[] Chunk(string tag, byte[] payload)
    {
        var r = new List<byte>(System.Text.Encoding.ASCII.GetBytes(tag));
        r.AddRange(BitConverter.GetBytes(payload.Length));
        r.AddRange(payload);
        if (payload.Length % 2 == 1) r.Add(0);
        return r.ToArray();
    }

    static byte[] Riff(params byte[][] chunks)
    {
        var body = new List<byte>(System.Text.Encoding.ASCII.GetBytes("WEBP"));
        foreach (var c in chunks) body.AddRange(c);
        var r = new List<byte>(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        r.AddRange(BitConverter.GetBytes(body.Count));
        r.AddRange(body);
        return r.ToArray();
    }

    static byte[] U24(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) };

    [Fact]
    public void ZeroCostLosslessImageDecodes()
    {
        var data = Riff(Chunk("VP8L", SolidVp8L(1000, 1000, 0x40, 0x80, 0x20, 0xFF)));
        Assert.True(data.Length < 40);
        var rgba = WebPCodec.DecodeRgba(data, out int w, out int h);
        Assert.Equal((1000, 1000), (w, h));
        for (int i = 0; i < rgba.Length; i += 4)
            if (rgba[i] != 0x40 || rgba[i + 1] != 0x80 || rgba[i + 2] != 0x20 || rgba[i + 3] != 0xFF) Assert.Fail($"pixel {i / 4}");
    }

    /// <summary>
    /// 1×1 图像，元 Huffman 图把唯一的像素映射到第 65535 组，因此要读取并校验 65536 组码（每组 5 棵只有一个符号的简单码，
    /// 约 20 位），且绿色树的字母表带 11 位颜色缓存（2328 个符号）。这是每输入字节工作量最大的合法结构之一。
    /// </summary>
    static byte[] MaxGroupsVp8L()
    {
        var w = new BitWriter();
        w.Put(0x2F, 8).Put(0, 14).Put(0, 14).Put(0, 1).Put(0, 3);
        w.Put(0, 1);                       // 无变换
        w.Put(1, 1).Put(11, 4);            // 颜色缓存 11 位
        w.Put(1, 1).Put(0, 3);             // 元 Huffman 码，huffBits = 2
        // 实体图像：无颜色缓存；绿 = 红 = 0xFF（组号 0xFFFF），其余 0
        w.Put(0, 1);
        foreach (var s in new byte[] { 0xFF, 0xFF, 0, 0, 0 }) w.Put(1, 1).Put(0, 1).Put(1, 1).Put(s, 8);
        // 65536 组，每棵树为单符号 0 的简单码
        for (int g = 0; g < 65536; g++)
            for (int t = 0; t < 5; t++) w.Put(1, 1).Put(0, 1).Put(0, 1).Put(0, 1);
        return w.ToArray();
    }

    [Fact]
    public void MaximumHuffmanGroupCountStaysFast()
    {
        // 评审补充：此前「大量未使用的 Huffman 组」的开销只有分析、没有测试
        var data = Riff(Chunk("VP8L", MaxGroupsVp8L()));
        // 计线程 CPU 时间而非墙钟：机器超额订阅时墙钟会把等待 CPU 的时间算进去（负载 280+ 时墙钟 13–40 s）
        double t0 = ThreadCpuClock.NowMs();
        var rgba = WebPCodec.DecodeRgba(data, out int w, out int h);
        double cpuMs = ThreadCpuClock.NowMs() - t0;
        output.WriteLine($"{data.Length} bytes, 65536 groups: {cpuMs:F0} ms {(ThreadCpuClock.IsThreadCpuTime ? "thread CPU" : "wall")}");
        Assert.Equal((1, 1), (w, h));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, rgba);
        Assert.True(cpuMs < 10_000, $"{cpuMs:F0} ms");
    }

    [Fact]
    public void ZeroCostLosslessImageBeyondBudgetIsRejected()
    {
        var data = Riff(Chunk("VP8L", SolidVp8L(16384, 16384, 1, 2, 3, 4)));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<NotSupportedException>(() => WebPCodec.Decode(data));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void HugeLossyFrameWithShortPartitionIsRejected()
    {
        var vp8 = new byte[100];
        int partLen = 50;
        int tag = (0) | (0 << 1) | (1 << 4) | (partLen << 5);
        vp8[0] = (byte)tag; vp8[1] = (byte)(tag >> 8); vp8[2] = (byte)(tag >> 16);
        vp8[3] = 0x9D; vp8[4] = 0x01; vp8[5] = 0x2A;
        vp8[6] = 0xFF; vp8[7] = 0x3F; vp8[8] = 0xFF; vp8[9] = 0x3F;   // 16383 × 16383
        var data = Riff(Chunk("VP8 ", vp8));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => WebPCodec.Decode(data));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void ManyLargeAnimationFramesAreRejected()
    {
        var frame = SolidVp8L(1000, 1000, 1, 2, 3, 255);
        var chunks = new List<byte[]>
        {
            Chunk("VP8X", new byte[] { 0x02, 0, 0, 0 }.Concat(U24(999)).Concat(U24(999)).ToArray()),
            Chunk("ANIM", new byte[] { 0, 0, 0, 0, 0, 0 }),
        };
        for (int i = 0; i < 300; i++)
            chunks.Add(Chunk("ANMF", U24(0).Concat(U24(0)).Concat(U24(999)).Concat(U24(999)).Concat(U24(10)).Concat(new byte[] { 0 })
                .Concat(Chunk("VP8L", frame)).ToArray()));
        var data = Riff(chunks.ToArray());
        var sw = Stopwatch.StartNew();
        Assert.Throws<NotSupportedException>(() => WebPCodec.DecodeAnimation(data));
        Assert.True(sw.ElapsedMilliseconds < 1000);
        // 同样结构、尺寸在预算之内时可以正常合成
        var small = SolidVp8L(100, 100, 1, 2, 3, 255);
        var smallChunks = new List<byte[]>
        {
            Chunk("VP8X", new byte[] { 0x02, 0, 0, 0 }.Concat(U24(99)).Concat(U24(99)).ToArray()),
            Chunk("ANIM", new byte[] { 0, 0, 0, 0, 0, 0 }),
        };
        for (int i = 0; i < 3; i++)
            smallChunks.Add(Chunk("ANMF", U24(0).Concat(U24(0)).Concat(U24(99)).Concat(U24(99)).Concat(U24(10)).Concat(new byte[] { 0 })
                .Concat(Chunk("VP8L", small)).ToArray()));
        var anim = WebPCodec.DecodeAnimation(Riff(smallChunks.ToArray()));
        Assert.Equal(3, anim.FrameCount);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, anim.RenderFrameRgba(2).AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void HugeCanvasIsRejected()
    {
        var data = Riff(Chunk("VP8X", new byte[] { 0x02, 0, 0, 0 }.Concat(U24(0xFFFFFF)).Concat(U24(0xFFFFFF)).ToArray()),
            Chunk("ANIM", new byte[6]));
        Assert.Throws<NotSupportedException>(() => WebPCodec.DecodeAnimation(data));
    }

    [Fact]
    public void StructuralErrors()
    {
        var solid = SolidVp8L(4, 4, 1, 2, 3, 4);
        Assert.Throws<InvalidDataException>(() => WebPCodec.Decode(Riff()));
        Assert.Throws<InvalidDataException>(() => WebPCodec.Decode(Riff(Chunk("VP8X", new byte[10]))));
        var good = Riff(Chunk("VP8L", solid));
        Assert.Throws<InvalidDataException>(() => WebPCodec.Decode(good.AsSpan(0, good.Length - 1).ToArray()));
        // 版本号非 0
        var badVersion = (byte[])solid.Clone();
        badVersion[4] |= 0x20;
        Assert.Throws<InvalidDataException>(() => WebPCodec.Decode(Riff(Chunk("VP8L", badVersion))));
        // VP8X 画布与图像尺寸不一致
        var mismatch = Riff(Chunk("VP8X", new byte[] { 0, 0, 0, 0 }.Concat(U24(4)).Concat(U24(3)).ToArray()), Chunk("VP8L", solid));
        Assert.Throws<InvalidDataException>(() => WebPCodec.Decode(mismatch));
        // 帧超出画布
        var outside = Riff(Chunk("VP8X", new byte[] { 2, 0, 0, 0 }.Concat(U24(3)).Concat(U24(3)).ToArray()), Chunk("ANIM", new byte[6]),
            Chunk("ANMF", U24(1).Concat(U24(0)).Concat(U24(3)).Concat(U24(3)).Concat(U24(10)).Concat(new byte[] { 0 }).Concat(Chunk("VP8L", solid)).ToArray()));
        Assert.Throws<InvalidDataException>(() => WebPCodec.DecodeAnimation(outside));
        Assert.True(WebPCodec.CanDecode(good));
        Assert.False(WebPCodec.CanDecode(good.AsSpan(0, 11)));
    }

    /// <summary>从测试文件中取出各位流块，直接对位流解码器做变异测试（不经过 RIFF 大小检查）。</summary>
    public static TheoryData<string> Bitstreams()
    {
        var d = new TheoryData<string>();
        foreach (var n in new[]
                 {
                     "ll-photo-m6.webp", "ll-palette-4.webp", "ll-noise-alpha.webp", "ll-gradients-z9.webp", "lossy-q75.webp",
                     "lossy-q5-simple-filter.webp", "lossy-partitions.webp", "lossy-libvpx-8parts.webp", "lossy-alpha-lossless.webp",
                     "lossy-alpha-filter-gradient.webp",
                 })
            d.Add(n);
        return d;
    }

    static IEnumerable<(string Tag, byte[] Payload)> Chunks(byte[] d)
    {
        int pos = 12;
        while (pos + 8 <= d.Length)
        {
            string tag = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            int size = BitConverter.ToInt32(d, pos + 4);
            if (tag == "VP8X") { pos += 8 + size; continue; }
            yield return (tag, d.AsSpan(pos + 8, size).ToArray());
            pos += 8 + size + (size & 1);
        }
    }

    [Theory]
    [MemberData(nameof(Bitstreams))]
    public void MutatedBitstreamsOnlyThrowDocumentedExceptions(string name)
    {
        var file = FormatTestImages.Read("webp", name);
        foreach (var (tag, payload) in Chunks(file))
        {
            Action<byte[]> decode = tag switch
            {
                "VP8L" => p => Vp8LDecoder.Decode(p, 0, p.Length, p.Length, out _, out _),
                "VP8 " => p =>
                {
                    var img = Vp8Decoder.Decode(p, 0, p.Length, p.Length);
                    YuvConverter.ToRgba(img, new byte[img.Width * img.Height * 4], 0, img.Width * 4, null);
                },
                "ALPH" => p =>
                {
                    var info = WebPCodec.ReadInfo(file);
                    WebPAlpha.Decode(p, 0, p.Length, info.Width, info.Height);
                },
                _ => _ => { },
            };
            var sw = Stopwatch.StartNew();
            var (ok, failed) = FormatTestImages.Fuzz(payload, 2000, name.GetHashCode() ^ tag.GetHashCode(), decode, 64);
            output.WriteLine($"{name} {tag}: {ok} decoded, {failed} rejected in {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60));
        }
    }
}
