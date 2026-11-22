using AwAUI.Graphics;
using AwAUI.Text.OpenType;
using AwAUI.Unicode;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

/// <summary>第八批文本整形复审发现的问题的回归测试（段落布局部分）。</summary>
public class ShapingReviewLayoutTests
{
    static OpenTypeFace Font => OpenTypeShaperTests.Face("AwATest-Layout.ttf");

    static DefaultTextEngine CreateEngine()
    {
        var fonts = new FontCollection();
        fonts.Register(Font);
        return new DefaultTextEngine(fonts, defaultFontFamilies: new[] { Font.Descriptor.FamilyName });
    }

    static ushort[] DrawnGlyphs(TextLayout l) => Record(l).Glyphs.SelectMany(g => g.Run.Glyphs.ToArray()).ToArray();

    [Fact]
    public void Arabic_joins_across_a_style_change_inside_a_word()
    {
        // 词中间换颜色（例如高亮一个字母）：两段分别整形，但连写必须看到相邻的字符（与浏览器相同）
        var engine = CreateEngine();
        var p = new ParagraphStyle { TextStyle = new TextStyle { FontSize = 1000 }, Direction = ParagraphDirection.RightToLeft };
        var whole = DrawnGlyphs(engine.CreateLayout("ببب", p));
        var b = engine.CreateParagraphBuilder(p);
        b.AddText("ب");
        b.PushStyle(new TextStyle { FontSize = 1000, Color = Color.FromRgb(255, 0, 0) });
        b.AddText("ب");
        b.Pop();
        b.AddText("ب");
        var split = b.Build();
        split.Layout(float.PositiveInfinity);
        var ids = DrawnGlyphs(split);
        Assert.Equal(3, ids.Length);
        Assert.Equal(whole.OrderBy(g => g), ids.OrderBy(g => g));
    }

    [Fact]
    public void Arabic_does_not_join_across_a_hard_line_break()
    {
        // 整形文本把控制符（含换行）换成 ZWSP，而 ZWSP 的 Joining_Type 是 T（透明）：上下文不能越过它们连写到下一行
        var engine = CreateEngine();
        var p = new ParagraphStyle { TextStyle = new TextStyle { FontSize = 1000 }, Direction = ParagraphDirection.RightToLeft };
        var single = DrawnGlyphs(engine.CreateLayout("ب", p));
        foreach (var text in new[] { "ب\nب", "ب\u2028ب", "ب\rب", "ب\r\nب", "ب\u2029ب", "ب\u0085ب" })
        {
            var ids = DrawnGlyphs(engine.CreateLayout(text, p));
            Assert.True(ids.Count(g => g == single[0]) == 2, $"{(int)text[1]:X4}: {string.Join(",", ids)}");
        }
        // ZWSP 本身（字符数据中真的存在）是透明的，连写照常跨过它（与整段整形一致）
        var zw = DrawnGlyphs(engine.CreateLayout("ب\u200Bب", p));
        Assert.DoesNotContain(single[0], zw);
    }

    [Fact]
    public void Letter_spacing_disables_optional_ligatures()
    {
        // CSS Text 3 §8.2：字母间距不为 0 时不应用可选连字。否则 "ffi" 画成一个字形，三个单元却各自加上间距，连字后面留出空隙
        var engine = CreateEngine();
        var p = new ParagraphStyle { TextStyle = new TextStyle { FontSize = 1000, LetterSpacing = 100 } };
        var l = engine.CreateLayout("ffi", p);
        var (glyphs, _, _) = Record(l);
        var ids = glyphs.SelectMany(g => g.Run.Glyphs.ToArray()).ToArray();
        Assert.True(Font.TryGetGlyph('f', out var f));
        Assert.True(Font.TryGetGlyph('i', out var i));
        Assert.Equal(new[] { f, f, i }, ids);
        var xs = glyphs.SelectMany(GlyphXs).ToArray();
        Assert.Equal(Font.GetAdvance(f) + 100, xs[1] - xs[0], 1);
        // 显式的特性设置优先于这一默认行为
        var forced = engine.CreateLayout("ffi", new ParagraphStyle
        {
            TextStyle = new TextStyle { FontSize = 1000, LetterSpacing = 100, Features = new[] { new FontFeature("liga", 1) } },
        });
        Assert.Single(Record(forced).Glyphs.SelectMany(g => g.Run.Glyphs.ToArray()));
    }
}
