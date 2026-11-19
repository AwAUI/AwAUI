using System.Globalization;

namespace AwAUI.Unicode.UnitTests;

public class PropertyTests
{
    [Fact]
    public void Version_is_18()
    {
        Assert.Equal("18.0.0", UnicodeData.UnicodeVersion);
    }

    /// <summary>Unicode 18 相对运行时数据改过 General_Category 的已分配字符（逐一核对过 UnicodeData.txt）。</summary>
    static readonly HashSet<int> KnownGeneralCategoryChanges = new()
    {
        0x0295, // LATIN LETTER PHARYNGEAL VOICED FRICATIVE：Ll → Lo（Unicode 18）
    };

#if NET
    /// <summary>
    /// 与运行时自带的 General_Category 对照（.NET Framework 的 Unicode 数据过旧，且没有按码位查询的重载，不参与）。运行时的 Unicode 版本较旧：只比较运行时认为已分配的码位，
    /// 不一致的码位必须恰好是上面列出的、在新版本中改过类别的字符。
    /// </summary>
    [Fact]
    public void GeneralCategory_matches_runtime_where_assigned()
    {
        var mismatches = new List<string>();
        int compared = 0;
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            var expected = CharUnicodeInfo.GetUnicodeCategory(cp);
            if (expected == UnicodeCategory.OtherNotAssigned) continue;
            compared++;
            var actual = UnicodeData.GetGeneralCategory(cp);
            if ((int)actual != (int)expected && !KnownGeneralCategoryChanges.Contains(cp))
                mismatches.Add($"U+{cp:X4}: runtime {expected}, ours {actual}");
        }
        Assert.True(compared > 280_000, $"compared {compared}");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(50)) + $"\n({mismatches.Count} total)");
    }

