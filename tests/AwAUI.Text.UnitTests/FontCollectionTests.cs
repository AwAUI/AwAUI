using AwAUI.Text.OpenType;

namespace AwAUI.Text.UnitTests;

public class FontCollectionTests
{
    /// <summary>用同一份字体数据注册若干个不同描述符的「字型」，只验证匹配算法。</summary>
    static (FontCollection Fonts, Dictionary<string, FontFace> Faces) Family(params (int Weight, FontStyle Style, FontStretch Stretch)[] variants)
    {
        var fonts = new FontCollection();
        var faces = new Dictionary<string, FontFace>();
        foreach (var v in variants)
        {
            var face = TestFonts.Load(TestFonts.Abel);   // 每个变体是不同的对象
            fonts.Register(face, new FontDescriptor("Test", v.Weight, v.Style, v.Stretch));
            faces[$"{v.Weight}{v.Style}{v.Stretch}"] = face;
        }
        return (fonts, faces);
    }

    static string Key(int w, FontStyle s = FontStyle.Normal, FontStretch st = FontStretch.Normal) => $"{w}{s}{st}";

    [Fact]
    public void Weight_matching_follows_css_rules()
    {
        var (fonts, faces) = Family((300, FontStyle.Normal, FontStretch.Normal), (400, FontStyle.Normal, FontStretch.Normal),
            (600, FontStyle.Normal, FontStretch.Normal), (900, FontStyle.Normal, FontStretch.Normal));
        FontFace? M(int w) => fonts.Match(new FontQuery("Test", w));
        Assert.Same(faces[Key(400)], M(400));
        Assert.Same(faces[Key(400)], M(500));    // 期望 400–500：先找 ≤500 的更重者，没有则找更轻者
        Assert.Same(faces[Key(400)], M(450));
        Assert.Same(faces[Key(300)], M(350));    // < 400：先找更轻者
        Assert.Same(faces[Key(300)], M(100));    // 没有更轻的：找最近的更重者
        Assert.Same(faces[Key(600)], M(550));    // > 500：先找更重者
        Assert.Same(faces[Key(900)], M(700));
        Assert.Same(faces[Key(900)], M(1000));   // 没有更重的：找最近的更轻者
    }

    [Fact]
    public void Weight_500_prefers_lighter_when_nothing_between_400_and_500()
    {
        var (fonts, faces) = Family((300, FontStyle.Normal, FontStretch.Normal), (700, FontStyle.Normal, FontStretch.Normal));
        Assert.Same(faces[Key(300)], fonts.Match(new FontQuery("Test", 500)));
        Assert.Same(faces[Key(300)], fonts.Match(new FontQuery("Test", 400)));
    }

    [Fact]
    public void Style_fallback_order()
    {
        var (fonts, faces) = Family((400, FontStyle.Normal, FontStretch.Normal), (400, FontStyle.Oblique, FontStretch.Normal));
        Assert.Same(faces[Key(400, FontStyle.Oblique)], fonts.Match(new FontQuery("Test", 400, FontStyle.Italic)));
        Assert.Same(faces[Key(400)], fonts.Match(new FontQuery("Test", 400, FontStyle.Normal)));

        var (fonts2, faces2) = Family((700, FontStyle.Italic, FontStretch.Normal), (400, FontStyle.Normal, FontStretch.Normal));
        // 风格先于字重：要 400 italic 时选 700 italic，而不是 400 normal
        Assert.Same(faces2[Key(700, FontStyle.Italic)], fonts2.Match(new FontQuery("Test", 400, FontStyle.Italic)));
        // 要 oblique 时没有 oblique，退到 italic
        Assert.Same(faces2[Key(700, FontStyle.Italic)], fonts2.Match(new FontQuery("Test", 400, FontStyle.Oblique)));
    }

    [Fact]
    public void Stretch_is_matched_before_style_and_weight()
    {
        var (fonts, faces) = Family(
            (400, FontStyle.Normal, FontStretch.Condensed),
            (700, FontStyle.Italic, FontStretch.SemiExpanded),
            (400, FontStyle.Normal, FontStretch.Expanded));
        // 期望 normal(100%)：先找更窄的（condensed 75%），即使更宽的 semi-expanded 更近
        Assert.Same(faces[Key(400, FontStyle.Normal, FontStretch.Condensed)], fonts.Match(new FontQuery("Test")));
        // 期望 semi-expanded（>100%）：精确匹配，风格与字重让步
        Assert.Same(faces[Key(700, FontStyle.Italic, FontStretch.SemiExpanded)],
            fonts.Match(new FontQuery("Test", 400, FontStyle.Normal, FontStretch.SemiExpanded)));
        // 期望 extra-expanded（150%）：没有更宽的，取最近的更窄者 expanded
        Assert.Same(faces[Key(400, FontStyle.Normal, FontStretch.Expanded)],
            fonts.Match(new FontQuery("Test", 400, FontStyle.Normal, FontStretch.ExtraExpanded)));
    }

