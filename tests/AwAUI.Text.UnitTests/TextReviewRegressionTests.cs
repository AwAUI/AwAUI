using AwAUI.Graphics;
using AwAUI.Text.OpenType;
using AwAUI.Unicode;

namespace AwAUI.Text.UnitTests;

/// <summary>第八批文本整形复审发现的问题的回归测试：恶意字体的解析与整形时间上限、长段落的操作预算。</summary>
public class TextReviewRegressionTests
{
    // ------------------------------------------------------------------ CFF2：大量字体字典共享同一个很大的 Private DICT

    /// <summary>
    /// CFF2 表：FDArray 有 <paramref name="fdCount"/> 个字典，全部指向同一个由 <paramref name="privatePairs"/> 个 (操作数, StdHW) 组成的 Private DICT。
    /// <paramref name="overlapping"/> 时第 i 个字典指向 Private DICT 的第 i 对之后（各不相同、彼此重叠，按偏移去重无效）。
    /// </summary>
    internal static byte[] Cff2WithSharedPrivate(int fdCount, int privatePairs, bool overlapping)
    {
        static void Int(BeWriter w, int v) => w.U8(29).U32((uint)v);
        const int hdr = 5, topLen = 5 + 1 + 5 + 2 + 5 + 2;
        int globalSubrs = hdr + topLen;
        int charStrings = globalSubrs + 4;
        int charStringsLen = 4 + 1 + 2 + 3;
        int fdSelect = charStrings + charStringsLen;
        int fdArray = fdSelect + 2;
        int fdDataLen = 11;
        int fdArrayLen = 4 + 1 + 4 * (fdCount + 1) + fdDataLen * fdCount;
        int priv = fdArray + fdArrayLen;
        int privLen = 2 * privatePairs;

        var w = new BeWriter();
        w.U8(2).U8(0).U8(hdr).U16(topLen);
        Int(w, charStrings); w.U8(17);
        Int(w, fdArray); w.U8(12).U8(36);
        Int(w, fdSelect); w.U8(12).U8(37);
        w.U32(0);                                             // GlobalSubrs：空
        w.U32(1).U8(1).U8(1).U8(4).U8(139).U8(139).U8(21);    // CharStrings：一个字形 "0 0 rmoveto"
        w.U8(0).U8(0);                                        // FDSelect 格式 0
        w.U32((uint)fdCount).U8(4);
        for (int i = 0; i <= fdCount; i++) w.U32((uint)(1 + i * fdDataLen));
        for (int i = 0; i < fdCount; i++)
        {
            int skip = overlapping ? 2 * (i % privatePairs) : 0;
            Int(w, privLen - skip); Int(w, priv + skip); w.U8(18);
        }
        for (int i = 0; i < privatePairs; i++) w.U8(139).U8(10);   // 0 StdHW
        return w.ToArray();
    }

