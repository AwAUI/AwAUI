using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Text.OpenType;

namespace AwAUI.Text.UnitTests;

public class OutlineTests
{
    static VectorPath Outline(FontFace face, ushort glyph)
    {
        var pb = new PathBuilder();
        Assert.True(face.GetOutline(glyph, default, pb));
        return pb.ToPath();
    }

    static ushort Glyph(FontFace face, char c)
    {
        Assert.True(face.TryGetGlyph(c, out var g));
        return g;
    }

    [Fact]
    public void Noto_capital_O_has_two_closed_quadratic_contours()
    {
        var face = TestFonts.Load(TestFonts.NotoSans);
        var path = Outline(face, Glyph(face, 'O'));
        var verbs = path.Verbs.ToArray();
        Assert.Equal(2, verbs.Count(v => v == PathVerb.Move));
        Assert.Equal(2, verbs.Count(v => v == PathVerb.Close));
        Assert.Contains(PathVerb.Quad, verbs);
        Assert.DoesNotContain(PathVerb.Cubic, verbs);

        // 路径边界（y 向下）与字形头部的边界框一致：二次曲线的控制点包含在 glyf 边界内
        Assert.True(face.TryGetGlyphBounds(Glyph(face, 'O'), out var bbox));
        Assert.Equal(bbox.Left, path.Bounds.Left, 0);
        Assert.Equal(bbox.Top, path.Bounds.Top, 0);
        Assert.Equal(bbox.Right, path.Bounds.Right, 0);
        Assert.Equal(bbox.Bottom, path.Bounds.Bottom, 0);
        Assert.True(bbox.Top < -600, "the top of 'O' is above the baseline (negative y)");
    }

    [Fact]
    public void Noto_capital_I_is_a_rectangle_with_known_points()
    {
        var face = TestFonts.Load(TestFonts.NotoSans);
        var outline = face.DecodeOutline(Glyph(face, 'I'))!;
        Assert.Equal(1, outline.ContourCount);
        Assert.All(outline.OnCurve, Assert.True);
        // Noto Sans 'I' 是 12 个点（带衬线），fontTools 交叉校验覆盖坐标；这里检查路径只有直线
        var path = Outline(face, Glyph(face, 'I'));
        Assert.All(path.Verbs.ToArray(), v => Assert.True(v is PathVerb.Move or PathVerb.Line or PathVerb.Close));
    }

    [Fact]
    public void Space_has_an_empty_outline()
    {
        var face = TestFonts.Load(TestFonts.NotoSans);
        var pb = new PathBuilder();
        Assert.True(face.GetOutline(Glyph(face, ' '), default, pb));
        Assert.True(pb.ToPath().IsEmpty);
        Assert.False(face.TryGetGlyphBounds(Glyph(face, ' '), out _));
    }

    [Fact]
    public void Invalid_glyph_id_returns_false_without_writing()
    {
        var face = TestFonts.Load(TestFonts.Abel);
        var pb = new PathBuilder();
        Assert.False(face.GetOutline((ushort)face.GlyphCount, default, pb));
        Assert.True(pb.ToPath().IsEmpty);
    }

    [Fact]
    public void Noto_A_ring_is_a_composite_of_A_and_ring()
    {
        var face = TestFonts.Load(TestFonts.NotoSans);
        var aring = face.DecodeOutline(Glyph(face, 'Å'))!;
        var a = face.DecodeOutline(Glyph(face, 'A'))!;
        Assert.True(aring.PointCount > a.PointCount);
        Assert.True(aring.ContourCount > a.ContourCount);
        // 第一个组件是未平移的 'A'
        for (int i = 0; i < a.PointCount; i++) Assert.Equal(a.Points[i], aring.Points[i]);
        var data = face.GetTable(OpenTypeTag.Glyf);
        Assert.False(data.IsEmpty);
    }

    [Fact]
    public void Transform_overload_scales_and_translates()
    {
        var face = TestFonts.Load(TestFonts.NotoSans);
        var g = Glyph(face, 'H');
        var plain = Outline(face, g);
        var pb = new PathBuilder();
        var m = Matrix3x2.CreateScale(0.02f) * Matrix3x2.CreateTranslation(10, 30);
        Assert.True(face.GetOutline(g, default, pb, m));
        var scaled = pb.ToPath();
        Assert.Equal(plain.Points.Length, scaled.Points.Length);
        Assert.Equal(plain.Bounds.Left * 0.02f + 10, scaled.Bounds.Left, 3);
        Assert.Equal(plain.Bounds.Top * 0.02f + 30, scaled.Bounds.Top, 3);
        // 'H' 的顶部在基线上方约 cap height
        Assert.Equal(30 - face.Metrics.CapHeight * 0.02f, scaled.Bounds.Top, 1);
        Assert.Equal(30, scaled.Bounds.Bottom, 1);
    }

