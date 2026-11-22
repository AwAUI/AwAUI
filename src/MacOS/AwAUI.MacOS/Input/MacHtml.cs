using System;
using System.Globalization;
using System.Text;

namespace AwAUI.MacOS;

/// <summary>
/// 富文本剪贴板的纯逻辑（与 AppKit 无关，放在 Input/ 目录里以便单元测试）：为 HTML 写入生成纯文本回退。
/// 不是完整的 HTML 解析器，只去掉标签、注释并解码常见实体，足以让只接受纯文本的接收方得到可读内容。
/// </summary>
internal static class MacHtml
{
    /// <summary>从 HTML 片段得到纯文本回退：去掉标签与注释、解码常见实体、把连续空白折成一个空格。</summary>
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<')
            {
                // 注释：整段跳过，不当作标签内容
                if (i + 3 < html.Length && html[i + 1] == '!' && html[i + 2] == '-' && html[i + 3] == '-')
                {
                    int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (end < 0) break;
                    i = end + 2;
                    inTag = false;
                    sb.Append(' ');
                    continue;
                }
                inTag = true;
                continue;
            }
            if (inTag)
            {
                if (c == '>')
                {
                    inTag = false;
                    sb.Append(' '); // 标签替换为空白，避免相邻文本粘连（<p>a</p><p>b</p> → "a b"）
                }
                continue;
            }
            if (c == '&' && TryEntity(html, ref i, sb)) continue;
            sb.Append(c);
        }
        return Collapse(sb.ToString());
    }

    static bool TryEntity(string html, ref int index, StringBuilder sb)
    {
        int semi = html.IndexOf(';', index + 1);
        if (semi < 0 || semi - index > 10) return false; // 不是实体（只是文本里的 &）
        string name = html.Substring(index + 1, semi - index - 1);
        switch (name)
        {
            case "amp": sb.Append('&'); break;
            case "lt": sb.Append('<'); break;
            case "gt": sb.Append('>'); break;
            case "quot": sb.Append('"'); break;
            case "apos": sb.Append('\''); break;
            case "nbsp": sb.Append(' '); break;
            default:
                if (name.Length < 2 || name[0] != '#') return false;
                int code;
                bool ok = name.Length > 2 && (name[1] == 'x' || name[1] == 'X')
                    ? int.TryParse(name.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
                    : int.TryParse(name.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
                if (!ok || code <= 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) return false;
                sb.Append(char.ConvertFromUtf32(code));
                break;
        }
        index = semi;
        return true;
    }

    static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0; // 前导空白忽略
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
