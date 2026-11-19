using AwAUI.Graphics;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>与 libjpeg-turbo（djpeg -dct int，默认平滑上采样）的解码结果逐像素比较。</summary>
public class JpegReferenceTests(ITestOutputHelper output)
{
    public static TheoryData<string> ReferenceImages()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(TestImages.Dir, "*.ref.png").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(path);
            data.Add(name.Substring(0, name.Length - ".ref.png".Length) + ".jpg");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ReferenceImages))]
    public void MatchesLibjpegReference(string name)
    {
        using var actual = JpegCodec.Decode(TestImages.Read(name));
        using var expected = PngCodec.Load(TestImages.PathOf(name.Replace(".jpg", ".ref.png")));
        var diff = TestImages.Compare(actual, expected);
        output.WriteLine($"{name}: {actual.Width}x{actual.Height} max diff {diff.Max} at ({diff.MaxX},{diff.MaxY}), " +
                         $"mean diff {diff.Mean:F4}, channels > 2: {diff.Over2}");
        Assert.True(diff.Max <= 2, $"{name}: max diff {diff.Max} at ({diff.MaxX},{diff.MaxY}), mean {diff.Mean:F4}");
    }

    [Fact]
    public void ComparisonDetectsDifferences()
    {
        // 同一张源图、不同质量：比较器必须能发现差异，避免「全部为 0」是比较本身失效
        using var q80 = JpegCodec.Load(TestImages.PathOf("baseline-422-q80.jpg"));
        using var q50 = JpegCodec.Load(TestImages.PathOf("progressive-422-q50.jpg"));
        var diff = TestImages.Compare(q80, q50);
        Assert.True(diff.Max > 2);
        Assert.True(diff.Mean > 0.1);
    }

    [Fact]
    public void ReferenceSetCoversAllFeatures()
    {
        var names = ((IEnumerable<object[]>)ReferenceImages()).Select(r => (string)r[0]).ToArray();
        Assert.True(names.Length >= 34);
        foreach (var required in new[]
                 {
                     "cmyk.jpg", "ycck.jpg", "progressive-444-restart.jpg",
                     // 色度平面宽度 <= 2（libjpeg 退化为复制上采样）
                     "tiny-2x3-420.jpg", "tiny-3x3-420-progressive.jpg", "tiny-3x5-422.jpg", "tiny-4x4-420.jpg",
                     "tiny-4x17-420.jpg", "tiny-5x5-420.jpg",
                     "sof1-16bit-q3.jpg", "sequential-noninterleaved.jpg", "progressive-custom-script.jpg",
                     "sample-3x1.jpg", "sample-3x2.jpg", "sample-4x2.jpg", "missing-dht.jpg",
                 })
        {
            Assert.Contains(required, names);
        }
    }
}
