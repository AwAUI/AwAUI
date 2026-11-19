using System.Collections.Generic;
using System.Text;

namespace AwAUI.Markup.Compiler.Parsing;

/// <summary>
/// 文本与属性字面量的解码：字符实体（<c>&amp;name;</c>、<c>&amp;#123;</c>、<c>&amp;#x1F600;</c>）与 <c>@@</c>。
/// 未知的命名实体与非法码点保持原样（与 HTML 的宽松处理一致）。
/// </summary>
internal static class Entities
{
    static readonly Dictionary<string, string> Named = new()
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
        ["nbsp"] = " ", ["ensp"] = " ", ["emsp"] = " ", ["thinsp"] = " ",
        ["zwnj"] = "‌", ["zwj"] = "‍", ["shy"] = "­",
        ["copy"] = "©", ["reg"] = "®", ["trade"] = "™",
        ["hellip"] = "…", ["mdash"] = "—", ["ndash"] = "–",
        ["lsquo"] = "‘", ["rsquo"] = "’", ["ldquo"] = "“", ["rdquo"] = "”",
        ["laquo"] = "«", ["raquo"] = "»", ["bull"] = "•", ["middot"] = "·",
        ["times"] = "×", ["divide"] = "÷", ["deg"] = "°", ["plusmn"] = "±",
        ["para"] = "¶", ["sect"] = "§", ["euro"] = "€", ["yen"] = "¥",
        ["pound"] = "£", ["cent"] = "¢", ["larr"] = "←", ["rarr"] = "→",
        ["uarr"] = "↑", ["darr"] = "↓", ["check"] = "✓",
    };

    /// <summary>解码源写法。<paramref name="atAt"/> 为 true 时把 <c>@@</c> 解码为 <c>@</c>。</summary>
    public static string Decode(string raw, bool atAt = true)
    {
        if (raw.IndexOf('&') < 0 && (!atAt || raw.IndexOf("@@", System.StringComparison.Ordinal) < 0)) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (atAt && c == '@' && i + 1 < raw.Length && raw[i + 1] == '@')
            {
                sb.Append('@');
                i++;
                continue;
            }
            if (c == '&' && TryDecodeEntity(raw, i, out string? value, out int length))
            {
                sb.Append(value);
                i += length - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>在 <paramref name="pos"/>（一个 &amp;）处尝试解码实体，成功时给出值与源长度。</summary>
    public static bool TryDecodeEntity(string s, int pos, out string? value, out int length)
    {
        value = null;
        length = 0;
        int semi = -1;
        for (int i = pos + 1; i < s.Length && i <= pos + 33; i++)
        {
            if (s[i] == ';') { semi = i; break; }
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '#') return false;
        }
        if (semi < 0 || semi == pos + 1) return false;
        string body = s.Substring(pos + 1, semi - pos - 1);
        if (body[0] == '#')
        {
            bool hex = body.Length > 1 && (body[1] == 'x' || body[1] == 'X');
            string digits = body.Substring(hex ? 2 : 1);
            if (digits.Length == 0 || digits.Length > 8) return false;
            int cp = 0;
            foreach (char d in digits)
            {
                int v = hex ? HexValue(d) : (d >= '0' && d <= '9' ? d - '0' : -1);
                if (v < 0) return false;
                cp = cp * (hex ? 16 : 10) + v;
                if (cp > 0x10FFFF) return false;
            }
            if (cp == 0 || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
            value = char.ConvertFromUtf32(cp);
        }
        else if (!Named.TryGetValue(body, out value))
        {
            return false;
        }
        length = semi - pos + 1;
        return true;
    }

    static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    /// <summary>把值编码为可放进文本的源写法：&lt; &amp; 与 @ 转义。</summary>
    public static string EncodeText(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '@': sb.Append("@@"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
