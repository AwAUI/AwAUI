using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.RenderTests;

/// <summary>光栅化的定量正确性：覆盖率是精确面积，而不是看起来差不多。</summary>
public class RasterAccuracyTests
{
    static Bitmap RenderBlackOnTransparent(Action<DrawingContext> draw, int w, int h)
    {
        var dc = new DrawingContext();
        draw(dc);
        return Golden.Render(dc.Finish(), w, h, 1, Color.Transparent);
    }

    static float Alpha(Bitmap b, int x, int y) => b.GetRow(y)[x * 4 + 3] / 255f;

    static double TotalCoverage(Bitmap b)
    {
        double sum = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++) sum += Alpha(b, x, y);
        return sum;
    }

    [Fact]
    public void Half_pixel_edge_gives_half_coverage()
    {
        // 走通用路径光栅器（FillPath），而不是矩形快速路径
        var path = VectorPath.FromRect(new Rect(0.5f, 0, 1, 1));
        using var b = RenderBlackOnTransparent(dc => dc.FillPath(path, Color.Black), 2, 1);
        Assert.Equal(0.5f, Alpha(b, 0, 0), 2);
        Assert.Equal(0.5f, Alpha(b, 1, 0), 2);
    }

    [Fact]
    public void Rect_fast_path_matches_path_rasterizer()
    {
        var r = new Rect(1.25f, 2.6f, 7.3f, 4.15f);
        using var fast = RenderBlackOnTransparent(dc => dc.FillRect(r, Color.Black), 12, 10);
        using var path = RenderBlackOnTransparent(dc => dc.FillPath(VectorPath.FromRect(r), Color.Black), 12, 10);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 12; x++)
                Assert.InRange(Alpha(fast, x, y) - Alpha(path, x, y), -1.5f / 255, 1.5f / 255);
    }

    [Theory]
    [InlineData(3.3f, 1.7f, 27.1f, 9.4f, 14.2f, 28.6f)]
    [InlineData(0.5f, 0.5f, 31.5f, 0.6f, 0.5f, 31.5f)]
    [InlineData(10f, -5f, 40f, 20f, -6f, 25f)] // 部分在画布外
    public void Triangle_coverage_sums_to_its_area(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        var path = new PathBuilder().MoveTo(x0, y0).LineTo(x1, y1).LineTo(x2, y2).Close().ToPath();
        using var b = RenderBlackOnTransparent(dc => dc.FillPath(path, Color.Black), 32, 32);
        // 与画布求交后的面积：用细网格数值积分作为参考
        double expected = ClippedArea(new Vector2(x0, y0), new Vector2(x1, y1), new Vector2(x2, y2), 32, 32);
        Assert.InRange(TotalCoverage(b), expected - 0.6, expected + 0.6);
    }

    static double ClippedArea(Vector2 a, Vector2 b, Vector2 c, int w, int h)
    {
        const int n = 16;
        double area = 0;
        for (int y = 0; y < h * n; y++)
            for (int x = 0; x < w * n; x++)
            {
                var p = new Vector2((x + 0.5f) / n, (y + 0.5f) / n);
                if (Inside(p, a, b, c)) area++;
            }
        return area / (n * n);
    }

    static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    [Fact]
    public void Even_odd_leaves_hole_and_nonzero_fills_it()
    {
        var pb = new PathBuilder();
        pb.AddRect(new Rect(0, 0, 10, 10));
        pb.AddRect(new Rect(3, 3, 4, 4)); // 同向
        var nonZero = pb.ToPath();
        var evenOdd = nonZero.WithFillRule(FillRule.EvenOdd);
        using var a = RenderBlackOnTransparent(dc => dc.FillPath(nonZero, Color.Black), 10, 10);
        using var b = RenderBlackOnTransparent(dc => dc.FillPath(evenOdd, Color.Black), 10, 10);
        Assert.Equal(1f, Alpha(a, 5, 5), 3);
        Assert.Equal(0f, Alpha(b, 5, 5), 3);
        Assert.Equal(1f, Alpha(b, 1, 1), 3);
    }

    [Fact]
    public void Stroke_has_exact_width()
    {
        using var b = RenderBlackOnTransparent(dc =>
            dc.DrawLine(new Point(2, 10), new Point(18, 10), Color.Black, new Stroke(2)), 20, 20);
        Assert.Equal(1f, Alpha(b, 10, 9), 2);
        Assert.Equal(1f, Alpha(b, 10, 10), 2);
        Assert.Equal(0f, Alpha(b, 10, 8), 2);
        Assert.Equal(0f, Alpha(b, 10, 11), 2);
        // 线帽 Butt：端点之外没有像素
        Assert.Equal(0f, Alpha(b, 1, 10), 2);
        Assert.Equal(16 * 2, TotalCoverage(b), 1);
    }

    [Fact]
    public void Closed_stroke_ring_has_hole()
    {
        using var b = RenderBlackOnTransparent(dc =>
            dc.StrokeRect(new Rect(4, 4, 12, 12), Color.Black, new Stroke(2)), 20, 20);
        Assert.Equal(0f, Alpha(b, 10, 10), 2);
        Assert.Equal(1f, Alpha(b, 4, 10), 2);
        // 外边长 14、内边长 10 → 面积 196 - 100
        Assert.Equal(96, TotalCoverage(b), 1);
    }

    [Fact]
    public void Overlapping_stroke_segments_do_not_double_cover()
    {
        // 自交折线在交点处覆盖率不应超过 1（非零规则）
        var path = new PathBuilder().MoveTo(2, 2).LineTo(18, 18).MoveTo(18, 2).LineTo(2, 18).ToPath();
        using var b = RenderBlackOnTransparent(dc => dc.StrokePath(path, Color.Black, new Stroke(3)), 20, 20);
        Assert.Equal(1f, Alpha(b, 10, 10), 2);
    }

    [Fact]
    public void Group_opacity_does_not_darken_overlaps()
    {
        using var b = RenderBlackOnTransparent(dc =>
        {
            dc.PushLayer(new LayerOptions(0.5f));
            dc.FillRect(new Rect(0, 0, 6, 4), Color.Black);
            dc.FillRect(new Rect(4, 0, 6, 4), Color.Black);
            dc.PopLayer();
        }, 10, 4);
        Assert.Equal(0.5f, Alpha(b, 2, 2), 2);
        Assert.Equal(0.5f, Alpha(b, 5, 2), 2); // 重叠处
    }

    [Fact]
    public void Rect_clip_restricts_drawing()
    {
        using var b = RenderBlackOnTransparent(dc =>
        {
            dc.ClipRect(new Rect(2, 2, 4, 4));
            dc.FillRect(new Rect(0, 0, 10, 10), Color.Black);
        }, 10, 10);
        Assert.Equal(16, TotalCoverage(b), 2);
        Assert.Equal(0f, Alpha(b, 1, 1), 3);
        Assert.Equal(1f, Alpha(b, 3, 3), 3);
    }

    [Fact]
    public void Path_clip_is_antialiased_and_restored()
    {
        using var b = RenderBlackOnTransparent(dc =>
        {
            dc.Save();
            dc.ClipPath(VectorPath.FromEllipse(new Rect(0, 0, 20, 20)));
            dc.FillRect(new Rect(0, 0, 20, 20), Color.Black);
            dc.Restore();
            dc.FillRect(new Rect(20, 0, 4, 4), Color.Black); // 裁剪已恢复
        }, 24, 20);
        // 曲线展平为内接折线（容差 0.05px）会让圆的面积系统性地小约 0.5%
        double expected = Math.PI * 100 + 16;
        Assert.InRange(TotalCoverage(b), expected * 0.99, expected + 0.5);
    }

    [Fact]
    public void Damage_rect_only_touches_that_area()
    {
        var dc = new DrawingContext();
        dc.FillRect(new Rect(0, 0, 10, 10), Color.Black);
        var list = dc.Finish();
        using var backend = new AwAUI.Render.SoftwareRenderBackend();
        using var target = (AwAUI.Render.BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(10, 10), 1);
        target.Render(list, new RenderOptions(Color.Transparent, new PixelRect(0, 0, 5, 10)));
        Assert.Equal(1f, Alpha(target.Bitmap, 2, 2), 3);
        Assert.Equal(0f, Alpha(target.Bitmap, 7, 2), 3);
    }
}
