using System;
using System.Collections.Generic;

namespace AwAUI.Svg.Values;

/// <summary>
/// RFC 2397 的 <c>data:</c> URI：<c>data:[媒体类型][;参数][;base64],数据</c>。解码是线性的，输出不超过输入长度；
/// base64 中的空白（SVG 里常见的折行）被跳过。
/// </summary>
internal static class SvgDataUri
{
    public static bool IsDataUri(string href) =>
        href.Length >= 5 && string.Compare(href, LeadingWhitespace(href), "data:", 0, 5, StringComparison.OrdinalIgnoreCase) == 0;

    static int LeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>拆出媒体类型（小写、不含参数）并解码载荷；格式错误时返回 false。</summary>
    public static bool TryDecode(string href, out string mediaType, out byte[] data)
    {
        mediaType = "";
        data = Array.Empty<byte>();
        int start = LeadingWhitespace(href) + 5;
        int comma = href.IndexOf(',', start);
        if (comma < 0) return false;
        var header = href.Substring(start, comma - start);
        bool base64 = false;
        var parts = header.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (i == 0) mediaType = p.ToLowerInvariant();
            else if (p.Equals("base64", StringComparison.OrdinalIgnoreCase)) base64 = true;
        }
        return base64 ? TryBase64(href, comma + 1, out data) : TryPercent(href, comma + 1, out data);
    }

    static bool TryBase64(string s, int start, out byte[] data)
    {
        data = Array.Empty<byte>();
        var output = new List<byte>(Math.Max(0, (s.Length - start) * 3 / 4));
        int acc = 0, bits = 0, pad = 0;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            int v;
            if (c >= 'A' && c <= 'Z') v = c - 'A';
            else if (c >= 'a' && c <= 'z') v = c - 'a' + 26;
            else if (c >= '0' && c <= '9') v = c - '0' + 52;
            else if (c == '+' || c == '-') v = 62;
            else if (c == '/' || c == '_') v = 63;
            else if (c == '=') { pad++; continue; }
            else if (char.IsWhiteSpace(c)) continue;
            else if (c == '%' && i + 2 < s.Length && s[i + 1] == '2' && (s[i + 2] == '0')) { i += 2; continue; }   // 转义的空格
            else return false;
            if (pad > 0) return false;   // 填充之后不能再有数据
            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(acc >> bits));
                acc &= (1 << bits) - 1;
            }
        }
        data = output.ToArray();
        return data.Length > 0;
    }

    static bool TryPercent(string s, int start, out byte[] data)
    {
        var output = new List<byte>(s.Length - start);
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '%' && i + 2 < s.Length && Hex(s[i + 1]) >= 0 && Hex(s[i + 2]) >= 0)
            {
                output.Add((byte)(Hex(s[i + 1]) * 16 + Hex(s[i + 2])));
                i += 2;
            }
            else if (c < 0x80) output.Add((byte)c);
            else
            {
                // 非 ASCII 字符按 UTF-8 编码（不能出现在规范的 URI 中，但手写的 SVG 里有）
                var bytes = System.Text.Encoding.UTF8.GetBytes(s.Substring(i, char.IsHighSurrogate(c) && i + 1 < s.Length ? 2 : 1));
                output.AddRange(bytes);
                if (bytes.Length > 0 && char.IsHighSurrogate(c)) i++;
            }
        }
        data = output.ToArray();
        return data.Length > 0;
    }

    static int Hex(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}
