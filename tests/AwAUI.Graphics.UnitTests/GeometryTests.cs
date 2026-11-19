using AwAUI.Graphics;

namespace AwAUI.Graphics.UnitTests;

public class GeometryTests
{
    [Theory]
    [InlineData("#fff", 1f, 1f, 1f, 1f)]
    [InlineData("#00000080", 0f, 0f, 0f, 128 / 255f)]
    [InlineData("#336699", 0x33 / 255f, 0x66 / 255f, 0x99 / 255f, 1f)]
    public void Parses_hex_colors(string text, float r, float g, float b, float a)
    {
        var c = Color.Parse(text);
        Assert.Equal(r, c.R, 4); Assert.Equal(g, c.G, 4); Assert.Equal(b, c.B, 4); Assert.Equal(a, c.A, 4);
    }

    [Theory]
    [InlineData("fff")]
    [InlineData("#ggg")]
    [InlineData("#12345")]
    public void Rejects_invalid_colors(string text) => Assert.False(Color.TryParse(text, out _));

    [Fact]
    public void RoundRect_scales_overlapping_radii_like_css()
    {
        // 宽 100：上边两个半径之和 150 → 所有半径乘以 100/150
        var rr = new RoundRect(new Rect(0, 0, 100, 400), 100, 50, 0, 0);
        Assert.Equal(66.666f, rr.TopLeft.Width, 2);
        Assert.Equal(33.333f, rr.TopRight.Width, 2);
    }

    [Fact]
    public void PathBuilder_starts_implicit_subpath_and_drops_trailing_move()
    {
        var p = new PathBuilder().LineTo(10, 0).LineTo(10, 10).Close().MoveTo(5, 5).ToPath();
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Close }, p.Verbs.ToArray());
        Assert.Equal(new Rect(0, 0, 10, 10), p.Bounds);
    }

    [Fact]
    public void Ellipse_path_bounds_match_rect()
    {
        var p = VectorPath.FromEllipse(new Rect(10, 20, 30, 40));
        Assert.Equal(new Rect(10, 20, 30, 40), p.Bounds);
    }

    [Fact]
    public void PixelRect_round_out_covers_fractional_rect()
    {
        Assert.Equal(PixelRect.FromLTRB(0, 1, 3, 4), PixelRect.RoundOut(Rect.FromLTRB(0.2f, 1.9f, 2.1f, 4.0f)));
    }

    [Fact]
    public void PixelRect_round_out_clamps_huge_finite_coordinates_on_both_sides()
    {
        // 坐标 ≥ 2^31 的有限矩形：两侧都钳制到 ±int.MaxValue/2，宽度不溢出（net8 x64 / netfx 上曾得到 int.MinValue）
        var r = PixelRect.RoundOut(Rect.FromLTRB(3e9f, -4e9f, 4e9f, -3e9f));
        Assert.Equal(int.MaxValue / 2, r.X);
        Assert.Equal(int.MinValue / 2, r.Y);
        Assert.True(r.Width >= 0 && r.Height >= 0);
        Assert.True(r.Intersect(new PixelRect(0, 0, 100, 100)).IsEmpty);
    }

    [Fact]
    public void Stroke_normalizes_odd_dash_arrays_and_ignores_invalid_ones()
    {
        Assert.Equal(new[] { 1f, 2f, 3f, 1f, 2f, 3f }, new Stroke(1, dashes: new[] { 1f, 2f, 3f }).Dashes);
        Assert.Null(new Stroke(1, dashes: new[] { 0f, 0f }).Dashes);
        Assert.Null(new Stroke(1, dashes: new[] { 1f, -1f }).Dashes);
    }
}
