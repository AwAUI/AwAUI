namespace AwAUI.Unicode.UnitTests;

/// <summary>运行 Unicode 官方分段一致性测试（GraphemeBreakTest / WordBreakTest / SentenceBreakTest / LineBreakTest）。</summary>
public class SegmentationConformanceTests
{
    static List<int> Graphemes(string text)
    {
        var result = new List<int> { 0 };
        foreach (var g in TextSegmenter.EnumerateGraphemes(text.AsSpan())) result.Add(g.End);
        return result;
    }

    static List<int> Words(string text)
    {
        var result = new List<int> { 0 };
        foreach (var w in TextSegmenter.EnumerateWords(text.AsSpan())) result.Add(w.End);
        return result;
    }

    static List<int> Sentences(string text)
    {
        var result = new List<int> { 0 };
        foreach (var w in TextSegmenter.EnumerateSentences(text.AsSpan())) result.Add(w.End);
        return result;
    }

    static List<int> Lines(string text)
    {
        // LineBreakTest 把位置 0 记作 ×（LB2），只列出之后的断点
        var result = new List<int>();
        foreach (var b in TextSegmenter.EnumerateLineBreaks(text.AsSpan())) result.Add(b.Position);
        return result;
    }

    static void Run(string file, int expectedCases, Func<string, List<int>> segment, int expectedFailures, Func<int[], int[]>? adjust = null)
    {
        var cases = TestData.ReadBreakTests(file);
        Assert.Equal(expectedCases, cases.Count);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var expected = adjust != null ? adjust(c.Boundaries) : c.Boundaries;
            var actual = segment(c.Text);
            if (!actual.SequenceEqual(expected))
                failures.Add($"line {c.Line}: [{TestData.Hex(c.Text)}] expected {string.Join(",", expected)} got {string.Join(",", actual)}\n    {c.Source}");
        }
        Assert.True(failures.Count == expectedFailures,
            $"{file}: {failures.Count} of {cases.Count} cases failed (expected {expectedFailures}).\n" + string.Join("\n", failures.Take(30)));
    }

    [Fact]
    public void GraphemeBreakTest() => Run("GraphemeBreakTest.txt", 853, Graphemes, 0);

    [Fact]
    public void WordBreakTest() => Run("WordBreakTest.txt", 1944, Words, 0);

    [Fact]
    public void SentenceBreakTest() => Run("SentenceBreakTest.txt", 512, Sentences, 0);

    [Fact]
    public void LineBreakTest() => Run("LineBreakTest.txt", 19346, Lines, 0, b => b.Where(x => x != 0).ToArray());
}
