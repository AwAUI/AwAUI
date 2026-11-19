using System.Diagnostics;
using System.Globalization;
using AwAUI.Text.OpenType;
using AwAUI.Tests.Shared;
using AwAUI.Unicode;

namespace AwAUI.Text.UnitTests;

/// <summary>
/// <see cref="OpenTypeShaper"/>：与 HarfBuzz 的期望输出逐字形比对（自制测试字体覆盖 GSUB 1–8、GPOS 1–9、查找标志、阿拉伯连写与标记），
/// 以及恶意 / 损坏布局表、范围特性的复杂度与系统字体（存在时）的检查。
/// </summary>
public class OpenTypeShaperTests
{
    static readonly Dictionary<string, OpenTypeFace> s_faces = new();

    internal static OpenTypeFace Face(string name)
    {
        lock (s_faces)
        {
            if (!s_faces.TryGetValue(name, out var f)) s_faces[name] = f = OpenTypeFace.Load(System.IO.Path.Combine(TestFonts.FontsDir, name));
            return f;
        }
    }

    /// <summary>hb-shape 风格的特性字符串："-liga,salt=2,kern[2:5]=0"。</summary>
    internal static FontFeature[] ParseFeatures(string s)
    {
        var list = new List<FontFeature>();
        foreach (var item in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = item;
            uint value = 1;
            if (t.StartsWith("-")) { value = 0; t = t.Substring(1); }
            else if (t.StartsWith("+")) t = t.Substring(1);
            int eq = t.IndexOf('=');
            if (eq >= 0)
            {
                value = uint.Parse(t.Substring(eq + 1), CultureInfo.InvariantCulture);
                t = t.Substring(0, eq);
            }
            int start = 0, end = int.MaxValue;
            int br = t.IndexOf('[');
            if (br >= 0)
            {
                var range = t.Substring(br + 1, t.Length - br - 2).Split(':');
                start = int.Parse(range[0], CultureInfo.InvariantCulture);
                end = int.Parse(range[1], CultureInfo.InvariantCulture);
                t = t.Substring(0, br);
            }
            list.Add(new FontFeature(t, value, start, end));
        }
        return list.ToArray();
    }

