using AwAUI.Text.OpenType;

namespace AwAUI.Text.UnitTests;

public class CmapAndTablesTests
{
    static OpenTypeFace FontWithCmap(byte[] cmap, int glyphs = 300)
    {
        var f = new SyntheticFont();
        for (int i = 0; i < glyphs; i++) f.AddGlyph(Array.Empty<byte>(), 100 + i);
        f.Cmap = cmap;
        return OpenTypeFace.Load(f.Build());
    }

    static ushort Map(FontFace face, int cp) => face.TryGetGlyph(cp, out var g) ? g : (ushort)0;

    [Fact]
    public void Format4_with_delta_segments()
    {
        var face = FontWithCmap(SyntheticFont.CmapFormat4((0x41, 5), (0x42, 6), (0x4E2D, 250)));
        Assert.Equal(5, Map(face, 'A'));
        Assert.Equal(6, Map(face, 'B'));
        Assert.Equal(250, Map(face, 0x4E2D));
        Assert.Equal(0, Map(face, 'C'));
        Assert.Equal(0, Map(face, 0x1F600));
        Assert.Equal(4, face.Cmap!.Format);
    }

    [Fact]
    public void Format4_with_glyph_id_array()
    {
        // 一个段 0x61–0x63 使用 idRangeOffset 指向 glyphIdArray，另有 idDelta = 2
        var w = new BeWriter();
        int segX2 = 4;
        w.U16(4).U16(0).U16(0).U16(segX2).U16(0).U16(0).U16(0);
        w.U16(0x63).U16(0xFFFF).U16(0);                 // endCode
        w.U16(0x61).U16(0xFFFF);                        // startCode
        w.U16(2).U16(1);                                // idDelta
        w.U16(4).U16(0);                                // idRangeOffset：从本字段到 glyphIdArray 的距离 = 2 段 × 2 字节
        w.U16(10).U16(0).U16(30);                       // glyphIdArray（第二个为 0 → 缺字）
        var face = FontWithCmap(SyntheticFont.CmapWith((3, 1, w.ToArray())));
        Assert.Equal(12, Map(face, 'a'));
        Assert.Equal(0, Map(face, 'b'));
        Assert.Equal(32, Map(face, 'c'));
    }

    [Fact]
    public void Format12_maps_supplementary_planes_and_wins_over_format4()
    {
        var f12 = new BeWriter();
        f12.U16(12).U16(0).U32(16 + 2 * 12).U32(0).U32(2);
        f12.U32(0x41).U32(0x5A).U32(10);                // A–Z → 10..35
        f12.U32(0x1F600).U32(0x1F602).U32(200);         // 😀..😂 → 200..202
        var cmap = SyntheticFont.CmapWith(
            (3, 1, SyntheticFont.CmapFormat4((0x41, 99)).AsSpan(12).ToArray()),
            (3, 10, f12.ToArray()));
        var face = FontWithCmap(cmap);
        Assert.Equal(12, face.Cmap!.Format);
        Assert.Equal(10, Map(face, 'A'));
        Assert.Equal(35, Map(face, 'Z'));
        Assert.Equal(201, Map(face, 0x1F601));
        Assert.Equal(0, Map(face, 0x1F603));
    }

    [Fact]
    public void Format13_maps_ranges_to_one_glyph()
    {
        var f13 = new BeWriter();
        f13.U16(13).U16(0).U32(28).U32(0).U32(1).U32(0x4E00).U32(0x9FFF).U32(42);
        var face = FontWithCmap(SyntheticFont.CmapWith((0, 6, f13.ToArray())));
        Assert.Equal(42, Map(face, 0x4E00));
        Assert.Equal(42, Map(face, 0x9000));
        Assert.Equal(0, Map(face, 0xA000));
    }

    [Fact]
    public void Format6_trimmed_table()
    {
        var f6 = new BeWriter();
        f6.U16(6).U16(16).U16(0).U16(0x30).U16(3).U16(7).U16(8).U16(9);
        var face = FontWithCmap(SyntheticFont.CmapWith((0, 3, f6.ToArray())));
        Assert.Equal(7, Map(face, '0'));
        Assert.Equal(9, Map(face, '2'));
        Assert.Equal(0, Map(face, '3'));
        Assert.Equal(0, Map(face, '/'));
    }

