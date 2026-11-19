using AwAUI.Graphics.Commands;
using AwAUI.Text.Testing;
using AwAUI.Unicode;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

/// <summary>评审中发现的问题的回归测试。</summary>
public class ReviewRegressionTests
{
    // ------------------------------------------------------------------ 占位盒是原子的字素簇

    [Fact]
    public void Placeholder_after_a_prepend_character_is_not_merged_into_the_text_cluster()
    {
        // U+0D4E（马拉雅拉姆文 Dot Reph）的字素簇类别是 Prepend：UAX #29 会把它与其后的 U+FFFC 合成一个簇
        var b = Engine.CreateParagraphBuilder(P());
        b.AddText("aൎ");
        b.AddPlaceholder(new PlaceholderStyle(20, 20));
        b.AddText("b");
        var l = b.Build();
        l.Layout(Inf);
        Assert.Equal(new Rect(20, 0, 20, 20), l.GetPlaceholderRect(0));
        Assert.Equal(50, l.Width);
        Assert.Equal(new TextRange(1, 2), l.GetGraphemeAt(1));
        Assert.Equal(new TextRange(2, 3), l.GetGraphemeAt(2));
        Assert.Equal(new TextPosition(3), l.Move(new TextPosition(2), CaretMove.Forward));
        Assert.Equal(new TextPosition(2), l.Move(new TextPosition(3), CaretMove.Backward));
        Assert.Equal(20, l.GetCaretRect(new TextPosition(2)).X);
        Assert.Equal(40, l.GetCaretRect(new TextPosition(3)).X);
    }

    [Fact]
    public void Combining_mark_after_a_placeholder_does_not_swallow_it()
    {
        var b = Engine.CreateParagraphBuilder(P());
        b.AddPlaceholder(new PlaceholderStyle(20, 20));
        b.AddText("́b");
        var l = b.Build();
        l.Layout(Inf);
        Assert.Equal(new Rect(0, 0, 20, 20), l.GetPlaceholderRect(0));
        // 孤立的组合标记自成一个簇（方块字体中 1 em），其后的 b 也是 1 em
        Assert.Equal(40, l.Width);
        Assert.Equal(new TextPosition(1), l.HitTest(new Point(19, 10)));
    }

    // ------------------------------------------------------------------ 折叠字符不制造断行机会

    [Fact]
    public void Collapsed_newline_between_ideographs_does_not_allow_breaking_under_keep_all()
    {
        var l = Lay("中文\n中文", P(wordBreak: WordBreak.KeepAll), 30);
        Assert.Equal(new[] { "中文\n中文" }, Lines(l));
        Assert.Equal(40, l.MinIntrinsicWidth);
        Assert.Equal(40, l.MaxIntrinsicWidth);
    }

    [Fact]
    public void Collapsed_newline_does_not_let_a_line_start_with_closing_punctuation()
    {
        // 「。」（CL）之前不可断开；被移除的换行符（工作文本中的 ZWSP）不能在那里制造断点
        var l = Lay("中文\n。中", P(), 20);
        Assert.Equal(new[] { "中", "文\n。", "中" }, Lines(l));
        Assert.Equal(20, l.MinIntrinsicWidth);
    }

    [Fact]
    public void Collapsed_space_runs_keep_their_break_positions()
    {
        Assert.Equal(new[] { "  abc   ", "def" }, Lines(Lay("  abc   def", P(), 30)));
        // pre-line：换行符前后的空格被移除；强制断点仍紧跟换行符，换行后被移除的空格属于下一行
        Assert.Equal(new[] { "a  \n", "  b" }, Lines(Lay("a  \n  b", P(WhiteSpace.PreLine))));
        var trailing = Lay("a\n  ", P(WhiteSpace.PreLine));
        Assert.Equal(2, trailing.LineCount);
        Assert.Equal(2, trailing.GetLine(1).StartOffset);
        Assert.True(trailing.GetLine(0).HardBreak);
        // break-all 时也不在折叠字符之前断开
        Assert.Equal(new[] { "ab  ", "cd" }, Lines(Lay("ab  cd", P(wordBreak: WordBreak.BreakAll), 25)));
        Assert.Equal(new[] { "ab  ", "cd" }, Lines(Lay("ab  cd", P(wordBreak: WordBreak.BreakAll), 20)));
        Assert.Equal(new[] { "a", "b  ", "c", "d" }, Lines(Lay("ab  cd", P(wordBreak: WordBreak.BreakAll), 10)));
    }

    // ------------------------------------------------------------------ 整形缓存的哈希

    [Fact]
    public void Shape_cache_key_hash_is_not_the_deterministic_fnv_hash()
    {
        // 构造在旧的确定性 FNV-1a 下全部碰撞的 2^6 个词（逐级生日碰撞，迭代哈希的多重碰撞）
        var words = FnvMulticollisions(stages: 6);
        Assert.Equal(64, words.Count);
        uint fnv0 = Fnv(words[0]);
        Assert.All(words, w => Assert.Equal(fnv0, Fnv(w)));

        var face = Engine.Face;
        var hashes = new HashSet<int>();
        foreach (var w in words)
            hashes.Add(new ShapeKey(w.AsMemory(), face, 10, false, Script.Latin, null, default).GetHashCode());
        Assert.True(hashes.Count > 32, $"{hashes.Count} distinct hashes for 64 FNV-colliding words");
    }

