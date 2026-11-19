using System;
using System.Runtime.CompilerServices;

namespace AwAUI.Unicode;

/// <summary>
/// Unicode 字符属性查询。数据由构建期工具从 UCD 生成为紧凑三级表（design/03 文本栈），不依赖 ICU 与运行时自带的 Unicode 数据，
/// 因此所有运行时（含 netstandard2.0）得到相同结果。所有方法都不分配；超出 0..0x10FFFF 的值按未分配码位处理。
/// </summary>
public static class UnicodeData
{
    /// <summary>数据对应的 Unicode 版本（如 "18.0.0"）。</summary>
    public const string UnicodeVersion = UnicodeTables.Version;

    /// <summary>最大码位 U+10FFFF。</summary>
    public const int MaxCodePoint = 0x10FFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool InRange(int cp) => (uint)cp <= MaxCodePoint;

    /// <summary>通用类别（General_Category）。</summary>
    public static GeneralCategory GetGeneralCategory(int codePoint)
        => InRange(codePoint) ? (GeneralCategory)UnicodeTables.GeneralCategory(codePoint) : GeneralCategory.Unassigned;

    /// <summary>文字（Script）。</summary>
    public static Script GetScript(int codePoint)
        => InRange(codePoint) ? (Script)UnicodeTables.Script(codePoint) : Script.Unknown;

    /// <summary>
    /// 文字扩展（Script_Extensions）：写入 <paramref name="destination"/> 并返回个数；目标不够长时返回所需长度的相反数。
    /// 没有显式扩展的码位返回它的 Script（一个元素）。
    /// </summary>
    public static int GetScriptExtensions(int codePoint, Span<Script> destination)
    {
        var set = GetScriptExtensionSet(codePoint, out var single);
        int count = set.IsEmpty ? 1 : set.Length;
        if (destination.Length < count) return -count;
        if (set.IsEmpty) destination[0] = single;
        else
            for (int i = 0; i < set.Length; i++) destination[i] = (Script)set[i];
        return count;
    }

    /// <summary>码位的 Script_Extensions 是否包含 <paramref name="script"/>。</summary>
    public static bool HasScript(int codePoint, Script script)
    {
        var set = GetScriptExtensionSet(codePoint, out var single);
        if (set.IsEmpty) return single == script;
        return set.IndexOf((byte)script) >= 0;
    }

    static ReadOnlySpan<byte> GetScriptExtensionSet(int codePoint, out Script single)
    {
        single = GetScript(codePoint);
        if (!InRange(codePoint)) return default;
        int index = UnicodeTables.ScriptExtensions(codePoint);
        if (index == 0) return default;
        var offsets = UnicodeTables.ScriptExtensionOffsets;
        int start = offsets[index * 2] | (offsets[index * 2 + 1] << 8);
        int end = offsets[index * 2 + 2] | (offsets[index * 2 + 3] << 8);
        return UnicodeTables.ScriptExtensionData.Slice(start, end - start);
    }

    /// <summary>文字的 ISO 15924 四字母代码（如 Latin → "Latn"，Han → "Hani"，Unknown → "Zzzz"）。</summary>
    public static string GetScriptCode(Script script)
    {
        int i = (int)script;
        if ((uint)i >= (uint)(UnicodeTables.ScriptCodes.Length / 4)) i = 0;
        return ScriptCodeCache.Codes[i];
    }

    /// <summary>
    /// 文字的 ISO 15924 代码打包为大端 32 位标签（如 Latn → 0x4C61746E）。OpenType 文字标签多数可由它小写化得到。
    /// </summary>
    public static uint GetScriptTag(Script script)
    {
        int i = (int)script;
        if ((uint)i >= (uint)(UnicodeTables.ScriptCodes.Length / 4)) i = 0;
        var s = UnicodeTables.ScriptCodes;
        return ((uint)s[i * 4] << 24) | ((uint)s[i * 4 + 1] << 16) | ((uint)s[i * 4 + 2] << 8) | s[i * 4 + 3];
    }

    /// <summary>由 ISO 15924 代码（大小写不敏感）查找文字；未知代码返回 false。</summary>
    public static bool TryParseScriptCode(ReadOnlySpan<char> code, out Script script)
    {
        script = Script.Unknown;
        if (code.Length != 4) return false;
        var s = UnicodeTables.ScriptCodes;
        for (int i = 0; i < s.Length / 4; i++)
        {
            bool match = true;
            for (int j = 0; j < 4 && match; j++)
                match = (s[i * 4 + j] | 0x20) == (code[j] | 0x20);
            if (match)
            {
                script = (Script)i;
                return true;
            }
        }
        return false;
    }

