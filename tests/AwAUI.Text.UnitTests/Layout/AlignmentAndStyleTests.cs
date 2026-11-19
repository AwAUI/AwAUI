using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

public class AlignmentAndStyleTests
{
    [Theory]
    [InlineData(TextAlign.Left, BidiDirection.LeftToRight, 0)]
    [InlineData(TextAlign.Start, BidiDirection.LeftToRight, 0)]
    [InlineData(TextAlign.Justify, BidiDirection.LeftToRight, 0)]
    [InlineData(TextAlign.Right, BidiDirection.LeftToRight, 70)]
    [InlineData(TextAlign.End, BidiDirection.LeftToRight, 70)]
    [InlineData(TextAlign.Center, BidiDirection.LeftToRight, 35)]
    [InlineData(TextAlign.Start, BidiDirection.RightToLeft, 70)]
    [InlineData(TextAlign.End, BidiDirection.RightToLeft, 0)]
    [InlineData(TextAlign.Left, BidiDirection.RightToLeft, 0)]
    public void Aligns_lines_within_the_available_width(TextAlign align, BidiDirection dir, float left)
    {
        var l = Lay("abc", P(align: align, dir: dir), 100);
        Assert.Equal(left, l.GetLine(0).Left);
        Assert.Equal(30, l.Width);
        var (glyphs, _, _) = Record(l);
        Assert.Equal(left, GlyphXs(glyphs[0]).Min());
    }

    [Fact]
    public void Alignment_ignores_hanging_spaces()
    {
        var l = Lay("abc def", P(align: TextAlign.Right), 50);
        Assert.Equal(20, l.GetLine(0).Left);   // "abc" 右对齐，悬挂空格在 50 之外
        var (glyphs, _, _) = Record(l);
        Assert.Equal(new float[] { 20, 30, 40, 50 }, GlyphXs(glyphs[0]));
        Assert.Equal(20, l.GetLine(1).Left);
    }

    [Fact]
    public void Overflowing_line_is_start_aligned()
    {
        Assert.Equal(0, Lay("abcdefghijkl", P(WhiteSpace.NoWrap, TextAlign.Right), 100).GetLine(0).Left);
        Assert.Equal(-20, Lay(Hebrew + Hebrew + Hebrew + Hebrew, P(WhiteSpace.NoWrap, dir: BidiDirection.RightToLeft), 100).GetLine(0).Left);
    }

    [Fact]
    public void Infinite_width_aligns_relative_to_the_widest_line()
    {
        var l = Lay("a\nabc", P(WhiteSpace.Pre, TextAlign.Right));
        Assert.Equal(20, l.GetLine(0).Left);
        Assert.Equal(0, l.GetLine(1).Left);
        Assert.Equal(10, Lay("a\nabc", P(WhiteSpace.Pre, TextAlign.Center)).GetLine(0).Left);
    }

    [Fact]
    public void Justify_expands_word_spaces_except_on_the_last_line()
    {
        var l = Lay("aa bb cc dd", P(align: TextAlign.Justify), 70);
        Assert.Equal(new[] { "aa bb ", "cc dd" }, Lines(l));
        Assert.Equal(70, l.GetLine(0).Width);
        Assert.Equal(50, l.GetLine(1).Width);
        var (glyphs, _, _) = Record(l);
        Assert.Equal(new float[] { 0, 10, 20, 50, 60, 70 }, GlyphXs(glyphs[0]));
        Assert.Equal(new float[] { 0, 10, 20, 30, 40 }, GlyphXs(glyphs[1]));
        // 强制换行前的行也不两端对齐
        var hard = Lay("aa bb\ncc dd", P(WhiteSpace.PreWrap, TextAlign.Justify), 70);
        Assert.Equal(50, hard.GetLine(0).Width);
    }

    [Fact]
    public void Justify_distributes_between_ideographs_when_there_are_no_spaces()
    {
        var l = Lay("中文字符测试", P(align: TextAlign.Justify), 45);
        Assert.Equal(new[] { "中文字符", "测试" }, Lines(l));
        Assert.Equal(45, l.GetLine(0).Width);
        var (glyphs, _, _) = Record(l);
        // 4 个字之间 3 个扩展点（行末字之后不扩展）：每处 5/3
        Assert.Equal(new float[] { 0, 11.667f, 23.333f, 35 }, GlyphXs(glyphs[0]).Select(x => (float)Math.Round(x, 3)).ToArray());
    }

