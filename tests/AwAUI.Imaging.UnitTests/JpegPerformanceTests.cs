using System.Diagnostics;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>
/// 解码耗时（只报告，不设硬阈值）。有意义的数字需要优化构建：
/// dotnet test tests/AwAUI.Imaging.UnitTests -c ImagingDev -p:Optimize=true --logger "console;verbosity=detailed"
/// </summary>
public class JpegPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void BaselineAndProgressiveDecodeIdenticallyAndReportTiming()
    {
        // 两个文件由同一张图以相同质量编码，量化后的系数相同，解码结果必须逐字节一致
        var baseline = TestImages.Read("perf-1280x880.jpg");
        var progressive = TestImages.Read("perf-1280x880-progressive.jpg");
        using (var a = JpegCodec.Decode(baseline))
        using (var b = JpegCodec.Decode(progressive))
        {
            Assert.Equal(1280, a.Width);
            Assert.Equal(880, a.Height);
            Assert.Equal(a.ToRgbaUnpremultiplied(), b.ToRgbaUnpremultiplied());
        }

        bool optimized = !typeof(JpegCodec).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled);
        output.WriteLine($"Library JIT optimizer enabled: {optimized}");
        output.WriteLine($"baseline    1280x880: {Measure(baseline)}");
        output.WriteLine($"progressive 1280x880: {Measure(progressive)}");
    }

    /// <summary>预热后解码 25 次，报告最小值与中位数（机器繁忙时最小值更有参考意义）。</summary>
    static string Measure(byte[] data)
    {
        for (int i = 0; i < 40; i++) JpegCodec.Decode(data).Dispose();   // 越过分层编译的 tier-0 阶段
        var times = new List<double>();
        for (int i = 0; i < 25; i++)
        {
            var sw = Stopwatch.StartNew();
            JpegCodec.Decode(data).Dispose();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return $"min {times[0]:F2} ms, median {times[times.Count / 2]:F2} ms";
    }
}
