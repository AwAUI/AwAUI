using System;
using System.Globalization;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 本目录（Input/）只含与 Win32 API 无关的纯逻辑：由测试以源码链接方式在任何系统上编译运行（含 net48），
// 只能使用 netstandard2.0 可用的 API，不能引用 Interop/ 中的 P/Invoke。

/// <summary>
/// Windows CF_HTML（"HTML Format"）的编码与解析。格式由若干 ASCII 头行与紧随其后的 UTF-8 文档组成，
/// StartHTML / EndHTML / StartFragment / EndFragment 是**字节**偏移（从数据开头算起），十进制定宽 10 位。
/// 读取侧把内容视为不可信：偏移越界时回退到片段标记，仍找不到时返回 null，且总长度不超过
/// <see cref="Clipboard.MaxTextLength"/> 字节。
/// </summary>
internal static class Win32ClipboardHtml
{
    const string FragmentStart = "<!--StartFragment-->";
    const string FragmentEnd = "<!--EndFragment-->";
    const string DocumentStart = "<html><body>";
    const string DocumentEnd = "</body></html>";

    /// <summary>Windows 剪贴板格式名（RegisterClipboardFormatW 的输入）。</summary>
    public const string FormatName = "HTML Format";

    /// <summary>CF_HTML 数据的字节上限（与文本上限一致，防止对方声明的偏移导致无界分配）。</summary>
    public const int MaxBytes = Clipboard.MaxTextLength;