    [Fact]
    public void Line_height_policies_and_half_leading()
    {
        var two = Lay("a b", P(lineHeight: LineHeight.Multiple(2)), 20);
        Assert.Equal(2, two.LineCount);
        Assert.Equal(20, two.GetLine(0).Height);
        Assert.Equal(13, two.FirstBaseline);           // 半行距 5
        Assert.Equal(33, two.LastBaseline);
        Assert.Equal(40, two.Height);

        var px = Lay("a", P(lineHeight: LineHeight.Pixels(30)));
        Assert.Equal(30, px.Height);
        Assert.Equal(18, px.FirstBaseline);

        var tight = Lay("a", P(lineHeight: LineHeight.Multiple(0.5f)));
        Assert.Equal(5, tight.Height);
        Assert.Equal(5.5f, tight.FirstBaseline);

        // 无效值按 normal
        Assert.True(LineHeight.Multiple(float.NaN).IsNormal);
        Assert.True(LineHeight.Pixels(-1).IsNormal);
    }

    [Fact]
    public void Mixed_font_sizes_share_a_baseline()
    {
        var b = Engine.CreateParagraphBuilder(P());
        b.AddText("a");
        b.PushStyle(Ts(20));
        b.AddText("B");
        b.Pop();
        b.AddText("c");
        var l = b.Build();
        Assert.Equal(16, l.FirstBaseline);
        Assert.Equal(20, l.Height);
        Assert.Equal(40, l.Width);
        var (glyphs, _, _) = Record(l);
        Assert.Equal(3, glyphs.Count);
        Assert.All(glyphs, g => Assert.Equal(16, g.Origin.Y));
        Assert.Equal(new float[] { 0, 10, 30 }, glyphs.Select(g => g.Origin.X).ToArray());
        Assert.Equal(new float[] { 10, 20, 10 }, glyphs.Select(g => g.Run.Size).ToArray());
    }

    [Fact]
    public void Text_style_line_height_overrides_the_paragraph()
    {
        var b = Engine.CreateParagraphBuilder(P());
        b.PushStyle(new TextStyle { FontSize = 10, LineHeight = LineHeight.Pixels(40) });
        b.AddText("a");
        var l = b.Build();
        Assert.Equal(40, l.Height);
        Assert.Equal(23, l.FirstBaseline);
    }

    [Fact]
    public void Letter_and_word_spacing()
    {
        var ls = Lay("abc", P(style: new TextStyle { FontSize = 10, LetterSpacing = 2 }));
        Assert.Equal(36, ls.Width);
        Assert.Equal(new float[] { 0, 12, 24 }, GlyphXs(Record(ls).Glyphs[0]));

        var ws = Lay("a b", P(style: new TextStyle { FontSize = 10, WordSpacing = 5 }));
        Assert.Equal(35, ws.Width);
        Assert.Equal(new float[] { 0, 10, 25 }, GlyphXs(Record(ws).Glyphs[0]));

        // 折叠掉的空白不加间距
        var collapsed = Lay("a   b", P(style: new TextStyle { FontSize = 10, WordSpacing = 5, LetterSpacing = 1 }));
        Assert.Equal(11 + 16 + 11, collapsed.Width);
    }

    [Fact]
    public void One_glyph_run_per_line_and_style_run()
    {
        var b = Engine.CreateParagraphBuilder(P());
        b.AddText("ab");
        b.PushStyle(new TextStyle { FontSize = 10, Color = Color.Parse("#ff0000") });
        b.AddText("cd");
        b.Pop();
        b.AddText("ef gh");
        var l = b.Build();
        l.Layout(70);
        Assert.Equal(new[] { "abcdef ", "gh" }, Lines(l));
        var (glyphs, _, _) = Record(l, new Point(100, 50));
        Assert.Equal(4, glyphs.Count);
        Assert.Equal(new Point(100, 58), glyphs[0].Origin);
        Assert.Equal(new Point(120, 58), glyphs[1].Origin);
        Assert.Equal(new Point(140, 58), glyphs[2].Origin);
        Assert.Equal(new Point(100, 68), glyphs[3].Origin);
        Assert.Equal(new[] { 2, 2, 3, 2 }, glyphs.Select(g => g.Run.Count).ToArray());
    }

