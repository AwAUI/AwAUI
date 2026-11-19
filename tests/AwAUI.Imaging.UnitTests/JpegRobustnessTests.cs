using System.Diagnostics;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>截断与损坏的数据只能抛出 InvalidDataException / NotSupportedException，且不能卡死。</summary>
public class JpegRobustnessTests(ITestOutputHelper output)
{
    static readonly string[] Samples =
    {
        "baseline-420-q90.jpg", "baseline-restart-rows.jpg", "progressive-420.jpg",
        "progressive-444-restart.jpg", "gray-progressive.jpg", "ycck.jpg", "odd-422-progressive.jpg",
    };

    static void DecodeExpectingFailureOrSuccess(byte[] data, bool mustFail)
    {
        try
        {
            using var bmp = JpegCodec.Decode(data);
            Assert.False(mustFail, "Decoding truncated data should fail.");
        }
        catch (InvalidDataException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void TruncatedFilesThrowInvalidData(string name)
    {
        var data = TestImages.Read(name);
        var sw = Stopwatch.StartNew();
        // 每个长度都试：头部、扫描中间、标记中间、只缺 EOI
        int step = Math.Max(1, data.Length / 600);
        for (int len = 0; len < data.Length; len += step)
            DecodeExpectingFailureOrSuccess(data.AsSpan(0, len).ToArray(), mustFail: true);
        for (int len = Math.Max(0, data.Length - 40); len < data.Length; len++)
            DecodeExpectingFailureOrSuccess(data.AsSpan(0, len).ToArray(), mustFail: true);
        output.WriteLine($"{name}: {data.Length} bytes, truncations checked in {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void CorruptedFilesNeverCrashOrHang(string name)
    {
        var original = TestImages.Read(name);
        var rng = new Random(name.GetHashCode() ^ 12345);
        var sw = Stopwatch.StartNew();
        int ok = 0, failed = 0;
        for (int iter = 0; iter < 400; iter++)
        {
            var data = (byte[])original.Clone();
            int flips = 1 + rng.Next(8);
            // 一半的变异集中在头部（表、帧、扫描头），一半落在整个文件
            for (int f = 0; f < flips; f++)
            {
                int pos = iter % 2 == 0 ? rng.Next(Math.Min(data.Length, 700)) : rng.Next(data.Length);
                switch (rng.Next(3))
                {
                    case 0: data[pos] = (byte)rng.Next(256); break;
                    case 1: data[pos] ^= (byte)(1 << rng.Next(8)); break;
                    default: data[pos] = 0xFF; break;
                }
            }
            try
            {
                using var bmp = JpegCodec.Decode(data);
                ok++;
            }
            catch (InvalidDataException)
            {
                failed++;
            }
            catch (NotSupportedException)
            {
                failed++;
            }
        }
        output.WriteLine($"{name}: decoded {ok}, rejected {failed}, {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60));
    }

    public static TheoryData<string> SampleNames()
    {
        var d = new TheoryData<string>();
        foreach (var s in Samples) d.Add(s);
        return d;
    }

    [Fact]
    public void GarbageAndEdgeCasesThrowInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(new byte[] { 0xFF, 0xD8 }));
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.Throws<InvalidDataException>(() => JpegCodec.ReadInfo(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0, 2 }));
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0, 200, 8 }));
    }

    [Fact]
    public void HugeDeclaredDimensionsAreRejectedWithoutAllocating()
    {
        var data = TestImages.Read("gray.jpg");
        int sof = FindMarker(data, 0xC0);
        // 20000×20000：未超过位图上限，但 4 KB 的文件不可能容纳这么多块
        data[sof + 5] = 0x4E; data[sof + 6] = 0x20; // 高度
        data[sof + 7] = 0x4E; data[sof + 8] = 0x20; // 宽度
        long allocated = MeasureAllocation(() => Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(data)));
        Assert.True(allocated < 1 << 20, $"allocated {allocated} bytes");
        Assert.Equal(20000, JpegCodec.ReadInfo(data).Width);

        // 65520×65520：超过位图上限
        data[sof + 5] = 0xFF; data[sof + 6] = 0xF0;
        data[sof + 7] = 0xFF; data[sof + 8] = 0xF0;
        Assert.Throws<NotSupportedException>(() => JpegCodec.Decode(data));
    }

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC2)]
    public void HugeMultiComponentHeaderIsRejectedWithoutAllocating(int sofMarker)
    {
        // 64 KB 的文件：23000×23000、4 个分量都用 4x4 采样。MCU 数（约 52 万）不超过 8 × 文件字节数，
        // 但每个 MCU 有 64 个块；旧的「按 MCU 计数」检查会放行并分配数 GB 的平面与系数。
        var file = new List<byte> { 0xFF, 0xD8 };
        file.AddRange(new byte[] { 0xFF, (byte)sofMarker, 0, 20, 8, 0x59, 0xD8, 0x59, 0xD8, 4 });
        for (int i = 1; i <= 4; i++) file.AddRange(new byte[] { (byte)i, 0x44, 0 });
        while (file.Count < 64000)
        {
            file.AddRange(new byte[] { 0xFF, 0xFE, 0x3F, 0xFF });   // COM 段填充
            file.AddRange(new byte[0x3FFD]);
        }
        file.AddRange(new byte[] { 0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 0, 0, 0x12, 0x34, 0xFF, 0xD9 });
        var data = file.ToArray();

        Assert.True(JpegCodec.ReadInfo(data).IsDecodable);
        long allocated = MeasureAllocation(() => Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(data)));
        output.WriteLine($"SOF {sofMarker:X2}: {data.Length} bytes, allocated {allocated} bytes before rejecting");
        Assert.True(allocated < 1 << 20, $"allocated {allocated} bytes");
    }

    [Fact]
    public void TruncatedProgressiveFileDoesNotAllocateFullPlanes()
    {
        // 渐进模式的采样平面在全部扫描成功后才分配；截断在第一个扫描中的文件只分配系数缓冲
        // 截在文件 60% 处（已越过若干扫描），保证能通过 SOF 时的尺寸检查、真正进入扫描解码
        var data = TestImages.Read("perf-1280x880-progressive.jpg");
        var truncated = data.AsSpan(0, data.Length * 6 / 10).ToArray();
        long allocated = MeasureAllocation(() => Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(truncated)));
        output.WriteLine($"truncated progressive 1280x880: allocated {allocated} bytes");
        // 系数：1280×880 的 4:2:0 共 26400 块 × 128 字节 ≈ 3.4 MB 必须已分配；平面（约 1.7 MB）与位图都不应出现
        Assert.True(allocated > 3_000_000, $"allocated {allocated} bytes: scans were not reached");
        Assert.True(allocated < 4_000_000, $"allocated {allocated} bytes");
    }

    [Fact]
    public void RepeatedRefinementScansAreRejected()
    {
        // 构造的渐进文件可以无限重复同一频带的细化扫描，每次都要遍历所有块；必须按进度状态拒绝
        var data = TestImages.Read("gray-progressive.jpg");
        var segments = JpegFile.Segments(data);
        var scans = segments.Where(s => s.Marker == 0xDA).ToList();
        var refine = scans.Last(s => (data[s.Offset + 4 + 2 * data[s.Offset + 4] + 3] >> 4) != 0);
        var firstAc = scans.First(s => data[s.Offset + 4 + 2 * data[s.Offset + 4] + 1] != 0);
        int eoi = segments.Count - 1;

        var repeated = new List<JpegFile.Segment>(segments.Take(eoi));
        for (int i = 0; i < 300; i++) repeated.Add(refine);
        repeated.Add(segments[eoi]);
        var sw = Stopwatch.StartNew();
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(JpegFile.Build(data, repeated)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));

        // 重复的首扫描（Ah = 0）同样不合进度
        var twice = new List<JpegFile.Segment>(segments.Take(eoi)) { firstAc, segments[eoi] };
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(JpegFile.Build(data, twice)));

        // 原文件本身合法
        using var ok = JpegCodec.Decode(JpegFile.Build(data, segments));
    }

    [Fact]
    public void RefinementMustAdvanceOneBit()
    {
        var data = TestImages.Read("gray-progressive.jpg");
        foreach (var scan in JpegFile.Segments(data).Where(s => s.Marker == 0xDA))
        {
            int ahal = scan.Offset + 4 + 2 * data[scan.Offset + 4] + 3;
            if (data[ahal] >> 4 == 0) continue;
            var bad = (byte[])data.Clone();
            bad[ahal] = (byte)((data[ahal] & 0xF0) | ((data[ahal] & 15) + 1));   // Al = Ah：没有推进
            Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(bad));
            return;
        }
        Assert.Fail("gray-progressive.jpg has no refinement scan");
    }

    static long MeasureAllocation(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void ZeroHeightNeedsDnlAndIsNotSupported()
    {
        var data = TestImages.Read("gray.jpg");
        int sof = FindMarker(data, 0xC0);
        data[sof + 5] = 0; data[sof + 6] = 0;
        Assert.Throws<NotSupportedException>(() => JpegCodec.Decode(data));
    }

    [Fact]
    public void MissingRestartMarkerThrows()
    {
        var data = TestImages.Read("baseline-restart-rows.jpg");
        int sos = FindMarker(data, 0xDA);
        int i = sos + 2;
        while (!(data[i] == 0xFF && data[i + 1] == 0xD0)) i++;
        data[i + 1] = 0xD3; // 序号错乱
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(data));
    }

    static int FindMarker(byte[] data, byte marker)
    {
        for (int i = 2; i + 1 < data.Length; i++)
            if (data[i] == 0xFF && data[i + 1] == marker) return i;
        throw new InvalidOperationException("marker not found");
    }
}
