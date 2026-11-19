namespace AwAUI.Unicode.UnitTests;

public class SegmentationApiTests
{
    static List<TextSegment> Graphemes(string s)
    {
        var list = new List<TextSegment>();
        foreach (var g in TextSegmenter.EnumerateGraphemes(s.AsSpan())) list.Add(g);
        return list;
    }

    [Fact]
    public void Graphemes_handle_emoji_flags_and_indic()
    {
        // 👨‍👩‍👧 家庭 ZWJ 序列、🇨🇳🇯🇵 两面旗、e + 组合尖音、क्ष（GB9c）、CRLF
        var text = "\U0001F468\u200D\U0001F469\u200D\U0001F467\U0001F1E8\U0001F1F3\U0001F1EF\U0001F1F5e\u0301\u0915\u094D\u0937\r\n";
        var g = Graphemes(text);
        Assert.Equal(new[] { 8, 4, 4, 2, 3, 2 }, g.Select(x => x.Length).ToArray());
        Assert.Equal(text.Length, g[g.Count - 1].End);
    }

    [Fact]
    public void Empty_text_has_no_segments()
    {
        Assert.Empty(Graphemes(""));
        var e = TextSegmenter.EnumerateLineBreaks(ReadOnlySpan<char>.Empty);
        Assert.False(e.MoveNext());
        var w = TextSegmenter.EnumerateWords(ReadOnlySpan<char>.Empty);
        Assert.False(w.MoveNext());
    }

    [Fact]
    public void Lone_surrogates_are_their_own_clusters()
    {
        var text = "a\uD800b\uDC00";
        Assert.Equal(new[] { 1, 1, 1, 1 }, Graphemes(text).Select(x => x.Length).ToArray());
    }

    [Fact]
    public void Grapheme_navigation_matches_forward_enumeration()
    {
        var rng = new Random(1234);
        int[] pool =
        {
            'a', 'B', ' ', '\r', '\n', 0x0300, 0x200D, 0x1F468, 0x1F469, 0x1F1E8, 0x1F1F3, 0x0915, 0x094D, 0x0937,
            0x1100, 0x1161, 0x11A8, 0xAC00, 0x0600, 0x0E33, 0x1F3FB, 0xD800, 0x00A9, 0xFE0F, 0x0001,
        };
        var sb = new System.Text.StringBuilder();
        for (int iter = 0; iter < 300; iter++)
        {
            sb.Clear();
            int n = rng.Next(1, 25);
            for (int i = 0; i < n; i++) sb.Append(FromCodePoint(pool[rng.Next(pool.Length)]));
            var text = sb.ToString();
            var boundaries = new SortedSet<int> { 0 };
            foreach (var g in Graphemes(text)) boundaries.Add(g.End);
            for (int i = 0; i <= text.Length; i++)
            {
                int next = boundaries.Where(b => b > i).DefaultIfEmpty(text.Length).First();
                int prev = boundaries.Where(b => b < i).DefaultIfEmpty(0).Max();
                Assert.Equal(next, TextSegmenter.GetNextGraphemeBoundary(text.AsSpan(), i));
                Assert.Equal(prev, TextSegmenter.GetPreviousGraphemeBoundary(text.AsSpan(), i));
                Assert.Equal(boundaries.Contains(i), TextSegmenter.IsGraphemeBoundary(text.AsSpan(), i));
                if (i < text.Length)
                {
                    var at = TextSegmenter.GetGraphemeAt(text.AsSpan(), i);
                    Assert.True(at.Start <= i && i < at.End && boundaries.Contains(at.Start) && boundaries.Contains(at.End));
                }
            }
        }
    }

