using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Graphics.Text;

namespace AwAUI.Graphics.UnitTests;

/// <summary>最小的字型实现：每个字形是 500 × 700 设计单位的方块（upem 1000，'head' 边界框 0..600 × −200..800）。</summary>
sealed class BoxFace : FontFace
{
    public override FontDescriptor Descriptor => new("Box");
    public override FontMetrics Metrics => new() { UnitsPerEm = 1000, Ascender = 800, Descender = -200, XMin = 0, XMax = 600, YMin = -200, YMax = 800 };
    public override int GlyphCount => 10;
    public override OutlineFormat OutlineFormat => OutlineFormat.TrueType;
    public override ReadOnlyMemory<byte> GetTable(uint tag) => default;

    public override bool TryGetGlyph(int codePoint, out ushort glyph)
    {
        glyph = codePoint is >= 'a' and <= 'i' ? (ushort)(codePoint - 'a' + 1) : (ushort)0;
        return glyph != 0;
    }

    public override int GetAdvance(ushort glyph) => 600;

    public override bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink, in Matrix3x2 transform)
    {
        if (glyph >= GlyphCount) return false;
        var m = transform;
        Point T(float x, float y) => new Point(x, y).Transform(m);
        sink.MoveTo(T(0, -700)).LineTo(T(500, -700)).LineTo(T(500, 0)).LineTo(T(0, 0)).Close();
        return true;
    }

    public override GlyphImage? GetColorGlyph(ushort glyph, float size) => null;
    public override IReadOnlyList<VariationAxis> Axes => Array.Empty<VariationAxis>();
}

public class GlyphRunTests
{
    static readonly BoxFace Face = new();

    static GlyphRun Run(int count, float size = 10, GlyphRunFlags flags = GlyphRunFlags.None)
    {
        var glyphs = new ushort[count];
        var positions = new Point[count];
        for (int i = 0; i < count; i++)
        {
            glyphs[i] = (ushort)(i + 1);
            positions[i] = new Point(i * size * 0.6f, 0);
        }
        return new GlyphRun(Face, size, glyphs, positions, flags);
    }

    [Fact]
    public void Constructor_copies_and_validates()
    {
        var glyphs = new ushort[] { 1, 2 };
        var positions = new[] { new Point(0, 0), new Point(6, 0) };
        var run = new GlyphRun(Face, 10, glyphs, positions);
        glyphs[0] = 9;
        positions[1] = new Point(100, 100);
        Assert.Equal(1, run.Glyphs[0]);
        Assert.Equal(new Point(6, 0), run.Positions[1]);
        Assert.Equal(2, run.Count);
        Assert.Same(Face, run.Face);
        Assert.False(run.IsEmpty);
        Assert.True(new GlyphRun(Face, 0, glyphs, positions).IsEmpty);
        Assert.Throws<ArgumentException>(() => new GlyphRun(Face, 10, glyphs, new Point[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlyphRun(Face, -1, glyphs, positions));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlyphRun(Face, float.NaN, glyphs, positions));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlyphRun(Face, float.PositiveInfinity, glyphs, positions));
        Assert.Throws<ArgumentNullException>(() => new GlyphRun(null!, 10, glyphs, positions));
    }

    [Fact]
    public void Bounds_use_the_font_bounding_box_and_synthetic_styles()
    {
        var run = Run(3);   // 位置 0、6、12；每个字形 0..6 × −8..2
        Assert.Equal(Rect.FromLTRB(0, -8, 18, 2), run.Bounds);

        var bold = Run(3, flags: GlyphRunFlags.SyntheticBold);
        float e = GlyphRun.GetSyntheticBoldExtent(10);
        Assert.Equal(-e, bold.Bounds.Left, 4);
        Assert.Equal(-8 - e, bold.Bounds.Top, 4);
        Assert.Equal(18 + e, bold.Bounds.Right, 4);
        Assert.Equal(2 + e, bold.Bounds.Bottom, 4);

        var oblique = Run(3, flags: GlyphRunFlags.SyntheticOblique);
        // x' = x − y·0.25：顶部（y = −8）右移 2，底部（y = 2）左移 0.5
        Assert.Equal(-0.5f, oblique.Bounds.Left, 4);
        Assert.Equal(20f, oblique.Bounds.Right, 4);

        var nan = new GlyphRun(Face, 10, new ushort[] { 1 }, new[] { new Point(float.NaN, 0) });
        Assert.False(nan.Bounds.IsFinite);
    }

    [Fact]
    public void Synthetic_bold_extent_follows_the_skia_curve()
    {
        Assert.Equal(9f / 24, GlyphRun.GetSyntheticBoldExtent(9), 5);
        Assert.Equal(36f / 32, GlyphRun.GetSyntheticBoldExtent(36), 5);
        Assert.Equal(72f / 32, GlyphRun.GetSyntheticBoldExtent(72), 5);
        float mid = GlyphRun.GetSyntheticBoldExtent(20) / 20;
        Assert.InRange(mid, 1f / 32, 1f / 24);
    }

