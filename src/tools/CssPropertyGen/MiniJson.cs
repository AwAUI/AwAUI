using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Tools.CssPropertyGen;

/// <summary>
/// 极简 JSON 读取器：对象 → <see cref="Dictionary{TKey,TValue}"/>（保持键序无关），数组 → <see cref="List{T}"/>，
/// 字符串、double、bool、null。只用于读取仓库内的属性清单，不依赖 System.Text.Json，以便测试在 .NET Framework 上也能编译。
/// </summary>
internal static class MiniJson
{
    public static object? Parse(string text)
    {
        int i = 0;
        var v = ParseValue(text, ref i, 0);
        SkipWs(text, ref i);
        if (i != text.Length) throw Error(text, i, "多余的内容");
        return v;
    }

    static object? ParseValue(string s, ref int i, int depth)
    {
        if (depth > 64) throw Error(s, i, "嵌套过深");
        SkipWs(s, ref i);
        if (i >= s.Length) throw Error(s, i, "意外的结尾");
        char c = s[i];
        switch (c)
        {
            case '{':
            {
                i++;
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    SkipWs(s, ref i);
                    var key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    Expect(s, ref i, ':');
                    obj[key] = ParseValue(s, ref i, depth + 1);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return obj;
                }
            }
            case '[':
            {
                i++;
                var list = new List<object?>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref i, depth + 1));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return list;
                }
            }
            case '"':
                return ParseString(s, ref i);
            case 't':
                ExpectWord(s, ref i, "true");
                return true;
            case 'f':
                ExpectWord(s, ref i, "false");
                return false;
            case 'n':
                ExpectWord(s, ref i, "null");
                return null;
            default:
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '-' or '+' or '.' or 'e' or 'E')) i++;
                if (start == i) throw Error(s, i, "无法识别的值");
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    static string ParseString(string s, ref int i)
    {
        Expect(s, ref i, '"');
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw Error(s, i, "字符串未结束");
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) throw Error(s, i, "转义未结束");
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) throw Error(s, i, "\\u 转义不完整");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: throw Error(s, i, "未知转义");
            }
        }
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    static void Expect(string s, ref int i, char c)
    {
        if (i >= s.Length || s[i] != c) throw Error(s, i, "应为 '" + c + "'");
        i++;
    }

    static void ExpectWord(string s, ref int i, string word)
    {
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Error(s, i, "应为 " + word);
        i += word.Length;
    }

    static FormatException Error(string s, int i, string message)
    {
        int line = 1;
        for (int k = 0; k < i && k < s.Length; k++) if (s[k] == '\n') line++;
        return new FormatException("Properties.json 第 " + line.ToString(CultureInfo.InvariantCulture) + " 行：" + message);
    }
}
