using System.Diagnostics;
using System.Globalization;
using System.Text;
using AwAUI.Graphics;
using AwAUI.Text.OpenType;

namespace AwAUI.Text.UnitTests;

/// <summary>CFF / CFF2 轮廓：自制测试字体与 fontTools 的独立解码逐字形比对，恶意数据的限制，以及系统 CFF 字体（存在时）。</summary>
public class CffTests
{
    static string Font(string name) => System.IO.Path.Combine(TestFonts.FontsDir, name);

    /// <summary>与 dump_test_fonts.py 相同的格式：设计单位、y 向上、1 位小数。</summary>
    internal static string Dump(FontFace face, ushort glyph)
    {
        var pb = new PathBuilder();
        if (!face.GetOutline(glyph, default, pb)) return "FAIL";
        var path = pb.ToPath();
        var sb = new StringBuilder();
        var pts = path.Points;
        int k = 0;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.Move: sb.Append(" M ").Append(F(pts[k].X)).Append(' ').Append(F(-pts[k].Y)); k++; break;
                case PathVerb.Line: sb.Append(" L ").Append(F(pts[k].X)).Append(' ').Append(F(-pts[k].Y)); k++; break;
                case PathVerb.Cubic:
                    sb.Append(" C");
                    for (int i = 0; i < 3; i++) sb.Append(' ').Append(F(pts[k + i].X)).Append(' ').Append(F(-pts[k + i].Y));
                    k += 3;
                    break;
                case PathVerb.Quad: sb.Append(" Q"); k += 2; break;
                case PathVerb.Close: sb.Append(" Z"); break;
            }
        }
        return sb.ToString().TrimStart();

        static string F(float v)
        {
            double r = Math.Round(v, 1);
            if (r == 0) r = 0;
            return r.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    public static IEnumerable<object[]> ExpectedOutlines()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Expected", "AwATest-outlines.txt");
        foreach (var line in File.ReadAllLines(path))
        {
            var p = line.Split('\t');
            yield return new object[] { p[0], int.Parse(p[1], CultureInfo.InvariantCulture), p[2] };
        }
    }

    [Theory]
    [MemberData(nameof(ExpectedOutlines))]
    public void Outlines_match_fonttools(string font, int glyph, string expected)
    {
        var face = OpenTypeFace.Load(Font(font));
        Assert.Equal(expected, Dump(face, (ushort)glyph));
    }

    [Theory]
    [InlineData("AwATest-Cff.otf")]
    [InlineData("AwATest-Cid.otf")]
    [InlineData("AwATest-Cid0.otf")]
    public void Arithmetic_and_storage_operators_are_executed(string font)
    {
        // 字形 X（ID 15）：30 70 add 2 mul 4 div … 留下 (100, 50) 作为 rmoveto 的参数，宽度参数 600 被丢弃
        var face = OpenTypeFace.Load(Font(font));
        Assert.Equal("M 100 50 L 500 50 L 500 450 L 100 450 Z", Dump(face, 15));
    }

    [Fact]
    public void Formats_and_seac_are_recognised()
    {
        var cff = OpenTypeFace.Load(Font("AwATest-Cff.otf"));
        Assert.Equal(OutlineFormat.Cff, cff.OutlineFormat);
        Assert.False(cff.Cff!.IsCid);
        // Aacute（seac）= A 的轮廓 + 平移 (160, 120) 的 acute
        string a = Dump(cff, 2), aacute = Dump(cff, 12);
        Assert.StartsWith(a, aacute);
        Assert.Contains("M 260 720", aacute);

        var cid = OpenTypeFace.Load(Font("AwATest-Cid.otf"));
        Assert.True(cid.Cff!.IsCid);
        Assert.Equal(2, cid.Cff.FontDicts.Length);
        // A 位于 FD1：它的局部子程序 0 从 x = 40 开始，而 FD0 的从 20 开始
        Assert.StartsWith("M 40 0", Dump(cid, 2));
        Assert.Equal(Dump(cid, 2), Dump(OpenTypeFace.Load(Font("AwATest-Cid0.otf")), 2));

        var cff2 = OpenTypeFace.Load(Font("AwATest-Cff2.otf"));
        Assert.Equal(OutlineFormat.Cff2, cff2.OutlineFormat);
        Assert.True(cff2.Cff!.IsCff2);
    }

    [Fact]
    public void Glyph_bounds_include_curve_extrema()
    {
        var face = OpenTypeFace.Load(Font("AwATest-Cff.otf"));
        Assert.True(face.TryGetGlyph('o', out var o));
        Assert.True(face.TryGetGlyphBounds(o, out var b));
        // 与 fontTools BoundsPen 的精确边界一致（控制点边界为 (-100, 110, 400, 620)，更宽）
        Assert.Equal(-31.080, b.Left, 2);
        Assert.Equal(400, b.Right, 2);
        Assert.Equal(-553.461, b.Top, 2);
        Assert.Equal(-145.881, b.Bottom, 2);
        Assert.True(face.TryGetGlyph(' ', out var space));
        Assert.False(face.TryGetGlyphBounds(space, out _));
        var pb = new PathBuilder();
        Assert.True(face.GetOutline(space, default, pb));
        Assert.True(pb.ToPath().IsEmpty);
    }

    [Fact]
    public void Hostile_charstrings_fail_without_writing_and_within_budget()
    {
        var face = OpenTypeFace.Load(Font("AwATest-Hostile.otf"));
        var sw = Stopwatch.StartNew();
        // g1 自递归、g2 指数级展开、g3 栈溢出、g4 截断的 hintmask、g5 缺失字形的 seac、g7 子程序之外的 return
        foreach (ushort g in new ushort[] { 1, 2, 3, 4, 5, 7 })
        {
            var pb = new PathBuilder();
            Assert.False(face.GetOutline(g, default, pb), $"glyph {g}");
            Assert.True(pb.ToPath().IsEmpty);
            Assert.False(face.TryGetGlyphBounds(g, out _));
        }
        // g6：没有 moveto 的 lineto 宽松处理，从原点开始
        Assert.Equal("M 0 0 L 100 100 L 150 100 Z", Dump(face, 6));
        Assert.Equal("M 50 0 L 450 0 L 450 700 L 50 700 Z", Dump(face, 0));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    [Fact]
    public void Mutated_cff_tables_never_throw()
    {
        foreach (var name in new[] { "AwATest-Cff.otf", "AwATest-Cid.otf", "AwATest-Cff2.otf" })
        {
            var original = File.ReadAllBytes(Font(name));
            var face0 = OpenTypeFace.Load(original);
            uint tag = face0.OutlineFormat == OutlineFormat.Cff2 ? OpenTypeTag.Cff2 : OpenTypeTag.Cff;
            int tableOffset = FindTable(original, tag, out int tableLength);
            var rng = new Random(1234 + name.Length);
            for (int iter = 0; iter < 300; iter++)
            {
                var bytes = (byte[])original.Clone();
                int edits = rng.Next(1, 8);
                for (int e = 0; e < edits; e++) bytes[tableOffset + rng.Next(tableLength)] = (byte)rng.Next(256);
                if (!OpenTypeFace.TryLoad(bytes, 0, out var face)) continue;
                for (ushort g = 0; g < face.GlyphCount; g++)
                {
                    face.GetOutline(g, default, new PathBuilder());
                    face.TryGetGlyphBounds(g, out _);
                }
            }
        }
    }

    static int FindTable(byte[] font, uint tag, out int length)
    {
        int numTables = (font[4] << 8) | font[5];
        for (int i = 0; i < numTables; i++)
        {
            int r = 12 + i * 16;
            uint t = (uint)((font[r] << 24) | (font[r + 1] << 16) | (font[r + 2] << 8) | font[r + 3]);
            if (t != tag) continue;
            int off = (font[r + 8] << 24) | (font[r + 9] << 16) | (font[r + 10] << 8) | font[r + 11];
            length = (font[r + 12] << 24) | (font[r + 13] << 16) | (font[r + 14] << 8) | font[r + 15];
            return off;
        }
        throw new InvalidOperationException("table not found");
    }

    // ------------------------------------------------------------------ 系统 CFF 字体（macOS）

    public const string HiraginoSans = "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc";
    public const string HiraginoSansGb = "/System/Library/Fonts/Hiragino Sans GB.ttc";
    public const string SfIndia = "/System/Library/Fonts/SFIndia.ttc";

    [SystemFontFact(HiraginoSans)]
    public void System_cid_keyed_cff_renders_cjk()
    {
        var face = OpenTypeFace.Load(HiraginoSans);
        Assert.Equal(OutlineFormat.Cff, face.OutlineFormat);
        Assert.True(face.Cff!.IsCid);
        int fails = 0;
        foreach (char c in "日本語のテキスト、中文漢字かなカナ")
        {
            Assert.True(face.TryGetGlyph(c, out var g));
            var pb = new PathBuilder();
            if (!face.GetOutline(g, default, pb)) fails++;
            var p = pb.ToPath();
            Assert.False(p.IsEmpty);
            Assert.True(face.TryGetGlyphBounds(g, out var b));
            Assert.True(b.Width > 50 && b.Height > 50);
            // 墨迹在字体声明的 head 边界框之内
            var m = face.Metrics;
            Assert.True(b.Left >= m.XMin - 1 && b.Right <= m.XMax + 1 && -b.Top <= m.YMax + 1 && -b.Bottom >= m.YMin - 1);
        }
        Assert.Equal(0, fails);
    }

    [SystemFontFact(HiraginoSansGb)]
    public void System_cff_fonts_decode_every_glyph()
    {
        var face = OpenTypeFace.Load(HiraginoSansGb);
        int fails = 0;
        for (int g = 0; g < face.GlyphCount; g += 7)
            if (!face.GetOutline((ushort)g, default, new PathBuilder())) fails++;
        Assert.Equal(0, fails);
    }

    [SystemFontFact(SfIndia)]
    public void System_cff2_font_decodes_default_instance()
    {
        var face = OpenTypeFace.Load(SfIndia, 1);
        Assert.Equal(OutlineFormat.Cff2, face.OutlineFormat);
        Assert.True(face.TryGetGlyph(0x0915, out var ka));   // देवनागरी क
        Assert.False(Dump(face, ka) is "FAIL" or "");
        int fails = 0;
        for (int g = 0; g < face.GlyphCount; g++)
            if (!face.GetOutline((ushort)g, default, new PathBuilder())) fails++;
        Assert.Equal(0, fails);
    }
}