    /// <summary>断行类别（Line_Break，未经 LB1 解析的原始值）。</summary>
    public static LineBreakClass GetLineBreakClass(int codePoint)
        => InRange(codePoint) ? (LineBreakClass)(UnicodeTables.Line(codePoint) & UnicodeTables.LineLbMask) : LineBreakClass.XX;

    /// <summary>东亚宽度（East_Asian_Width）。</summary>
    public static EastAsianWidth GetEastAsianWidth(int codePoint)
        => InRange(codePoint) ? (EastAsianWidth)((UnicodeTables.Line(codePoint) >> UnicodeTables.LineEawShift) & 7) : EastAsianWidth.Neutral;

    /// <summary>字素簇断点属性（Grapheme_Cluster_Break）。</summary>
    public static GraphemeClusterBreak GetGraphemeClusterBreak(int codePoint)
        => InRange(codePoint) ? (GraphemeClusterBreak)(UnicodeTables.Segment(codePoint) & UnicodeTables.SegGcbMask) : GraphemeClusterBreak.Other;

    /// <summary>Indic_Conjunct_Break。</summary>
    public static IndicConjunctBreak GetIndicConjunctBreak(int codePoint)
        => InRange(codePoint) ? (IndicConjunctBreak)((UnicodeTables.Segment(codePoint) >> UnicodeTables.SegInCBShift) & 3) : IndicConjunctBreak.None;

    /// <summary>单词断点属性（Word_Break）。</summary>
    public static WordBreak GetWordBreak(int codePoint)
        => InRange(codePoint) ? (WordBreak)((UnicodeTables.Segment(codePoint) >> UnicodeTables.SegWbShift) & UnicodeTables.SegWbMask) : WordBreak.Other;

    /// <summary>句子断点属性（Sentence_Break）。</summary>
    public static SentenceBreak GetSentenceBreak(int codePoint)
        => InRange(codePoint) ? (SentenceBreak)(UnicodeTables.Segment(codePoint) >> UnicodeTables.SegSbShift) : SentenceBreak.Other;

    /// <summary>Extended_Pictographic（emoji-data.txt）。</summary>
    public static bool IsExtendedPictographic(int codePoint)
        => InRange(codePoint) && (UnicodeTables.Segment(codePoint) & UnicodeTables.SegExtPictBit) != 0;

    /// <summary>Emoji 属性。</summary>
    public static bool IsEmoji(int codePoint) => HasFlag(codePoint, 1 << 0);

    /// <summary>Emoji_Presentation：默认以彩色表情呈现。</summary>
    public static bool IsEmojiPresentation(int codePoint) => HasFlag(codePoint, 1 << 1);

    /// <summary>Emoji_Modifier：肤色修饰符。</summary>
    public static bool IsEmojiModifier(int codePoint) => HasFlag(codePoint, 1 << 2);

    /// <summary>Emoji_Modifier_Base：可接肤色修饰符的表情。</summary>
    public static bool IsEmojiModifierBase(int codePoint) => HasFlag(codePoint, 1 << 3);

    /// <summary>Emoji_Component：表情序列的组成部分（键帽、标签、肤色等）。</summary>
    public static bool IsEmojiComponent(int codePoint) => HasFlag(codePoint, 1 << 4);

    /// <summary>Default_Ignorable_Code_Point：无字形时应不可见地渲染。</summary>
    public static bool IsDefaultIgnorable(int codePoint) => HasFlag(codePoint, 1 << 5);

    /// <summary>White_Space（PropList.txt）。</summary>
    public static bool IsWhiteSpace(int codePoint) => HasFlag(codePoint, 1 << 6);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool HasFlag(int codePoint, int bit) => InRange(codePoint) && (UnicodeTables.Flags(codePoint) & bit) != 0;

    /// <summary>双向类别（Bidi_Class，含未分配码位的派生默认值）。</summary>
    public static BidiClass GetBidiClass(int codePoint)
        => InRange(codePoint) ? (BidiClass)(UnicodeTables.Bidi(codePoint) & UnicodeTables.BidiClassMask) : BidiClass.L;

    /// <summary>Bidi_Mirrored：在从右到左上下文中应镜像显示。</summary>
    public static bool IsBidiMirrored(int codePoint)
        => InRange(codePoint) && (UnicodeTables.Bidi(codePoint) & UnicodeTables.BidiMirroredBit) != 0;