    [Fact]
    public void Mac_roman_format0_is_used_when_no_unicode_subtable_exists()
    {
        var f0 = new BeWriter();
        f0.U16(0).U16(262).U16(0);
        var glyphs = new byte[256];
        glyphs['A'] = 3;
        glyphs[0x80] = 4;    // Mac Roman 0x80 = Ä
        glyphs[0xDB] = 5;    // Mac Roman 0xDB = €
        f0.Bytes(glyphs);
        var face = FontWithCmap(SyntheticFont.CmapWith((1, 0, f0.ToArray())));
        Assert.Equal(1, face.Cmap!.PlatformId);
        Assert.Equal(3, Map(face, 'A'));
        Assert.Equal(4, Map(face, 'Ä'));
        Assert.Equal(5, Map(face, '€'));
        Assert.Equal(0, Map(face, 0x80));   // U+0080 不在 Mac Roman 中
    }

    [Fact]
    public void Symbol_cmap_maps_ascii_through_private_use_area()
    {
        var sub = SyntheticFont.CmapFormat4((0xF041, 7)).AsSpan(12).ToArray();
        var face = FontWithCmap(SyntheticFont.CmapWith((3, 0, sub)));
        Assert.Equal(7, Map(face, 'A'));
        Assert.Equal(7, Map(face, 0xF041));
    }

    [Fact]
    public void Glyph_ids_beyond_glyph_count_are_treated_as_missing()
    {
        var face = FontWithCmap(SyntheticFont.CmapFormat4((0x41, 5000)), glyphs: 10);
        Assert.False(face.TryGetGlyph('A', out var g));
        Assert.Equal(0, g);
    }

