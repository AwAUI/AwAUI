using AwAUI.Text.Testing;
using AwAUI.Unicode;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

public class LineBreakingTests
{
    [Fact]
    public void Single_line_metrics_follow_the_box_font()
    {
        var l = Lay("hello");
        Assert.Equal(1, l.LineCount);
        Assert.Equal(50, l.Width);
        Assert.Equal(10, l.Height);
        Assert.Equal(8, l.FirstBaseline);
        Assert.Equal(8, l.LastBaseline);
        Assert.False(l.DidExceedMaxLines);
        var m = l.GetLine(0);
        Assert.Equal(0, m.StartOffset);
        Assert.Equal(5, m.EndOffset);
        Assert.Equal(8, m.Ascent);
        Assert.Equal(2, m.Descent);
        Assert.Equal(0, m.Left);
    }

    [Fact]
    public void Wraps_at_spaces_and_trailing_space_hangs()
    {
        var l = Lay("hello world", width: 80);
        Assert.Equal(new[] { "hello ", "world" }, Lines(l));
        Assert.Equal(50, l.GetLine(0).Width);      // 行尾空格悬挂，不计入宽度
        Assert.Equal(50, l.GetLine(1).Width);
        Assert.Equal(20, l.Height);
        Assert.Equal(18, l.LastBaseline);
        Assert.Equal(50, l.Width);
        // 恰好放得下（悬挂空格除外）
        Assert.Equal(new[] { "hello ", "world" }, Lines(Lay("hello world", width: 50)));
        Assert.Equal(new[] { "hello world" }, Lines(Lay("hello world", width: 110)));
    }

    [Fact]
    public void Empty_text_has_one_empty_line_with_strut_height()
    {
        var l = Lay("");
        Assert.Equal(1, l.LineCount);
        Assert.Equal(0, l.Width);
        Assert.Equal(10, l.Height);
        Assert.Equal(8, l.FirstBaseline);
        Assert.Equal(0, l.MinIntrinsicWidth);
        Assert.Equal(0, l.MaxIntrinsicWidth);
    }

    [Fact]
    public void Intrinsic_widths_are_widest_word_and_unwrapped_width()
    {
        var l = Engine.CreateParagraphBuilder(P()).Also(b => b.AddText("aa bbbb ccc")).Build();
        Assert.Equal(40, l.MinIntrinsicWidth);
        Assert.Equal(110, l.MaxIntrinsicWidth);
        // 固有宽度不触发断行
        Assert.True(float.IsNaN(l.MaxWidth));
        l.Layout(l.MinIntrinsicWidth);
        Assert.Equal(new[] { "aa ", "bbbb ", "ccc" }, Lines(l));
        Assert.Equal(40, l.Width);
        l.Layout(l.MaxIntrinsicWidth);
        Assert.Equal(1, l.LineCount);
        Assert.Equal(110, l.Width);
        l.Layout(l.MaxIntrinsicWidth - 0.01f);
        Assert.Equal(2, l.LineCount);
    }

    [Fact]
    public void Max_intrinsic_width_is_the_widest_hard_line()
    {
        var l = Lay("abc\nabcdef\nab", P(WhiteSpace.Pre));
        Assert.Equal(60, l.MaxIntrinsicWidth);
        Assert.Equal(60, l.MinIntrinsicWidth);   // pre 不自动换行
        var wrap = Lay("abc\nabcdef\nab", P(WhiteSpace.PreWrap));
        Assert.Equal(60, wrap.MaxIntrinsicWidth);
        Assert.Equal(60, wrap.MinIntrinsicWidth);
        Assert.Equal(70, Lay("aaa bbb", P(WhiteSpace.NoWrap)).MinIntrinsicWidth);
    }

