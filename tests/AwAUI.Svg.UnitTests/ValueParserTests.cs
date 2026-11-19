using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Svg.Values;

namespace AwAUI.Svg.UnitTests;

/// <summary>数字、路径数据、变换、颜色、长度解析的边界情况。</summary>
public class ValueParserTests
{
    // ---------------------------------------------------------------- 数字

    [Theory]
    [InlineData("1.5.5", new[] { 1.5f, 0.5f })]
    [InlineData("-1e-2", new[] { -0.01f })]
    [InlineData("1e2-3", new[] { 100f, -3f })]
    [InlineData("+.5-.5", new[] { 0.5f, -0.5f })]
    [InlineData("0.0005", new[] { 0.0005f })]
    [InlineData("1E+3 2e0", new[] { 1000f, 2f })]
    [InlineData("12345678901234567890123", new[] { 1.2345679e22f })]
    [InlineData("1.", new[] { 1f })]
    [InlineData("0000.25", new[] { 0.25f })]
    public void Number_lists_split_without_separators(string text, float[] expected)
    {
        var values = SvgNumber.ParseList(text, out bool error);
        Assert.False(error);
        Assert.Equal(expected.Length, values.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], values[i], Math.Abs(expected[i]) * 1e-6f + 1e-9f);
    }

    [Fact]
    public void Exponent_without_digits_is_not_consumed()
    {
        var s = "1em".AsSpan();
        int pos = 0;
        Assert.True(SvgNumber.TryScan(s, ref pos, out float v));
        Assert.Equal(1f, v);
        Assert.Equal(1, pos);
    }

    [Theory]
    [InlineData("1e39")]
    [InlineData("1e999999999")]
    [InlineData("-1e400")]
    public void Overflowing_numbers_are_infinite_and_rejected(string text)
    {
        Assert.False(SvgNumber.TryParse(text, out _));
        var s = text.AsSpan();
        int pos = 0;
        Assert.True(SvgNumber.TryScan(s, ref pos, out float v));
        Assert.True(float.IsInfinity(v));
    }

    [Fact]
    public void Tiny_numbers_underflow_to_zero()
    {
        Assert.True(SvgNumber.TryParse("-1e-38", out float sub));
        Assert.Equal(0f, sub);   // 次正规数按 0 处理
        Assert.True(SvgNumber.TryParse("2e-38", out float normal));
        Assert.Equal(2e-38f, normal);
        Assert.True(SvgNumber.TryParse("1e-999999", out float v));
        Assert.Equal(0f, v);
        Assert.True(SvgNumber.TryParse("123e-330", out v));
        Assert.Equal(0f, v);
    }

    // ---------------------------------------------------------------- 路径数据

    static (VectorPath Path, string? Error) ParsePath(string d)
    {
        var pb = new PathBuilder();
        int budget = 1_000_000;
        var err = SvgPathData.Parse(d.AsSpan(), pb, ref budget);
        return (pb.ToPath(), err);
    }

    static string Describe(VectorPath p)
    {
        var sb = new System.Text.StringBuilder();
        int pi = 0;
        var pts = p.Points.ToArray();
        foreach (var v in p.Verbs.ToArray())
        {
            int n = v switch { PathVerb.Move or PathVerb.Line => 1, PathVerb.Quad => 2, PathVerb.Cubic => 3, _ => 0 };
            sb.Append(v switch { PathVerb.Move => 'M', PathVerb.Line => 'L', PathVerb.Quad => 'Q', PathVerb.Cubic => 'C', _ => 'Z' });
            for (int i = 0; i < n; i++, pi++)
                sb.Append(System.FormattableString.Invariant($"{Math.Round(pts[pi].X, 3)},{Math.Round(pts[pi].Y, 3)} "));
            if (n == 0) sb.Append(' ');
        }
        return sb.ToString().TrimEnd();
    }

    [Theory]
    [InlineData("M10 20L30 40", "M10,20 L30,40")]
    [InlineData("M10,20,30,40,50,60", "M10,20 L30,40 L50,60")]          // M 之后的隐式 L
    [InlineData("m10 20 5 5 5 5", "M10,20 L15,25 L20,30")]               // m 之后的隐式 l
    [InlineData("M1.5.5.5.5", "M1.5,0.5 L0.5,0.5")]
    [InlineData("M-1e-2-1e1L0 0", "M-0.01,-10 L0,0")]
    [InlineData("M0 0H10V10h-5v-5Z", "M0,0 L10,0 L10,10 L5,10 L5,5 Z")]
    [InlineData("M0 0 l10 0 z l0 10", "M0,0 L10,0 Z M0,0 L0,10")]       // Z 之后的相对命令从子路径起点开始
    [InlineData("M0 0 Q10 0 10 10 T20 20", "M0,0 Q10,0 10,10 Q10,20 20,20")]
    [InlineData("M0 0 C0 10 10 10 10 0 S20 -10 20 0", "M0,0 C0,10 10,10 10,0 C10,-10 20,-10 20,0")]
    [InlineData("M0 0 S10 10 20 0", "M0,0 C0,0 10,10 20,0")]           // 前一个不是 C/S：第一控制点为当前点
    [InlineData("M0 0 T10 10", "M0,0 Q0,0 10,10")]
    [InlineData("M1.,2.L3.,4.", "M1,2 L3,4")]
    [InlineData("  \t\nM 1 2 L 3 4\r\n", "M1,2 L3,4")]
    public void Path_grammar(string d, string expected)
    {
        var (p, err) = ParsePath(d);
        Assert.Null(err);
        Assert.Equal(expected, Describe(p));
    }

    [Fact]
    public void Arc_flags_without_separators()
    {
        var (a, errA) = ParsePath("M0 0a5 5 0 1010 0");
        var (b, errB) = ParsePath("M0 0 a 5 5 0 1 0 10 0");
        Assert.Null(errA);
        Assert.Null(errB);
        Assert.Equal(Describe(b), Describe(a));

        var (c, errC) = ParsePath("M0 0a1 1 0 00.5.5");
        var (d, errD) = ParsePath("M0 0 a 1 1 0 0 0 0.5 0.5");
        Assert.Null(errC);
        Assert.Null(errD);
        Assert.Equal(Describe(d), Describe(c));
    }

    [Fact]
    public void Semicircle_arc_ends_exactly_and_bulges_on_the_sweep_side()
    {
        // 从 (0,0) 到 (10,0)，半径 5，sweep=1（顺时针，在 y 向下坐标系中弧在上方 y<0）
        var (p, err) = ParsePath("M0 0 A5 5 0 0 1 10 0");
        Assert.Null(err);
        var bounds = Rendering.PathGeometry.TightBounds(p);
        Assert.Equal(0, bounds.Left, 3);
        Assert.Equal(10, bounds.Right, 3);
        Assert.Equal(-5, bounds.Top, 2);
        Assert.Equal(0, bounds.Bottom, 3);
        Assert.Equal(new Point(10, 0), p.Points[p.Points.Length - 1]);
    }

    [Fact]
    public void Arc_radii_are_scaled_up_when_too_small()
    {
        var (p, err) = ParsePath("M0 0 A1 1 0 0 0 10 0");
        Assert.Null(err);
        var bounds = Rendering.PathGeometry.TightBounds(p);
        Assert.Equal(5, bounds.Bottom, 2);   // sweep=0：弧在下方
        Assert.Equal(10, bounds.Width, 2);
    }

    [Fact]
    public void Large_arc_flag_selects_the_long_way_round()
    {
        var (small, _) = ParsePath("M0 0 A10 10 0 0 1 10 10");
        var (large, _) = ParsePath("M0 0 A10 10 0 1 1 10 10");
        var bs = Rendering.PathGeometry.TightBounds(small);
        var bl = Rendering.PathGeometry.TightBounds(large);
        Assert.True(bl.Width > 19 && bl.Height > 19, bl.ToString());
        Assert.True(bs.Width < 10.5f && bs.Height < 10.5f, bs.ToString());
    }

    [Fact]
    public void Rotated_elliptical_arc_is_continuous()
    {
        var (p, err) = ParsePath("M10 10 A20 10 30 1 0 40 30");
        Assert.Null(err);
        Assert.Equal(new Point(40, 30), p.Points[p.Points.Length - 1]);
        Assert.True(p.Verbs.Length >= 3);
    }

    [Theory]
    [InlineData("M0 0 A0 5 0 0 1 10 0", "M0,0 L10,0")]   // 零半径：直线
    [InlineData("M0 0 A5 5 0 0 1 0 0 L1 1", "M0,0 L1,1")]  // 端点重合：省略
    public void Degenerate_arcs(string d, string expected)
    {
        var (p, err) = ParsePath(d);
        Assert.Null(err);
        Assert.Equal(expected, Describe(p));
    }

    [Theory]
    [InlineData("L10 10", "")]                         // 必须以 M 开始
    [InlineData("M10 10 L20 20 L30", "M10,10 L20,20")]  // 参数不全：保留之前的段
    [InlineData("M10 10 L20 20 X 30 30", "M10,10 L20,20")]
    [InlineData("M10 10 L20 20,", "M10,10 L20,20")]    // 结尾多余的逗号
    [InlineData("M10 10 L1 1 A5 5 0 2 0 20 20", "M10,10 L1,1")]  // 非法弧线标志
    [InlineData("M,10 10", "")]
    [InlineData("M10 10 Z 5 5", "M10,10 Z")]           // Z 不接受参数
    [InlineData("M0 0 L1 1 L1e39 0", "M0,0 L1,1")]     // 数字溢出
    public void Errors_keep_the_valid_prefix(string d, string expected)
    {
        var (p, err) = ParsePath(d);
        Assert.NotNull(err);
        Assert.Equal(expected, Describe(p));
    }

    [Fact]
    public void Segment_budget_stops_parsing()
    {
        var pb = new PathBuilder();
        int budget = 10;
        var d = "M0 0" + string.Concat(Enumerable.Repeat(" L1 1", 100));
        var err = SvgPathData.Parse(d.AsSpan(), pb, ref budget);
        Assert.NotNull(err);
        Assert.True(pb.ToPath().Verbs.Length <= 11);
    }

    // ---------------------------------------------------------------- 变换

    static Vector2 Apply(string transform, float x, float y)
    {
        Assert.True(SvgTransform.TryParse(transform, out var m), transform);
        return Vector2.Transform(new Vector2(x, y), m);
    }

    [Theory]
    [InlineData("translate(10)", 1, 2, 11, 2)]
    [InlineData("translate(10 20)", 1, 2, 11, 22)]
    [InlineData("translate(10,20)", 1, 2, 11, 22)]
    [InlineData("scale(2)", 1, 2, 2, 4)]
    [InlineData("scale(2,-1)", 1, 2, 2, -2)]
    [InlineData("rotate(90)", 1, 0, 0, 1)]                 // y 向下：正角为视觉顺时针
    [InlineData("rotate(90 10 10)", 20, 10, 10, 20)]
    [InlineData("rotate(-90, 10, 10)", 20, 10, 10, 0)]
    [InlineData("skewX(45)", 0, 10, 10, 10)]
    [InlineData("skewY(45)", 10, 0, 10, 10)]
    [InlineData("matrix(1 2 3 4 5 6)", 1, 1, 9, 12)]
    [InlineData("translate(10) scale(2)", 1, 1, 12, 2)]    // 先 scale 后 translate
    [InlineData("scale(2) translate(10)", 1, 1, 22, 2)]
    [InlineData("translate(10),scale(2)", 1, 1, 12, 2)]
    [InlineData("  translate( 10 , 0 )  rotate(180)  ", 1, 1, 9, -1)]
    [InlineData("", 3, 4, 3, 4)]
    public void Transform_lists(string text, float x, float y, float ex, float ey)
    {
        var p = Apply(text, x, y);
        Assert.Equal(ex, p.X, 4);
        Assert.Equal(ey, p.Y, 4);
    }

    [Theory]
    [InlineData("translate(10")]
    [InlineData("translate()")]
    [InlineData("rotate(1 2)")]
    [InlineData("matrix(1 2 3)")]
    [InlineData("frobnicate(1)")]
    [InlineData("translate(1e39)")]
    [InlineData("translate(10,)")]
    [InlineData("scale(1 2 3)")]
    public void Invalid_transforms_are_rejected(string text) => Assert.False(SvgTransform.TryParse(text, out _));

    // ---------------------------------------------------------------- 颜色

    [Theory]
    [InlineData("#f00", "#ff0000ff")]
    [InlineData("#F008", "#ff000088")]
    [InlineData("#00ff7f", "#00ff7fff")]
    [InlineData("#11223344", "#11223344")]
    [InlineData("red", "#ff0000ff")]
    [InlineData("  CornflowerBlue ", "#6495edff")]
    [InlineData("rebeccapurple", "#663399ff")]
    [InlineData("transparent", "#00000000")]
    [InlineData("rgb(255, 128, 0)", "#ff8000ff")]
    [InlineData("rgb(100%, 50%, 0%)", "#ff8000ff")]
    [InlineData("rgba(0, 0, 255, 0.5)", "#0000ff80")]
    [InlineData("RGB(300,-20,0)", "#ff0000ff")]
    [InlineData("rgb(0 0 255 / 50%)", "#0000ff80")]
    [InlineData("hsl(120, 100%, 25%)", "#008000ff")]
    [InlineData("hsla(240, 100%, 50%, 0.25)", "#0000ff40")]
    [InlineData("hsl(-120deg 100% 50%)", "#0000ffff")]
    [InlineData("#abc icc-color(acmecmyk, 0.11, 0.48, 0.83, 0.00)", "#aabbccff")]
    public void Colors(string text, string expected)
    {
        Assert.True(SvgColor.TryParse(text, out var c), text);
        Assert.Equal(expected, c.ToString());
    }

    [Theory]
    [InlineData("#ff")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2)")]
    [InlineData("rgb(1,2,3,4,5)")]
    [InlineData("rgb(10%, 2, 3)")]
    [InlineData("rgb(1 2, 3)")]
    [InlineData("hsl(1, 2, 3)")]
    [InlineData("notacolor")]
    [InlineData("rgb(1,2,3")]
    [InlineData("")]
    public void Invalid_colors(string text) => Assert.False(SvgColor.TryParse(text, out _));

    // ---------------------------------------------------------------- 长度

    [Theory]
    [InlineData("10", 10)]
    [InlineData("10px", 10)]
    [InlineData("1in", 96)]
    [InlineData("72pt", 96)]
    [InlineData("2.54cm", 96)]
    [InlineData("25.4mm", 96)]
    [InlineData("1pc", 16)]
    [InlineData("2em", 32)]
    [InlineData("50%", 100)]
    public void Lengths(string text, float expected)
    {
        Assert.True(SvgLength.TryParse(text, out var len));
        Assert.Equal(expected, len.Resolve(new Size(200, 100), LengthAxis.X, 16), 3);
    }

    [Fact]
    public void Percentages_use_the_right_axis()
    {
        Assert.True(SvgLength.TryParse("50%", out var len));
        var vp = new Size(300, 400);
        Assert.Equal(150, len.Resolve(vp, LengthAxis.X, 16), 3);
        Assert.Equal(200, len.Resolve(vp, LengthAxis.Y, 16), 3);
        Assert.Equal((float)(Math.Sqrt((300.0 * 300 + 400.0 * 400) / 2) / 2), len.Resolve(vp, LengthAxis.Other, 16), 2);
    }

    [Theory]
    [InlineData("10 px")]
    [InlineData("10furlongs")]
    [InlineData("px")]
    [InlineData("1e39")]
    public void Invalid_lengths(string text) => Assert.False(SvgLength.TryParse(text, out _));
}