    /// <summary>与 char.ConvertFromUtf32 相同，但允许孤立代理码位。</summary>
    static string FromCodePoint(int cp) => cp is >= 0xD800 and <= 0xDFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);

    [Fact]
    public void Words_are_classified()
    {
        var text = "Hello, world! 3.14 can't 你好";
        var words = new List<(string, bool)>();
        foreach (var w in TextSegmenter.EnumerateWords(text.AsSpan())) words.Add((text.Substring(w.Start, w.Length), w.IsWordLike));
        Assert.Equal(new[]
        {
            ("Hello", true), (",", false), (" ", false), ("world", true), ("!", false), (" ", false), ("3.14", true),
            (" ", false), ("can't", true), (" ", false), ("你", true), ("好", true),
        }, words.ToArray());
    }

    [Fact]
    public void Sentences_are_split()
    {
        var text = "Mr. Smith arrived. He said \"hi!\" Then he left.\nNew paragraph";
        var list = new List<string>();
        foreach (var t in TextSegmenter.EnumerateSentences(text.AsSpan())) list.Add(text.Substring(t.Start, t.Length));
        Assert.Equal(new[] { "Mr. ", "Smith arrived. ", "He said \"hi!\" ", "Then he left.\n", "New paragraph" }, list.ToArray());
    }

    [Fact]
    public void Line_breaks_report_mandatory_breaks()
    {
        var text = "Hello world\r\nnext-line\u2028end";
        var breaks = new List<LineBreakOpportunity>();
        foreach (var b in TextSegmenter.EnumerateLineBreaks(text.AsSpan())) breaks.Add(b);
        Assert.Equal(new[] { "6÷", "13!", "18÷", "23!", "26!" }, breaks.Select(b => b.ToString()).ToArray());
    }

    [Fact]
    public void Line_breaks_cjk_and_numbers()
    {
        // 中文逐字可断，但不能断在句号前；数字 "$(12.35)" 整体不断
        var text = "中文。价格$(12.35)元";
        var positions = new List<int>();
        foreach (var b in TextSegmenter.EnumerateLineBreaks(text.AsSpan())) positions.Add(b.Position);
        Assert.Equal(new[] { 1, 3, 4, 5, 13, 14 }, positions.ToArray());
    }

    [Fact]
    public void Enumeration_does_not_allocate()
    {
        var text = "The quick brown 🦊 jumps over the lazy 🐶! 中文测试 العربية 12,345.67".AsSpan();
        // 预热
        Count(text);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 100; i++) total += Count(text);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(total > 0);
        Assert.Equal(0, allocated);

        static int Count(ReadOnlySpan<char> s)
        {
            int n = 0;
            foreach (var g in TextSegmenter.EnumerateGraphemes(s)) n += g.Length;
            foreach (var w in TextSegmenter.EnumerateWords(s)) n += w.Length;
            foreach (var t in TextSegmenter.EnumerateSentences(s)) n += t.Length;
            foreach (var b in TextSegmenter.EnumerateLineBreaks(s)) n += b.Position;
            foreach (var cp in CodePoint.Enumerate(s)) n += cp & 1;
            n += (int)UnicodeData.GetScript(s[0]) + (int)UnicodeData.GetBidiClass(s[1]);
            Span<byte> levels = stackalloc byte[s.Length];
            Span<int> order = stackalloc int[s.Length];
            int para = BidiAlgorithm.ResolveLevels(s, BidiDirection.Auto, levels);
            BidiAlgorithm.ResetWhitespaceLevels(s, para, levels);
            BidiAlgorithm.ReorderVisual(levels, order);
            return n + order[0];
        }
    }

    [Fact]
    public void CodePoint_helpers()
    {
        var s = "a\U0001F600b".AsSpan();
        Assert.Equal(0x1F600, CodePoint.Decode(s, 1, out int len));
        Assert.Equal(2, len);
        Assert.Equal(0x1F600, CodePoint.DecodeBefore(s, 3, out len));
        Assert.Equal(2, len);
        Assert.Equal(0xDE00, CodePoint.Decode(s, 2, out len)); // 落在代理对后半：返回孤立低代理
        Assert.Equal(1, CodePoint.AlignToStart(s, 2));
        Span<char> buf = stackalloc char[2];
        Assert.Equal(2, CodePoint.Encode(0x1F600, buf));
        Assert.Equal("\U0001F600", buf.ToString());
        Assert.True(CodePoint.IsScalarValue(0x10FFFF));
        Assert.False(CodePoint.IsScalarValue(0xD800));
        Assert.False(CodePoint.IsScalarValue(0x110000));
        Assert.False(CodePoint.IsScalarValue(-1));
        var cps = new List<int>();
        foreach (var cp in CodePoint.Enumerate(s)) cps.Add(cp);
        Assert.Equal(new[] { 'a', 0x1F600, 'b' }, cps.ToArray());
    }
}

public class BidiApiTests
{
    [Fact]
    public void Mixed_paragraph_levels_and_order()
    {
        // "abc אבג 123"：LTR 段落中，希伯来字母与其后的数字一起反向
        var text = "abc אבג 123";
        var levels = new byte[text.Length];
        int para = BidiAlgorithm.ResolveLevels(text.AsSpan(), BidiDirection.Auto, levels);
        Assert.Equal(0, para);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2 }, levels);
        var order = new int[text.Length];
        BidiAlgorithm.ReorderVisual(levels, order);
        Assert.Equal(new[] { 0, 1, 2, 3, 8, 9, 10, 7, 6, 5, 4 }, order);
    }

    [Fact]
    public void Auto_direction_uses_first_strong_character()
    {
        Assert.Equal(1, BidiAlgorithm.GetParagraphLevel("123 ال abc".AsSpan()));
        Assert.Equal(0, BidiAlgorithm.GetParagraphLevel("⁧א⁩ abc".AsSpan())); // 跳过隔离段
        Assert.Equal(1, BidiAlgorithm.GetParagraphLevel("123".AsSpan(), defaultLevel: 1));
    }

    [Fact]
    public void Surrogate_pairs_share_a_level()
    {
        var text = "\U00010900\U00010901 a"; // 腓尼基字母（R）
        var levels = new byte[text.Length];
        int para = BidiAlgorithm.ResolveLevels(text.AsSpan(), BidiDirection.Auto, levels);
        Assert.Equal(1, para);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 2 }, levels);
    }

    [Fact]
    public void Each_paragraph_is_resolved_separately()
    {
        var text = "אב\nab";
        var levels = new byte[text.Length];
        int first = BidiAlgorithm.ResolveLevels(text.AsSpan(), BidiDirection.Auto, levels);
        Assert.Equal(1, first);
        Assert.Equal(new byte[] { 1, 1, 1, 0, 0 }, levels);
    }
}