    /// <summary>用 AwATest-Cff2.otf 的其余表加上替换的 'CFF2' 表组装字体。</summary>
    internal static byte[] WithTable(string baseFont, string tag, byte[] table)
    {
        var font = File.ReadAllBytes(Path.Combine(TestFonts.FontsDir, baseFont));
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        int numTables = (font[4] << 8) | font[5];
        for (int i = 0; i < numTables; i++)
        {
            int r = 12 + i * 16;
            string t = System.Text.Encoding.ASCII.GetString(font, r, 4);
            int off = (font[r + 8] << 24) | (font[r + 9] << 16) | (font[r + 10] << 8) | font[r + 11];
            int len = (font[r + 12] << 24) | (font[r + 13] << 16) | (font[r + 14] << 8) | font[r + 15];
            tables[t] = font.AsSpan(off, len).ToArray();
        }
        tables[tag] = table;
        uint version = (uint)((font[0] << 24) | (font[1] << 16) | (font[2] << 8) | font[3]);
        return SyntheticFont.Assemble(tables, version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cff2_font_dicts_sharing_a_huge_private_dict_parse_in_bounded_time(bool overlapping)
    {
        var bytes = WithTable("AwATest-Cff2.otf", "CFF2", Cff2WithSharedPrivate(4000, 100_000, overlapping));
        var cpu = CpuTimer.Start();
        var face = OpenTypeFace.Load(bytes);
        bool ok = face.GetOutline(0, default, new PathBuilder());
        // 共享同一个 Private DICT：只解析一次，字体可用；彼此重叠的各不相同的 Private DICT：总字节数超出预算，整张表视为损坏
        Assert.Equal(!overlapping, ok);
        // 修复前：每个 FD 都重新解析 200 KB 的 Private DICT（4000 × 100000 次操作，CPU 时间 10–20 秒）
        Assert.True(cpu.Elapsed < TimeSpan.FromSeconds(3), cpu.Elapsed.ToString());
    }

    // ------------------------------------------------------------------ GSUB：大量查找 / 子表共享同一个很大的覆盖表

    /// <summary>
    /// GSUB：'ccmp' 引用 <paramref name="lookups"/> 个查找，全部指向同一个查找表；该查找有 <paramref name="subtables"/> 个子表，
    /// 全部指向同一个单一替换子表，其覆盖表列出 <paramref name="coverage"/> 个字形（2..），不含 'a'（字形 1），但摘要全部命中。
    /// </summary>
    internal static byte[] GsubWithSharedSubtables(int lookups, int subtables, int coverage)
    {
        var w = new BeWriter();
        int scriptList = 10, featureList = scriptList + 2 + 6 + 4 + 8, lookupList = featureList + 2 + 6 + 4 + 2 * lookups;
        w.U32(0x00010000).U16(scriptList).U16(featureList).U16(lookupList);
        w.U16(1).Tag("DFLT").U16(8).U16(4).U16(0).U16(0).U16(0xFFFF).U16(1).U16(0);
        w.U16(1).Tag("ccmp").U16(8).U16(0).U16(lookups);
        for (int i = 0; i < lookups; i++) w.U16(i);
        w.U16(lookups);
        for (int i = 0; i < lookups; i++) w.U16(2 + 2 * lookups);
        w.U16(1).U16(0).U16(subtables);
        for (int i = 0; i < subtables; i++) w.U16(6 + 2 * subtables);
        w.U16(1).U16(6).U16(0);
        w.U16(1).U16(coverage);
        for (int i = 0; i < coverage; i++) w.U16(2 + i);
        return w.ToArray();
    }

    static OpenTypeFace FontWithGsub(byte[] gsub)
    {
        var f = new SyntheticFont();
        f.AddGlyph(Array.Empty<byte>());
        f.AddGlyph(SyntheticFont.Simple(SyntheticFont.Square(0, 0, 100)));
        f.Cmap = SyntheticFont.CmapFormat4(('a', 1));
        f.ExtraTables["GSUB"] = gsub;
        return OpenTypeFace.Load(f.Build());
    }

    [Theory]
    [InlineData(1, 10)]      // 修复前：解析一个查找就要扫描 30000 × 60000 个覆盖条目（约 50 秒）
    [InlineData(2000, 200)]  // 修复前（只限解析时）：每个字形尝试数十个查找 × 30000 个子表（约 30 秒）
    public void Gsub_with_shared_subtables_and_coverage_shapes_in_bounded_time(int lookups, int length)
    {
        var face = FontWithGsub(GsubWithSharedSubtables(lookups, 30000, 60000));
        var text = new string('a', length);
        var cpu = CpuTimer.Start();
        var run = OpenTypeShaper.Instance.Shape(new ShapeRequest(text, face, 16) { Script = Script.Latin });
        var elapsed = cpu.Elapsed;
        Assert.Equal(length, run.Count);
        Assert.All(run.Glyphs.ToArray(), g => Assert.Equal(1, g));
        Assert.True(elapsed < TimeSpan.FromSeconds(5), elapsed.ToString());
    }

    // ------------------------------------------------------------------ 操作预算不能截断真实字体的长段落

    const string DecoTypeNastaleeq = "/System/Library/Fonts/DecoTypeNastaleeqUrdu.ttc";

    [SystemFontFact(DecoTypeNastaleeq)]
    public void Heavy_nastaliq_font_shapes_a_long_paragraph_completely()
    {
        // DecoType Nastaleeq Urdu 每字符约 1600 步操作；旧预算 1024 / 字符在约 860 个字符后耗尽，余下的文本留在未整形的状态
        var face = OpenTypeFace.Load(DecoTypeNastaleeq);
        const string word = "یہ ایک مثال ہے کہ نستعلیق کیسے کام کرتا ہے ";
        var one = OpenTypeShaper.Instance.Shape(new ShapeRequest(word, face, 16) { Script = Script.Arabic, RightToLeft = true });
        var text = string.Concat(Enumerable.Repeat(word, 60));
        var whole = OpenTypeShaper.Instance.Shape(new ShapeRequest(text, face, 16) { Script = Script.Arabic, RightToLeft = true });
        Assert.Equal(60 * one.Count, whole.Count);
        var a = whole.Glyphs.ToArray();
        for (int rep = 0; rep < 60; rep++) Assert.Equal(one.Glyphs.ToArray(), a.AsSpan(rep * one.Count, one.Count).ToArray());
    }
}
