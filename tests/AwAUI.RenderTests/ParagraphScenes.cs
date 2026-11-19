using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Text;
using AwAUI.Text.Testing;
using AwAUI.Unicode;

namespace AwAUI.RenderTests;

/// <summary>
/// 段落布局（AwAUI.Text 的 ParagraphBuilder / TextLayout）的参考场景：多行断行、对齐、装饰线、混合样式、占位盒、选区、省略号与双向文本。
/// 拉丁文用仓库内的 Noto Sans；希伯来文回退到方块字体（Noto Sans 不含希伯来文），以显示双向重排的结果。
/// </summary>
public static class ParagraphScenes
{
    static Color C(string hex) => Color.Parse(hex);

    static readonly Lazy<TextEngine> s_engine = new(() =>
    {
        var fonts = new FontCollection();
        fonts.Register(TextScenes.Noto);
        fonts.Register(new BoxFontFace("Box", advance: 560, coverage: cp => UnicodeData.GetScript(cp) == Script.Hebrew));
        fonts.SetGenericFamily("sans-serif", "Noto Sans");
        return new DefaultTextEngine(fonts);
    });

    public static TextEngine Engine => s_engine.Value;

    const string Pangrams =
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs! How vexingly quick daft zebras jump.";

    public static readonly (string Name, int Width, int Height, Action<DrawingContext> Draw)[] All =
    {
        ("paragraph-align", 540, 170, Alignment),
        ("paragraph-styles", 420, 140, Styles),
        ("paragraph-truncation", 420, 140, Truncation),
    };

    static TextStyle Base(float size = 13, string color = "#212121") => new() { FontSize = size, Color = C(color) };

    static TextLayout Paragraph(string text, ParagraphStyle style, float width)
    {
        var b = Engine.CreateParagraphBuilder(style);
        b.AddText(text);
        var l = b.Build();
        l.Layout(width);
        return l;
    }