#endif

    /// <summary>每个码位的每个属性都落在枚举定义的范围内（打包位域未串位）。</summary>
    [Fact]
    public void All_code_points_have_defined_property_values()
    {
        static int Count<T>() where T : struct, Enum => Enum.GetValues(typeof(T)).Length;
        int gc = Count<GeneralCategory>(), sc = Count<Script>(), lb = Count<LineBreakClass>(), gcb = Count<GraphemeClusterBreak>();
        int wb = Count<WordBreak>(), sb = Count<SentenceBreak>(), ea = Count<EastAsianWidth>(), bc = Count<BidiClass>();
        int jt = Count<JoiningType>(), jg = Count<JoiningGroup>(), insc = Count<IndicSyllabicCategory>(), inpc = Count<IndicPositionalCategory>();
        int vo = Count<VerticalOrientation>(), incb = Count<IndicConjunctBreak>(), bpt = Count<BidiPairedBracketType>();
        Span<Script> scx = stackalloc Script[64];
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            Assert.True((int)UnicodeData.GetGeneralCategory(cp) < gc);
            Assert.True((int)UnicodeData.GetScript(cp) < sc);
            Assert.True((int)UnicodeData.GetLineBreakClass(cp) < lb);
            Assert.True((int)UnicodeData.GetGraphemeClusterBreak(cp) < gcb);
            Assert.True((int)UnicodeData.GetWordBreak(cp) < wb);
            Assert.True((int)UnicodeData.GetSentenceBreak(cp) < sb);
            Assert.True((int)UnicodeData.GetEastAsianWidth(cp) < ea);
            Assert.True((int)UnicodeData.GetBidiClass(cp) < bc);
            Assert.True((int)UnicodeData.GetJoiningType(cp) < jt);
            Assert.True((int)UnicodeData.GetJoiningGroup(cp) < jg);
            Assert.True((int)UnicodeData.GetIndicSyllabicCategory(cp) < insc);
            Assert.True((int)UnicodeData.GetIndicPositionalCategory(cp) < inpc);
            Assert.True((int)UnicodeData.GetVerticalOrientation(cp) < vo);
            Assert.True((int)UnicodeData.GetIndicConjunctBreak(cp) < incb);
            Assert.True((int)UnicodeData.GetBidiPairedBracketType(cp) < bpt);
            int n = UnicodeData.GetScriptExtensions(cp, scx);
            Assert.True(n >= 1);
            Assert.True(CodePoint.IsValid(UnicodeData.ToUpper(cp)) && CodePoint.IsValid(UnicodeData.ToLower(cp)) && CodePoint.IsValid(UnicodeData.ToTitle(cp)));
            int mirror = UnicodeData.GetBidiMirroringGlyph(cp);
            Assert.True(mirror == cp || UnicodeData.GetBidiMirroringGlyph(mirror) == cp || UnicodeData.IsBidiMirrored(mirror));
        }
    }

    [Fact]
    public void Out_of_range_values_are_unassigned()
    {
        Assert.Equal(GeneralCategory.Unassigned, UnicodeData.GetGeneralCategory(-1));
        Assert.Equal(GeneralCategory.Unassigned, UnicodeData.GetGeneralCategory(0x110000));
        Assert.Equal(Script.Unknown, UnicodeData.GetScript(int.MaxValue));
        Assert.Equal(LineBreakClass.XX, UnicodeData.GetLineBreakClass(-5));
        Assert.Equal(0x110000, UnicodeData.ToUpper(0x110000));
    }

    [Theory]
    [InlineData('A', Script.Latin)]
    [InlineData(0x4E00, Script.Han)]
    [InlineData(0x0627, Script.Arabic)]
    [InlineData(0x0300, Script.Inherited)]
    [InlineData(' ', Script.Common)]
    [InlineData(0x0378, Script.Unknown)]
    [InlineData(0x3042, Script.Hiragana)]
    public void Script_lookup(int cp, Script expected) => Assert.Equal(expected, UnicodeData.GetScript(cp));

    [Fact]
    public void Script_codes_and_extensions()
    {
        Assert.Equal("Latn", UnicodeData.GetScriptCode(Script.Latin));
        Assert.Equal("Zzzz", UnicodeData.GetScriptCode(Script.Unknown));
        Assert.Equal(0x4C61746Eu, UnicodeData.GetScriptTag(Script.Latin));
        Assert.True(UnicodeData.TryParseScriptCode("hani".AsSpan(), out var han));
        Assert.Equal(Script.Han, han);
        Assert.False(UnicodeData.TryParseScriptCode("Qqqq".AsSpan(), out _));

        Span<Script> buffer = stackalloc Script[16];
        int n = UnicodeData.GetScriptExtensions(0x0640, buffer); // ARABIC TATWEEL
        Assert.Equal(9, n);
        Assert.Contains(Script.Arabic, buffer.Slice(0, n).ToArray());
        Assert.Contains(Script.Syriac, buffer.Slice(0, n).ToArray());
        Assert.True(UnicodeData.HasScript(0x0640, Script.Adlam));
        Assert.False(UnicodeData.HasScript(0x0640, Script.Latin));
        Assert.Equal(1, UnicodeData.GetScriptExtensions('A', buffer));
        Assert.Equal(Script.Latin, buffer[0]);
        Assert.True(UnicodeData.HasScript('A', Script.Latin));
        Assert.Equal(-9, UnicodeData.GetScriptExtensions(0x0640, buffer.Slice(0, 2)));
    }

    [Theory]
    [InlineData(0x00AD, LineBreakClass.HH)]   // Unicode 18：BA → HH
    [InlineData(0x2013, LineBreakClass.BA)]   // Unicode 18：HH → BA
    [InlineData(0x2014, LineBreakClass.B2)]
    [InlineData(' ', LineBreakClass.SP)]
    [InlineData('\n', LineBreakClass.LF)]
    [InlineData(0x4E00, LineBreakClass.ID)]
    [InlineData(0x0E01, LineBreakClass.SA)]
    [InlineData(0x3041, LineBreakClass.CJ)]
    [InlineData(0x1F1E6, LineBreakClass.RI)]
    [InlineData(0x200D, LineBreakClass.ZWJ)]
    [InlineData(0x3FFFD, LineBreakClass.ID)]  // 未分配的 CJK 平面默认 ID
    public void LineBreak_lookup(int cp, LineBreakClass expected) => Assert.Equal(expected, UnicodeData.GetLineBreakClass(cp));

    [Theory]
    [InlineData(0x3000, EastAsianWidth.Fullwidth)]
    [InlineData(0xFF61, EastAsianWidth.Halfwidth)]
    [InlineData('A', EastAsianWidth.Narrow)]
    [InlineData(0x4E00, EastAsianWidth.Wide)]
    [InlineData(0x00A1, EastAsianWidth.Ambiguous)]
    [InlineData(0x0100, EastAsianWidth.Neutral)]
    public void EastAsianWidth_lookup(int cp, EastAsianWidth expected) => Assert.Equal(expected, UnicodeData.GetEastAsianWidth(cp));

    [Fact]
    public void Segmentation_properties()
    {
        Assert.Equal(GraphemeClusterBreak.CR, UnicodeData.GetGraphemeClusterBreak('\r'));
        Assert.Equal(GraphemeClusterBreak.Extend, UnicodeData.GetGraphemeClusterBreak(0x0300));
        Assert.Equal(GraphemeClusterBreak.ZWJ, UnicodeData.GetGraphemeClusterBreak(0x200D));
        Assert.Equal(GraphemeClusterBreak.L, UnicodeData.GetGraphemeClusterBreak(0x1100));
        Assert.Equal(GraphemeClusterBreak.LV, UnicodeData.GetGraphemeClusterBreak(0xAC00));
        Assert.Equal(GraphemeClusterBreak.LVT, UnicodeData.GetGraphemeClusterBreak(0xAC01));
        Assert.Equal(GraphemeClusterBreak.SpacingMark, UnicodeData.GetGraphemeClusterBreak(0x0E33));
        Assert.Equal(GraphemeClusterBreak.Prepend, UnicodeData.GetGraphemeClusterBreak(0x0600));
        Assert.Equal(IndicConjunctBreak.Linker, UnicodeData.GetIndicConjunctBreak(0x094D));
        Assert.Equal(IndicConjunctBreak.Consonant, UnicodeData.GetIndicConjunctBreak(0x0915));
        Assert.Equal(IndicConjunctBreak.Extend, UnicodeData.GetIndicConjunctBreak(0x200D));
        Assert.Equal(IndicConjunctBreak.None, UnicodeData.GetIndicConjunctBreak('a'));
        Assert.Equal(WordBreak.ALetter, UnicodeData.GetWordBreak('a'));
        Assert.Equal(WordBreak.Numeric, UnicodeData.GetWordBreak('0'));
        Assert.Equal(WordBreak.SingleQuote, UnicodeData.GetWordBreak('\''));
        Assert.Equal(WordBreak.MidNumLet, UnicodeData.GetWordBreak('.'));
        Assert.Equal(WordBreak.MidNum, UnicodeData.GetWordBreak(','));
        Assert.Equal(WordBreak.MidLetter, UnicodeData.GetWordBreak(':'));
        Assert.Equal(WordBreak.ExtendNumLet, UnicodeData.GetWordBreak('_'));
        Assert.Equal(WordBreak.WSegSpace, UnicodeData.GetWordBreak(' '));
        Assert.Equal(WordBreak.Katakana, UnicodeData.GetWordBreak(0x30A2));
        Assert.Equal(WordBreak.HebrewLetter, UnicodeData.GetWordBreak(0x05D0));
        Assert.Equal(SentenceBreak.ATerm, UnicodeData.GetSentenceBreak('.'));
        Assert.Equal(SentenceBreak.STerm, UnicodeData.GetSentenceBreak('!'));
        Assert.Equal(SentenceBreak.Lower, UnicodeData.GetSentenceBreak('a'));
        Assert.Equal(SentenceBreak.Upper, UnicodeData.GetSentenceBreak('A'));
    }

    [Fact]
    public void Emoji_and_binary_properties()
    {
        Assert.True(UnicodeData.IsExtendedPictographic(0x1F600));
        Assert.True(UnicodeData.IsExtendedPictographic(0x00A9));
        Assert.False(UnicodeData.IsExtendedPictographic('A'));
        Assert.True(UnicodeData.IsEmojiPresentation(0x1F600));
        Assert.False(UnicodeData.IsEmojiPresentation(0x263A));
        Assert.True(UnicodeData.IsEmoji(0x263A));
        Assert.True(UnicodeData.IsEmojiModifier(0x1F3FB));
        Assert.True(UnicodeData.IsEmojiModifierBase(0x1F44D));
        Assert.True(UnicodeData.IsEmojiComponent('#'));
        Assert.True(UnicodeData.IsDefaultIgnorable(0x200B));
        Assert.True(UnicodeData.IsDefaultIgnorable(0x00AD));
        Assert.False(UnicodeData.IsDefaultIgnorable('a'));
        Assert.True(UnicodeData.IsWhiteSpace(' '));
        Assert.True(UnicodeData.IsWhiteSpace(0x3000));
        Assert.False(UnicodeData.IsWhiteSpace(0x200B));
    }

    [Theory]
    [InlineData('A', BidiClass.L)]
    [InlineData(0x05D0, BidiClass.R)]
    [InlineData(0x0627, BidiClass.AL)]
    [InlineData('1', BidiClass.EN)]
    [InlineData(0x0661, BidiClass.AN)]
    [InlineData(',', BidiClass.CS)]
    [InlineData(0x202E, BidiClass.RLO)]
    [InlineData(0x2067, BidiClass.RLI)]
    [InlineData(0x0300, BidiClass.NSM)]
    [InlineData(0x0590, BidiClass.R)]      // 未分配，来自 @missing 默认值
    [InlineData(0x20C9, BidiClass.ET)]     // 货币符号区的默认值
    public void BidiClass_lookup(int cp, BidiClass expected) => Assert.Equal(expected, UnicodeData.GetBidiClass(cp));

    [Fact]
    public void Bidi_mirroring_and_brackets()
    {
        Assert.Equal(')', UnicodeData.GetBidiMirroringGlyph('('));
        Assert.Equal('>', UnicodeData.GetBidiMirroringGlyph('<'));
        Assert.Equal(0x220B, UnicodeData.GetBidiMirroringGlyph(0x2208));
        Assert.Equal('A', UnicodeData.GetBidiMirroringGlyph('A'));
        Assert.True(UnicodeData.IsBidiMirrored('('));
        Assert.False(UnicodeData.IsBidiMirrored('A'));
        Assert.Equal(BidiPairedBracketType.Open, UnicodeData.GetBidiPairedBracketType('('));
        Assert.Equal(BidiPairedBracketType.Close, UnicodeData.GetBidiPairedBracketType(']'));
        Assert.Equal(BidiPairedBracketType.None, UnicodeData.GetBidiPairedBracketType('<'));
        Assert.Equal('[', UnicodeData.GetBidiPairedBracket(']'));
        Assert.Equal(0x232A, UnicodeData.GetBidiPairedBracket(0x2329));
        Assert.Equal('<', UnicodeData.GetBidiPairedBracket('<'));
    }

    [Fact]
    public void Canonical_combining_class()
    {
        Assert.Equal(0, UnicodeData.GetCombiningClass('a'));
        Assert.Equal(230, UnicodeData.GetCombiningClass(0x0301));   // 上方
        Assert.Equal(220, UnicodeData.GetCombiningClass(0x0323));   // 下方
        Assert.Equal(1, UnicodeData.GetCombiningClass(0x0334));     // 叠加
        Assert.Equal(30, UnicodeData.GetCombiningClass(0x064E));    // fatha
        Assert.Equal(33, UnicodeData.GetCombiningClass(0x0651));    // shadda
        Assert.Equal(18, UnicodeData.GetCombiningClass(0x05B8));    // qamats
        Assert.Equal(9, UnicodeData.GetCombiningClass(0x094D));     // virama
        Assert.Equal(0, UnicodeData.GetCombiningClass(0x0915));
        Assert.Equal(0, UnicodeData.GetCombiningClass(-1));
        Assert.Equal(0, UnicodeData.GetCombiningClass(0x110000));
        // 与 .NET 的规范分解一致：分解后的标记序列按组合类非降序排列
        var nfd = "\u1EC7".Normalize(System.Text.NormalizationForm.FormD);    // ệ = e + 0323 + 0302
        Assert.True(UnicodeData.GetCombiningClass(nfd[1]) <= UnicodeData.GetCombiningClass(nfd[2]));
    }

    [Fact]
    public void Shaping_properties()
    {
        Assert.Equal(JoiningType.DualJoining, UnicodeData.GetJoiningType(0x0628));
        Assert.Equal(JoiningType.RightJoining, UnicodeData.GetJoiningType(0x0627));
        Assert.Equal(JoiningType.JoinCausing, UnicodeData.GetJoiningType(0x200D));
        Assert.Equal(JoiningType.JoinCausing, UnicodeData.GetJoiningType(0x0640));
        Assert.Equal(JoiningType.Transparent, UnicodeData.GetJoiningType(0x064B));
        Assert.Equal(JoiningType.NonJoining, UnicodeData.GetJoiningType('A'));
        Assert.Equal(JoiningGroup.Beh, UnicodeData.GetJoiningGroup(0x0628));
        Assert.Equal(JoiningGroup.Alef, UnicodeData.GetJoiningGroup(0x0627));
        Assert.Equal(JoiningGroup.NoJoiningGroup, UnicodeData.GetJoiningGroup('A'));
        Assert.Equal(IndicSyllabicCategory.Consonant, UnicodeData.GetIndicSyllabicCategory(0x0915));
        Assert.Equal(IndicSyllabicCategory.Virama, UnicodeData.GetIndicSyllabicCategory(0x094D));
        Assert.Equal(IndicSyllabicCategory.VowelDependent, UnicodeData.GetIndicSyllabicCategory(0x093E));
        Assert.Equal(IndicSyllabicCategory.Other, UnicodeData.GetIndicSyllabicCategory('A'));
        Assert.Equal(IndicPositionalCategory.Left, UnicodeData.GetIndicPositionalCategory(0x093F));
        Assert.Equal(IndicPositionalCategory.Right, UnicodeData.GetIndicPositionalCategory(0x093E));
        Assert.Equal(IndicPositionalCategory.Bottom, UnicodeData.GetIndicPositionalCategory(0x0941));
        Assert.Equal(IndicPositionalCategory.NotApplicable, UnicodeData.GetIndicPositionalCategory('A'));
        Assert.Equal(VerticalOrientation.Rotated, UnicodeData.GetVerticalOrientation('A'));
        Assert.Equal(VerticalOrientation.Upright, UnicodeData.GetVerticalOrientation(0x4E00));
        Assert.Equal(VerticalOrientation.TransformedUpright, UnicodeData.GetVerticalOrientation(0x3001));
        Assert.Equal(VerticalOrientation.TransformedRotated, UnicodeData.GetVerticalOrientation(0x3008));
    }

    [Fact]
    public void Simple_case_mapping()
    {
        Assert.Equal('A', UnicodeData.ToUpper('a'));
        Assert.Equal('a', UnicodeData.ToLower('A'));
        Assert.Equal('1', UnicodeData.ToUpper('1'));
        Assert.Equal(0x01C5, UnicodeData.ToTitle(0x01C6));
        Assert.Equal(0x01C4, UnicodeData.ToUpper(0x01C6));
        Assert.Equal(0x01C6, UnicodeData.ToLower(0x01C5));
        Assert.Equal('A', UnicodeData.ToTitle('a'));
        Assert.Equal(0x10428, UnicodeData.ToLower(0x10400));
        Assert.Equal(0x00DF, UnicodeData.ToUpper(0x00DF));
        Assert.Equal('I', UnicodeData.ToUpper(0x0131));
        Assert.Equal('i', UnicodeData.ToLower(0x0130));
        Assert.Equal(0x13A0, UnicodeData.ToUpper(0xAB70)); // 切罗基文
    }

