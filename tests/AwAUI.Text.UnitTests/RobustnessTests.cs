using AwAUI.Graphics;
using AwAUI.Text.OpenType;

namespace AwAUI.Text.UnitTests;

/// <summary>截断与损坏的字体数据：加载要么成功，要么抛出 InvalidFontException；加载成功后的任何查询都不能抛出异常。</summary>
public class RobustnessTests
{
    static readonly char[] SampleText = "Hello AV Å€ﬁ中😀".ToCharArray();

    /// <summary>返回 true 表示加载成功并完成了全部查询。</summary>
    static bool Exercise(byte[] data)
    {
        int faces;
        try
        {
            faces = OpenTypeFace.GetFaceCount(data);
        }
        catch (InvalidFontException)
        {
            return false;
        }
        bool any = false;
        for (int i = 0; i < Math.Min(faces, 4); i++)
        {
            OpenTypeFace face;
            try
            {
                face = OpenTypeFace.Load(data, i);
            }
            catch (InvalidFontException)
            {
                continue;
            }
            any = true;
            _ = face.Descriptor.ToString();
            _ = face.Metrics.LineHeight;
            _ = face.Names.FullName;
            foreach (var c in SampleText) face.TryGetGlyph(c, out _);
            for (int cp = 0; cp < 0x3000; cp += 13) face.TryGetGlyph(cp, out _);
            face.TryGetGlyph(0x1F600, out _);
            int n = Math.Min(face.GlyphCount, 1500);
            var pb = new PathBuilder();
            for (int g = 0; g < n; g++)
            {
                face.GetAdvance((ushort)g);
                face.GetLeftSideBearing((ushort)g);
                face.TryGetGlyphBounds((ushort)g, out _);
                face.GetOutline((ushort)g, default, pb);
                face.GetKerning((ushort)g, (ushort)(g + 1));
            }
            pb.ToPath();
            var run = BasicTextShaper.Instance.Shape(new ShapeRequest(SampleText, face, 16));
            run.AppendOutlines(new PathBuilder(), new Point(0, 16));
        }
        return any;
    }

    [Theory]
    [InlineData(TestFonts.Abel, 97)]
    [InlineData(TestFonts.NotoSans, 4099)]
    public void Truncated_fonts_never_crash(string file, int step)
    {
        var full = TestFonts.Bytes(file);
        int loaded = 0;
        for (int len = 0; len < full.Length; len += len < 400 ? 1 : step)
            if (Exercise(full.AsSpan(0, len).ToArray())) loaded++;
        Assert.True(Exercise(full));
        // 只截掉末尾若干个表时 head/hhea/maxp 仍在，字体可以部分加载
        Assert.True(loaded > 0);
    }

    [Theory]
    [InlineData(TestFonts.Abel, 400)]
    [InlineData(TestFonts.NotoSans, 60)]
    public void Randomly_corrupted_fonts_never_crash(string file, int iterations)
    {
        var full = TestFonts.Bytes(file);
        var rng = new Random(12345);
        int loaded = 0;
        for (int it = 0; it < iterations; it++)
        {
            var data = (byte[])full.Clone();
            int flips = 1 + rng.Next(64);
            for (int k = 0; k < flips; k++) data[rng.Next(data.Length)] = (byte)rng.Next(256);
            if (Exercise(data)) loaded++;
        }
        Assert.True(loaded > iterations / 2, $"most randomly corrupted fonts should still load ({loaded}/{iterations})");
    }

    [Theory]
    [InlineData("glyf")]
    [InlineData("loca")]
    [InlineData("cmap")]
    [InlineData("kern")]
    [InlineData("hmtx")]
    [InlineData("name")]
    [InlineData("OS/2")]
    [InlineData("post")]
    [InlineData("head")]
    [InlineData("hhea")]
    [InlineData("maxp")]
    public void Corrupted_tables_never_crash(string tag)
    {
        var full = TestFonts.Bytes(TestFonts.Abel);
        var face = OpenTypeFace.Load(full);
        var table = face.GetTable(OpenTypeTag.Make(tag));
        Assert.False(table.IsEmpty);
        // 找到表在文件中的位置
        int start = IndexOf(full, table.ToArray());
        var rng = new Random(StringComparer.Ordinal.GetHashCode(tag));
        for (int it = 0; it < 150; it++)
        {
            var data = (byte[])full.Clone();
            int flips = 1 + rng.Next(Math.Min(32, table.Length));
            for (int k = 0; k < flips; k++)
            {
                int pos = start + rng.Next(Math.Min(table.Length, it % 3 == 0 ? 64 : table.Length));
                data[pos] = (byte)(it % 5 == 0 ? 0xFF : rng.Next(256));
            }
            Exercise(data);
        }
    }

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        throw new InvalidOperationException("table not found");
    }

    [Fact]
    public void Garbage_and_tiny_inputs_throw_invalid_font()
    {
        Assert.Throws<InvalidFontException>(() => OpenTypeFace.Load(Array.Empty<byte>()));
        Assert.Throws<InvalidFontException>(() => OpenTypeFace.Load(new byte[] { 0, 1, 0, 0 }));
        Assert.Throws<InvalidFontException>(() => OpenTypeFace.Load(new byte[100]));
        var ttcf = new byte[16];
        "ttcf"u8.CopyTo(ttcf);
        ttcf[8] = 0x7F;   // 声明了海量字型
        Assert.Throws<InvalidFontException>(() => OpenTypeFace.GetFaceCount(ttcf));
        // 表目录中的偏移指向文件之外：必需表缺失
        var bad = SyntheticFont.Assemble(new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["head"] = new byte[54] });
        bad[12 + 8] = 0x7F;
        Assert.Throws<InvalidFontException>(() => OpenTypeFace.Load(bad));
    }

    [Fact]
    public void Every_prefix_of_a_synthetic_font_is_handled()
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());
        int sq = f.AddGlyph(SyntheticFont.Simple(SyntheticFont.Square(0, 0, 100), new[] { (0, 0, false), (50, 80, false), (90, 10, true) }));
        f.AddGlyph(SyntheticFont.Composite(new Component(sq, 10, 10, Transform: new[] { 0.5f, 0.1f, -0.1f, 0.5f }), new Component(sq, 3, 1, XY: false)));
        f.Cmap = SyntheticFont.CmapFormat4((0x41, 1), (0x42, 2));
        var full = f.Build();
        for (int len = 0; len <= full.Length; len++) Exercise(full.AsSpan(0, len).ToArray());
        // 逐字节破坏
        for (int pos = 0; pos < full.Length; pos++)
        {
            var data = (byte[])full.Clone();
            data[pos] ^= 0xFF;
            Exercise(data);
        }
    }
}