    [Fact]
    public void Repeated_layout_does_not_reshape()
    {
        var shaper = new CountingShaper();
        var engine = new TestTextEngine(shaper);
        var text = string.Join(" ", Enumerable.Range(0, 200).Select(i => "word" + (i % 37)));
        var l = engine.CreateParagraphBuilder(P()).Also(b => b.AddText(text)).Build();
        int afterBuild = shaper.Calls;
        Assert.True(afterBuild > 0);
        Assert.True(afterBuild <= 37 + 1, $"word cache should shape each distinct word once, got {afterBuild}");
        _ = l.MinIntrinsicWidth;
        _ = l.MaxIntrinsicWidth;
        for (float w = 30; w < 2000; w += 37)
        {
            l.Layout(w);
            Assert.True(l.Width <= Math.Max(w, l.MinIntrinsicWidth) + 0.01f);
        }
        Assert.Equal(afterBuild, shaper.Calls);
        // 第二个段落复用全局词缓存
        engine.CreateParagraphBuilder(P()).Also(b => b.AddText(text)).Build();
        Assert.Equal(afterBuild, shaper.Calls);
        Assert.True(engine.ShapeCacheStatistics.Hits > 0);
    }

    [Fact]
    public void Same_width_layout_is_a_no_op_and_widths_are_sanitised()
    {
        var l = Lay("aaa bbb");
        l.Layout(float.NaN);
        Assert.Equal(1, l.LineCount);
        l.Layout(-5);
        Assert.Equal(0, l.MaxWidth);
        Assert.Equal(new[] { "aaa ", "bbb" }, Lines(l));   // 放不下时每个词一行，溢出
        Assert.Equal(30, l.Width);
    }

    [Fact]
    public void Collapses_white_space_in_normal_mode()
    {
        var l = Lay("  a   b \n\n c\t\td  ");
        // 显示为 "a b c d"
        Assert.Equal(70, l.MaxIntrinsicWidth);
        Assert.Equal(70, l.Width);
        Assert.Equal(1, l.LineCount);
        // 偏移仍对应原文
        Assert.Equal("  a   b \n\n c\t\td  ".Length, l.GetLine(0).EndOffset);
    }

    [Fact]
    public void Segment_break_between_ideographs_is_removed()
    {
        Assert.Equal(20, Lay("中\n文").MaxIntrinsicWidth);
        Assert.Equal(30, Lay("a\nb").MaxIntrinsicWidth);
        Assert.Equal(30, Lay("a\r\nb").MaxIntrinsicWidth);
    }

    [Fact]
    public void NoWrap_never_wraps_but_still_collapses()
    {
        var l = Lay("aaa   bbb ccc", P(WhiteSpace.NoWrap), 20);
        Assert.Equal(1, l.LineCount);
        Assert.Equal(110, l.Width);
    }

    [Fact]
    public void Pre_preserves_spaces_and_breaks_only_at_newlines()
    {
        var l = Lay("a  b\n  c  ", P(WhiteSpace.Pre), 10);
        Assert.Equal(new[] { "a  b\n", "  c  " }, Lines(l));
        Assert.Equal(40, l.GetLine(0).Width);
        Assert.Equal(50, l.GetLine(1).Width);      // pre 的行尾空格不悬挂
        Assert.True(l.GetLine(0).HardBreak);
        Assert.Equal(4, l.GetLine(0).EndExcludingBreak);
    }

    [Fact]
    public void Trailing_newline_creates_an_empty_last_line()
    {
        var l = Lay("ab\ncd\n", P(WhiteSpace.Pre));
        Assert.Equal(3, l.LineCount);
        Assert.Equal(6, l.GetLine(2).StartOffset);
        Assert.Equal(30, l.Height);
        var crlf = Lay("ab\r\ncd", P(WhiteSpace.PreWrap));
        Assert.Equal(new[] { "ab\r\n", "cd" }, Lines(crlf));
        Assert.Equal(2, crlf.GetLine(0).EndExcludingBreak);
    }

    [Fact]
    public void Pre_wrap_hangs_trailing_spaces()
    {
        var l = Lay("aaa    bbb", P(WhiteSpace.PreWrap), 50);
        Assert.Equal(new[] { "aaa    ", "bbb" }, Lines(l));
        Assert.Equal(30, l.GetLine(0).Width);
        Assert.Equal(100, l.MaxIntrinsicWidth);
    }