#if NET
    /// <summary>简单大小写映射与运行时对照（只比较 BMP 中运行时也映射的字符，排除土耳其语等区域规则）。</summary>
    [Fact]
    public void Case_mapping_matches_runtime_for_bmp()
    {
        var inv = CultureInfo.InvariantCulture.TextInfo;
        int mismatches = 0;
        var report = new List<string>();
        for (int cp = 0; cp < 0x10000; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) continue;
            if (CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.OtherNotAssigned) continue;
            // 不变区域性有意不映射 İ/ı（Unicode 的简单映射是 İ→i、ı→I）
            if (cp == 0x0130 || cp == 0x0131) continue;
            char c = (char)cp;
            int up = inv.ToUpper(c), low = inv.ToLower(c);
            int ourUp = UnicodeData.ToUpper(cp), ourLow = UnicodeData.ToLower(cp);
            // 映射目标在运行时的 Unicode 版本中尚未分配（如 Unicode 18 新增的 U+A7DD 等大写字母）
            if (CharUnicodeInfo.GetUnicodeCategory(ourUp) == UnicodeCategory.OtherNotAssigned
                || CharUnicodeInfo.GetUnicodeCategory(ourLow) == UnicodeCategory.OtherNotAssigned) continue;
            if (up != ourUp || low != ourLow)
            {
                mismatches++;
                if (report.Count < 20) report.Add($"U+{cp:X4}: runtime {up:X4}/{low:X4}, ours {UnicodeData.ToUpper(cp):X4}/{UnicodeData.ToLower(cp):X4}");
            }
        }
        Assert.True(mismatches == 0, string.Join("\n", report) + $"\n({mismatches} total)");
    }
#endif
}