    [Fact]
    public void Family_list_and_generic_families()
    {
        var fonts = new FontCollection();
        fonts.Register(TestFonts.Bytes(TestFonts.Abel));
        fonts.RegisterFile(System.IO.Path.Combine(TestFonts.FontsDir, TestFonts.NotoSans));
        Assert.Equal(new[] { "Abel", "Noto Sans" }, fonts.FamilyNames);

        Assert.Equal("Abel", fonts.Match(new FontQuery(new[] { "Missing", "abel", "Noto Sans" }))!.Descriptor.FamilyName);
        Assert.Null(fonts.Match(new FontQuery("sans-serif")));
        fonts.SetGenericFamily("sans-serif", "Nope", "Noto Sans");
        Assert.Equal("Noto Sans", fonts.Match(new FontQuery(new[] { "Missing", "sans-serif" }))!.Descriptor.FamilyName);
        // 粗体请求在只有常规体时仍返回常规体（合成粗体由渲染层决定）
        Assert.Equal(400, fonts.Match(new FontQuery("Noto Sans", FontWeight.Bold))!.Descriptor.Weight.Value);
    }

    [Fact]
    public void Fallback_finds_a_face_with_the_glyph()
    {
        var fonts = new FontCollection();
        var abel = fonts.Register(TestFonts.Bytes(TestFonts.Abel)).Faces[0];
        var noto = fonts.Register(TestFonts.Bytes(TestFonts.NotoSans)).Faces[0];
        var query = new FontQuery("Abel");
        Assert.Same(abel, fonts.Fallback('A', query, null));
        Assert.False(abel.TryGetGlyph('Ж', out _));
        Assert.Same(noto, fonts.Fallback('Ж', query, "ru"));                   // 西里尔字母只有 Noto 有
        Assert.Same(noto, fonts.Fallback(0x10780, query, null));               // 补充平面
        Assert.Null(fonts.Fallback(0x4E2D, query, "zh"));                       // 都没有汉字
        Assert.Same(noto, fonts.Fallback('Ж', query, "ru"));                   // 缓存命中
    }

    [Fact]
    public void Override_descriptor_replaces_font_names_for_matching()
    {
        var fonts = new FontCollection();
        var reg = fonts.Register(FontSource.FromMemory(TestFonts.Bytes(TestFonts.Abel)), new FontDescriptor("Brand", FontWeight.Bold));
        Assert.Equal("Brand", reg.Descriptors[0].FamilyName);
        Assert.Equal("Abel", reg.Faces[0].Descriptor.FamilyName);   // 字型自身的描述符不变
        Assert.NotNull(fonts.Match(new FontQuery("Brand", FontWeight.Bold)));
        Assert.Null(fonts.Match(new FontQuery("Abel")));
    }

    [SystemFontFact(TestFonts.Helvetica)]
    public void System_collection_registration_and_matching()
    {
        var fonts = new FontCollection();
        var reg = fonts.Register(FontSource.FromFile(TestFonts.Helvetica));
        Assert.Equal(6, reg.Faces.Count);
        var bold = (OpenTypeFace)fonts.Match(new FontQuery("Helvetica", FontWeight.Bold))!;
        Assert.Equal("Helvetica Bold", bold.Names.FullName);
        var light = (OpenTypeFace)fonts.Match(new FontQuery("Helvetica", FontWeight.Light))!;
        Assert.Equal("Helvetica Light", light.Names.FullName);
        var boldItalic = (OpenTypeFace)fonts.Match(new FontQuery("Helvetica", FontWeight.Bold, FontStyle.Italic))!;
        Assert.Equal("Helvetica Bold Oblique", boldItalic.Names.FullName);

        // 只注册集合中的单个字型
        var single = new FontCollection().Register(FontSource.FromFile(TestFonts.Menlo, 1));
        Assert.Single(single.Faces);
    }
}
