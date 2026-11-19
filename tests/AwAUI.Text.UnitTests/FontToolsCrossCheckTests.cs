using AwAUI.Text.OpenType;

namespace AwAUI.Text.UnitTests;

/// <summary>表解析结果与 fontTools 4.68 的输出逐项比对（期望值见 Expected/*.txt）。</summary>
public class FontToolsCrossCheckTests
{
    public static IEnumerable<object[]> Fonts => new[] { new object[] { TestFonts.NotoSans }, new object[] { TestFonts.Abel } };

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Global_tables_match(string file)
    {
        var face = TestFonts.Load(file);
        var e = ExpectedData.Load(file);
        var m = face.Metrics;

        var head = e.Records["head"];
        Assert.Equal(head[0], m.UnitsPerEm);
        Assert.Equal((head[1], head[2], head[3], head[4]), (m.XMin, m.YMin, m.XMax, m.YMax));

        var hhea = e.Records["hhea"];
        Assert.Equal(new VerticalMetrics(hhea[0], hhea[1], hhea[2]).ToString(), m.Hhea.ToString());
        Assert.Equal(hhea[3], m.AdvanceWidthMax);

        Assert.Equal(e.Records["maxp"][0], face.GlyphCount);

        var os2 = e.Records["os2"];
        Assert.Equal(os2[1], face.Descriptor.Weight.Value);
        Assert.Equal(os2[2], (int)face.Descriptor.Stretch);
        Assert.Equal((os2[4], os2[5], os2[6]), (m.Typo.Ascender, m.Typo.Descender, m.Typo.LineGap));
        Assert.Equal((os2[7], -os2[8]), (m.Win.Ascender, m.Win.Descender));
        Assert.Equal(os2[9], m.XHeight);
        Assert.Equal(os2[10], m.CapHeight);
        Assert.Equal(os2[11], m.StrikeoutThickness);
        Assert.Equal(os2[12], m.StrikeoutPosition);

        var post = e.Records["post"];
        Assert.Equal(e.ItalicAngle, m.ItalicAngle);
        Assert.Equal(post[0], m.UnderlinePosition);
        Assert.Equal(post[1], m.UnderlineThickness);
        Assert.Equal(post[2] != 0, m.IsFixedPitch);

        Assert.Equal(e.Names[1], face.Names.Family);
        Assert.Equal(e.Names[2], face.Names.Subfamily);
        Assert.Equal(e.Names[4], face.Names.FullName);
        Assert.Equal(e.Names[6], face.Names.PostScriptName);
        Assert.Equal(e.Names.TryGetValue(16, out var tf) ? tf : null, face.Names.TypographicFamily);
        Assert.Equal(e.Names.TryGetValue(16, out tf) ? tf : e.Names[1], face.Descriptor.FamilyName);
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Every_cmap_entry_matches(string file)
    {
        var face = TestFonts.Load(file);
        var e = ExpectedData.Load(file);
        Assert.NotEmpty(e.Cmap);
        var expected = e.Cmap.ToDictionary(x => x.Cp, x => x.Gid);
        foreach (var (cp, gid) in e.Cmap)
        {
            Assert.True(face.TryGetGlyph(cp, out var g), $"U+{cp:X4} should be mapped");
            Assert.Equal(gid, g);
        }
        // 未映射的码点（抽样整个 BMP 与部分补充平面）必须返回 false 和 .notdef
        for (int cp = 0; cp < 0x20000; cp += cp < 0x10000 ? 1 : 7)
        {
            if (expected.ContainsKey(cp) || cp is >= 0xD800 and <= 0xDFFF) continue;
            Assert.False(face.TryGetGlyph(cp, out var g), $"U+{cp:X4} should not be mapped");
            Assert.Equal(0, g);
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Every_horizontal_metric_matches(string file)
    {
        var face = TestFonts.Load(file);
        foreach (var (gid, adv, lsb) in ExpectedData.Load(file).Hmtx)
        {
            Assert.Equal(adv, face.GetAdvance((ushort)gid));
            Assert.Equal(lsb, face.GetLeftSideBearing((ushort)gid));
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Every_glyph_outline_matches(string file)
    {
        var face = TestFonts.Load(file);
        var e = ExpectedData.Load(file);
        Assert.Equal(face.GlyphCount, e.Glyphs.Count);
        int composites = 0;
        foreach (var v in e.Glyphs)
        {
            int gid = v[0];
            var outline = face.DecodeOutline((ushort)gid)!;
            Assert.Equal(v[1], outline.PointCount);
            Assert.Equal(v[2], outline.ContourCount);
            Assert.Equal(v[3], outline.OnCurve.Count(x => x));
            long sx = 0, sy = 0, chk = 0;
            for (int i = 0; i < outline.PointCount; i++)
            {
                long x = (long)Math.Round(outline.Points[i].X, MidpointRounding.ToEven);
                long y = (long)Math.Round(outline.Points[i].Y, MidpointRounding.ToEven);
                sx += x;
                sy += y;
                chk = ((chk + (i + 1) * (x * 31 + y * 17)) % 2147483647 + 2147483647) % 2147483647;
            }
            Assert.True(v[4] == sx && v[5] == sy && v[6] == chk, $"glyph {gid}: coordinates differ");
            composites += v[7];

            if (v[1] > 0)
            {
                Assert.True(face.TryGetGlyphBounds((ushort)gid, out var b));
                Assert.Equal((v[8], -v[11], v[10] - v[8], v[11] - v[9]), ((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height));
            }
        }
        if (file == TestFonts.NotoSans) Assert.True(composites > 1000, "Noto Sans should exercise composite glyphs");
    }

    [Fact]
    public void Every_kerning_pair_matches()
    {
        var face = TestFonts.Load(TestFonts.Abel);
        var e = ExpectedData.Load(TestFonts.Abel);
        Assert.Equal(748, e.Kern.Count);
        Assert.True(face.HasKerning);
        foreach (var (l, r, v) in e.Kern) Assert.Equal(v, face.GetKerning((ushort)l, (ushort)r));
        // 不在表中的组合为 0
        Assert.Equal(0, face.GetKerning(0, 0));
        Assert.False(TestFonts.Load(TestFonts.NotoSans).HasKerning);   // Noto Sans 只有 GPOS
    }
}