    [Fact]
    public void DrawGlyphRun_records_op_36_with_the_run_in_the_resource_table()
    {
        var run = Run(4);
        var dc = new DrawingContext();
        dc.DrawGlyphRun(run, new Point(10, 20), Color.Parse("#336699"));
        dc.DrawGlyphRun(run, new Point(10, 40), new LinearGradientBrush(new Point(0, 0), new Point(10, 0),
            new[] { new GradientStop(0, Color.Black), new GradientStop(1, Color.White) }));
        var list = dc.Finish();

        Assert.Equal(36, (int)DrawOp.DrawGlyphRun);
        var r = list.GetReader();
        Assert.True(r.MoveNext());
        Assert.Equal(DrawOp.DrawGlyphRun, r.Op);
        Assert.Equal(DrawFlags.AntiAlias, r.Flags);
        var c = r.Read<DrawGlyphRunCmd>();
        Assert.Same(run, r.GetGlyphRun(c));
        Assert.Same(run, r.GetResource<GlyphRun>(c.GlyphRunRef));
        Assert.Equal(new Point(10, 20), c.Origin);
        Assert.True(c.Paint.IsSolid);
        Assert.Equal(Color.Parse("#336699"), c.Paint.Color);

        Assert.True(r.MoveNext());
        var c2 = r.Read<DrawGlyphRunCmd>();
        Assert.Equal(c.GlyphRunRef, c2.GlyphRunRef);          // 同一个字形串只进资源表一次
        Assert.False(c2.Paint.IsSolid);
        Assert.IsType<LinearGradientBrush>(r.GetBrush(c2.Paint));
        Assert.False(r.MoveNext());

        Assert.Equal(2, list.Resources.Count);
        Assert.Equal(Rect.FromLTRB(10, 12, 34, 42), list.Bounds);
        int payload = System.Runtime.InteropServices.Marshal.SizeOf<DrawGlyphRunCmd>();
        Assert.Equal(0, payload % 4);
        Assert.Equal(2 * (4 + payload), list.CommandBytes.Length);   // 命令头 + 固定布局的载荷
    }

    [Fact]
    public void DrawGlyphRun_skips_invisible_and_degenerate_input()
    {
        var dc = new DrawingContext();
        dc.DrawGlyphRun(Run(0), default, Color.Black);
        dc.DrawGlyphRun(Run(3, size: 0), default, Color.Black);
        dc.DrawGlyphRun(Run(3), new Point(float.NaN, 0), Color.Black);
        dc.DrawGlyphRun(Run(3), default, Color.Transparent);
        dc.DrawGlyphRun(new GlyphRun(Face, 10, new ushort[] { 1 }, new[] { new Point(float.PositiveInfinity, 0) }), default, Color.Black);
        Assert.True(dc.Finish().IsEmpty);
        Assert.Throws<ArgumentNullException>(() => new DrawingContext().DrawGlyphRun(null!, default, Color.Black));
    }

    [Fact]
    public void DrawGlyphRun_bounds_follow_transform_and_clip()
    {
        var dc = new DrawingContext();
        dc.Translate(100, 50);
        dc.ClipRect(new Rect(0, -5, 10, 100));
        dc.DrawGlyphRun(Run(3), new Point(0, 0), Color.Black);
        Assert.Equal(Rect.FromLTRB(100, 45, 110, 52), dc.Finish().Bounds);
    }

    [Fact]
    public void Content_hash_tracks_glyph_run_identity()
    {
        var run = Run(2);
        DisplayList Make(GlyphRun r)
        {
            var dc = new DrawingContext();
            dc.DrawGlyphRun(r, default, Color.Black);
            return dc.Finish();
        }
        Assert.Equal(Make(run).ContentHash, Make(run).ContentHash);
        Assert.NotEqual(Make(run).ContentHash, Make(Run(2)).ContentHash);
    }

    /// <summary>记录 <see cref="Metrics"/> 被读取的次数。</summary>
    sealed class CountingFace : FontFace
    {
        readonly BoxFace _inner = new();
        public int MetricsReads;
        public override FontDescriptor Descriptor => _inner.Descriptor;
        public override FontMetrics Metrics { get { System.Threading.Interlocked.Increment(ref MetricsReads); return _inner.Metrics; } }
        public override int GlyphCount => _inner.GlyphCount;
        public override OutlineFormat OutlineFormat => _inner.OutlineFormat;
        public override ReadOnlyMemory<byte> GetTable(uint tag) => default;
        public override bool TryGetGlyph(int codePoint, out ushort glyph) => _inner.TryGetGlyph(codePoint, out glyph);
        public override int GetAdvance(ushort glyph) => _inner.GetAdvance(glyph);
        public override bool GetOutline(ushort glyph, in FontVariation variation, PathBuilder sink, in Matrix3x2 transform) =>
            _inner.GetOutline(glyph, variation, sink, transform);
        public override GlyphImage? GetColorGlyph(ushort glyph, float size) => null;
        public override IReadOnlyList<VariationAxis> Axes => Array.Empty<VariationAxis>();
    }

    [Fact]
    public void Bounds_are_computed_once_at_construction_and_safe_to_read_concurrently()
    {
        // 回归：Bounds 曾惰性缓存在一个 Rect? 字段里，并发读取时可能看到 HasValue 已置位而数值尚未写入的撕裂值。
        // 现在构造时就算好（之后不再读取字型度量）
        var face = new CountingFace();
        var run = new GlyphRun(face, 10, new ushort[] { 1, 2, 3 }, new[] { new Point(0, 0), new Point(6, 0), new Point(12, 0) });
        Assert.Equal(1, face.MetricsReads);
        var results = new Rect[16];
        System.Threading.Tasks.Parallel.For(0, results.Length, i => results[i] = run.Bounds);
        foreach (var r in results) Assert.Equal(Rect.FromLTRB(0, -8, 18, 2), r);
        Assert.Equal(1, face.MetricsReads);
    }

    [Fact]
    public void Font_variation_equality_is_by_value()
    {
        var a = new FontVariation(new VariationSetting(0x77676874, 700));
        var b = new FontVariation(new VariationSetting(0x77676874, 700));
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new FontVariation(new VariationSetting(0x77676874, 400)));
        Assert.NotEqual(a, FontVariation.Default);
        Assert.Equal(FontVariation.Default, new FontVariation());
        Assert.True(FontVariation.Default.IsDefault);
    }
}