    [Fact]
    public void Decorations_use_font_metrics_and_paint_order()
    {
        var style = new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline | TextDecorationLine.Overline | TextDecorationLine.LineThrough };
        var l = Lay("abc", P(style: style));
        var (_, rects, ops) = Record(l);
        Assert.Equal(new[] { DrawOp.FillRect, DrawOp.FillRect, DrawOp.DrawGlyphRun, DrawOp.FillRect }, ops.ToArray());
        Assert.Equal(new Rect(0, 9, 30, 0.5f), rects[0].Rect);   // 下划线：post 位置 −100、粗细 50
        Assert.Equal(new Rect(0, 0, 30, 0.5f), rects[1].Rect);   // 上划线：上升线
        Assert.Equal(new Rect(0, 4, 30, 0.5f), rects[2].Rect);   // 删除线：OS/2 位置 400
    }

    [Fact]
    public void Decorations_skip_hanging_spaces_and_support_styles()
    {
        var style = new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationThickness = 2, DecorationColor = Color.Parse("#00ff00") };
        var l = Lay("abc def", P(style: style), 40);
        var (_, rects, _) = Record(l);
        Assert.Equal(new Rect(0, 9, 30, 2), rects[0].Rect);
        Assert.Equal(new Rect(0, 19, 30, 2), rects[1].Rect);

        var dbl = Record(Lay("abc", P(style: new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Double, DecorationThickness = 1 }))).Rects;
        Assert.Equal(new[] { new Rect(0, 9, 30, 1), new Rect(0, 11, 30, 1) }, dbl.Select(r => r.Rect).ToArray());

        var dotted = Record(Lay("abc", P(style: new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Dotted, DecorationThickness = 1 }))).Rects;
        Assert.Equal(15, dotted.Count);

        var wavy = Record(Lay("abc", P(style: new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Wavy })));
        Assert.Contains(DrawOp.StrokePath, wavy.Ops);

        // 极细的点线退化为实线，不产生海量命令
        var thin = Record(Lay(new string('a', 1000), P(style: new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Dotted, DecorationThickness = 1e-6f })));
        Assert.Single(thin.Rects);
    }

    [Fact]
    public void Synthesizes_bold_and_oblique_when_the_face_lacks_them()
    {
        var l = Lay("ab", P(style: new TextStyle { FontSize = 10, FontWeight = FontWeight.Bold, FontStyle = FontStyle.Italic }));
        var run = Record(l).Glyphs[0].Run;
        Assert.Equal(GlyphRunFlags.SyntheticBold | GlyphRunFlags.SyntheticOblique, run.Flags);
        Assert.Equal(GlyphRunFlags.None, Record(Lay("ab")).Glyphs[0].Run.Flags);
    }

    [Fact]
    public void Foreground_brush_is_used_for_glyphs()
    {
        var brush = new LinearGradientBrush(new Point(0, 0), new Point(100, 0),
            new[] { new GradientStop(0, Color.Black), new GradientStop(1, Color.White) });
        var g = Record(Lay("ab", P(style: new TextStyle { FontSize = 10, Foreground = brush }))).Glyphs[0];
        Assert.True(g.Paint.BrushRef >= 0);
    }

    [Fact]
    public void Text_transform_keeps_offsets()
    {
        var l = Lay("abc", P(style: new TextStyle { FontSize = 10, TextTransform = TextTransform.Uppercase }));
        Assert.Equal("abc", l.Text);
        Assert.Equal(30, l.Width);
    }

    [Fact]
    public void Placeholders_take_space_and_raise_the_line()
    {
        var b = Engine.CreateParagraphBuilder(P());
        b.AddText("ab");
        b.AddPlaceholder(new PlaceholderStyle(20, 30));
        b.AddText("cd");
        var l = b.Build();
        Assert.Equal(1, l.PlaceholderCount);
        Assert.Equal(60, l.MaxIntrinsicWidth);
        Assert.Equal(32, l.Height);
        Assert.Equal(30, l.FirstBaseline);
        Assert.Equal(new Rect(20, 0, 20, 30), l.GetPlaceholderRect(0));
        Assert.Equal("ab\uFFFCcd", l.Text);
        // 占位盒不绘制字形
        Assert.Equal(new[] { 2, 2 }, Record(l).Glyphs.Select(g => g.Run.Count).ToArray());
    }

    [Theory]
    [InlineData(PlaceholderAlignment.Top, 30, 8, 0)]
    [InlineData(PlaceholderAlignment.Bottom, 30, 28, 0)]
    [InlineData(PlaceholderAlignment.Middle, 30, 19, 0)]
    [InlineData(PlaceholderAlignment.Baseline, 32, 30, 0)]
    public void Placeholder_vertical_alignment(PlaceholderAlignment align, float height, float baseline, float top)
    {
        var b = Engine.CreateParagraphBuilder(P());
        b.AddText("a");
        b.AddPlaceholder(new PlaceholderStyle(10, 30, align));
        var l = b.Build();
        Assert.Equal(height, l.Height);
        Assert.Equal(baseline, l.FirstBaseline);
        Assert.Equal(top, l.GetPlaceholderRect(0).Y);
    }

    [Fact]
    public void Placeholders_are_break_opportunities_and_truncated_ones_are_empty()
    {
        var b = Engine.CreateParagraphBuilder(P());
        b.AddText("aaa");
        b.AddPlaceholder(new PlaceholderStyle(30, 30));
        b.AddText("bbb");
        var l = b.Build();
        l.Layout(40);
        Assert.Equal(3, l.LineCount);
        Assert.Equal(new Rect(0, 10, 30, 30), l.GetPlaceholderRect(0));

        var b2 = new ParagraphBuilder(P(maxLines: 1), Engine);
        b2.AddText("aaa ");
        b2.AddPlaceholder(new PlaceholderStyle(30, 30));
        var l2 = b2.Build();
        l2.Layout(40);
        Assert.True(l2.DidExceedMaxLines);
        Assert.Equal(default, l2.GetPlaceholderRect(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => l2.GetPlaceholderRect(1));
    }

    [Fact]
    public void Font_fallback_uses_another_face_per_grapheme_cluster()
    {
        var fonts = new FontCollection();
        fonts.Register(new Testing.BoxFontFace("Main", coverage: cp => cp < 0x0590));
        fonts.Register(new Testing.BoxFontFace("Narrow", advance: 500));
        var engine = new DefaultTextEngine(fonts, defaultFontFamilies: new[] { "Main" });
        var l = Lay("ab " + Hebrew + " \U0001F44D\U0001F3FD", engine: engine);
        // ab␣ = 30，希伯来文 3 × 5，空格 10，表情符号 + 肤色修饰符（一个簇）2 × 5
        Assert.Equal(30 + 15 + 10 + 10, l.Width);
        var faces = Record(l).Glyphs.Select(g => g.Run.Face.Descriptor.FamilyName).ToArray();
        Assert.Equal(new[] { "Main", "Narrow", "Main", "Narrow" }, faces);
    }

    [Fact]
    public void Missing_glyphs_without_fallback_render_notdef()
    {
        var fonts = new FontCollection();
        fonts.Register(new Testing.BoxFontFace("Main", coverage: cp => cp < 0x0590));
        var engine = new DefaultTextEngine(fonts, defaultFontFamilies: new[] { "Main" });
        var l = Lay(Hebrew, engine: engine);
        Assert.Equal(30, l.Width);
        var run = Record(l).Glyphs.Single().Run;
        Assert.All(run.Glyphs.ToArray(), g => Assert.Equal(Testing.BoxFontFace.NotDefGlyph, g));
    }

    [Fact]
    public void Unknown_family_and_empty_font_manager_do_not_throw()
    {
        var l = Lay("abc", P(style: new TextStyle { FontSize = 10, FontFamilies = new[] { "Nope" } }));
        Assert.Equal(30, l.Width);

        var empty = new DefaultTextEngine(new FontCollection());
        var e = Lay("abc def", engine: empty);
        Assert.Equal(35, e.Width);   // 占位字型：每字符 0.5 em
        Assert.Equal(10, e.Height);
        Record(e);
    }
}