    /// <summary>Bidi_Mirroring_Glyph：镜像字符；没有时返回码位本身。</summary>
    public static int GetBidiMirroringGlyph(int codePoint)
    {
        if (!IsBidiMirrored(codePoint)) return codePoint;
        int m = UnicodeTables.LookupPair(UnicodeTables.MirrorPairs, UnicodeTables.MirrorPairCount, codePoint);
        return m < 0 ? codePoint : m;
    }

    /// <summary>Bidi_Paired_Bracket_Type。</summary>
    public static BidiPairedBracketType GetBidiPairedBracketType(int codePoint)
        => InRange(codePoint) ? (BidiPairedBracketType)((UnicodeTables.Bidi(codePoint) >> UnicodeTables.BidiBracketShift) & 3) : BidiPairedBracketType.None;

    /// <summary>Bidi_Paired_Bracket：配对括号；不是成对括号时返回码位本身。</summary>
    public static int GetBidiPairedBracket(int codePoint)
    {
        if (GetBidiPairedBracketType(codePoint) == BidiPairedBracketType.None) return codePoint;
        int m = UnicodeTables.LookupPair(UnicodeTables.BracketPairs, UnicodeTables.BracketPairCount, codePoint);
        return m < 0 ? codePoint : m;
    }

    /// <summary>连写类型（Joining_Type，含由通用类别派生的 T）。</summary>
    public static JoiningType GetJoiningType(int codePoint)
        => InRange(codePoint) ? (JoiningType)(UnicodeTables.Shaping(codePoint) & UnicodeTables.ShapingJtMask) : JoiningType.NonJoining;

    /// <summary>规范组合类（Canonical_Combining_Class，0..254；0 表示不参与规范重排）。整形器据此对组合标记做规范排序。</summary>
    public static int GetCombiningClass(int codePoint)
        => InRange(codePoint) ? UnicodeTables.CombiningClass(codePoint) : 0;

    /// <summary>连写分组（Joining_Group）。</summary>
    public static JoiningGroup GetJoiningGroup(int codePoint)
        => InRange(codePoint) ? (JoiningGroup)UnicodeTables.JoiningGroup(codePoint) : JoiningGroup.NoJoiningGroup;

    /// <summary>Indic_Syllabic_Category。</summary>
    public static IndicSyllabicCategory GetIndicSyllabicCategory(int codePoint)
        => InRange(codePoint)
            ? (IndicSyllabicCategory)((UnicodeTables.Shaping(codePoint) >> UnicodeTables.ShapingInscShift) & UnicodeTables.ShapingInscMask)
            : IndicSyllabicCategory.Other;

    /// <summary>Indic_Positional_Category。</summary>
    public static IndicPositionalCategory GetIndicPositionalCategory(int codePoint)
        => InRange(codePoint)
            ? (IndicPositionalCategory)((UnicodeTables.Shaping(codePoint) >> UnicodeTables.ShapingInpcShift) & UnicodeTables.ShapingInpcMask)
            : IndicPositionalCategory.NotApplicable;

    /// <summary>竖排方向（Vertical_Orientation）。</summary>
    public static VerticalOrientation GetVerticalOrientation(int codePoint)
        => InRange(codePoint) ? (VerticalOrientation)((UnicodeTables.Shaping(codePoint) >> UnicodeTables.ShapingVoShift) & 3) : VerticalOrientation.Rotated;

    /// <summary>简单小写映射（Simple_Lowercase_Mapping）；无映射时返回码位本身。</summary>
    public static int ToLower(int codePoint) => MapCase(codePoint, 0);

    /// <summary>简单大写映射（Simple_Uppercase_Mapping）；无映射时返回码位本身。</summary>
    public static int ToUpper(int codePoint) => MapCase(codePoint, 4);

    /// <summary>简单词首大写映射（Simple_Titlecase_Mapping）；无映射时返回码位本身。</summary>
    public static int ToTitle(int codePoint) => MapCase(codePoint, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int MapCase(int codePoint, int field)
    {
        if (!InRange(codePoint)) return codePoint;
        int record = UnicodeTables.Case(codePoint);
        if (record == 0) return codePoint;
        return codePoint + UnicodeTables.ReadInt32(UnicodeTables.CaseRecords, record * 12 + field);
    }

    /// <summary>ISO 15924 代码字符串缓存（字符串常量驻留，不在查询时分配）。</summary>
    static class ScriptCodeCache
    {
        public static readonly string[] Codes = Build();

        static string[] Build()
        {
            var s = UnicodeTables.ScriptCodes;
            var result = new string[s.Length / 4];
            for (int i = 0; i < result.Length; i++) result[i] = s.Substring(i * 4, 4);
            return result;
        }
    }
}
