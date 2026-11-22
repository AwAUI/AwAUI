using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Text.Testing;
using AwAUI.Unicode;

namespace AwAUI.Text.UnitTests.Layout;

/// <summary>段落布局测试的公共工具：方块字体（每字符 1 em）、字号 10 使每个字符宽 10、行高 10、基线 8。</summary>
static class Kit
{
    public static readonly TestTextEngine Engine = new();

    public const float Inf = float.PositiveInfinity;

    public static TextStyle Ts(float size = 10) => new() { FontSize = size };

    public static ParagraphStyle P(WhiteSpace ws = WhiteSpace.Normal, TextAlign align = TextAlign.Start, ParagraphDirection dir = ParagraphDirection.LeftToRight,
        int maxLines = 0, string? ellipsis = null, LineHeight lineHeight = default, WordBreak wordBreak = WordBreak.Normal,
        OverflowWrap overflowWrap = OverflowWrap.Normal, TextStyle? style = null, LineBreakStrictness strictness = LineBreakStrictness.Normal) => new()
    {
        TextStyle = style ?? Ts(),
        WhiteSpace = ws,
        TextAlign = align,
        Direction = dir,
        MaxLines = maxLines,
        Ellipsis = ellipsis,
        LineHeight = lineHeight,
        WordBreak = wordBreak,
        OverflowWrap = overflowWrap,
        LineBreak = strictness,
    };

    public static TextLayout Lay(string text, ParagraphStyle? p = null, float width = Inf, TextEngine? engine = null)
    {
        var layout = (engine ?? Engine).CreateLayout(text, p ?? P(), width);
        return layout;
    }

    /// <summary>每行的文本（按偏移切出原文）。</summary>
    public static string[] Lines(TextLayout l)
    {
        var r = new string[l.LineCount];
        for (int i = 0; i < r.Length; i++)
        {
            var m = l.GetLine(i);
            r[i] = l.Text.Substring(m.StartOffset, m.EndOffset - m.StartOffset);
        }
        return r;
    }

    public sealed record GlyphOp(GlyphRun Run, Point Origin, PaintData Paint);

    public sealed record RectOp(Rect Rect);

    public static (List<GlyphOp> Glyphs, List<RectOp> Rects, List<DrawOp> Ops) Record(TextLayout layout, Point origin = default)
    {
        var dc = new DrawingContext();
        layout.Paint(dc, origin);
        var list = dc.Finish();
        var glyphs = new List<GlyphOp>();
        var rects = new List<RectOp>();
        var ops = new List<DrawOp>();
        var reader = list.GetReader();
        while (reader.MoveNext())
        {
            ops.Add(reader.Op);
            if (reader.Op == DrawOp.DrawGlyphRun)
            {
                var cmd = reader.Read<DrawGlyphRunCmd>();
                glyphs.Add(new GlyphOp(reader.GetGlyphRun(cmd), cmd.Origin, cmd.Paint));
            }
            else if (reader.Op == DrawOp.FillRect)
                rects.Add(new RectOp(reader.Read<FillRectCmd>().Rect));
        }
        return (glyphs, rects, ops);
    }

    /// <summary>每个字形在布局坐标中的 x（按绘制顺序）。</summary>
    public static float[] GlyphXs(GlyphOp op) => op.Run.Positions.ToArray().Select(p => p.X + op.Origin.X).ToArray();

    /// <summary>统计调用次数的整形器。</summary>
    public sealed class CountingShaper : TextShaper
    {
        int _calls;
        public int Calls => _calls;
        public override ShapedRun Shape(in ShapeRequest request)
        {
            Interlocked.Increment(ref _calls);
            return BasicTextShaper.Instance.Shape(request);
        }
    }

    public const string Alef = "א", Bet = "ב", Gimel = "ג";
    public const string Hebrew = Alef + Bet + Gimel;
}
