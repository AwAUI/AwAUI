using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Tailwind.Internal;

/// <summary>
/// 与 JavaScript 语义一致的小工具（数字解析与格式化、按顶层分隔符切分、CSS 转义等）。
/// Tailwind 的输出依赖这些细节（例如 <c>String(Number(x)) === x</c> 的规范化判断），移植时逐一对齐。
/// </summary>
internal static class Js
{
    /// <summary>JavaScript <c>Number(text)</c>：空串为 0，非法为 NaN。</summary>
    public static double Number(string text)
    {
        string s = text.Trim();
        if (s.Length == 0) return 0;
        if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
        {
            double hex = 0;
            for (int i = 2; i < s.Length; i++)
            {
                int d = HexValue(s[i]);
                if (d < 0) return double.NaN;
                hex = hex * 16 + d;
            }
            return hex;
        }
        // 只接受 JS 数字字面量允许的字符（double.Parse 还会接受千分位等）
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-'))
            {
                if (s == "Infinity" || s == "+Infinity") return double.PositiveInfinity;
                if (s == "-Infinity") return double.NegativeInfinity;
                return double.NaN;
            }
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    /// <summary>JavaScript <c>parseFloat(text)</c>：取最长的数字前缀。</summary>
    public static double ParseFloat(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        int start = i;
        if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
        int digitsStart = i;
        while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
        }
        if (i == digitsStart || (i == digitsStart + 1 && text[digitsStart] == '.')) return double.NaN;
        int mantissaEnd = i;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            int j = i + 1;
            if (j < text.Length && (text[j] == '+' || text[j] == '-')) j++;
            int expStart = j;
            while (j < text.Length && text[j] >= '0' && text[j] <= '9') j++;
            if (j > expStart) mantissaEnd = j;
        }
        return double.TryParse(text.Substring(start, mantissaEnd - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    /// <summary>JavaScript <c>String(number)</c>。</summary>
    public static string NumberToString(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        if (v == 0) return "0";
        double abs = Math.Abs(v);
        if (abs >= 1e21 || abs < 1e-6)
        {
            string e = v.ToString("R", CultureInfo.InvariantCulture);
            int idx = e.IndexOf('E');
            if (idx < 0) return e;
            string mant = e.Substring(0, idx);
            string exp = e.Substring(idx + 1);
            if (exp.Length > 0 && exp[0] != '-' && exp[0] != '+') exp = "+" + exp;
            exp = exp[0] + exp.Substring(1).TrimStart('0');
            return mant + "e" + exp;
        }
        if (v == Math.Floor(v) && abs < 1e21) return v.ToString("F0", CultureInfo.InvariantCulture);
        string r = v.ToString("R", CultureInfo.InvariantCulture);
        int ei = r.IndexOf('E');
        if (ei < 0) return r;
        return ExpandExponent(r, ei);
    }

    static string ExpandExponent(string r, int ei)
    {
        bool neg = r[0] == '-';
        string mant = r.Substring(neg ? 1 : 0, ei - (neg ? 1 : 0));
        int exp = int.Parse(r.Substring(ei + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = mant.IndexOf('.');
        string digits = dot < 0 ? mant : mant.Remove(dot, 1);
        int pointPos = (dot < 0 ? mant.Length : dot) + exp;
        string result;
        if (pointPos <= 0) result = "0." + new string('0', -pointPos) + digits;
        else if (pointPos >= digits.Length) result = digits + new string('0', pointPos - digits.Length);
        else result = digits.Substring(0, pointPos) + "." + digits.Substring(pointPos);
        return neg ? "-" + result : result;
    }

    /// <summary>Tailwind <c>isPositiveInteger</c>：非负整数且为规范写法。</summary>
    public static bool IsPositiveInteger(string value)
    {
        double n = Number(value);
        return !double.IsNaN(n) && n >= 0 && n == Math.Floor(n) && !double.IsInfinity(n) && NumberToString(n) == value;
    }

    /// <summary>数值版本（Tailwind 中 <c>U(g)</c> 传入数字的情形）。</summary>
    public static bool IsPositiveInteger(double n) => !double.IsNaN(n) && n >= 0 && n == Math.Floor(n) && !double.IsInfinity(n);

    /// <summary>Tailwind <c>isStrictPositiveInteger</c>：正整数且为规范写法。</summary>
    public static bool IsStrictPositiveInteger(string value)
    {
        double n = Number(value);
        return !double.IsNaN(n) && n > 0 && n == Math.Floor(n) && !double.IsInfinity(n) && NumberToString(n) == value;
    }

    /// <summary>Tailwind <c>isValidSpacingMultiplier</c> / <c>isValidOpacityValue</c>：0.25 的非负倍数且为规范写法。</summary>
    public static bool IsMultipleOfQuarter(string value)
    {
        double n = Number(value);
        if (double.IsNaN(n) || n < 0 || double.IsInfinity(n)) return false;
        return n % 0.25 == 0 && NumberToString(n) == value;
    }

    /// <summary>按顶层分隔符切分（忽略括号、方括号、花括号与引号内的分隔符）。Tailwind <c>segment</c>。</summary>
    public static List<string> Segment(string input, char separator)
    {
        var parts = new List<string>();
        var stack = new List<char>();
        int lastPos = 0;
        int len = input.Length;
        for (int i = 0; i < len; i++)
        {
            char c = input[i];
            if (stack.Count == 0 && c == separator)
            {
                parts.Add(input.Substring(lastPos, i - lastPos));
                lastPos = i + 1;
                continue;
            }
            switch (c)
            {
                case '\\':
                    i += 1;
                    break;
                case '\'':
                case '"':
                    while (++i < len)
                    {
                        char n = input[i];
                        if (n == '\\') { i += 1; continue; }
                        if (n == c) break;
                    }
                    break;
                case '(': stack.Add(')'); break;
                case '[': stack.Add(']'); break;
                case '{': stack.Add('}'); break;
                case ']':
                case '}':
                case ')':
                    if (stack.Count > 0 && c == stack[stack.Count - 1]) stack.RemoveAt(stack.Count - 1);
                    break;
            }
        }
        parts.Add(input.Substring(Math.Min(lastPos, len)));
        return parts;
    }

    /// <summary>CSS.escape（与 Tailwind 内置的 polyfill 一致）。</summary>
    public static string Escape(string value)
    {
        int len = value.Length;
        if (len == 0) return value;
        char first = value[0];
        if (len == 1 && first == '-') return "\\-";
        StringBuilder? sb = null;
        for (int i = 0; i < len; i++)
        {
            char c = value[i];
            bool plain = c >= 128 || c == '-' || c == '_' || (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            bool hexEscape = (c >= 1 && c <= 31) || c == 127 || (i == 0 && c >= '0' && c <= '9') || (i == 1 && c >= '0' && c <= '9' && first == '-');
            if (c != 0 && !hexEscape && plain)
            {
                sb?.Append(c);
                continue;
            }
            if (sb == null)
            {
                sb = new StringBuilder(len + 8);
                sb.Append(value, 0, i);
            }
            if (c == 0) sb.Append('�');
            else if (hexEscape) sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
            else sb.Append('\\').Append(c);
        }
        return sb?.ToString() ?? value;
    }

    /// <summary>反转义 CSS 标识符中的 <c>\xx</c> 与 <c>\c</c>。</summary>
    public static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }
            int j = i + 1;
            int hexLen = 0;
            int code = 0;
            while (j < value.Length && hexLen < 6 && HexValue(value[j]) >= 0)
            {
                code = code * 16 + HexValue(value[j]);
                j++;
                hexLen++;
            }
            if (hexLen > 0)
            {
                if (j < value.Length && (value[j] == ' ' || value[j] == '\t' || value[j] == '\n' || value[j] == '\f' || value[j] == '\r')) j++;
                if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) sb.Append('�');
                else sb.Append(char.ConvertFromUtf32(code));
                i = j - 1;
            }
            else
            {
                sb.Append(value[j]);
                i = j;
            }
        }
        return sb.ToString();
    }

    /// <summary>自然排序比较（数字段按数值比较）。Tailwind <c>compare</c>。</summary>
    public static int NaturalCompare(string a, string b)
    {
        int aLen = a.Length, bLen = b.Length;
        int min = aLen < bLen ? aLen : bLen;
        for (int i = 0; i < min; i++)
        {
            char ac = a[i], bc = b[i];
            if (ac >= '0' && ac <= '9' && bc >= '0' && bc <= '9')
            {
                int aEnd = i + 1, bEnd = i + 1;
                while (aEnd < aLen && a[aEnd] >= '0' && a[aEnd] <= '9') aEnd++;
                while (bEnd < bLen && b[bEnd] >= '0' && b[bEnd] <= '9') bEnd++;
                string aNum = a.Substring(i, aEnd - i), bNum = b.Substring(i, bEnd - i);
                double diff = Number(aNum) - Number(bNum);
                if (diff != 0 && !double.IsNaN(diff)) return diff < 0 ? -1 : 1;
                int c = string.CompareOrdinal(aNum, bNum);
                if (c != 0) return c < 0 ? -1 : 1;
                // JS 版本在此 continue，但 i 只前进一位；行为等价于逐字符继续比较
                continue;
            }
            if (ac != bc) return ac - bc;
        }
        return aLen - bLen;
    }

    /// <summary>断点比较（单位相同按数值，否则按单位字典序）。Tailwind <c>compareBreakpoints</c>。</summary>
    public static int CompareBreakpoints(string a, string b, bool ascending)
    {
        if (a == b) return 0;
        int aParen = a.IndexOf('(');
        int bParen = b.IndexOf('(');
        string aUnit = aParen == -1 ? StripNumbers(a) : a.Substring(0, aParen);
        string bUnit = bParen == -1 ? StripNumbers(b) : b.Substring(0, bParen);
        double result = string.CompareOrdinal(aUnit, bUnit) == 0 ? 0 : (string.CompareOrdinal(aUnit, bUnit) < 0 ? -1 : 1);
        if (result == 0) result = ascending ? ParseFloat(a) - ParseFloat(b) : ParseFloat(b) - ParseFloat(a);
        if (double.IsNaN(result)) return string.CompareOrdinal(a, b) < 0 ? -1 : 1;
        return result < 0 ? -1 : result > 0 ? 1 : 0;
    }

    static string StripNumbers(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (!((c >= '0' && c <= '9') || c == '.')) sb.Append(c);
        return sb.ToString();
    }

    public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;

    public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;

    public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;

    public static char CharAt(this string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary><c>s.slice(start, end)</c>，负数从末尾计。</summary>
    public static string Slice(this string s, int start, int end)
    {
        int len = s.Length;
        if (start < 0) start = Math.Max(0, len + start);
        if (end < 0) end = Math.Max(0, len + end);
        if (start > len) start = len;
        if (end > len) end = len;
        return end <= start ? "" : s.Substring(start, end - start);
    }

    public static string Slice(this string s, int start) => s.Slice(start, s.Length);

    public static string ReplaceAll(this string s, string from, string to) => s.IndexOf(from, StringComparison.Ordinal) < 0 ? s : s.Replace(from, to);

    /// <summary>不区分大小写的 ASCII 比较。</summary>
    public static bool EqualsIgnoreCase(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    public static bool IsAsciiLower(char c) => c >= 'a' && c <= 'z';
}