    // ---------------------------------------------------------------- 合成字体：隐含点与复合字形的全部变换

    static SyntheticFont BaseFont(out int square)
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());                                   // 0 .notdef（空）
        square = f.AddGlyph(SyntheticFont.Simple(SyntheticFont.Square(0, 0, 200)));
        return f;
    }

    static List<Vector2> Points(OpenTypeFace face, int glyph) => face.DecodeOutline((ushort)glyph)!.Points;

    [Fact]
    public void All_off_curve_contour_uses_implied_on_curve_points()
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());
        // 菱形的四个控制点：起点是首末控制点的中点，四段二次曲线
        int g = f.AddGlyph(SyntheticFont.Simple(new[] { (0, 100, false), (100, 200, false), (200, 100, false), (100, 0, false) }));
        var face = OpenTypeFace.Load(f.Build());
        var path = Outline(face, (ushort)g);
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Quad, PathVerb.Quad, PathVerb.Quad, PathVerb.Quad, PathVerb.Close }, path.Verbs.ToArray());
        var pts = path.Points.ToArray();
        Assert.Equal(new Point(50, -50), pts[0]);              // (0,100) 与 (100,0) 的中点，y 翻转
        Assert.Equal(new Point(0, -100), pts[1]);
        Assert.Equal(new Point(50, -150), pts[2]);
        Assert.Equal(new Point(50, -50), pts.Last());             // 回到起点
    }

    [Fact]
    public void Mixed_contour_inserts_midpoints_between_consecutive_off_curve_points()
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());
        int g = f.AddGlyph(SyntheticFont.Simple(new[] { (0, 0, true), (0, 100, false), (100, 100, false), (100, 0, true) }));
        var face = OpenTypeFace.Load(f.Build());
        var path = Outline(face, (ushort)g);
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Quad, PathVerb.Quad, PathVerb.Line, PathVerb.Close }, path.Verbs.ToArray());
        Assert.Equal(new Point(50, -100), path.Points[2]);     // 隐含的中点
    }

    [Fact]
    public void Composite_offsets_and_uniform_scale()
    {
        var f = BaseFont(out int sq);
        int c = f.AddGlyph(SyntheticFont.Composite(
            new Component(sq, 0, 0),
            new Component(sq, 100, -50, Words: false, Transform: new[] { 0.5f })));
        var face = OpenTypeFace.Load(f.Build());
        var p = Points(face, c);
        Assert.Equal(8, p.Count);
        Assert.Equal(2, face.DecodeOutline((ushort)c)!.ContourCount);
        Assert.Equal(new Vector2(100, -50), p[4]);
        Assert.Equal(new Vector2(100, 50), p[5]);
        Assert.Equal(new Vector2(200, 50), p[6]);
    }

    [Fact]
    public void Composite_x_and_y_scale()
    {
        var f = BaseFont(out int sq);
        int c = f.AddGlyph(SyntheticFont.Composite(new Component(sq, 10, 20, Transform: new[] { 1.5f, 0.25f })));
        var p = Points(OpenTypeFace.Load(f.Build()), c);
        Assert.Equal(new[] { new Vector2(10, 20), new Vector2(10, 70), new Vector2(310, 70), new Vector2(310, 20) }, p);
    }

    [Fact]
    public void Composite_two_by_two_rotates()
    {
        var f = BaseFont(out int sq);
        // [a b c d] = [0 1 -1 0]：x' = a·x + c·y = -y，y' = b·x + d·y = x（逆时针 90°）
        int c = f.AddGlyph(SyntheticFont.Composite(new Component(sq, 0, 0, Transform: new[] { 0f, 1f, -1f, 0f })));
        var p = Points(OpenTypeFace.Load(f.Build()), c);
        Assert.Equal(new[] { new Vector2(0, 0), new Vector2(-200, 0), new Vector2(-200, 200), new Vector2(0, 200) }, p);
    }

    [Fact]
    public void Composite_scaled_component_offset_flag_transforms_the_offset()
    {
        var f = BaseFont(out int sq);
        int scaled = f.AddGlyph(SyntheticFont.Composite(new Component(sq, 100, 100, Transform: new[] { 0.5f }, ExtraFlags: 0x0800)));
        int unscaled = f.AddGlyph(SyntheticFont.Composite(new Component(sq, 100, 100, Transform: new[] { 0.5f }, ExtraFlags: 0x1000)));
        int both = f.AddGlyph(SyntheticFont.Composite(new Component(sq, 100, 100, Transform: new[] { 0.5f }, ExtraFlags: 0x1800)));
        var face = OpenTypeFace.Load(f.Build());
        Assert.Equal(new Vector2(50, 50), Points(face, scaled)[0]);
        Assert.Equal(new Vector2(100, 100), Points(face, unscaled)[0]);
        Assert.Equal(new Vector2(100, 100), Points(face, both)[0]);   // 两者同时设置时按不缩放处理
    }

    [Fact]
    public void Composite_point_matching_aligns_anchor_points()
    {
        var f = BaseFont(out int sq);
        // 第二个组件的第 0 点对齐到已有轮廓的第 2 点 (200,200)；字节参数与字参数各一次
        int c = f.AddGlyph(SyntheticFont.Composite(
            new Component(sq, 0, 0),
            new Component(sq, 2, 0, XY: false, Words: false),
            new Component(sq, 6, 1, XY: false, Words: true)));
        var face = OpenTypeFace.Load(f.Build());
        var p = Points(face, c);
        Assert.Equal(12, p.Count);
        Assert.Equal(new Vector2(200, 200), p[4]);
        // 第三个组件的第 1 点 (0,200) 对齐到第 6 点 (400,400)
        Assert.Equal(new Vector2(400, 200), p[8]);
    }

    [Fact]
    public void Nested_composites_compose_transforms()
    {
        var f = BaseFont(out int sq);
        int inner = f.AddGlyph(SyntheticFont.Composite(new Component(sq, 10, 0, Transform: new[] { 0.5f })));
        int outer = f.AddGlyph(SyntheticFont.Composite(new Component(inner, 0, 100, Transform: new[] { 1.5f, 1f })));
        var p = Points(OpenTypeFace.Load(f.Build()), outer);
        // inner: (10,0)-(110,100)；outer 先 x×1.5 再平移 (0,100)
        Assert.Equal(new[] { new Vector2(15, 100), new Vector2(15, 200), new Vector2(165, 200), new Vector2(165, 100) }, p);
    }

    [Fact]
    public void Recursive_composites_fail_cleanly()
    {
        var f = BaseFont(out int sq);
        int self = f.Glyphs.Count;
        f.AddGlyph(SyntheticFont.Composite(new Component(self, 0, 0)));             // 引用自身
        int a = f.Glyphs.Count;
        f.AddGlyph(SyntheticFont.Composite(new Component(a + 1, 0, 0)));            // a → b → a
        f.AddGlyph(SyntheticFont.Composite(new Component(a, 0, 0)));
        // 指数展开：每层引用下一层两次，共 24 层
        int expStart = f.Glyphs.Count;
        for (int i = 0; i < 24; i++)
            f.AddGlyph(SyntheticFont.Composite(new Component(expStart + i + 1, 0, 0), new Component(expStart + i + 1, 1, 1)));
        f.AddGlyph(SyntheticFont.Simple(SyntheticFont.Square(0, 0, 10)));
        // 引用不存在的字形
        int dangling = f.AddGlyph(SyntheticFont.Composite(new Component(60000, 0, 0)));
        var face = OpenTypeFace.Load(f.Build());

        foreach (int g in new[] { self, a, a + 1, expStart, dangling })
        {
            var pb = new PathBuilder();
            Assert.False(face.GetOutline((ushort)g, default, pb));
            Assert.True(pb.ToPath().IsEmpty);
        }
        // 正常字形不受影响
        Assert.True(face.GetOutline((ushort)sq, default, new PathBuilder()));
        // 不超过深度上限的嵌套是允许的
        Assert.True(face.GetOutline((ushort)(expStart + 24 - 10), default, new PathBuilder()));
    }

    [Fact]
    public void Short_loca_format_is_supported()
    {
        var f = BaseFont(out int sq);
        f.LongLoca = false;
        var face = OpenTypeFace.Load(f.Build());
        Assert.Equal(4, Points(face, sq).Count);
    }
}
