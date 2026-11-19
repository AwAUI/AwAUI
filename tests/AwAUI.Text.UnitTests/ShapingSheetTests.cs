using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Text.OpenType;
using AwAUI.Unicode;

namespace AwAUI.Text.UnitTests;

/// <summary>
/// 整形总览图：拉丁连字与字距、阿拉伯文连写与标记、CFF 中日文字体，经段落布局（<see cref="TextLayout"/>）与软件后端绘制。
/// 设置环境变量 AWAUI_SHAPING_SHEET=输出路径.png 时写出图片供人工检查；系统字体缺失的行跳过。不是黄金图测试。
/// </summary>
public class ShapingSheetTests
{
    const string SfArabic = "/System/Library/Fonts/SFArabic.ttf";
    const string GeezaPro = "/System/Library/Fonts/GeezaPro.ttc";
    const string SfHebrew = "/System/Library/Fonts/SFHebrew.ttf";
    const string HiraginoSans = "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc";
    const string HiraginoMincho = "/System/Library/Fonts/ヒラギノ明朝 ProN.ttc";
    const string HiraginoGb = "/System/Library/Fonts/Hiragino Sans GB.ttc";

    static TextLayout Layout(FontFace face, string text, float size, TextShaper? shaper = null, IReadOnlyList<FontFeature>? features = null,
        BidiDirection dir = BidiDirection.Auto, string? locale = null)
    {
        var fonts = new FontCollection();
        fonts.Register(face);
        var engine = new DefaultTextEngine(fonts, shaper, defaultFontFamilies: new[] { face.Descriptor.FamilyName });
        var style = new ParagraphStyle
        {
            TextStyle = new TextStyle { FontSize = size, Color = Color.Parse("#202020"), Features = features, Locale = locale },
            Direction = dir,
        };
        return engine.CreateLayout(text, style);
    }

    static void Label(DrawingContext dc, FontFace noto, string text, float y)
    {
        var l = Layout(noto, text, 11);
        l.Paint(dc, new Point(12, y));
    }

    [Fact]
    public void Overview_sheet()
    {
        var path = Environment.GetEnvironmentVariable("AWAUI_SHAPING_SHEET");
        var noto = TestFonts.Load(TestFonts.NotoSans);
        var dc = new DrawingContext();
        float y = 10;
        const float W = 900;

        void Row(string label, TextLayout layout, float height)
        {
            Label(dc, noto, label, y);
            dc.FillRect(new Rect(12, y + 16 + layout.FirstBaseline, layout.Width, 0.5f), Color.Parse("#90caf9"));
            layout.Paint(dc, new Point(12, y + 16));
            y += 16 + height;
        }

        Row("Noto Sans, OpenTypeShaper: liga (fi, ffl) + GPOS kern (AV, To, WAVE)",
            Layout(noto, "office waffle fine flow — AVATAR To Ty WAVE Yo", 34), 52);
        Row("Same text, BasicTextShaper (no GSUB/GPOS; Noto has no 'kern' table)",
            Layout(noto, "office waffle fine flow — AVATAR To Ty WAVE Yo", 34, BasicTextShaper.Instance), 52);
        Row("font-feature-settings: liga 0, kern 0",
            Layout(noto, "office waffle fine flow — AVATAR To Ty WAVE Yo", 34,
                features: new[] { new FontFeature("liga", 0), new FontFeature("kern", 0) }), 52);
        Row("Noto Sans combining marks via GPOS mark/mkmk",
            Layout(noto, "ą́ x̂̃ q̣̇ ĕ́ ñ", 34), 56);

        if (File.Exists(SfArabic))
        {
            var sfa = OpenTypeFace.Load(SfArabic);
            Row("SF Arabic (GSUB init/medi/fina/isol, rlig lam-alef, GPOS mark/mkmk)",
                Layout(sfa, "بِسْمِ اللَّهِ الرَّحْمَٰنِ الرَّحِيمِ ، مرحبا بالعالم، لا", 34), 70);
            Row("SF Arabic: beh ZWJ beh, beh ZWNJ beh, tatweel-beh-tatweel, salam",
                Layout(sfa, "ب‍ب  ب‌ب  ـبـ  سلام", 34), 64);
        }
        if (File.Exists(GeezaPro))
        {
            var geeza = OpenTypeFace.Load(GeezaPro);
            Row("Geeza Pro (AAT only, no GSUB): Unicode presentation-form fallback",
                Layout(geeza, "مرحبا بالعالم ، السلام عليكم، لا إله", 34), 64);
        }
        if (File.Exists(SfHebrew))
        {
            var sfh = OpenTypeFace.Load(SfHebrew);
            Row("SF Hebrew with niqqud (GPOS mark)", Layout(sfh, "בְּרֵאשִׁית בָּרָא אֱלֹהִים", 34), 58);
        }
        if (File.Exists(HiraginoSans))
        {
            var hira = OpenTypeFace.Load(HiraginoSans);
            Row("Hiragino Sans W3 (CID-keyed CFF): Japanese", Layout(hira, "日本語のテキスト、「引用」。カタカナ・ひらがな", 32, locale: "ja"), 52);
            Row("Hiragino Sans W3, palt", Layout(hira, "日本語のテキスト、「引用」。カタカナ・ひらがな", 32, features: new[] { new FontFeature("palt") }, locale: "ja"), 52);
        }
        if (File.Exists(HiraginoGb))
        {
            var gb = OpenTypeFace.Load(HiraginoGb);
            Row("Hiragino Sans GB (CID-keyed CFF): Simplified Chinese", Layout(gb, "中文排版：标点挤压，“引号”与汉字。", 32, locale: "zh-Hans"), 52);
        }
        if (File.Exists(HiraginoMincho))
        {
            var min = OpenTypeFace.Load(HiraginoMincho);
            Row("Hiragino Mincho ProN (CFF)", Layout(min, "明朝体の漢字と仮名 ABC fi", 32, locale: "ja"), 52);
        }

        using var bmp = Golden.Render(dc.Finish(), (int)W, (int)Math.Ceiling(y + 6));
        if (!string.IsNullOrEmpty(path)) PngCodec.Save(bmp, path!);
    }
}
