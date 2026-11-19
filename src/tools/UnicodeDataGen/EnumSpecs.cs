using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AwAUI.Tools.UnicodeDataGen;

/// <summary>一个生成的公共枚举：成员顺序即数值，成员与 UCD 属性值短名一一对应。</summary>
internal sealed class EnumSpec
{
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string Property { get; init; }
    public required List<(string Member, string Short, string Doc)> Members { get; init; }
    public string UnderlyingType { get; init; } = "byte";

    Dictionary<string, int>? _index;

    public int IndexOfShort(string shortName)
    {
        _index ??= Members.Select((m, i) => (m.Short, i)).ToDictionary(t => t.Short, t => t.i);
        if (_index.TryGetValue(shortName, out var i)) return i;
        throw new InvalidDataException($"{Name}: value '{shortName}' has no enum member (UCD has a new value?).");
    }

    public string MemberOf(int index) => Members[index].Member;

    static (string, string, string) M(string member, string shortName, string doc) => (member, shortName, doc);

    public static EnumSpec GeneralCategory() => new()
    {
        Name = "GeneralCategory",
        Summary = "Unicode 通用类别（General_Category）。数值与 System.Globalization.UnicodeCategory 一致，可直接强制转换。",
        Property = "gc",
        Members = new()
        {
            M("UppercaseLetter", "Lu", "Lu：大写字母。"),
            M("LowercaseLetter", "Ll", "Ll：小写字母。"),
            M("TitlecaseLetter", "Lt", "Lt：词首大写字母。"),
            M("ModifierLetter", "Lm", "Lm：修饰字母。"),
            M("OtherLetter", "Lo", "Lo：其他字母（含表意文字）。"),
            M("NonspacingMark", "Mn", "Mn：非间距组合符号。"),
            M("SpacingMark", "Mc", "Mc：间距组合符号。"),
            M("EnclosingMark", "Me", "Me：封闭符号。"),
            M("DecimalNumber", "Nd", "Nd：十进制数字。"),
            M("LetterNumber", "Nl", "Nl：字母数字（如罗马数字）。"),
            M("OtherNumber", "No", "No：其他数字。"),
            M("SpaceSeparator", "Zs", "Zs：空格分隔符。"),
            M("LineSeparator", "Zl", "Zl：行分隔符。"),
            M("ParagraphSeparator", "Zp", "Zp：段落分隔符。"),
            M("Control", "Cc", "Cc：控制字符。"),
            M("Format", "Cf", "Cf：格式字符。"),
            M("Surrogate", "Cs", "Cs：代理码位。"),
            M("PrivateUse", "Co", "Co：私用区。"),
            M("ConnectorPunctuation", "Pc", "Pc：连接标点。"),
            M("DashPunctuation", "Pd", "Pd：破折号标点。"),
            M("OpenPunctuation", "Ps", "Ps：开标点。"),
            M("ClosePunctuation", "Pe", "Pe：闭标点。"),
            M("InitialPunctuation", "Pi", "Pi：起始引号。"),
            M("FinalPunctuation", "Pf", "Pf：结束引号。"),
            M("OtherPunctuation", "Po", "Po：其他标点。"),
            M("MathSymbol", "Sm", "Sm：数学符号。"),
            M("CurrencySymbol", "Sc", "Sc：货币符号。"),
            M("ModifierSymbol", "Sk", "Sk：修饰符号。"),
            M("OtherSymbol", "So", "So：其他符号。"),
            M("Unassigned", "Cn", "Cn：未分配。"),
        },
    };

