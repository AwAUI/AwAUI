using AwAUI.Unicode;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

public class HitTestAndBidiTests
{
    // a0 b1 c2 ␣3 א4 ב5 ג6 ␣7 d8 e9 f10 → 视觉：a b c ␣ ג ב א ␣ d e f
    static readonly string Mixed = "abc " + Hebrew + " def";

    static float CaretX(TextLayout l, int offset, TextAffinity a = TextAffinity.Downstream) => l.GetCaretRect(new TextPosition(offset, a)).X;

    [Fact]
    public void Mixed_direction_line_is_reordered_visually()
    {
        var l = Lay(Mixed);
        Assert.False(l.IsRightToLeft);
        var (glyphs, _, _) = Record(l);
        Assert.Equal(3, glyphs.Count);
        Assert.Equal(new float[] { 0, 10, 20, 30 }, GlyphXs(glyphs[0]));
        Assert.Equal(new float[] { 40, 50, 60 }, GlyphXs(glyphs[1]));   // ג ב א
        Assert.Equal(new float[] { 70, 80, 90, 100 }, GlyphXs(glyphs[2]));
    }

    [Fact]
    public void Caret_positions_at_direction_boundaries_depend_on_affinity()
    {
        var l = Lay(Mixed);
        Assert.Equal(70, CaretX(l, 4));                              // א 的前缘（右侧）
        Assert.Equal(40, CaretX(l, 4, TextAffinity.Upstream));       // 空格之后
        Assert.Equal(70, CaretX(l, 7));                              // 第二个空格之前
        Assert.Equal(40, CaretX(l, 7, TextAffinity.Upstream));       // ג 之后（左侧）
        Assert.Equal(60, CaretX(l, 5));
        Assert.Equal(0, CaretX(l, 0));
        Assert.Equal(110, CaretX(l, 11));
    }

    [Fact]
    public void Hit_test_inside_rtl_run()
    {
        var l = Lay(Mixed);
        Assert.Equal(new TextPosition(7, TextAffinity.Upstream), l.HitTest(new Point(44, 5)));
        Assert.Equal(new TextPosition(4), l.HitTest(new Point(68, 5)));
        Assert.Equal(new TextPosition(5), l.HitTest(new Point(58, 5)));
        Assert.Equal(new TextPosition(0), l.HitTest(new Point(-50, -50)));
        Assert.Equal(new TextPosition(11), l.HitTest(new Point(500, 500)));   // 文本末尾：上下游位置相同，规范化为下游
    }

    [Fact]
    public void Selection_rects_are_split_by_bidi()
    {
        var l = Lay(Mixed);
        var rects = new List<Rect>();
        l.GetSelectionRects(new TextRange(2, 6), rects);
        Assert.Equal(new[] { new Rect(20, 0, 20, 10), new Rect(50, 0, 20, 10) }, rects.ToArray());
        rects.Clear();
        l.GetSelectionRects(new TextRange(6, 2), rects);   // 反向选区
        Assert.Equal(2, rects.Count);
        rects.Clear();
        l.GetSelectionRects(new TextRange(0, 11), rects);
        Assert.Equal(new[] { new Rect(0, 0, 110, 10) }, rects.ToArray());
        rects.Clear();
        l.GetSelectionRects(new TextRange(3, 3), rects);
        Assert.Empty(rects);
    }

    [Fact]
    public void Selection_spans_lines()
    {
        var l = Lay("aaaa bbbb cccc", width: 50);
        var rects = new List<Rect>();
        l.GetSelectionRects(new TextRange(2, 12), rects);
        Assert.Equal(new[] { new Rect(20, 0, 30, 10), new Rect(0, 10, 50, 10), new Rect(0, 20, 20, 10) }, rects.ToArray());
    }

    [Fact]
    public void Visual_moves_are_monotonic_in_mixed_text()
    {
        var l = Lay(Mixed);
        var pos = new TextPosition(0);
        float x = CaretX(l, 0);
        for (int i = 0; i < 11; i++)
        {
            pos = l.Move(pos, CaretMove.Right);
            float nx = l.GetCaretRect(pos).X;
            Assert.True(nx > x, $"step {i}: {pos} at {nx} not right of {x}");
            x = nx;
        }
        Assert.Equal(110, x);
        for (int i = 0; i < 11; i++)
        {
            pos = l.Move(pos, CaretMove.Left);
            float nx = l.GetCaretRect(pos).X;
            Assert.True(nx < x, $"step {i}: {pos} at {nx} not left of {x}");
            x = nx;
        }
        Assert.Equal(0, x);
    }

    [Fact]
    public void Rtl_paragraph_layout_and_navigation()
    {
        var l = Lay(Hebrew + " def", P(dir: BidiDirection.RightToLeft), 100);
        Assert.True(l.IsRightToLeft);
        Assert.Equal(30, l.GetLine(0).Left);
        Assert.Equal(100, CaretX(l, 0));
        Assert.Equal(90, CaretX(l, 1));
        Assert.Equal(60, CaretX(l, 7));
        // 从右端（逻辑起点）向左逐步移动
        var pos = new TextPosition(0);
        float x = 100;
        for (int i = 0; i < 7; i++)
        {
            pos = l.Move(pos, CaretMove.Left);
            float nx = l.GetCaretRect(pos).X;
            Assert.True(nx < x, $"step {i}: {pos} at {nx}");
            x = nx;
        }
        Assert.Equal(30, x);
    }