    static uint Fnv(string s, uint h = 2166136261)
    {
        foreach (var c in s) h = (h ^ c) * 16777619;
        return h;
    }

    /// <summary>
    /// FNV-1a 的多重碰撞：每级找两个 2 字符块，使同一起始状态下的结果相同。
    /// (s ^ c1)·P 的高 16 位相同的两个 c1 在 32768 个候选中必然存在（生日），低 16 位的差由第二个字符的异或补齐。
    /// </summary>
    static List<string> FnvMulticollisions(int stages)
    {
        var prefixes = new List<string> { "" };
        uint state = 2166136261;
        for (int stage = 0; stage < stages; stage++)
        {
            var seen = new Dictionary<uint, char>();
            string? a = null, b = null;
            for (char c1 = '\u1000'; a is null && c1 < '\u9000'; c1++)
            {
                uint mid = (state ^ c1) * 16777619;
                if (seen.TryGetValue(mid >> 16, out char o1))
                {
                    uint omid = (state ^ o1) * 16777619;
                    char c2 = (char)0x1000;
                    char o2 = (char)(c2 ^ ((mid ^ omid) & 0xFFFF));
                    if (char.IsSurrogate(o2)) continue;
                    a = new string(new[] { o1, o2 });
                    b = new string(new[] { c1, c2 });
                    state = Fnv(b, state);
                }
                else seen[mid >> 16] = c1;
            }
            Assert.NotNull(a);
            var next = new List<string>(prefixes.Count * 2);
            foreach (var p in prefixes)
            {
                next.Add(p + a);
                next.Add(p + b);
            }
            prefixes = next;
        }
        return prefixes;
    }

    // ------------------------------------------------------------------ 数值上限

    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    [Fact]
    public void Huge_line_heights_are_clamped_and_stay_finite()
    {
        var l = Lay(string.Join("\n", Enumerable.Repeat("a", 100)), P(WhiteSpace.Pre, lineHeight: LineHeight.Pixels(3e37f)));
        Assert.Equal(100, l.LineCount);
        Assert.InRange(l.Height, 100 * 1e6f - 1e3f, 100 * 1e6f + 1e3f);
        Assert.True(Finite(l.LastBaseline));
        var caret = l.GetCaretRect(new TextPosition(198));
        Assert.True(Finite(caret.Y) && Finite(caret.Height));
        Assert.Equal(1e6f, Lay("a", P(lineHeight: LineHeight.Multiple(1e30f))).Height);
    }

    [Theory]
    [InlineData(TextAlign.Center)]
    [InlineData(TextAlign.Right)]
    [InlineData(TextAlign.Justify)]
    public void Near_max_value_widths_are_unbounded_and_keep_coordinates_finite(TextAlign align)
    {
        var text = "aa bb cc dd ee\nff gg " + Hebrew + " hh";
        var l = Lay(text, P(WhiteSpace.PreWrap, align), float.MaxValue);
        Assert.True(float.IsPositiveInfinity(l.MaxWidth));
        Assert.Equal(2, l.LineCount);
        foreach (var g in Record(l).Glyphs)
            Assert.All(GlyphXs(g), x => Assert.True(Finite(x) && x >= 0 && x <= 200, $"glyph x {x}"));
        for (int o = 0; o <= text.Length; o++)
        {
            var r = l.GetCaretRect(new TextPosition(o));
            Assert.True(Finite(r.X) && r.X >= 0 && r.X <= 200, $"caret {o} at {r.X}");
        }
        var rects = new List<Rect>();
        l.GetSelectionRects(new TextRange(0, text.Length), rects);
        Assert.All(rects, r => Assert.True(Finite(r.Width) && r.Width <= 200));
        l.Layout(2e9f);
        Assert.True(float.IsPositiveInfinity(l.MaxWidth));
        l.Layout(TextLayout.MaxFiniteWidth);
        Assert.Equal(TextLayout.MaxFiniteWidth, l.MaxWidth);
    }

    [Fact]
    public void Dotted_decorations_are_bounded_for_the_whole_paragraph()
    {
        // 2000 行，每行 100px；0.5px 粗的点线每行约 100 个小段：总数按段落限界，超出的行退化为实线（每行一个矩形）
        var style = new TextStyle
        {
            FontSize = 10,
            Decoration = TextDecorationLine.Underline,
            DecorationStyle = TextDecorationStyle.Dotted,
            DecorationThickness = 0.5f,
        };
        var text = string.Join(" ", Enumerable.Repeat("aaaaaaaaaa", 2000));
        var l = Lay(text, P(style: style), 100);
        Assert.Equal(2000, l.LineCount);
        var (_, rects, _) = Record(l);
        Assert.InRange(rects.Count, 2000, (1 << 16) + 2000);
        // 每行都有下划线：最后一行是实线
        var last = l.GetLine(1999);
        Assert.Contains(rects, r => r.Rect.Width == 100 && r.Rect.Top > last.Top && r.Rect.Top < last.Top + last.Height);
        // 短文本仍是点线
        var (_, few, _) = Record(Lay("aaaaaaaaaa", P(style: style)));
        Assert.Equal(100, few.Count);
    }
}