    [Fact]
    public void Pre_line_collapses_spaces_but_keeps_newlines()
    {
        var l = Lay("a   b  \n   c", P(WhiteSpace.PreLine));
        Assert.Equal(2, l.LineCount);
        Assert.Equal(30, l.GetLine(0).Width);
        Assert.Equal(10, l.GetLine(1).Width);
    }

    [Fact]
    public void Break_spaces_wraps_between_spaces_and_spaces_take_width()
    {
        var l = Lay("a    b", P(WhiteSpace.BreakSpaces), 30);
        Assert.Equal(new[] { "a  ", "  b" }, Lines(l));
        Assert.Equal(30, l.GetLine(0).Width);
        Assert.Equal(30, l.GetLine(1).Width);
    }

    [Fact]
    public void Hard_line_breaks_in_normal_mode_from_line_separator()
    {
        var l = Lay("ab\u2028cd");
        Assert.Equal(2, l.LineCount);
        Assert.True(l.GetLine(0).HardBreak);
    }

    [Fact]
    public void Long_word_overflows_unless_overflow_wrap_allows_breaking()
    {
        var normal = Lay("abcdefghij xy", width: 30);
        Assert.Equal(new[] { "abcdefghij ", "xy" }, Lines(normal));
        Assert.Equal(100, normal.Width);

        var bw = Lay("abcdefghij xy", P(overflowWrap: OverflowWrap.BreakWord), 30);
        Assert.Equal(new[] { "abc", "def", "ghi", "j ", "xy" }, Lines(bw));
        Assert.Equal(30, bw.Width);
        Assert.Equal(100, bw.MinIntrinsicWidth);   // break-word 不影响最小固有宽度

        var any = Lay("abcdefghij xy", P(overflowWrap: OverflowWrap.Anywhere), 30);
        Assert.Equal(Lines(bw), Lines(any));
        Assert.Equal(10, any.MinIntrinsicWidth);
    }

    [Fact]
    public void Overflow_wrap_prefers_normal_break_opportunities()
    {
        // "aa" 放在第一行，长词换到下一行后才紧急断开
        var l = Lay("aa bbbbbb", P(overflowWrap: OverflowWrap.BreakWord), 40);
        Assert.Equal(new[] { "aa ", "bbbb", "bb" }, Lines(l));
    }

    [Fact]
    public void Word_break_break_all_and_keep_all()
    {
        var all = Lay("abcdef ghi", P(wordBreak: WordBreak.BreakAll), 40);
        Assert.Equal(new[] { "abcd", "ef g", "hi" }, Lines(all));
        Assert.Equal(10, all.MinIntrinsicWidth);

        var cjk = Lay("中文字 漢字", width: 20);
        Assert.Equal(new[] { "中文", "字 ", "漢字" }, Lines(cjk));
        var keep = Lay("中文字 漢字", P(wordBreak: WordBreak.KeepAll), 20);
        Assert.Equal(new[] { "中文字 ", "漢字" }, Lines(keep));
        Assert.Equal(30, keep.MinIntrinsicWidth);
    }

    [Fact]
    public void Cjk_breaks_between_ideographs_but_not_before_closing_punctuation()
    {
        var l = Lay("中文字符测试", width: 30);
        Assert.Equal(new[] { "中文字", "符测试" }, Lines(l));
        Assert.Equal(10, l.MinIntrinsicWidth);
        // 。不能出现在行首：与前一个字一起换行
        var p = Lay("中文字。", width: 30);
        Assert.Equal(new[] { "中文", "字。" }, Lines(p));
    }

    [Fact]
    public void Line_break_strictness_controls_small_kana()
    {
        // ァ（CJ）：strict 下不能出现在行首
        var strict = Lay("アアァ", P(strictness: LineBreakStrictness.Strict), 20);
        Assert.Equal(new[] { "ア", "アァ" }, Lines(strict));
        var normal = Lay("アアァ", P(strictness: LineBreakStrictness.Normal), 20);
        Assert.Equal(new[] { "アア", "ァ" }, Lines(normal));
    }