    public static EnumSpec LineBreak() => new()
    {
        Name = "LineBreakClass",
        Summary = "UAX #14 断行类别（Line_Break）。成员名采用 UAX #14 的两字母短名。",
        Property = "lb",
        Members = new()
        {
            M("XX", "XX", "Unknown：未知（按 LB1 解析为 AL）。"),
            M("AI", "AI", "Ambiguous：宽度不定的字母/符号（按 LB1 解析为 AL）。"),
            M("AK", "AK", "Aksara：婆罗米系辅音字母。"),
            M("AL", "AL", "Alphabetic：普通字母与符号。"),
            M("AP", "AP", "Aksara_Prebase：前置字母。"),
            M("AS", "AS", "Aksara_Start：可起始音节的字母。"),
            M("B2", "B2", "Break_Both：两侧可断（长破折号）。"),
            M("BA", "BA", "Break_After：其后可断。"),
            M("BB", "BB", "Break_Before：其前可断。"),
            M("BK", "BK", "Mandatory_Break：强制换行。"),
            M("CB", "CB", "Contingent_Break：视上下文的断点（如对象替换符）。"),
            M("CJ", "CJ", "Conditional_Japanese_Starter：日文小假名等（默认解析为 NS）。"),
            M("CL", "CL", "Close_Punctuation：闭标点。"),
            M("CM", "CM", "Combining_Mark：组合符号。"),
            M("CP", "CP", "Close_Parenthesis：闭括号。"),
            M("CR", "CR", "Carriage_Return：回车。"),
            M("EB", "EB", "E_Base：表情修饰基。"),
            M("EM", "EM", "E_Modifier：表情修饰符（肤色）。"),
            M("EX", "EX", "Exclamation：感叹/疑问号。"),
            M("GL", "GL", "Glue：不断行粘连（如 NBSP）。"),
            M("H2", "H2", "H2：谚文 LV 音节。"),
            M("H3", "H3", "H3：谚文 LVT 音节。"),
            M("HH", "HH", "Unambiguous_Hyphen：明确的连字符。"),
            M("HL", "HL", "Hebrew_Letter：希伯来字母。"),
            M("HY", "HY", "Hyphen：连字符减号。"),
            M("ID", "ID", "Ideographic：表意文字。"),
            M("IN", "IN", "Inseparable：不可分（省略号）。"),
            M("IS", "IS", "Infix_Numeric：数字中缀分隔符。"),
            M("JL", "JL", "JL：谚文初声。"),
            M("JT", "JT", "JT：谚文终声。"),
            M("JV", "JV", "JV：谚文中声。"),
            M("LF", "LF", "Line_Feed：换行。"),
            M("NL", "NL", "Next_Line：下一行（NEL）。"),
            M("NS", "NS", "Nonstarter：不能位于行首。"),
            M("NU", "NU", "Numeric：数字。"),
            M("OP", "OP", "Open_Punctuation：开标点。"),
            M("PO", "PO", "Postfix_Numeric：数字后缀。"),
            M("PR", "PR", "Prefix_Numeric：数字前缀。"),
            M("QU", "QU", "Quotation：引号。"),
            M("RI", "RI", "Regional_Indicator：区域指示符。"),
            M("SA", "SA", "Complex_Context：东南亚文字（需词典断词；默认按 LB1 解析）。"),
            M("SG", "SG", "Surrogate：代理码位（按 LB1 解析为 AL）。"),
            M("SP", "SP", "Space：空格。"),
            M("SY", "SY", "Break_Symbols：斜线。"),
            M("VF", "VF", "Virama_Final：词尾 virama。"),
            M("VI", "VI", "Virama：virama。"),
            M("WJ", "WJ", "Word_Joiner：连词符。"),
            M("ZW", "ZW", "ZWSpace：零宽空格。"),
            M("ZWJ", "ZWJ", "ZWJ：零宽连接符。"),
        },
    };

    public static EnumSpec GraphemeClusterBreak() => new()
    {
        Name = "GraphemeClusterBreak",
        Summary = "UAX #29 字素簇断点属性（Grapheme_Cluster_Break）。",
        Property = "GCB",
        Members = new()
        {
            M("Other", "XX", "其他。"),
            M("CR", "CR", "回车。"),
            M("LF", "LF", "换行。"),
            M("Control", "CN", "控制字符。"),
            M("Extend", "EX", "扩展（组合符号等）。"),
            M("ZWJ", "ZWJ", "零宽连接符。"),
            M("RegionalIndicator", "RI", "区域指示符。"),
            M("Prepend", "PP", "前置字符。"),
            M("SpacingMark", "SM", "间距符号。"),
            M("L", "L", "谚文初声。"),
            M("V", "V", "谚文中声。"),
            M("T", "T", "谚文终声。"),
            M("LV", "LV", "谚文 LV 音节。"),
            M("LVT", "LVT", "谚文 LVT 音节。"),
        },
    };

