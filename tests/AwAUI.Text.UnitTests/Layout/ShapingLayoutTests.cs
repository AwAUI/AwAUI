using AwAUI.Graphics;
using AwAUI.Text.OpenType;
using AwAUI.Unicode;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

/// <summary>OpenType 整形接入段落布局：连字内部的光标位置、从右到左的连字、跨词字距、特性设置与整形缓存。</summary>
public class ShapingLayoutTests
{
    static OpenTypeFace Font => OpenTypeShaperTests.Face("AwATest-Layout.ttf");

    static DefaultTextEngine CreateEngine(TextShaper? shaper = null)
    {
        var fonts = new FontCollection();
        fonts.Register(Font);
        return new DefaultTextEngine(fonts, shaper, defaultFontFamilies: new[] { Font.Descriptor.FamilyName });
    }

    static ParagraphStyle Para(float size = 1000, BidiDirection dir = BidiDirection.LeftToRight, IReadOnlyList<FontFeature>? features = null) =>
        new() { TextStyle = new TextStyle { FontSize = size, Features = features }, Direction = dir };

    [Fact]
    public void Default_engine_uses_the_opentype_shaper()
    {
        Assert.Same(OpenTypeShaper.Instance, CreateEngine().Shaper);
    }

    [Fact]
    public void Ligature_advance_is_shared_by_its_graphemes_for_carets_and_hit_testing()
    {
        // "ffi" → 一个连字字形（前进量 760），三个字素单元各得 1/3
        var l = Lay("ffi", Para(), engine: CreateEngine());
        Assert.Equal(760, l.Width, 2);
        float x1 = l.GetCaretRect(new TextPosition(1)).X, x2 = l.GetCaretRect(new TextPosition(2)).X;
        Assert.Equal(760f / 3, x1, 1);
        Assert.Equal(760f * 2 / 3, x2, 1);
        Assert.Equal(1, l.HitTest(new Point(760f / 3 + 10, 10)).Offset);
        Assert.Equal(2, l.HitTest(new Point(760f * 2 / 3 - 10, 10)).Offset);
        // 绘制：只有一个字形
        var (glyphs, _, _) = Record(l);
        Assert.Single(glyphs);
        Assert.Equal(1, glyphs[0].Run.Glyphs.Length);
        Assert.True(Font.TryGetGlyph('f', out var f));
        Assert.NotEqual(f, glyphs[0].Run.Glyphs[0]);
    }

    [Fact]
    public void Rtl_ligature_is_drawn_at_the_left_of_its_units()
    {
        // lam + alef → lam_alef（rlig），从右到左：字形归视觉上最左的单元（逻辑上的 alef）
        var l = Lay("لا", Para(dir: BidiDirection.RightToLeft), engine: CreateEngine());
        var (glyphs, _, _) = Record(l);
        var run = Assert.Single(glyphs).Run;
        Assert.Equal(1, run.Glyphs.Length);
        float left = glyphs[0].Origin.X + run.Positions[0].X;
        Assert.Equal(0, left, 1);
        Assert.Equal(560, l.Width, 1);
        // 光标：偏移 0 在右端，1 在中间，2 在左端
        Assert.Equal(560, l.GetCaretRect(new TextPosition(0)).X, 1);
        Assert.Equal(280, l.GetCaretRect(new TextPosition(1)).X, 1);
        Assert.Equal(0, l.GetCaretRect(new TextPosition(2)).X, 1);
    }

    [Fact]
    public void Kerning_across_word_boundaries_is_kept()
    {
        // "a To"：词 "a " 与 "To" 分别整形；空格与 T 之间的 GPOS 字距（−50）在边界处补上
        var engine = CreateEngine();
        var l = Lay("a To", Para(), engine: engine);
        var whole = OpenTypeShaper.Instance.Shape(new ShapeRequest("a To", Font, 1000) { Script = Script.Latin });
        Assert.Equal(whole.Width, l.Width, 2);
        Assert.Equal(whole.Width, l.MaxIntrinsicWidth, 2);
        var parts = OpenTypeShaper.Instance.Shape(new ShapeRequest("a ", Font, 1000) { Script = Script.Latin }).Width
            + OpenTypeShaper.Instance.Shape(new ShapeRequest("To", Font, 1000) { Script = Script.Latin }).Width;
        Assert.Equal(parts - 50, l.Width, 2);
        // 绘制位置与整段整形一致
        var (glyphs, _, _) = Record(l);
        var drawn = glyphs.SelectMany(g => g.Run.Positions.ToArray().Select(p => p.X + g.Origin.X)).ToArray();
        var expected = new List<float>();
        float x = 0;
        for (int i = 0; i < whole.Count; i++)
        {
            if ((whole.Flags[i] & GlyphFlags.Hidden) == 0) expected.Add(x + whole.Offsets[i].X);
            x += whole.Advances[i].X;
        }
        Assert.Equal(expected.Count, drawn.Length);
        for (int i = 0; i < drawn.Length; i++) Assert.Equal(expected[i], drawn[i], 1);

        // 关闭 kern 时没有边界字距
        var off = Lay("a To", Para(features: new[] { new FontFeature("kern", 0) }), engine: engine);
        Assert.Equal(parts + 70, off.Width, 2);   // 词内的 T-o（−70）也一起关闭
    }