    [Fact]
    public void Emoji_grapheme_clusters_are_never_split()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";   // 一个字素簇：3 个可见码点 + 2 个 ZWJ
        var l = Lay(family + family, P(overflowWrap: OverflowWrap.Anywhere), 10);
        Assert.Equal(2, l.LineCount);
        Assert.Equal(family, Lines(l)[0]);
        Assert.Equal(30, l.GetLine(0).Width);       // ZWJ 零宽
        Assert.Equal(30, l.MinIntrinsicWidth);
        Assert.Equal(new TextRange(0, family.Length), l.GetGraphemeAt(3));
    }

    [Fact]
    public void Max_lines_truncates_and_reports_overflow()
    {
        var l = Lay("aaaa bbbb cccc dddd", P(maxLines: 2), 50);
        Assert.Equal(2, l.LineCount);
        Assert.True(l.DidExceedMaxLines);
        Assert.Equal(20, l.Height);
        var fits = Lay("aaaa bbbb", P(maxLines: 2), 50);
        Assert.False(fits.DidExceedMaxLines);
    }

    [Fact]
    public void Ellipsis_after_max_lines()
    {
        var l = Lay("aaaa bbbbbb cccc", P(maxLines: 2, ellipsis: ParagraphStyle.HorizontalEllipsis), 50);
        Assert.Equal(2, l.LineCount);
        Assert.True(l.DidExceedMaxLines);
        // 第二行 "bbbbbb " 截成 "bbbb" + "…"
        Assert.Equal(50, l.GetLine(1).Width);
        var (glyphs, _, _) = Record(l);
        // 第一行一个 run（含悬挂空格的空字形），第二行文字一个 run、省略号一个 run
        Assert.Equal(new[] { 5, 4, 1 }, glyphs.Select(g => g.Run.Count).ToArray());
        Assert.Equal(40, glyphs[2].Origin.X);
        Assert.Equal(18, glyphs[2].Origin.Y);
    }

    [Fact]
    public void Ellipsis_for_overflowing_nowrap_line()
    {
        var l = Lay("abcdefghij", P(WhiteSpace.NoWrap, ellipsis: "…"), 50);
        Assert.Equal(1, l.LineCount);
        Assert.Equal(50, l.Width);
        var (glyphs, _, _) = Record(l);
        Assert.Equal(5, glyphs.Sum(g => g.Run.Count));   // abcd + …
        // 放得下时不加省略号
        Assert.Equal(100, Lay("abcdefghij", P(WhiteSpace.NoWrap, ellipsis: "…"), 100).Width);
        // 宽度小于省略号本身：只剩省略号
        var tiny = Lay("abcdefghij", P(WhiteSpace.NoWrap, ellipsis: "…"), 5);
        Assert.Equal(10, tiny.Width);
    }

    [Fact]
    public void Ellipsis_in_rtl_paragraph_is_on_the_left()
    {
        var l = Lay(Hebrew + Hebrew + Hebrew, P(WhiteSpace.NoWrap, ellipsis: "…", dir: BidiDirection.RightToLeft), 50);
        Assert.Equal(50, l.Width);
        var (glyphs, _, _) = Record(l);
        var xs = glyphs.SelectMany(GlyphXs).OrderBy(x => x).ToArray();
        Assert.Equal(new float[] { 0, 10, 20, 30, 40 }, xs);
        // 省略号是最左边的字形（视觉上的行尾）
        var ell = glyphs.Single(g => g.Run.Count == 1);
        Assert.Equal(0, ell.Origin.X);
    }
}

static class BuilderExtensions
{
    public static ParagraphBuilder Also(this ParagraphBuilder b, Action<ParagraphBuilder> a)
    {
        a(b);
        return b;
    }
}