    public static EnumSpec WordBreak() => new()
    {
        Name = "WordBreak",
        Summary = "UAX #29 单词断点属性（Word_Break）。",
        Property = "WB",
        Members = new()
        {
            M("Other", "XX", "其他。"),
            M("CR", "CR", "回车。"),
            M("LF", "LF", "换行。"),
            M("Newline", "NL", "其他换行符。"),
            M("Extend", "Extend", "扩展。"),
            M("ZWJ", "ZWJ", "零宽连接符。"),
            M("RegionalIndicator", "RI", "区域指示符。"),
            M("Format", "FO", "格式字符。"),
            M("Katakana", "KA", "片假名。"),
            M("HebrewLetter", "HL", "希伯来字母。"),
            M("ALetter", "LE", "字母。"),
            M("SingleQuote", "SQ", "单引号。"),
            M("DoubleQuote", "DQ", "双引号。"),
            M("MidNumLet", "MB", "字母与数字中间符（如句点）。"),
            M("MidLetter", "ML", "字母中间符（如冒号）。"),
            M("MidNum", "MN", "数字中间符（如逗号）。"),
            M("Numeric", "NU", "数字。"),
            M("ExtendNumLet", "EX", "连接符（如下划线）。"),
            M("WSegSpace", "WSegSpace", "水平空白。"),
        },
    };

    public static EnumSpec SentenceBreak() => new()
    {
        Name = "SentenceBreak",
        Summary = "UAX #29 句子断点属性（Sentence_Break）。",
        Property = "SB",
        Members = new()
        {
            M("Other", "XX", "其他。"),
            M("CR", "CR", "回车。"),
            M("LF", "LF", "换行。"),
            M("Extend", "EX", "扩展。"),
            M("Sep", "SE", "段落分隔。"),
            M("Format", "FO", "格式字符。"),
            M("Sp", "SP", "空白。"),
            M("Lower", "LO", "小写。"),
            M("Upper", "UP", "大写。"),
            M("OLetter", "LE", "其他字母。"),
            M("Numeric", "NU", "数字。"),
            M("ATerm", "AT", "句点。"),
            M("SContinue", "SC", "句子延续标点。"),
            M("STerm", "ST", "句末标点。"),
            M("Close", "CL", "闭合标点。"),
        },
    };

    public static EnumSpec IndicConjunctBreak() => new()
    {
        Name = "IndicConjunctBreak",
        Summary = "Indic_Conjunct_Break（InCB），UAX #29 GB9c 使用。",
        Property = "InCB",
        Members = new()
        {
            M("None", "None", "无。"),
            M("Linker", "Linker", "连接符（virama 类）。"),
            M("Consonant", "Consonant", "辅音。"),
            M("Extend", "Extend", "扩展。"),
        },
    };

    public static EnumSpec EastAsianWidth() => new()
    {
        Name = "EastAsianWidth",
        Summary = "UAX #11 东亚宽度（East_Asian_Width）。",
        Property = "ea",
        Members = new()
        {
            M("Neutral", "N", "N：中性（非东亚）。"),
            M("Ambiguous", "A", "A：宽度不定。"),
            M("Halfwidth", "H", "H：半角。"),
            M("Wide", "W", "W：宽。"),
            M("Fullwidth", "F", "F：全角。"),
            M("Narrow", "Na", "Na：窄。"),
        },
    };

    public static EnumSpec BidiClass() => new()
    {
        Name = "BidiClass",
        Summary = "UAX #9 双向类别（Bidi_Class）。成员名采用 UAX #9 的短名。",
        Property = "bc",
        Members = new()
        {
            M("L", "L", "从左到右的强字符。"),
            M("R", "R", "从右到左的强字符。"),
            M("AL", "AL", "阿拉伯字母（从右到左的强字符）。"),
            M("EN", "EN", "欧洲数字。"),
            M("ES", "ES", "欧洲数字分隔符（+ -）。"),
            M("ET", "ET", "欧洲数字终止符（货币、百分号等）。"),
            M("AN", "AN", "阿拉伯数字。"),
            M("CS", "CS", "通用数字分隔符（, . : /）。"),
            M("NSM", "NSM", "非间距符号。"),
            M("BN", "BN", "边界中性（格式控制等）。"),
            M("B", "B", "段落分隔符。"),
            M("S", "S", "段内分隔符（制表符）。"),
            M("WS", "WS", "空白。"),
            M("ON", "ON", "其他中性字符。"),
            M("LRE", "LRE", "从左到右嵌入。"),
            M("LRO", "LRO", "从左到右覆盖。"),
            M("RLE", "RLE", "从右到左嵌入。"),
            M("RLO", "RLO", "从右到左覆盖。"),
            M("PDF", "PDF", "结束嵌入/覆盖。"),
            M("LRI", "LRI", "从左到右隔离。"),
            M("RLI", "RLI", "从右到左隔离。"),
            M("FSI", "FSI", "首强字符隔离。"),
            M("PDI", "PDI", "结束隔离。"),
        },
    };

