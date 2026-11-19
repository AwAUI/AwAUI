using System.Diagnostics;

namespace AwAUI.Unicode.UnitTests;

/// <summary>回溯安全边界的正确性，以及病态输入下分段保持线性时间。</summary>
public class SegmentationRobustnessTests
{
    const int GraphemeBits = UnicodeTables.SegGcbMask | (3 << UnicodeTables.SegInCBShift) | UnicodeTables.SegExtPictBit;

    /// <summary>每种（GCB, InCB, ExtPict）组合取实际出现的打包值。</summary>
    static int[] GraphemeClasses()
    {
        var set = new SortedSet<int>();
        for (int cp = 0; cp <= 0x10FFFF; cp++) set.Add(UnicodeTables.Segment(cp) & GraphemeBits);
        return set.ToArray();
    }

    [Fact]
    public void Safe_grapheme_boundary_is_context_independent()
    {
        // 对任意至多两个字符的前文 ctx：若 IsSafeBoundary(a, b)，则连续运行在 b 前必然断开，
        // 且其后任意 c 的判定与从 b 重新开始的状态机相同。
        var classes = GraphemeClasses();
        Assert.InRange(classes.Length, 10, 64);
        int safePairs = 0;
        foreach (int a in classes)
        foreach (int b in classes)
        {
            if (!GraphemeBreaker.IsSafeBoundary(a, b)) continue;
            safePairs++;
            var restarted = default(GraphemeBreaker);
            restarted.Reset(b);
            for (int len = 0; len <= 2; len++)
            {
                int combos = 1;
                for (int i = 0; i < len; i++) combos *= classes.Length;
                for (int k = 0; k < combos; k++)
                {
                    var run = default(GraphemeBreaker);
                    int rest = k;
                    bool first = true;
                    for (int i = 0; i < len; i++)
                    {
                        int ctx = classes[rest % classes.Length];
                        rest /= classes.Length;
                        if (first) { run.Reset(ctx); first = false; }
                        else run.BreakBefore(ctx);
                    }
                    if (first) run.Reset(a);
                    else run.BreakBefore(a);
                    Assert.True(run.BreakBefore(b), $"no break between {a:X} and {b:X} (context {k}/{len})");
                    foreach (int c in classes)
                    {
                        var x = run;
                        var y = restarted;
                        Assert.Equal(y.BreakBefore(c), x.BreakBefore(c));
                    }
                }
            }
        }
        Assert.True(safePairs > classes.Length * classes.Length / 2, $"only {safePairs} safe pairs");
    }

    [Fact]
    public void Grapheme_navigation_matches_conformance_cases()
    {
        var cases = TestData.ReadBreakTests("GraphemeBreakTest.txt");
        foreach (var c in cases)
        {
            var text = c.Text.AsSpan();
            var b = c.Boundaries;
            for (int i = 0; i <= text.Length; i++)
            {
                int next = b.Where(x => x > i).DefaultIfEmpty(text.Length).First();
                int prev = b.Where(x => x < i).DefaultIfEmpty(0).Max();
                Assert.Equal(next, TextSegmenter.GetNextGraphemeBoundary(text, i));
                Assert.Equal(prev, TextSegmenter.GetPreviousGraphemeBoundary(text, i));
                Assert.Equal(b.Contains(i), TextSegmenter.IsGraphemeBoundary(text, i));
            }
        }
    }

    [Theory]
    [InlineData(0xAC00)]   // 가（LV）
    [InlineData(0xAC01)]   // 각（LVT）
    [InlineData(0x1F600)]  // 😀（ExtPict，无 ZWJ）
    [InlineData(0x0915)]   // क（InCB=Consonant）
    [InlineData(0x4E00)]   // 一
    public void Safe_grapheme_start_is_local_in_long_runs(int cp)
    {
        var unit = char.ConvertFromUtf32(cp);
        var text = string.Concat(Enumerable.Repeat(unit, 5000)).AsSpan();
        for (int i = 0; i < text.Length; i += 37)
        {
            int start = TextSegmenter.FindSafeGraphemeStart(text, i);
            Assert.InRange(start, i - 2 * unit.Length, i);
        }
    }

    [Fact]
    public void Sentence_lookahead_after_terminator_is_linear()
    {
        var text = "Done." + new string(' ', 200_000) + "Next";
        var sw = Stopwatch.StartNew();
        var list = new List<TextSegment>();
        foreach (var s in TextSegmenter.EnumerateSentences(text.AsSpan())) list.Add(s);
        Assert.Equal(new[] { new TextSegment(0, text.Length - 4), new TextSegment(text.Length - 4, 4) }, list.ToArray());

        var closers = "a." + new string(')', 200_000) + " b";
        list.Clear();
        foreach (var s in TextSegmenter.EnumerateSentences(closers.AsSpan())) list.Add(s);
        Assert.Equal(new[] { new TextSegment(0, closers.Length) }, list.ToArray()); // SB8：后面是小写，不断开
        sw.Stop();
        // 修复前（每个位置重新向后扫描）20 万字符需要数十秒；线性实现只需几毫秒
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Sentence_lookahead_cache_handles_repeated_terminators()
    {
        var text = "x.  ) y. Z! ) a. ( B? c.\u2029d.  e";
        var list = new List<string>();
        foreach (var s in TextSegmenter.EnumerateSentences(text.AsSpan())) list.Add(text.Substring(s.Start, s.Length));
        Assert.Equal(new[] { "x.  ) y. ", "Z! ", ") a. ", "( B? ", "c.\u2029", "d.  e" }, list.ToArray());
    }

    [Fact]
    public void Lone_connector_punctuation_is_not_word_like()
    {
        var text = "_ foo_bar __ 3_";
        var words = new List<(string, bool)>();
        foreach (var w in TextSegmenter.EnumerateWords(text.AsSpan())) words.Add((text.Substring(w.Start, w.Length), w.IsWordLike));
        Assert.Equal(new[]
        {
            ("_", false), (" ", false), ("foo_bar", true), (" ", false), ("__", false), (" ", false), ("3_", true),
        }, words.ToArray());
    }

    [Fact]
    public void Line_break_strictness_controls_cj()
    {
        // あぁ（ぁ 为 CJ）以及 ー（U+30FC，CJ）
        var text = "あぁかー";
        Assert.Equal(new[] { 2, 4 }, Positions(text, LineBreakStrictness.Strict));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Positions(text, LineBreakStrictness.Normal));
        Assert.Equal(Positions(text, LineBreakStrictness.Strict), Positions(text, null));

        static int[] Positions(string s, LineBreakStrictness? strictness)
        {
            var list = new List<int>();
            if (strictness is { } st) foreach (var b in TextSegmenter.EnumerateLineBreaks(s.AsSpan(), st)) list.Add(b.Position);
            else foreach (var b in TextSegmenter.EnumerateLineBreaks(s.AsSpan())) list.Add(b.Position);
            return list.ToArray();
        }
    }

    [Fact]
    public void Utf16_length_agrees_with_encode()
    {
        Span<char> buf = stackalloc char[2];
        foreach (int v in new[] { -1, 0, 0x41, 0xD800, 0xFFFF, 0x10000, 0x10FFFF, 0x110000, int.MaxValue, int.MinValue })
            Assert.Equal(CodePoint.Encode(v, buf), CodePoint.Utf16Length(v));
    }
}
