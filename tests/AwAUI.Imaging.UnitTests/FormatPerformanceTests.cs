using System.Diagnostics;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>
/// 1280×880 的无损 / 有损 WebP 与 480×320×24 帧 GIF 的解码耗时（只报告，不设硬阈值），并用参考解码器输出的
/// SHA-256 校验结果逐字节一致。有意义的数字需要优化构建：
/// dotnet test tests/AwAUI.Imaging.UnitTests -c ImagingFormats -p:Optimize=true --filter FormatPerformance --logger "console;verbosity=detailed"
/// </summary>
public class FormatPerformanceTests(ITestOutputHelper output)
{
    static string Sha256(IEnumerable<byte[]> parts)
    {
        using var sha = SHA256.Create();
        foreach (var p in parts) sha.TransformBlock(p, 0, p.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return string.Concat(sha.Hash!.Select(b => b.ToString("x2")));
    }

    static string Expected(string sub, string name) => File.ReadAllText(FormatTestImages.PathOf(sub, name)).Trim();

    bool Optimized => !typeof(GifCodec).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
        .OfType<DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled);

    [Theory]
    [InlineData("perf-1280x880-lossless")]
    [InlineData("perf-1280x880-lossy")]
    public void WebP(string name)
    {
        var data = FormatTestImages.Read("webp", name + ".webp");
        var rgba = WebPCodec.DecodeRgba(data, out int w, out int h);
        Assert.Equal((1280, 880), (w, h));
        Assert.Equal(Expected("webp", name + ".sha256"), Sha256(new[] { rgba }));
        output.WriteLine($"optimized: {Optimized}");
        output.WriteLine($"{name}: {Measure(() => WebPCodec.DecodeRgba(data, out _, out _))}");
        output.WriteLine($"{name} (to Bitmap): {Measure(() => WebPCodec.Decode(data).Dispose())}");
    }

    [Fact]
    public void AnimatedGif()
    {
        var data = FormatTestImages.Read("gif", "perf-480x320-anim.gif");
        var anim = GifCodec.DecodeAnimation(data);
        Assert.Equal(24, anim.FrameCount);
        Assert.Equal(Expected("gif", "perf-480x320-anim.sha256"), Sha256(Enumerable.Range(0, anim.FrameCount).Select(anim.RenderFrameRgba)));
        output.WriteLine($"optimized: {Optimized}");
        output.WriteLine($"GIF 480x320 x 24 frames (all frames composed): {Measure(() =>
        {
            var a = GifCodec.DecodeAnimation(data);
            for (int i = 0; i < a.FrameCount; i++) a.RenderFrameRgba(i);
        })}");
    }

    /// <summary>预热（越过分层编译的 tier-0 阶段）后执行 15 次，报告最小值与中位数。</summary>
    static string Measure(Action action)
    {
        for (int i = 0; i < 40; i++) action();
        var times = new List<double>();
        for (int i = 0; i < 15; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return $"min {times[0]:F2} ms, median {times[times.Count / 2]:F2} ms";
    }
}
