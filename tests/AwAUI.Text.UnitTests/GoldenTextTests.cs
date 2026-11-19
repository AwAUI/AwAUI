using AwAUI.Graphics;

namespace AwAUI.Text.UnitTests;

/// <summary>用软件后端把整形后的字形轮廓画成图片，与 tests/TestFiles/Golden/Text 中人工审核过的图片比对。</summary>
public class GoldenTextTests
{
    static void DrawLine(DrawingContext dc, FontFace face, string text, float size, Point origin, Color color, bool kerning = true)
    {
        var features = kerning ? Array.Empty<FontFeature>() : new[] { new FontFeature("kern", 0) };
        var run = BasicTextShaper.Instance.Shape(new ShapeRequest(text, face, size) { Features = features });
        // 基线与前进量的参考线
        dc.FillRect(new Rect(origin.X, origin.Y, run.Width, 0.5f), Color.Parse("#90caf9"));
        var pb = new PathBuilder();
        Assert.Equal(0, run.AppendOutlines(pb, origin));
        dc.FillPath(pb.ToPath(), color);
    }

    [Fact]
    public void Text_lines()
    {
        var noto = TestFonts.Load(TestFonts.NotoSans);
        var abel = TestFonts.Load(TestFonts.Abel);
        var dc = new DrawingContext();
        DrawLine(dc, noto, "Hello AV Typography Ååéñ ЖΩ 0123", 26, new Point(10, 38), Color.Parse("#212121"));
        DrawLine(dc, abel, "Hello AV WAVE To Yo", 34, new Point(10, 88), Color.Parse("#1a237e"));
        DrawLine(dc, abel, "Hello AV WAVE To Yo", 34, new Point(10, 134), Color.Parse("#b71c1c"), kerning: false);
        DrawLine(dc, noto, "small 11px text: quick brown fox", 11, new Point(10, 158), Color.Parse("#000000"));
        using var bmp = Golden.Render(dc.Finish(), 480, 168);
        Golden.AssertMatches("text-lines", bmp);
    }

    [Fact]
    public void Text_lines_at_2x()
    {
        var noto = TestFonts.Load(TestFonts.NotoSans);
        var dc = new DrawingContext();
        DrawLine(dc, noto, "AgÅ fi", 20, new Point(6, 26), Color.Parse("#004d40"));
        using var bmp = Golden.Render(dc.Finish(), 110, 36, scale: 2);
        Golden.AssertMatches("text-2x", bmp);
    }
}