    /// <summary>把 HTML 片段编码为 CF_HTML 字节。超过上限时截断片段文本（保持偏移自洽），不抛异常。</summary>
    public static byte[] Encode(string html)
    {
        html ??= string.Empty;

        // 定宽偏移使头行长度与数值无关：先取头部长度，再按真实偏移重写一次。
        int headerLength = Encoding.ASCII.GetByteCount(BuildHeader(0, 0, 0, 0));
        int overhead = Encoding.UTF8.GetByteCount(DocumentStart + FragmentStart + FragmentEnd + DocumentEnd);
        int budget = Math.Max(0, MaxBytes - headerLength - overhead);
        if (Encoding.UTF8.GetByteCount(html) > budget)
        {
            // 一个 UTF-16 码元最多编码为 4 个 UTF-8 字节；逐步收紧直到放得下，保证偏移仍然自洽。
            int maxChars = budget / 4;
            html = Clipboard.LimitLength(html, maxChars);
            while (maxChars > 0 && Encoding.UTF8.GetByteCount(html) > budget)
            {
                maxChars = maxChars * 3 / 4;
                html = Clipboard.LimitLength(html, maxChars);
            }
        }

        string body = DocumentStart + FragmentStart + html + FragmentEnd + DocumentEnd;
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(DocumentStart + FragmentStart);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
        int endHtml = startHtml + bodyBytes.Length;

        byte[] headerBytes = Encoding.ASCII.GetBytes(BuildHeader(startHtml, endHtml, startFragment, endFragment));
        if (headerBytes.Length != headerLength)
        {
            // 只可能在偏移超过 10 位时发生（超过 9,999,999,999 字节，不可能达到上限内）；保守回退。
            headerBytes = Encoding.ASCII.GetBytes(BuildHeader(startHtml, Math.Min(endHtml, 999999999), startFragment, Math.Min(endFragment, 999999999)));
        }

        var result = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, result, headerBytes.Length, bodyBytes.Length);
        return result;
    }

    static string BuildHeader(int startHtml, int endHtml, int startFragment, int endFragment) =>
        "Version:0.9\r\n" +
        "StartHTML:" + Fixed(startHtml) + "\r\n" +
        "EndHTML:" + Fixed(endHtml) + "\r\n" +
        "StartFragment:" + Fixed(startFragment) + "\r\n" +
        "EndFragment:" + Fixed(endFragment) + "\r\n";

    static string Fixed(int value) => value.ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>解析 CF_HTML 字节并取出片段；格式无效时返回 null。</summary>
    public static string? Decode(byte[] data)
    {
        if (data is null || data.Length == 0) return null;
        int length = Math.Min(data.Length, MaxBytes);

        int startHtml = FindOffset(data, length, "StartHTML:");
        int endHtml = FindOffset(data, length, "EndHTML:");
        int startFragment = FindOffset(data, length, "StartFragment:");
        int endFragment = FindOffset(data, length, "EndFragment:");

        if (startFragment >= 0 && endFragment > startFragment && endFragment <= length)
        {
            int actualStart = startFragment;
            // 有些写入方把 StartFragment 指向标记之前；以标记本身为准更可靠。
            int marker = IndexOf(data, actualStart, Math.Min(length - actualStart, 256), FragmentStart);
            if (marker >= 0 && marker + FragmentStart.Length <= endFragment)
                actualStart = marker + FragmentStart.Length;
            if (actualStart < 0 || actualStart > endFragment) actualStart = Math.Min(startFragment, endFragment);
            string fragment = Encoding.UTF8.GetString(data, actualStart, endFragment - actualStart);
            return Clipboard.LimitLength(Clipboard.NormalizeNewlines(fragment));
        }

        // 偏移缺失或越界：在 HTML 文本中寻找片段标记。
        if (startHtml < 0 || startHtml > length) startHtml = 0;
        if (endHtml < 0 || endHtml > length || endHtml <= startHtml) endHtml = length;
        string all = Encoding.UTF8.GetString(data, startHtml, endHtml - startHtml);
        int s = all.IndexOf(FragmentStart, StringComparison.Ordinal);
        int e = all.IndexOf(FragmentEnd, StringComparison.Ordinal);
        if (s >= 0 && e > s)
            return Clipboard.LimitLength(Clipboard.NormalizeNewlines(all.Substring(s + FragmentStart.Length, e - s - FragmentStart.Length)));
        return null;
    }

    static int FindOffset(byte[] data, int length, string field)
    {
        int headerLimit = Math.Min(length, 4096);
        int at = IndexOf(data, 0, headerLimit, field);
        if (at < 0) return -1;
        int i = at + field.Length;
        int value = 0, digits = 0;
        while (i < length && digits < 10)
        {
            byte c = data[i];
            if (c < (byte)'0' || c > (byte)'9') break;
            value = value * 10 + (c - '0');
            digits++;
            i++;
        }
        return digits > 0 ? value : -1;
    }

    static int IndexOf(byte[] data, int start, int count, string ascii)
    {
        if (start < 0 || count <= 0 || ascii.Length == 0) return -1;
        int end = Math.Min(data.Length, start + count);
        int last = end - ascii.Length;
        for (int i = start; i <= last; i++)
        {
            int j = 0;
            while (j < ascii.Length && data[i + j] == (byte)ascii[j]) j++;
            if (j == ascii.Length) return i;
        }
        return -1;
    }

    /// <summary>
    /// HTML → 纯文本回退（写入 HTML 时同时写入 CF_UNICODETEXT）。去掉 script/style 与标签，块级标签转换为换行，
    /// 解码常见实体；本函数不追求完整的 HTML 解析，只保证纯文本可用。
    /// </summary>
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new StringBuilder(html.Length);
        int i = 0;
        while (i < html.Length)
        {
            char c = html[i];
            if (c != '<')
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (StartsWith(html, i, "<!--"))
            {
                int close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
                continue;
            }
            int end = html.IndexOf('>', i + 1);
            if (end < 0) { i = html.Length; break; }
            string tag = html.Substring(i + 1, end - i - 1).Trim();
            string name = TagName(tag);
            if (name == "script" || name == "style")
            {
                int close = html.IndexOf("</" + name, end + 1, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close;
                if (close >= 0)
                {
                    int gt = html.IndexOf('>', close);
                    i = gt < 0 ? html.Length : gt + 1;
                }
                continue;
            }
            sb.Append(BlockTag(name) ? '\n' : ' ');
            i = end + 1;
        }

        string text = DecodeEntities(sb.ToString());
        // 合并块标签产生的连续空白，但保留单个换行。
        var clean = new StringBuilder(text.Length);
        bool lastSpace = false, lastNewline = false;
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                if (!lastNewline) clean.Append('\n');
                lastNewline = true;
                lastSpace = false;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace && !lastNewline) clean.Append(' ');
                lastSpace = true;
                continue;
            }
            clean.Append(c);
            lastSpace = false;
            lastNewline = false;
        }
        return Clipboard.LimitLength(clean.ToString().Trim());
    }

    static bool StartsWith(string s, int index, string value)
    {
        if (index + value.Length > s.Length) return false;
        for (int i = 0; i < value.Length; i++)
            if (s[index + i] != value[i]) return false;
        return true;
    }

    static string TagName(string tag)
    {
        int i = 0;
        if (i < tag.Length && tag[i] == '/') i++;
        int start = i;
        while (i < tag.Length && (char.IsLetterOrDigit(tag[i]))) i++;
        return tag.Substring(start, i - start).ToLowerInvariant();
    }

    static bool BlockTag(string name) =>
        name is "br" or "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" or "table" or "ul" or "ol" or "hr";

    static string DecodeEntities(string text)
    {
        if (text.IndexOf('&') < 0) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int semicolon = text[i] == '&' ? text.IndexOf(';', i + 1) : -1;
            if (semicolon > i + 1 && semicolon - i <= 10)
            {
                string name = text.Substring(i + 1, semicolon - i - 1);
                string? replacement = Entity(name);
                if (replacement is not null)
                {
                    sb.Append(replacement);
                    i = semicolon + 1;
                    continue;
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    static string? Entity(string name)
    {
        switch (name)
        {
            case "amp": return "&";
            case "lt": return "<";
            case "gt": return ">";
            case "quot": return "\"";
            case "apos": return "'";
            case "nbsp": return " ";
        }
        if (name.Length > 1 && name[0] == '#')
        {
            bool hex = name.Length > 2 && (name[1] == 'x' || name[1] == 'X');
            int start = hex ? 2 : 1;
            int value = 0;
            for (int i = start; i < name.Length; i++)
            {
                int digit = Digit(name[i], hex);
                if (digit < 0) return null;
                value = value * (hex ? 16 : 10) + digit;
                if (value > 0x10FFFF) return null;
            }
            if (value > 0 && value <= 0x10FFFF) return char.ConvertFromUtf32(value);
        }
        return null;
    }

    static int Digit(char c, bool hex)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (!hex) return -1;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}