    /// <summary>左、居中、右、两端对齐；背景是布局盒，细线是每行的基线。</summary>
    public static void Alignment(DrawingContext dc)
    {
        var aligns = new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right, TextAlign.Justify };
        for (int i = 0; i < aligns.Length; i++)
        {
            float x = 10 + (i % 2) * 270, y = 10 + (i / 2) * 80;
            const float width = 250;
            var l = Paragraph(Pangrams, new ParagraphStyle { TextStyle = Base(), TextAlign = aligns[i], LineHeight = LineHeight.Multiple(1.35f) }, width);
            dc.FillRect(new Rect(x, y, width, l.Height), C("#eef3f8"));
            for (int k = 0; k < l.LineCount; k++)
            {
                var m = l.GetLine(k);
                dc.FillRect(new Rect(x + m.Left, y + m.Baseline, m.Width, 0.5f), C("#90caf9"));
            }
            l.Paint(dc, new Point(x, y));
        }
    }

    /// <summary>装饰线的各种样式、混合字号与字重、字母间距、行内占位盒、选区与光标。</summary>
    public static void Styles(DrawingContext dc)
    {
        var b = Engine.CreateParagraphBuilder(new ParagraphStyle { TextStyle = Base(15), WhiteSpace = WhiteSpace.PreWrap });
        void Span(string text, TextStyle style)
        {
            b.PushStyle(style);
            b.AddText(text);
            b.Pop();
        }
        Span("Underline", new TextStyle { FontSize = 15, Decoration = TextDecorationLine.Underline });
        b.AddText(" ");
        Span("overline", new TextStyle { FontSize = 15, Decoration = TextDecorationLine.Overline, Color = C("#1565c0") });
        b.AddText(" ");
        Span("line-through", new TextStyle { FontSize = 15, Decoration = TextDecorationLine.LineThrough, DecorationColor = C("#d32f2f") });
        b.AddText("\n");
        Span("double", new TextStyle { FontSize = 15, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Double, DecorationColor = C("#2e7d32") });
        b.AddText(" ");
        Span("dotted", new TextStyle { FontSize = 15, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Dotted, DecorationThickness = 1.5f });
        b.AddText(" ");
        Span("dashed", new TextStyle { FontSize = 15, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Dashed, DecorationThickness = 1.5f });
        b.AddText(" ");
        Span("wavy", new TextStyle { FontSize = 15, Decoration = TextDecorationLine.Underline, DecorationStyle = TextDecorationStyle.Wavy, DecorationColor = C("#e53935"), DecorationThickness = 1 });
        b.AddText("\n");
        Span("Bold", new TextStyle { FontSize = 15, FontWeight = FontWeight.Bold });
        b.AddText(" ");
        Span("Italic", new TextStyle { FontSize = 15, FontStyle = FontStyle.Italic });
        b.AddText(" ");
        Span("BIG", new TextStyle { FontSize = 26, Color = C("#6a1b9a") });
        b.AddText(" ");
        Span("small", new TextStyle { FontSize = 10 });
        b.AddText(" ");
        b.AddPlaceholder(new PlaceholderStyle(18, 18, PlaceholderAlignment.Middle));
        b.AddText(" ");
        Span("spaced", new TextStyle { FontSize = 15, LetterSpacing = 3, Color = C("#00695c") });
        b.AddText("\nSelected text wraps across lines when the paragraph is narrower than its content.");
        var l = b.Build();
        l.Layout(400);

        var origin = new Point(10, 10);
        dc.FillRect(new Rect(origin.X, origin.Y, 400, l.Height), C("#fafafa"));
        var selection = new List<Rect>();
        int start = l.Text.IndexOf("Selected", StringComparison.Ordinal);
        l.GetSelectionRects(new TextRange(start + 9, start + 45), selection);
        foreach (var r in selection) dc.FillRect(r.Offset(origin.X, origin.Y), C("#bbdefb"));
        var ph = l.GetPlaceholderRect(0);
        dc.FillRoundRect(new RoundRect(ph.Offset(origin.X, origin.Y), 4), C("#ff9800"));
        l.Paint(dc, origin);
        var caret = l.GetCaretRect(new TextPosition(start + 45));
        dc.FillRect(new Rect(origin.X + caret.X - 0.5f, origin.Y + caret.Y, 1, caret.Height), C("#000000"));
    }

    /// <summary>最大行数 + 省略号、不换行的省略号、从右到左的段落（双向重排与分离的选区）。</summary>
    public static void Truncation(DrawingContext dc)
    {
        var clamp = Paragraph(Pangrams, new ParagraphStyle { TextStyle = Base(), MaxLines = 2, Ellipsis = ParagraphStyle.HorizontalEllipsis }, 250);
        dc.FillRect(new Rect(10, 10, 250, clamp.Height), C("#eef3f8"));
        clamp.Paint(dc, new Point(10, 10));

        var nowrap = Paragraph(Pangrams, new ParagraphStyle { TextStyle = Base(13, "#4e342e"), WhiteSpace = WhiteSpace.NoWrap, Ellipsis = "…" }, 300);
        dc.FillRect(new Rect(10, 60, 300, nowrap.Height), C("#efebe9"));
        nowrap.Paint(dc, new Point(10, 60));

        const string alef = "א", bet = "ב", gimel = "ג";
        string hebrew = alef + bet + gimel + bet;
        var rtl = Paragraph(hebrew + " one " + hebrew + gimel + " two 123 " + alef + bet + "! " + hebrew + " three four five six seven",
            new ParagraphStyle { TextStyle = Base(15), Direction = BidiDirection.RightToLeft, TextAlign = TextAlign.Start }, 295);
        var o = new Point(115, 95);
        dc.FillRect(new Rect(o.X, o.Y, 295, rtl.Height), C("#f1f8e9"));
        var sel = new List<Rect>();
        rtl.GetSelectionRects(new TextRange(2, 12), sel);
        foreach (var r in sel) dc.FillRect(r.Offset(o.X, o.Y), C("#c5e1a5"));
        rtl.Paint(dc, o);

        var centered = Paragraph("centred · nowrap\nsecond line", new ParagraphStyle { TextStyle = Base(12, "#37474f"), TextAlign = TextAlign.Center, WhiteSpace = WhiteSpace.Pre }, 100);
        dc.StrokeRect(new Rect(10.5f, 95.5f, 100, centered.Height), C("#b0bec5"), new Stroke(1));
        centered.Paint(dc, new Point(10, 95));
    }
}

public class GoldenParagraphSceneTests
{
    public static IEnumerable<object[]> SceneNames => ParagraphScenes.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Paragraph_scene_matches_golden(string name)
    {
        var scene = ParagraphScenes.All.Single(s => s.Name == name);
        var dc = new DrawingContext();
        scene.Draw(dc);
        using var bmp = Golden.Render(dc.Finish(), scene.Width, scene.Height);
        Golden.AssertMatches(name, bmp);
    }
}