    /// <summary>与 dump_test_fonts.py 相同的格式：字形=簇@累加 x + 偏移, y | 总前进量（设计单位）。</summary>
    internal static string Format(ShapedRun run)
    {
        float x = 0;
        var parts = new List<string>();
        for (int i = 0; i < run.Count; i++)
        {
            var o = run.Offsets[i];
            parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1}@{2},{3}", run.Glyphs[i], run.Clusters[i],
                Math.Round(x + o.X) + 0.0, Math.Round(-o.Y) + 0.0));
            x += run.Advances[i].X;
        }
        return string.Join(" ", parts) + " |" + Math.Round(x).ToString(CultureInfo.InvariantCulture);
    }

    internal static ShapedRun Shape(OpenTypeFace face, string text, string script = "Latn", string lang = "", bool rtl = false, string features = "")
    {
        UnicodeData.TryParseScriptCode(script.AsSpan(), out var sc);
        return OpenTypeShaper.Instance.Shape(new ShapeRequest(text, face, face.Metrics.UnitsPerEm)
        {
            Script = sc,
            Language = lang.Length > 0 ? lang : null,
            RightToLeft = rtl,
            Features = ParseFeatures(features),
        });
    }

    public static IEnumerable<object[]> HarfBuzzCases()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Expected", "AwATest-shaping.txt");
        foreach (var line in File.ReadAllLines(path))
        {
            var p = line.Split('\t');
            yield return new object[] { p[0], p[1], p[2], p[3], p[4], Uri.UnescapeDataString(p[5]), p[6] };
        }
    }

    [Theory]
    [MemberData(nameof(HarfBuzzCases))]
    public void Matches_harfbuzz(string font, string script, string lang, string dir, string features, string text, string expected)
    {
        var run = Shape(Face(font), text, script, lang, dir == "rtl", features);
        Assert.Equal(expected, Format(run));
    }

    [Fact]
    public void Ligature_merges_clusters_and_marks_follow_components()
    {
        var face = Face("AwATest-Layout.ttf");
        // f + 组合重音 + f + i：IgnoreMarks 的连字跨过标记；标记保留，簇合并为 0
        var run = Shape(face, "f́fi");
        Assert.Equal(2, run.Count);
        Assert.Equal(new[] { 0, 0 }, run.Clusters.ToArray());
    }

    [Fact]
    public void Ranged_features_are_linear_in_the_number_of_settings()
    {
        // 每个字符一个范围设置（CSS 逐 span 的 font-feature-settings）：旧实现每个字形扫描全部设置，为二次方
        var face = Face("AwATest-Layout.ttf");
        const int n = 20000;
        var text = new string('a', n);
        var features = new FontFeature[n];
        for (int i = 0; i < n; i++) features[i] = new FontFeature("salt", (uint)(i % 3), i, i + 1);
        var request = new ShapeRequest(text, face, 1000) { Script = Script.Latin, Features = features };
        var cpu = CpuTimer.Start();
        var run = OpenTypeShaper.Instance.Shape(request);
        var elapsed = cpu.Elapsed;
        Assert.Equal(n, run.Count);
        Assert.True(face.TryGetGlyph('a', out var a));
        Assert.Equal(a, run.Glyphs[0]);           // salt=0
        Assert.Equal(a + 1, run.Glyphs[1]);       // salt=1 → a.alt1
        Assert.Equal(a + 2, run.Glyphs[2]);       // salt=2 → a.alt2
        Assert.True(elapsed < TimeSpan.FromSeconds(5), elapsed.ToString());

        var basic = BasicTextShaper.Instance.Shape(new ShapeRequest(text, face, 1000)
        {
            Features = Enumerable.Range(0, n).Select(i => new FontFeature("kern", (uint)(i & 1), i, i + 1)).ToArray(),
        });
        Assert.Equal(n, basic.Count);
    }

    [Fact]
    public void Corrupted_layout_tables_never_throw_and_stay_bounded()
    {
        var original = File.ReadAllBytes(System.IO.Path.Combine(TestFonts.FontsDir, "AwATest-Layout.ttf"));
        var tables = new List<(int Offset, int Length)>();
        foreach (var tag in new[] { "GSUB", "GPOS", "GDEF" })
            tables.Add(FindTable(original, OpenTypeTag.Make(tag)));
        var rng = new Random(42);
        var texts = new[] { "Ä ffi f́fi a b c d bcd xy ce cc dcd exe ToT oo", "بَّبِ لا بلا" };
        var cpu = CpuTimer.Start();
        for (int iter = 0; iter < 400; iter++)
        {
            var bytes = (byte[])original.Clone();
            var (off, len) = tables[rng.Next(tables.Count)];
            int edits = rng.Next(1, 6);
            for (int e = 0; e < edits; e++) bytes[off + rng.Next(len)] = (byte)rng.Next(256);
            if (!OpenTypeFace.TryLoad(bytes, 0, out var face)) continue;
            foreach (var t in texts)
            {
                bool rtl = t[0] >= 0x600;
                var run = OpenTypeShaper.Instance.Shape(new ShapeRequest(t, face, 16) { Script = rtl ? Script.Arabic : Script.Latin, RightToLeft = rtl });
                Assert.True(run.Count <= 8 * t.Length + 4096);
                foreach (var c in run.Clusters) Assert.InRange(c, 0, t.Length - 1);
            }
        }
        Assert.True(cpu.Elapsed < TimeSpan.FromSeconds(30), cpu.Elapsed.ToString());
    }

    static (int, int) FindTable(byte[] font, uint tag)
    {
        int numTables = (font[4] << 8) | font[5];
        for (int i = 0; i < numTables; i++)
        {
            int r = 12 + i * 16;
            uint t = (uint)((font[r] << 24) | (font[r + 1] << 16) | (font[r + 2] << 8) | font[r + 3]);
            if (t != tag) continue;
            int off = (font[r + 8] << 24) | (font[r + 9] << 16) | (font[r + 10] << 8) | font[r + 11];
            int len = (font[r + 12] << 24) | (font[r + 13] << 16) | (font[r + 14] << 8) | font[r + 15];
            return (off, len);
        }
        throw new InvalidOperationException("table not found");
    }

    [Fact]
    public void Long_text_shapes_in_linear_time()
    {
        var face = Face("AwATest-Layout.ttf");
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 200_000) sb.Append("bcd ffi á̀ ToT oo ");
        var text = sb.ToString();
        var cpu = CpuTimer.Start();
        var run = OpenTypeShaper.Instance.Shape(new ShapeRequest(text, face, 16) { Script = Script.Latin });
        Assert.True(run.Count > 100_000);
        Assert.True(cpu.Elapsed < TimeSpan.FromSeconds(10), cpu.Elapsed.ToString());

        var arabic = string.Concat(Enumerable.Repeat("بَّبِب", 30000));
        cpu = CpuTimer.Start();
        run = OpenTypeShaper.Instance.Shape(new ShapeRequest(arabic, face, 16) { Script = Script.Arabic, RightToLeft = true });
        Assert.Equal(arabic.Length, run.Count);
        Assert.True(cpu.Elapsed < TimeSpan.FromSeconds(10), cpu.Elapsed.ToString());
    }

    [Fact]
    public void Boundary_kerning_uses_gpos_pairs_and_respects_features()
    {
        var face = Face("AwATest-Layout.ttf");
        Assert.True(face.TryGetGlyph(' ', out var space));
        Assert.True(face.TryGetGlyph('T', out var t));
        Assert.True(face.TryGetGlyph('o', out var o));
        var req = new ShapeRequest(ReadOnlySpan<char>.Empty, face, 1000) { Script = Script.Latin };
        Assert.Equal(-50, OpenTypeShaper.Instance.GetBoundaryKerning(req, space, t), 3);
        Assert.Equal(-70, OpenTypeShaper.Instance.GetBoundaryKerning(req, t, o), 3);
        var off = new ShapeRequest(ReadOnlySpan<char>.Empty, face, 1000) { Script = Script.Latin, Features = new[] { new FontFeature("kern", 0) } };
        Assert.Equal(0, OpenTypeShaper.Instance.GetBoundaryKerning(off, t, o));
        var rtl = new ShapeRequest(ReadOnlySpan<char>.Empty, face, 1000) { Script = Script.Latin, RightToLeft = true };
        Assert.Equal(0, OpenTypeShaper.Instance.GetBoundaryKerning(rtl, t, o));
    }

    [Fact]
    public void Non_opentype_faces_fall_back_to_the_basic_shaper()
    {
        var box = new Testing.BoxFontFace();
        var run = OpenTypeShaper.Instance.Shape(new ShapeRequest("ab", box, 10));
        Assert.Equal(2, run.Count);
        Assert.Equal(20, run.Width, 3);
    }

    // ------------------------------------------------------------------ 系统字体（macOS）

    const string SfArabic = "/System/Library/Fonts/SFArabic.ttf";
    const string GeezaPro = "/System/Library/Fonts/GeezaPro.ttc";
    const string Hiragino = CffTests.HiraginoSans;

    [SystemFontFact(SfArabic)]
    public void System_arabic_font_joins_and_ligates()
    {
        var face = OpenTypeFace.Load(SfArabic);
        // 同一个 beh 在词首、词中、词尾、孤立时得到四个不同的字形（SF Arabic 的 ccmp 把点拆成独立的标记字形，只看有前进量的字形）
        var run = Shape(face, "\u0628\u0628\u0628 \u0628", "Arab", rtl: true);
        var bases = new Dictionary<int, ushort>();
        for (int i = 0; i < run.Count; i++)
            if (run.Advances[i].X > 0 && run.Clusters[i] != 3) bases[run.Clusters[i]] = run.Glyphs[i];
        Assert.Equal(4, bases.Count);
        Assert.Equal(4, bases.Values.Distinct().Count());
        // lam + alef → 一个连字（rlig），簇值合并
        run = Shape(face, "لا", "Arab", rtl: true);
        Assert.Equal(1, run.Count);
        // 标记有零前进量并被 GPOS 定位到基字形上方
        run = Shape(face, "بَ", "Arab", rtl: true);
        Assert.Equal(2, run.Count);
        Assert.Contains(run.Advances.ToArray(), a => a.X == 0);
        Assert.Contains(run.Offsets.ToArray(), o => o.Y < 0);
    }

    [SystemFontFact(GeezaPro)]
    public void Arabic_without_gsub_uses_presentation_form_fallback()
    {
        var face = OpenTypeFace.Load(GeezaPro);
        var run = Shape(face, "ببب", "Arab", rtl: true);
        Assert.True(face.TryGetGlyph(0xFE91, out var init));
        Assert.True(face.TryGetGlyph(0xFE92, out var medi));
        Assert.True(face.TryGetGlyph(0xFE90, out var fina));
        Assert.Equal(new[] { fina, medi, init }, run.Glyphs.ToArray());
        run = Shape(face, "لا", "Arab", rtl: true);
        Assert.True(face.TryGetGlyph(0xFEFB, out var lamAlef));
        Assert.Equal(new[] { lamAlef }, run.Glyphs.ToArray());
    }

    [SystemFontFact(Hiragino)]
    public void Cff_cjk_font_shapes_with_gpos_features()
    {
        var face = OpenTypeFace.Load(Hiragino);
        var plain = Shape(face, "「引用」。", "Hani", "ja");
        var palt = Shape(face, "「引用」。", "Hani", "ja", features: "palt");
        Assert.Equal(plain.Count, palt.Count);
        Assert.True(palt.Width < plain.Width - 300, $"{palt.Width} vs {plain.Width}");
    }
}

/// <summary>线程 CPU 时间计时（机器负载高时比墙钟稳定，见 tests/Shared/ThreadCpuClock.cs）。</summary>
readonly struct CpuTimer
{
    readonly double _start;
    CpuTimer(double start) => _start = start;
    public static CpuTimer Start() => new(ThreadCpuClock.NowMs());
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ThreadCpuClock.NowMs() - _start);
}