    [Fact]
    public void Basic_shaper_also_keeps_cross_word_kern_table_pairs()
    {
        // Abel 的 'kern' 表：拿一个以空格结束的词与下一个词之间的成对值验证（若字体没有这类对，至少不改变宽度）
        var abel = TestFonts.Load(TestFonts.Abel);
        var fonts = new FontCollection();
        fonts.Register(abel);
        var engine = new DefaultTextEngine(fonts, BasicTextShaper.Instance, defaultFontFamilies: new[] { abel.Descriptor.FamilyName });
        var p = new ParagraphStyle { TextStyle = new TextStyle { FontSize = 2048 } };
        foreach (var text in new[] { "A V", "T o", "Y A" })
        {
            var l = engine.CreateLayout(text, p);
            var whole = BasicTextShaper.Instance.Shape(new ShapeRequest(text, abel, 2048));
            Assert.Equal(whole.Width, l.Width, 1);
        }
    }

    [Fact]
    public void Features_take_part_in_the_shape_cache_key()
    {
        var engine = CreateEngine();
        var salt1 = Para(features: new[] { new FontFeature("salt", 1) });
        var salt2 = Para(features: new[] { new FontFeature("salt", 2) });
        var a = Lay("aaa aaa", salt1, engine: engine);
        var b = Lay("aaa aaa", salt2, engine: engine);
        var c = Lay("aaa aaa", salt1, engine: engine);
        Assert.NotEqual(a.Width, b.Width);
        Assert.Equal(a.Width, c.Width);
        // 同样内容的特性数组（不同实例）命中缓存
        var stats = engine.ShapeCacheStatistics;
        Assert.True(stats.Hits >= 2, stats.ToString());
    }

    [Fact]
    public void Arabic_paragraph_joins_across_the_layout()
    {
        var l = Lay("ببب لا", Para(dir: BidiDirection.RightToLeft), engine: CreateEngine());
        var (glyphs, _, _) = Record(l);
        var ids = glyphs.SelectMany(g => g.Run.Glyphs.ToArray()).ToArray();
        Assert.True(Font.TryGetGlyph(0x628, out var beh));
        Assert.DoesNotContain(beh, ids);    // 全部换成了 init / medi / fina 形式
        Assert.Equal(5, ids.Length);        // beh.fina beh.medi beh.init + 空格 + lam_alef（视觉顺序）
        for (int o = 0; o <= 6; o++) Assert.False(float.IsNaN(l.GetCaretRect(new TextPosition(o)).X));
    }
}

/// <summary>CFF 中文字体作为回退字体（Counter 示例的场景：Noto Sans 不含 CJK）。</summary>
public class CffFallbackLayoutTests
{
    [SystemFontFact(CffTests.HiraginoSansGb)]
    public void Cjk_falls_back_to_a_cff_font_and_paints_outlines()
    {
        var fonts = new FontCollection();
        fonts.Register(TestFonts.Load(TestFonts.NotoSans));
        fonts.RegisterFile(CffTests.HiraginoSansGb);
        fonts.SetGenericFamily("sans-serif", "Noto Sans");
        var engine = new DefaultTextEngine(fonts);
        var l = engine.CreateLayout("Count: 计数器 12", new ParagraphStyle { TextStyle = new TextStyle { FontSize = 20 } });
        var (glyphs, _, _) = Record(l);
        Assert.Contains(glyphs, g => g.Run.Face is OpenTypeFace { OutlineFormat: OutlineFormat.Cff });
        foreach (var g in glyphs)
        {
            var pb = new AwAUI.Graphics.PathBuilder();
            foreach (var id in g.Run.Glyphs.ToArray()) Assert.True(g.Run.Face.GetOutline(id, default, pb));
        }
    }
}
