using System;
using System.Globalization;
using System.Text;

namespace AwAUI.Css.Syntax;

/// <summary>CSS 文本序列化辅助（CSSOM §2.1 的标识符、字符串与 URL 序列化规则）。</summary>
public static class CssSerializer
{
    /// <summary>ASCII 大小写不敏感比较。<paramref name="lowerAscii"/> 必须已是小写。</summary>
    public static bool EqualsIgnoreAsciiCase(string value, string lowerAscii)
    {
        if (value.Length != lowerAscii.Length) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char a = value[i], b = lowerAscii[i];
            if (a == b) continue;
            if (a >= 'A' && a <= 'Z' && a + 32 == b) continue;
            return false;
        }
        return true;
    }

    /// <summary>只把 ASCII 大写字母转为小写；无需转换时返回原实例。</summary>
    public static string ToAsciiLower(string value)
    {
        int i = 0;
        for (; i < value.Length; i++)
        {
            char c = value[i];
            if (c >= 'A' && c <= 'Z') break;
        }
        if (i == value.Length) return value;
        var chars = value.ToCharArray();
        for (; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c >= 'A' && c <= 'Z') chars[i] = (char)(c + 32);
        }
        return new string(chars);
    }

    /// <summary>以「序列化标识符」规则写出 <paramref name="ident"/>。</summary>
    public static void WriteIdent(StringBuilder sb, string ident)
    {
        if (ident.Length == 1 && ident[0] == '-') { sb.Append("\\-"); return; }
        for (int i = 0; i < ident.Length; i++)
        {
            char c = ident[i];
            if (c == '\0') sb.Append('�');
            else if (c <= 0x1F || c == 0x7F) WriteCodePointEscape(sb, c);
            else if (c >= '0' && c <= '9' && (i == 0 || (i == 1 && ident[0] == '-'))) WriteCodePointEscape(sb, c);
            else if (c >= 0x80 || c == '-' || c == '_' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) sb.Append(c);
            else { sb.Append('\\'); sb.Append(c); }
        }
    }

    /// <summary>写出 name（不要求可作为标识符开头，如非 ID 型 hash 的值）。</summary>
    public static void WriteName(StringBuilder sb, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '\0') sb.Append('�');
            else if (c <= 0x1F || c == 0x7F) WriteCodePointEscape(sb, c);
            else if (c >= 0x80 || c == '-' || c == '_' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) sb.Append(c);
            else { sb.Append('\\'); sb.Append(c); }
        }
    }

    /// <summary>写出 dimension 的单位：首字符为 e/E 且其后会被误读为指数时转义。</summary>
    public static void WriteUnit(StringBuilder sb, string unit)
    {
        if (unit.Length > 1 && (unit[0] == 'e' || unit[0] == 'E')
            && (IsDigit(unit[1]) || ((unit[1] == '+' || unit[1] == '-') && unit.Length > 2 && IsDigit(unit[2]))))
        {
            WriteCodePointEscape(sb, unit[0]);
            WriteName(sb, unit.Substring(1));
            return;
        }
        WriteIdent(sb, unit);
    }

    /// <summary>以双引号字符串写出。</summary>
    public static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            if (c == '\0') sb.Append('�');
            else if (c <= 0x1F || c == 0x7F) WriteCodePointEscape(sb, c);
            else if (c == '"' || c == '\\') { sb.Append('\\'); sb.Append(c); }
            else sb.Append(c);
        }
        sb.Append('"');
    }

    /// <summary>写出 url() 的主体（带引号的字符串形式）。</summary>
    public static void WriteUrlBody(StringBuilder sb, string url) => WriteString(sb, url);

    /// <summary>标识符的序列化结果。</summary>
    public static string SerializeIdent(string ident)
    {
        var sb = new StringBuilder(ident.Length + 4);
        WriteIdent(sb, ident);
        return sb.ToString();
    }

    /// <summary>字符串的序列化结果。</summary>
    public static string SerializeString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        WriteString(sb, value);
        return sb.ToString();
    }

    /// <summary>与区域设置无关、可往返的数值文本。</summary>
    public static string FormatNumber(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            if (value == 0 && BitConverter.DoubleToInt64Bits(value) < 0) return "-0";
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 写出数值记号的数值部分，保证重新分词后得到相同的值与类型标志：溢出为无穷大的值写成同样会溢出的字面量，
    /// integer 类型不使用指数与小数点，number 类型在需要时补上 <c>.0</c>。
    /// </summary>
    internal static void WriteNumber(StringBuilder sb, double value, CssNumberType type)
    {
        if (double.IsInfinity(value))
        {
            if (value < 0) sb.Append('-');
            if (type == CssNumberType.Integer) { sb.Append('1'); sb.Append('0', 309); }
            else sb.Append("1e999");
            return;
        }
        if (double.IsNaN(value)) { sb.Append('0'); return; }
        if (type == CssNumberType.Integer)
        {
            if (Math.Abs(value) < 1e15) sb.Append(FormatNumber(value));
            else sb.Append(new System.Numerics.BigInteger(value).ToString(CultureInfo.InvariantCulture));
            return;
        }
        string text = FormatNumber(value);
        sb.Append(text);
        if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0) sb.Append(".0");
    }

    static void WriteCodePointEscape(StringBuilder sb, char c)
    {
        sb.Append('\\');
        sb.Append(((int)c).ToString("x", CultureInfo.InvariantCulture));
        sb.Append(' ');
    }

    static bool IsDigit(char c) => c >= '0' && c <= '9';
}
