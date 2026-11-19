using AwAUI.Graphics;
using AwAUI.Text.OpenType;
using AwAUI.Unicode;

namespace AwAUI.Text.UnitTests;

public class ShaperTests
{
    static ShapedRun Shape(FontFace face, string text, float size = 2048, bool rtl = false, FontFeature[]? features = null, TextRange? range = null)
    {
        var req = range is { } r ? new ShapeRequest(text, r, face, size) : new ShapeRequest(text, face, size);
        req = new ShapeRequest(text, req.Range, face, size) { RightToLeft = rtl, Features = features ?? Array.Empty<FontFeature>(), Script = Script.Latin };
        return BasicTextShaper.Instance.Shape(req);
    }

    static ushort G(FontFace f, char c) => f.TryGetGlyph(c, out var g) ? g : throw new InvalidOperationException();

    [Fact]
    public void Hello_AV_applies_pair_kerning()
    {
        var abel = TestFonts.Load(TestFonts.Abel);   // upem 2048，字号 2048 使像素 = 设计单位
        var run = Shape(abel, "Hello AV");
        Assert.Equal(8, run.Count);
        Assert.Equal("Hello AV".Select(c => G(abel, c)).ToArray(), run.Glyphs.ToArray());
        Assert.Equal(Enumerable.Range(0, 8).ToArray(), run.Clusters.ToArray());

        // fontTools：A/V 字距 −41；A 前进量 950
        Assert.Equal(-41, abel.GetKerning(G(abel, 'A'), G(abel, 'V')));
        Assert.Equal(950 - 41, run.Advances[6].X, 3);
        Assert.Equal(950, run.Advances[7].X, 3);
        Assert.Equal(GlyphFlags.UnsafeToBreak, run.Flags[7]);
        Assert.Equal(GlyphFlags.None, run.Flags[6]);

        // 每个字形的前进量 = hmtx + 与下一个字形的字距
        var glyphs = run.Glyphs.ToArray();
        float total = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            int expected = abel.GetAdvance(glyphs[i]) + (i + 1 < glyphs.Length ? abel.GetKerning(glyphs[i], glyphs[i + 1]) : 0);
            Assert.Equal(expected, run.Advances[i].X, 3);
            Assert.Equal(0, run.Advances[i].Y);
            Assert.Equal(System.Numerics.Vector2.Zero, run.Offsets[i]);
            total += run.Advances[i].X;
        }
        Assert.Equal(total, run.Width, 2);
    }

    [Fact]
    public void Kerning_can_be_disabled_with_a_feature()
    {
        var abel = TestFonts.Load(TestFonts.Abel);
        var off = Shape(abel, "AVAV", features: new[] { new FontFeature("kern", 0) });
        Assert.All(off.Advances.ToArray(), a => Assert.Equal(950, a.X, 3));
        // 只关闭后半部分
        var partial = Shape(abel, "AVAV", features: new[] { new FontFeature("kern", 0, 2, 4) });
        Assert.Equal(909, partial.Advances[0].X, 3);
        Assert.Equal(950, partial.Advances[2].X, 3);
    }

    [Fact]
    public void Advances_scale_with_font_size()
    {
        var noto = TestFonts.Load(TestFonts.NotoSans);   // upem 1000
        var run = Shape(noto, "H", size: 20);
        Assert.Equal(noto.GetAdvance(G(noto, 'H')) * 20f / 1000f, run.Advances[0].X, 4);
        Assert.Equal(20, run.Size);
    }

    [Fact]
    public void Surrogate_pairs_produce_one_glyph_with_the_cluster_at_the_high_surrogate()
    {
        var noto = TestFonts.Load(TestFonts.NotoSans);
        string text = "a\U00010780b";   // U+10780 在 Noto Sans 的 format 12 子表中
        var run = Shape(noto, text, 1000);
        Assert.Equal(3, run.Count);
        Assert.Equal(new[] { 0, 1, 3 }, run.Clusters.ToArray());
        Assert.Equal(3796, run.Glyphs[1]);

        // 孤立代理项按 U+FFFD 处理；缺字映射为 .notdef，前进量取 .notdef 的
        var lone = Shape(noto, "\uD800x\U0001F600", 1000);
        Assert.Equal(new[] { 0, 1, 2 }, lone.Clusters.ToArray());
        Assert.Equal(G(noto, '\uFFFD'), lone.Glyphs[0]);
        Assert.Equal(0, lone.Glyphs[2]);
        Assert.Equal(noto.GetAdvance(0), lone.Advances[2].X, 3);
    }

    [Fact]
    public void Range_is_shaped_with_absolute_clusters_and_does_not_split_pairs()
    {
        var noto = TestFonts.Load(TestFonts.NotoSans);
        string text = "xx\U00010780yy";
        var run = Shape(noto, text, 1000, range: new TextRange(2, 5));
        Assert.Equal(new[] { 2, 4 }, run.Clusters.ToArray());
        Assert.Equal(new TextRange(2, 5), run.Range);
        // 范围在代理对中间结束：高代理项单独出现，按 U+FFFD 处理
        var cut = Shape(noto, text, 1000, range: new TextRange(0, 3));
        Assert.Equal(3, cut.Count);
        Assert.True(noto.TryGetGlyph(0xFFFD, out var replacement));
        Assert.Equal(replacement, cut.Glyphs[2]);
    }

    [Fact]
    public void Right_to_left_runs_are_in_visual_order()
    {
        var abel = TestFonts.Load(TestFonts.Abel);
        var run = Shape(abel, "AVo", rtl: true);
        Assert.True(run.RightToLeft);
        Assert.Equal(new[] { 2, 1, 0 }, run.Clusters.ToArray());
        Assert.Equal(new[] { G(abel, 'o'), G(abel, 'V'), G(abel, 'A') }, run.Glyphs.ToArray());
        // 视觉顺序中 V 在 A 左侧：按 (V, A) 查字距，加在 V 上；逻辑上 A 之后的 V 之前不可断开
        Assert.Equal(abel.GetAdvance(G(abel, 'V')) + abel.GetKerning(G(abel, 'V'), G(abel, 'A')), run.Advances[1].X, 3);
        Assert.Equal(GlyphFlags.UnsafeToBreak, run.Flags[1]);
    }

    [Fact]
    public void Default_ignorables_are_zero_width()
    {
        var noto = TestFonts.Load(TestFonts.NotoSans);
        var run = Shape(noto, "a‍b­c️", 1000);
        Assert.Equal(6, run.Count);
        Assert.Equal(0, run.Advances[1].X);
        Assert.Equal(0, run.Advances[3].X);
        Assert.Equal(0, run.Advances[5].X);
        Assert.Equal(G(noto, ' '), run.Glyphs[1]);
        Assert.Equal(GlyphFlags.Hidden, run.Flags[1]);
        Assert.Equal(GlyphFlags.Hidden, run.Flags[5]);
        Assert.Equal(GlyphFlags.None, run.Flags[2]);

        // 可忽略字符不打断字距对
        var abel = TestFonts.Load(TestFonts.Abel);
        var kerned = Shape(abel, "A​V");
        Assert.Equal(909, kerned.Advances[0].X, 3);
    }

    [Fact]
    public void Empty_text_produces_an_empty_run()
    {
        var run = Shape(TestFonts.Load(TestFonts.Abel), "");
        Assert.Equal(0, run.Count);
        Assert.Equal(0, run.Width);
    }

    [Fact]
    public void Append_outlines_places_glyphs_along_the_baseline()
    {
        var abel = TestFonts.Load(TestFonts.Abel);
        var run = Shape(abel, "lll", size: 20.48f);   // 1 px = 100 设计单位
        var pb = new PathBuilder();
        Assert.Equal(0, run.AppendOutlines(pb, new Point(10, 50)));
        var path = pb.ToPath();
        Assert.Equal(3, path.Verbs.ToArray().Count(v => v == PathVerb.Move));
        Assert.True(abel.TryGetGlyphBounds(G(abel, 'l'), out var b));
        Assert.Equal(10 + b.Left / 100, path.Bounds.Left, 2);
        Assert.Equal(50, path.Bounds.Bottom, 1);
        Assert.Equal(10 + 2 * abel.GetAdvance(G(abel, 'l')) / 100f + b.Right / 100, path.Bounds.Right, 2);
    }

    [Fact]
    public void Uses_the_shared_unicode_code_point_and_script_data()
    {
        var noto = TestFonts.Load(TestFonts.NotoSans);
        // 补充平面码点占两个码元；孤立的低代理项按 U+FFFD 查找字形
        var run = Shape(noto, "\U00010780A\uDE00", 1000);
        Assert.Equal(new[] { 0, 2, 3 }, run.Clusters.ToArray());
        Assert.True(noto.TryGetGlyph(0x10780, out var aa));
        Assert.True(noto.TryGetGlyph(0xFFFD, out var replacement));
        Assert.Equal(new[] { aa, G(noto, 'A'), replacement }, run.Glyphs.ToArray());
        // 超出码点范围的值不会到达 cmap
        Assert.False(noto.TryGetGlyph(0x110000, out var g));
        Assert.Equal(0, g);
        Assert.False(noto.TryGetGlyph(-1, out _));
        Assert.Equal(Script.Latin, UnicodeData.GetScript('A'));
        Assert.Equal("Latn", UnicodeData.GetScriptCode(Script.Latin));
        // AwAUI.Unicode 的默认可忽略字符（含 U+180F、U+2065 等），谚文填充符除外
        Assert.True(BasicTextShaper.IsDefaultIgnorable(0x200D));
        Assert.True(BasicTextShaper.IsDefaultIgnorable(0xE0001));
        Assert.False(BasicTextShaper.IsDefaultIgnorable(0x3164));
        Assert.False(BasicTextShaper.IsDefaultIgnorable('A'));
    }

    [Fact]
    public void Default_ignorables_are_hidden_when_the_font_has_no_space_glyph()
    {
        // 字形 0（.notdef）是 400 单位的方框，字形 1 是 100 单位的 'A'；字体没有空格
        var f = new SyntheticFont();
        f.AddGlyph(SyntheticFont.Simple(SyntheticFont.Square(0, 0, 400)));
        f.AddGlyph(SyntheticFont.Simple(SyntheticFont.Square(0, 0, 100)), advance: 200);
        f.Cmap = SyntheticFont.CmapFormat4((0x41, 1));
        var face = OpenTypeFace.Load(f.Build());

        var run = Shape(face, "A\u200DA", size: 1000);
        Assert.Equal(new ushort[] { 1, 0, 1 }, run.Glyphs.ToArray());
        Assert.Equal(GlyphFlags.Hidden, run.Flags[1]);
        Assert.Equal(GlyphFlags.None, run.Flags[0]);
        Assert.Equal(0, run.Advances[1].X);

        var pb = new PathBuilder();
        Assert.Equal(0, run.AppendOutlines(pb, new Point(0, 0)));
        var bounds = pb.ToPath().Bounds;
        Assert.Equal(100, bounds.Height, 3);
        Assert.Equal(300, bounds.Width, 3);
    }

    [Fact]
    public void ToGlyphRun_accumulates_positions_and_drops_hidden_glyphs()
    {
        var abel = TestFonts.Load(TestFonts.Abel);
        var run = Shape(abel, "AV\u200Do", size: 20);
        Assert.Equal(GlyphFlags.Hidden, run.Flags[2]);
        var glyphRun = run.ToGlyphRun(GlyphRunFlags.SyntheticBold);
        Assert.Same(abel, glyphRun.Face);
        Assert.Equal(20, glyphRun.Size);
        Assert.Equal(GlyphRunFlags.SyntheticBold, glyphRun.Flags);
        Assert.Equal(new[] { G(abel, 'A'), G(abel, 'V'), G(abel, 'o') }, glyphRun.Glyphs.ToArray());
        Assert.Equal(new Point(0, 0), glyphRun.Positions[0]);
        Assert.Equal(run.Advances[0].X, glyphRun.Positions[1].X, 4);
        // 隐藏字形的前进量为 0，'o' 紧接在 'V' 之后
        Assert.Equal(run.Advances[0].X + run.Advances[1].X, glyphRun.Positions[2].X, 4);
        Assert.True(glyphRun.Bounds.Right >= run.Width);

        // 绘制结果与 AppendOutlines 的轮廓一致：录制为 DrawGlyphRun 命令
        var dc = new DrawingContext();
        dc.DrawGlyphRun(run.ToGlyphRun(), new Point(5, 30), Color.Black);
        var list = dc.Finish();
        var reader = list.GetReader();
        Assert.True(reader.MoveNext());
        Assert.Equal(DrawOp.DrawGlyphRun, reader.Op);
    }

    [Fact]
    public void ToGlyphRun_turns_an_invalid_size_into_an_empty_run_instead_of_throwing()
    {
        // 回归：ShapeRequest / ShapedRun 接受任意字号（例如来自样式的 NaN、无穷或负值），
        // 而 GlyphRun 的构造函数拒绝它们，ToGlyphRun 因此抛出异常、中断整帧绘制
        var abel = TestFonts.Load(TestFonts.Abel);
        foreach (var size in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -12f })
        {
            var glyphRun = Shape(abel, "AVo", size).ToGlyphRun();
            Assert.True(glyphRun.IsEmpty);
            Assert.Equal(0, glyphRun.Size);
            var dc = new DrawingContext();
            dc.DrawGlyphRun(glyphRun, new Point(5, 30), Color.Black);
            Assert.True(dc.Finish().IsEmpty);
        }
    }

    [Fact]
    public void Public_constructor_copies_its_arrays()
    {
        var face = TestFonts.Load(TestFonts.Abel);
        var glyphs = new ushort[] { 1, 2 };
        var clusters = new[] { 0, 1 };
        var advances = new[] { new System.Numerics.Vector2(10, 0), new System.Numerics.Vector2(20, 0) };
        var offsets = new System.Numerics.Vector2[2];
        var flags = new GlyphFlags[2];
        var run = new ShapedRun(face, 16, new TextRange(0, 2), false, glyphs, clusters, advances, offsets, flags);
        glyphs[0] = 99; clusters[0] = 5; advances[0] = new System.Numerics.Vector2(1000, 0); flags[0] = GlyphFlags.Hidden;
        Assert.Equal(1, run.Glyphs[0]);
        Assert.Equal(0, run.Clusters[0]);
        Assert.Equal(10, run.Advances[0].X);
        Assert.Equal(GlyphFlags.None, run.Flags[0]);
        Assert.Equal(30, run.Width);
        Assert.Throws<ArgumentException>(() => new ShapedRun(face, 16, new TextRange(0, 2), false, glyphs, new int[1], advances, offsets, flags));
    }
}