    [Fact]
    public void Auto_direction_follows_the_first_strong_character()
    {
        Assert.True(Lay(Hebrew + " abc", P(dir: BidiDirection.Auto)).IsRightToLeft);
        Assert.False(Lay("abc " + Hebrew, P(dir: BidiDirection.Auto)).IsRightToLeft);
        Assert.False(Lay("123", P(dir: BidiDirection.Auto)).IsRightToLeft);
        // 每个硬段落独立决定方向
        var two = Lay("abc\n" + Hebrew, P(WhiteSpace.Pre, TextAlign.Start, BidiDirection.Auto), 100);
        Assert.Equal(0, two.GetLine(0).Left);
        Assert.Equal(70, two.GetLine(1).Left);
    }

    [Fact]
    public void Mirrored_brackets_in_rtl_runs_are_shaped_as_their_mirror()
    {
        // 方块字体看不出镜像，用自定义字型记录被查询的码点
        var face = new RecordingFace();
        var fonts = new FontCollection();
        fonts.Register(face);
        var engine = new DefaultTextEngine(fonts, defaultFontFamilies: new[] { "Rec" });
        Lay(Alef + "(" + Bet + ")", engine: engine);
        Assert.Contains((int)')', face.Requested);
        Assert.Contains((int)'(', face.Requested);
    }

    sealed class RecordingFace : FontFace
    {
        readonly Testing.BoxFontFace _inner = new("Rec");
        public readonly HashSet<int> Requested = new();
        public override Graphics.Text.FontDescriptor Descriptor => _inner.Descriptor;
        public override Graphics.Text.FontMetrics Metrics => _inner.Metrics;
        public override int GlyphCount => _inner.GlyphCount;
        public override Graphics.Text.OutlineFormat OutlineFormat => _inner.OutlineFormat;
        public override ReadOnlyMemory<byte> GetTable(uint tag) => default;
        public override bool TryGetGlyph(int codePoint, out ushort glyph)
        {
            lock (Requested) Requested.Add(codePoint);
            glyph = (ushort)(codePoint == '(' ? 1 : codePoint == ')' ? 2 : 1);
            return true;
        }
        public override int GetAdvance(ushort glyph) => 1000;
        public override bool GetOutline(ushort glyph, in Graphics.Text.FontVariation variation, Graphics.PathBuilder sink, in System.Numerics.Matrix3x2 transform) => true;
        public override Graphics.Text.GlyphImage? GetColorGlyph(ushort glyph, float size) => null;
        public override IReadOnlyList<Graphics.Text.VariationAxis> Axes => Array.Empty<Graphics.Text.VariationAxis>();
    }

    [Fact]
    public void Caret_and_hit_test_round_trip_on_every_boundary()
    {
        var texts = new[]
        {
            ("aaaa bbbb cccc dddd", P()),
            (Mixed + " " + Mixed, P()),
            (Hebrew + " xyz " + Hebrew + " 12 " + Hebrew, P(dir: BidiDirection.RightToLeft)),
            ("e\u0301te\u0301 \U0001F44D\U0001F3FD x", P()),
            ("ab\ncd\n\nef", P(WhiteSpace.Pre)),
            ("中文字符测试中文", P(align: TextAlign.Center)),
        };
        foreach (var (text, p) in texts)
        {
            var l = Lay(text, p, 45);
            for (int o = 0; o <= text.Length; o++)
            {
                if (!TextSegmenter.IsGraphemeBoundary(text, o)) continue;
                foreach (var a in new[] { TextAffinity.Downstream, TextAffinity.Upstream })
                {
                    var pos = new TextPosition(o, a);
                    var caret = l.GetCaretRect(pos);
                    var hit = l.HitTest(new Point(caret.X, caret.Y + caret.Height / 2));
                    var back = l.GetCaretRect(hit);
                    Assert.True(Math.Abs(back.X - caret.X) < 0.01f && back.Y == caret.Y,
                        $"'{text}' {pos}: caret {caret} → hit {hit} → {back}");
                    Assert.True(TextSegmenter.IsGraphemeBoundary(text, hit.Offset));
                }
            }
        }
    }

    [Fact]
    public void Ltr_hit_test_returns_exact_offsets()
    {
        var l = Lay("aaaa bbbb cccc", width: 50);
        for (int o = 0; o <= 14; o++)
        {
            var c = l.GetCaretRect(new TextPosition(o));
            Assert.Equal(o, l.HitTest(new Point(c.X + 0.1f, c.Y + 1)).Offset);
        }
        Assert.Equal(new TextPosition(10, TextAffinity.Upstream), l.HitTest(new Point(300, 15)));
        Assert.Equal(50, l.GetCaretRect(new TextPosition(10, TextAffinity.Upstream)).X);
        Assert.Equal(new Rect(0, 20, 0, 10), l.GetCaretRect(new TextPosition(10)));
    }