    [Fact]
    public void Missing_cmap_maps_nothing()
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());
        var face = OpenTypeFace.Load(f.Build());
        Assert.False(face.TryGetGlyph('A', out _));
    }

    [Fact]
    public void Noto_maps_supplementary_code_points_via_format12()
    {
        var face = TestFonts.Load(TestFonts.NotoSans);
        Assert.Equal(12, face.Cmap!.Format);
        Assert.Equal((3, 10), (face.Cmap.PlatformId, face.Cmap.EncodingId));
        Assert.True(face.TryGetGlyph(0x10780, out var g));    // U+10780 MODIFIER LETTER SMALL CAPITAL AA
        Assert.Equal(3796, g);
    }

    [Fact]
    public void Abel_metrics_and_descriptor()
    {
        var face = TestFonts.Load(TestFonts.Abel);
        var m = face.Metrics;
        Assert.Equal(2048, m.UnitsPerEm);
        // 没有设置 USE_TYPO_METRICS：取 hhea
        Assert.Equal((2006, -604, 0), (m.Ascender, m.Descender, m.LineGap));
        Assert.Equal(2610, m.LineHeight);
        Assert.Equal(new FontDescriptor("Abel", FontWeight.Normal, FontStyle.Normal, FontStretch.Normal), face.Descriptor);
        Assert.Equal(OutlineFormat.TrueType, face.OutlineFormat);
        Assert.Empty(face.Axes);
        Assert.Null(face.GetColorGlyph(1, 16));
        Assert.Contains(OpenTypeTag.Kern, face.TableTags);
        Assert.Equal(OpenTypeTag.Kern, OpenTypeTag.Make("kern"));
        Assert.Equal("OS/2", OpenTypeTag.ToString(OpenTypeTag.OS2));
    }

    [Fact]
    public void Kern_table_with_apple_header()
    {
        var f = new SyntheticFont();
        for (int i = 0; i < 5; i++) f.AddGlyph(Array.Empty<byte>());
        var kern = new BeWriter();
        kern.U32(0x00010000).U32(5);
        // 子表 1：水平格式 0，两个对（coverage 字节 0，format 字节 0）
        kern.U32(8 + 8 + 2 * 6).U8(0x00).U8(0).U16(0);
        kern.U16(2).U16(12).U16(1).U16(0);
        kern.U16(1).U16(2).I16(-50);
        kern.U16(3).U16(4).I16(25);
        // 子表 2–4：垂直（0x80）、跨流方向（0x40）、变体（0x20），格式均为 0，应忽略
        foreach (byte flags in new byte[] { 0x80, 0x40, 0x20 })
        {
            kern.U32(8 + 8 + 6).U8(flags).U8(0).U16(0);
            kern.U16(1).U16(6).U16(0).U16(0);
            kern.U16(1).U16(2).I16(-999);
        }
        // 子表 5：未知格式 64，应忽略
        kern.U32(8 + 8 + 6).U8(0x00).U8(64).U16(0);
        kern.U16(1).U16(6).U16(0).U16(0);
        kern.U16(1).U16(2).I16(-777);
        f.ExtraTables["kern"] = kern.ToArray();
        var face = OpenTypeFace.Load(f.Build());
        Assert.Equal(-50, face.GetKerning(1, 2));
        Assert.Equal(25, face.GetKerning(3, 4));
        Assert.Equal(0, face.GetKerning(2, 1));
    }

    [Fact]
    public void Kern_table_microsoft_header_sums_subtables_and_skips_vertical()
    {
        var f = new SyntheticFont();
        for (int i = 0; i < 5; i++) f.AddGlyph(Array.Empty<byte>());
        var kern = new BeWriter();
        kern.U16(0).U16(3);
        void Sub(int coverage, int value)
        {
            kern.U16(0).U16(6 + 8 + 6).U16(coverage);
            kern.U16(1).U16(6).U16(0).U16(0);
            kern.U16(1).U16(2).I16(value);
        }
        Sub(0x0001, -30);   // 水平
        Sub(0x0000, -500);  // 垂直：忽略
        Sub(0x0001, -5);    // 水平：累加
        f.ExtraTables["kern"] = kern.ToArray();
        Assert.Equal(-35, OpenTypeFace.Load(f.Build()).GetKerning(1, 2));
    }

    [Fact]
    public void Synthetic_collection_loads_each_face()
    {
        var ttc = SyntheticFont.MakeCollection(TestFonts.Bytes(TestFonts.Abel), TestFonts.Bytes(TestFonts.NotoSans));
        Assert.Equal(2, OpenTypeFace.GetFaceCount(ttc));
        var faces = OpenTypeFace.LoadAll(ttc);
        Assert.Equal("Abel", faces[0].Descriptor.FamilyName);
        Assert.Equal("Noto Sans", faces[1].Descriptor.FamilyName);
        Assert.Equal(1, faces[1].FaceIndex);
        // 集合中的字型与单独加载的结果一致
        var single = TestFonts.Load(TestFonts.NotoSans);
        Assert.True(faces[1].TryGetGlyph('Å', out var g));
        Assert.Equal(single.DecodeOutline(g)!.Points, faces[1].DecodeOutline(g)!.Points);
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenTypeFace.Load(ttc, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenTypeFace.Load(TestFonts.Bytes(TestFonts.Abel), 1));
    }

    [SystemFontFact(TestFonts.Helvetica)]
    public void System_helvetica_collection()
    {
        var data = File.ReadAllBytes(TestFonts.Helvetica);
        var faces = OpenTypeFace.LoadAll(data);
        Assert.Equal(6, faces.Count);
        var regular = faces.First(f => f.Names.FullName == "Helvetica");
        var bold = faces.First(f => f.Names.FullName == "Helvetica Bold");
        var light = faces.First(f => f.Names.FullName == "Helvetica Light");
        Assert.Equal(FontWeight.Bold, bold.Descriptor.Weight);
        Assert.True(light.Descriptor.Weight.Value < 400);
        Assert.Equal(FontStyle.Normal, regular.Descriptor.Style);
        Assert.Contains(faces, f => f.Descriptor.Style != FontStyle.Normal);
        // Helvetica 的 'kern' 是 Apple 格式（version 1.0）；fontTools 读出 A/V = -151
        Assert.True(regular.TryGetGlyph('A', out var a));
        Assert.True(regular.TryGetGlyph('V', out var v));
        Assert.Equal(-151, regular.GetKerning(a, v));
        var pb = new AwAUI.Graphics.PathBuilder();
        Assert.True(regular.GetOutline(a, default, pb));
        Assert.False(pb.ToPath().IsEmpty);
    }

    [SystemFontFact(TestFonts.Arial)]
    public void System_arial_microsoft_kern_table()
    {
        var face = OpenTypeFace.Load(TestFonts.Arial);
        Assert.Equal("Arial", face.Descriptor.FamilyName);
        Assert.True(face.TryGetGlyph('A', out var a));
        Assert.True(face.TryGetGlyph('V', out var v));
        Assert.Equal(-152, face.GetKerning(a, v));   // fontTools：kern 格式 0，A/V = -152
    }

    [SystemFontFact(TestFonts.Geneva)]
    public void System_geneva_format12_only_cmap()
    {
        var face = OpenTypeFace.Load(TestFonts.Geneva);
        Assert.Equal(12, face.Cmap!.Format);
        Assert.Equal(0, face.Cmap.PlatformId);
        Assert.True(face.TryGetGlyph('g', out _));
    }

    [SystemFontFact(TestFonts.StixCff)]
    public void Cff_font_loads_with_cubic_outlines()
    {
        var face = OpenTypeFace.Load(TestFonts.StixCff);
        Assert.Equal(OutlineFormat.Cff, face.OutlineFormat);
        Assert.True(face.TryGetGlyph('A', out var g));
        Assert.True(face.GetAdvance(g) > 0);
        var pb = new AwAUI.Graphics.PathBuilder();
        Assert.True(face.GetOutline(g, default, pb));
        Assert.False(pb.ToPath().IsEmpty);
        Assert.True(face.TryGetGlyphBounds(g, out var bounds));
        Assert.True(bounds.Height > 500 && bounds.Top < 0);
    }

    [Fact]
    public void Synthetic_cff_font_reports_unsupported_outlines()
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());
        f.AddGlyph(Array.Empty<byte>(), 600);
        f.Cmap = SyntheticFont.CmapFormat4((0x41, 1));
        var bytes = f.Build();
        // 把 glyf/loca 改名为无关的表，再加一个 'CFF ' 表：模拟 OTTO 字体
        var tables = new Dictionary<string, byte[]>
        {
            ["head"] = OpenTypeFace.Load(bytes).GetTable(OpenTypeTag.Head).ToArray(),
            ["hhea"] = OpenTypeFace.Load(bytes).GetTable(OpenTypeTag.Hhea).ToArray(),
            ["maxp"] = OpenTypeFace.Load(bytes).GetTable(OpenTypeTag.Maxp).ToArray(),
            ["hmtx"] = OpenTypeFace.Load(bytes).GetTable(OpenTypeTag.Hmtx).ToArray(),
            ["cmap"] = f.Cmap,
            ["CFF "] = new byte[] { 1, 0, 4, 4 },
        };
        var face = OpenTypeFace.Load(SyntheticFont.Assemble(new SortedDictionary<string, byte[]>(tables, StringComparer.Ordinal), 0x4F54544F));
        Assert.Equal(OutlineFormat.Cff, face.OutlineFormat);
        Assert.True(face.TryGetGlyph('A', out var g));
        Assert.Equal(600, face.GetAdvance(g));
        Assert.False(face.GetOutline(g, default, new AwAUI.Graphics.PathBuilder()));
        Assert.False(face.TryGetGlyphBounds(g, out _));
    }

    [Fact]
    public void Woff_is_reported_as_not_supported()
    {
        var woff = new byte[64];
        "wOFF"u8.CopyTo(woff);
        Assert.Throws<NotSupportedException>(() => OpenTypeFace.Load(woff));
        Assert.False(OpenTypeFace.TryLoad(woff, 0, out _));
    }

    static byte[] NameTableWith(params (int Id, string Value)[] names)
    {
        var w = new BeWriter();
        w.U16(0).U16(names.Length).U16(6 + names.Length * 12);
        int offset = 0;
        foreach (var (id, value) in names)
        {
            w.U16(3).U16(1).U16(0x409).U16(id).U16(value.Length * 2).U16(offset);
            offset += value.Length * 2;
        }
        foreach (var (_, value) in names)
            foreach (char c in value) w.U16(c);
        return w.ToArray();
    }

    static byte[] PostTableWith(double italicAngle)
    {
        int fixedAngle = (int)Math.Round(italicAngle * 65536);
        return new BeWriter().U32(0x00030000).U32((uint)fixedAngle).I16(-100).I16(50).U32(0)
            .U32(0).U32(0).U32(0).U32(0).ToArray();
    }

    static FontStyle StyleOf(byte[]? name, byte[]? post)
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());
        if (name is not null) f.ExtraTables["name"] = name;
        if (post is not null) f.ExtraTables["post"] = post;
        return OpenTypeFace.Load(f.Build()).Descriptor.Style;
    }

    [Fact]
    public void Style_falls_back_to_subfamily_name_and_italic_angle_when_flags_are_clear()
    {
        // 合成字体没有 OS/2，head.macStyle 为 0
        Assert.Equal(FontStyle.Normal, StyleOf(NameTableWith((1, "X"), (2, "Medium")), PostTableWith(0)));
        Assert.Equal(FontStyle.Italic, StyleOf(NameTableWith((1, "X"), (2, "Medium Italic")), PostTableWith(0)));
        Assert.Equal(FontStyle.Oblique, StyleOf(NameTableWith((1, "X"), (2, "Bold Oblique")), null));
        // nameID 17 优先于 nameID 2
        Assert.Equal(FontStyle.Italic, StyleOf(NameTableWith((1, "X Light"), (2, "Regular"), (16, "X"), (17, "Light Italic")), null));
        // 名称无法判断时使用 post.italicAngle
        Assert.Equal(FontStyle.Italic, StyleOf(NameTableWith((1, "X"), (2, "Regular")), PostTableWith(-10)));
        Assert.Equal(FontStyle.Normal, StyleOf(null, null));
    }

    [SystemFontFact("/System/Library/Fonts/HelveticaNeue.ttc")]
    public void Helvetica_neue_italic_faces_without_style_bits_are_italic()
    {
        var faces = OpenTypeFace.LoadAll(File.ReadAllBytes("/System/Library/Fonts/HelveticaNeue.ttc"));
        foreach (var face in faces)
        {
            string full = face.Names.FullName ?? string.Empty;
            bool slanted = full.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0 || full.IndexOf("Oblique", StringComparison.OrdinalIgnoreCase) >= 0;
            Assert.True(slanted == (face.Descriptor.Style != FontStyle.Normal), $"{full}: {face.Descriptor}");
        }

        var fonts = new FontCollection();
        fonts.RegisterFile("/System/Library/Fonts/HelveticaNeue.ttc");
        var match = fonts.Match(new FontQuery("Helvetica Neue", new FontWeight(450), FontStyle.Normal))!;
        Assert.Equal(FontStyle.Normal, match.Descriptor.Style);
    }

    [Fact]
    public void Bitmap_only_font_with_bhed_instead_of_head_loads()
    {
        var font = new SyntheticFont();
        font.AddGlyph(Array.Empty<byte>());
        var bytes = font.Build();
        // 把表目录中的 'head' 标记改成 'bhed'（与 Apple 纯位图字体相同）
        for (int i = 12; i + 4 <= bytes.Length; i += 16)
            if (bytes[i] == 'h' && bytes[i + 1] == 'e' && bytes[i + 2] == 'a' && bytes[i + 3] == 'd')
            {
                bytes[i] = (byte)'b'; bytes[i + 1] = (byte)'h'; bytes[i + 2] = (byte)'e'; bytes[i + 3] = (byte)'d';
                break;
            }
        var face = OpenTypeFace.Load(bytes);
        Assert.Equal(1000, face.Metrics.UnitsPerEm);
        Assert.DoesNotContain(OpenTypeTag.Head, face.TableTags);
    }
}