    public static EnumSpec BidiPairedBracketType() => new()
    {
        Name = "BidiPairedBracketType",
        Summary = "Bidi_Paired_Bracket_Type，UAX #9 N0 规则使用。",
        Property = "bpt",
        Members = new()
        {
            M("None", "n", "不是成对括号。"),
            M("Open", "o", "开括号。"),
            M("Close", "c", "闭括号。"),
        },
    };

    public static EnumSpec JoiningType() => new()
    {
        Name = "JoiningType",
        Summary = "Joining_Type（ArabicShaping.txt），阿拉伯/叙利亚/蒙古等文字的连写类型。",
        Property = "jt",
        Members = new()
        {
            M("NonJoining", "U", "U：不连写。"),
            M("JoinCausing", "C", "C：引起连写（如 ZWJ、tatweel）。"),
            M("DualJoining", "D", "D：双向连写。"),
            M("LeftJoining", "L", "L：只与左侧连写。"),
            M("RightJoining", "R", "R：只与右侧连写。"),
            M("Transparent", "T", "T：透明（组合符号等）。"),
        },
    };

    public static EnumSpec VerticalOrientation() => new()
    {
        Name = "VerticalOrientation",
        Summary = "UAX #50 竖排方向（Vertical_Orientation）。",
        Property = "vo",
        Members = new()
        {
            M("Rotated", "R", "R：旋转 90 度显示。"),
            M("Upright", "U", "U：直立显示。"),
            M("TransformedUpright", "Tu", "Tu：使用竖排变体字形，否则直立。"),
            M("TransformedRotated", "Tr", "Tr：使用竖排变体字形，否则旋转。"),
        },
    };

    /// <summary>从 PropertyValueAliases.txt 生成的枚举（成员多、随版本增长）：首个成员固定为默认值。</summary>
    public static EnumSpec FromAliases(Ucd ucd, string name, string summary, string property, string[] leading, string docFormat)
    {
        var values = ucd.ValuesOf(property);
        var members = new List<(string, string, string)>();
        foreach (var lead in leading)
        {
            var v = values.First(x => x.Short == lead);
            members.Add((Pascal(v.Long), v.Short, string.Format(docFormat, v.Short, v.Long)));
        }
        foreach (var v in values)
        {
            if (leading.Contains(v.Short)) continue;
            members.Add((Pascal(v.Long), v.Short, string.Format(docFormat, v.Short, v.Long)));
        }
        return new EnumSpec { Name = name, Summary = summary, Property = property, Members = members };
    }

    static string Pascal(string longName)
    {
        var sb = new StringBuilder();
        bool upper = true;
        foreach (var c in longName)
        {
            if (c == '_' || c == ' ' || c == '-') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    public void Emit(StringBuilder sb)
    {
        sb.Append("/// <summary>").Append(Summary).Append("</summary>\n");
        sb.Append("public enum ").Append(Name).Append(" : ").Append(UnderlyingType).Append("\n{\n");
        for (int i = 0; i < Members.Count; i++)
        {
            var (member, _, doc) = Members[i];
            sb.Append("    /// <summary>").Append(Escape(doc)).Append("</summary>\n");
            sb.Append("    ").Append(member).Append(" = ").Append(i).Append(",\n");
        }
        sb.Append("}\n\n");
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void CheckAllUsed(int[] data)
    {
        // 只是诊断：列出 UCD 中未出现的成员（已废弃的值允许不出现）
        var seen = new bool[Members.Count];
        foreach (var v in data) seen[v] = true;
        for (int i = 0; i < seen.Length; i++)
            if (!seen[i]) Console.Error.WriteLine($"note: {Name}.{Members[i].Member} does not occur in the data");
    }
}