    [Fact]
    public void Vertical_moves_keep_the_column()
    {
        var l = Lay("aaaa bbbb cccc", width: 50);
        var p = new TextPosition(2);
        p = l.Move(p, CaretMove.Down);
        Assert.Equal(7, p.Offset);
        p = l.Move(p, CaretMove.Down);
        Assert.Equal(12, p.Offset);
        Assert.Equal(14, l.Move(p, CaretMove.Down).Offset);
        Assert.Equal(7, l.Move(p, CaretMove.Up).Offset);
        Assert.Equal(0, l.Move(new TextPosition(2), CaretMove.Up).Offset);
    }

    [Fact]
    public void Logical_word_and_line_moves()
    {
        var l = Lay("aaaa bbbb cccc", width: 50);
        Assert.Equal(new TextPosition(5), l.Move(new TextPosition(7), CaretMove.LineStart));
        Assert.Equal(new TextPosition(10, TextAffinity.Upstream), l.Move(new TextPosition(7), CaretMove.LineEnd));
        Assert.Equal(new TextPosition(14), l.Move(new TextPosition(12), CaretMove.LineEnd));
        Assert.Equal(5, l.Move(new TextPosition(7), CaretMove.WordStart).Offset);
        Assert.Equal(0, l.Move(new TextPosition(5), CaretMove.WordStart).Offset);
        Assert.Equal(9, l.Move(new TextPosition(7), CaretMove.WordEnd).Offset);
        Assert.Equal(14, l.Move(new TextPosition(9), CaretMove.WordEnd).Offset);
        Assert.Equal(0, l.Move(new TextPosition(9), CaretMove.TextStart).Offset);
        Assert.Equal(14, l.Move(new TextPosition(9), CaretMove.TextEnd).Offset);
        Assert.Equal(8, l.Move(new TextPosition(7), CaretMove.Forward).Offset);
        Assert.Equal(6, l.Move(new TextPosition(7), CaretMove.Backward).Offset);
        Assert.Equal(new TextRange(5, 9), l.GetWordAt(6));
        Assert.Equal(new TextRange(4, 5), l.GetWordAt(4));
        Assert.Equal(new TextRange(10, 14), l.GetWordAt(14));
        Assert.Equal(new TextRange(5, 10), l.GetLineAt(7));
        Assert.Equal(new TextRange(10, 14), l.GetLineAt(10));
    }

    [Fact]
    public void Logical_moves_step_over_grapheme_clusters_and_collapsed_spaces()
    {
        const string text = "e\u0301\U0001F44D\U0001F3FD a   b";
        var l = Lay(text);
        var p = new TextPosition(0);
        var offsets = new List<int>();
        while (p.Offset < text.Length)
        {
            p = l.Move(p, CaretMove.Forward);
            offsets.Add(p.Offset);
        }
        // é | 👍🏽 | ␣ | a | ␣（其后两个折叠空格被跳过）| b
        Assert.Equal(new[] { 2, 6, 7, 8, 11, 12 }, offsets);
        Assert.Equal(new TextRange(2, 6), l.GetGraphemeAt(3));
        Assert.Equal(new TextRange(0, 2), l.GetGraphemeAt(0));
        Assert.Equal(new TextRange(11, 12), l.GetGraphemeAt(99));
        Assert.Equal(8, l.Move(new TextPosition(11), CaretMove.Backward).Offset);
    }

    [Fact]
    public void Line_boundary_moves_cross_lines()
    {
        var l = Lay("aaaa bbbb", width: 50);
        Assert.Equal(new TextPosition(5), l.Move(new TextPosition(5, TextAffinity.Upstream), CaretMove.Right));
        Assert.Equal(new TextPosition(5, TextAffinity.Upstream), l.Move(new TextPosition(5), CaretMove.Left));
        Assert.Equal(new TextPosition(4), l.Move(new TextPosition(5, TextAffinity.Upstream), CaretMove.Left));
        Assert.Equal(new TextPosition(9), l.Move(new TextPosition(9), CaretMove.Right));
    }

    [Fact]
    public void Hard_break_lines_put_the_caret_before_the_newline()
    {
        var l = Lay("ab\ncd", P(WhiteSpace.Pre));
        Assert.Equal(new TextPosition(2), l.HitTest(new Point(100, 5)));
        Assert.Equal(20, CaretX(l, 2));
        Assert.Equal(new Rect(0, 10, 0, 10), l.GetCaretRect(new TextPosition(3)));
        Assert.Equal(new TextPosition(3), l.Move(new TextPosition(2), CaretMove.Right));
        Assert.Equal(new TextPosition(2), l.Move(new TextPosition(3), CaretMove.Left));
        Assert.Equal(new TextPosition(2), l.Move(new TextPosition(1), CaretMove.LineEnd));
        var trailing = Lay("ab\n", P(WhiteSpace.Pre));
        Assert.Equal(new Rect(0, 10, 0, 10), trailing.GetCaretRect(new TextPosition(3)));
    }
}
