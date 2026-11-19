using AwAUI.Graphics.Text;
using AwAUI.Text.OpenType;
using AwAUI.Text.Testing;
using AwAUI.Unicode;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

/// <summary>用真实字体（Noto Sans，不含阿拉伯文、希伯来文、CJK 与表情符号）的段落布局测试。</summary>
public class NotoLayoutTests
{
    static readonly Lazy<OpenTypeFace> s_noto = new(() => TestFonts.Load(TestFonts.NotoSans));

    public static OpenTypeFace Noto => s_noto.Value;

    public static DefaultTextEngine CreateEngine(params FontFace[] extra)
    {
        var fonts = new FontCollection();
        fonts.Register(Noto);
        foreach (var f in extra) fonts.Register(f);
        fonts.SetGenericFamily("sans-serif", "Noto Sans");
        return new DefaultTextEngine(fonts);
    }

    static readonly DefaultTextEngine Engine = CreateEngine();

    static ParagraphStyle NP(float size = 16, TextTransform transform = TextTransform.None, BidiDirection dir = BidiDirection.LeftToRight) =>
        new() { TextStyle = new TextStyle { FontSize = size, TextTransform = transform }, Direction = dir };

    /// <summary>引擎默认整形器（OpenType：GPOS 字距、连字）下的宽度。</summary>
    static float Advance(string text, float size = 16) =>
        OpenTypeShaper.Instance.Shape(new ShapeRequest(text, Noto, size)).Width;

    [Fact]
    public void Metrics_come_from_the_font()
    {
        var l = Lay("Hello", NP(), engine: Engine);
        var m = Noto.Metrics;
        float scale = 16f / m.UnitsPerEm;
        Assert.Equal(m.Ascender * scale + Math.Max(0, m.LineGap) * scale / 2, l.FirstBaseline, 3);
        Assert.Equal((m.Ascender - m.Descender + Math.Max(0, m.LineGap)) * scale, l.Height, 3);
        Assert.Equal(Advance("Hello"), l.Width, 3);
    }

    [Fact]
    public void Widths_are_sums_of_word_shapes()
    {
        var l = Lay("Hello brave new world", NP(), engine: Engine);
        Assert.Equal(Advance("Hello ") + Advance("brave ") + Advance("new ") + Advance("world"), l.MaxIntrinsicWidth, 3);
        // 词按「词 + 其后空格」整形（与 'kern' 表中字母与空格的字距一起），最小宽度减去悬挂的空格字形
        float space = Advance(" ");
        Assert.Equal(new[] { Advance("Hello ") - space, Advance("brave ") - space, Advance("new ") - space, Advance("world") }.Max(), l.MinIntrinsicWidth, 3);
        l.Layout(Advance("Hello brave "));
        Assert.Equal(new[] { "Hello brave ", "new world" }, Lines(l));
    }

    [Fact]
    public void Text_transform_changes_glyphs_not_offsets()
    {
        var upper = Lay("hello world", NP(transform: TextTransform.Uppercase), engine: Engine);
        var reference = Lay("HELLO WORLD", NP(), engine: Engine);
        Assert.Equal(reference.Width, upper.Width, 3);
        Assert.Equal(Record(reference).Glyphs[0].Run.Glyphs.ToArray(), Record(upper).Glyphs[0].Run.Glyphs.ToArray());
        Assert.Equal("hello world", upper.Text);

        var cap = Lay("hello wORLD o'neil", NP(transform: TextTransform.Capitalize), engine: Engine);
        Assert.Equal(Record(Lay("Hello WORLD O'neil", NP(), engine: Engine)).Glyphs[0].Run.Glyphs.ToArray(), Record(cap).Glyphs[0].Run.Glyphs.ToArray());

        var lower = Lay("ÀÉÎ", NP(transform: TextTransform.Lowercase), engine: Engine);
        Assert.Equal(Record(Lay("àéî", NP(), engine: Engine)).Glyphs[0].Run.Glyphs.ToArray(), Record(lower).Glyphs[0].Run.Glyphs.ToArray());
    }

    [Fact]
    public void Arabic_without_a_font_renders_notdef_in_visual_order()
    {
        const string arabic = "مرحبا";   // مرحبا
        Assert.False(Noto.TryGetGlyph(0x0645, out _));
        var l = Lay("Hi " + arabic + " ok", NP(), engine: Engine);
        var (glyphs, _, _) = Record(l);
        Assert.Equal(3, glyphs.Count);
        var notdef = glyphs[1].Run;
        Assert.All(notdef.Glyphs.ToArray(), g => Assert.Equal(0, g));
        Assert.Equal(5, notdef.Count);
        Assert.Equal(Advance("Hi ") + 5 * Noto.GetAdvance(0) * 16f / Noto.Metrics.UnitsPerEm + Advance(" ok"), l.Width, 2);
        // 命中测试在缺字的从右到左 run 中仍然按视觉顺序
        float left = glyphs[1].Origin.X;
        var hit = l.HitTest(new Point(left + 0.5f, 5));
        Assert.Equal(new TextPosition(3 + 5, TextAffinity.Upstream), hit);
    }

    [Fact]
    public void Arabic_uses_a_fallback_face_when_one_is_registered()
    {
        var box = new BoxFontFace("Box", coverage: cp => UnicodeData.GetScript(cp) is Script.Arabic or Script.Hebrew);
        var engine = CreateEngine(box);
        const string arabic = "مرحبا";
        var l = Lay("Hi " + arabic + " ok", NP(), engine: engine);
        var (glyphs, _, _) = Record(l);
        Assert.Equal(new[] { "Noto Sans", "Box", "Noto Sans" }, glyphs.Select(g => g.Run.Face.Descriptor.FamilyName).ToArray());
        Assert.Equal(Advance("Hi ") + 5 * 16 + Advance(" ok"), l.Width, 2);
        Assert.All(glyphs[1].Run.Glyphs.ToArray(), g => Assert.Equal(BoxFontFace.BoxGlyph, g));
        Assert.True(Lay(arabic + " abc", NP(dir: BidiDirection.Auto), engine: engine).IsRightToLeft);
    }

    [Fact]
    public void Cjk_and_emoji_without_glyphs_still_break_and_cluster()
    {
        var l = Lay("中文字符\U0001F44D\U0001F3FD", NP(), engine: Engine);
        float notdef = Noto.GetAdvance(0) * 16f / Noto.Metrics.UnitsPerEm;
        Assert.Equal(notdef * (4 + 2), l.Width, 2);   // 表情符号与肤色修饰符各一个 .notdef，但同属一个簇
        Assert.Equal(notdef * 2, l.MinIntrinsicWidth, 2);
        Assert.Equal(new TextRange(4, 8), l.GetGraphemeAt(5));
        l.Layout(notdef * 2.5f);
        Assert.Equal(new[] { "中文", "字符", "\U0001F44D\U0001F3FD" }, Lines(l));
    }

    [Fact]
    public void Kerning_inside_words_is_kept()
    {
        var abel = TestFonts.Load(TestFonts.Abel);
        var fonts = new FontCollection();
        fonts.Register(abel);
        var engine = new DefaultTextEngine(fonts, defaultFontFamilies: new[] { abel.Descriptor.FamilyName });
        var l = Lay("AV AV", new ParagraphStyle { TextStyle = new TextStyle { FontSize = 2048 } }, engine: engine);
        var run = BasicTextShaper.Instance.Shape(new ShapeRequest("AV ", abel, 2048));
        Assert.Equal(run.Width * 2 - abel.GetAdvance(run.Glyphs[2]), l.Width, 2);
    }
}
