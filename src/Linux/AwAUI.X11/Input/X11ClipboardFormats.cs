using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>
/// 剪贴板富格式的纯逻辑：<c>text/uri-list</c> 的生成与解析，以及 HTML → 纯文本回退。
/// 来自其他程序的数据视为不可信：条目数、单条长度与总长度都有上限，非本机文件 URI 被忽略。
/// </summary>
internal static class X11ClipboardFormats
{
    /// <summary><c>text/uri-list</c> 的总长度上限（8 MB）。</summary>
    public const int MaxUriListLength = 8 * 1024 * 1024;

    /// <summary>文件列表的条目数上限。</summary>
    public const int MaxUriListItems = 4096;

    /// <summary>单条路径长度上限。</summary>
    public const int MaxPathLength = 4096;

    /// <summary>把本机文件路径编码为 <c>text/uri-list</c>；没有有效条目时返回 null。</summary>
    public static string? BuildUriList(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return null;
        var sb = new StringBuilder();
        int count = 0;
        foreach (var path in paths)
        {
            if (count >= MaxUriListItems || sb.Length >= MaxUriListLength) break;
            if (!X11LaunchRules.IsSafePath(path)) continue;
            Uri uri;
            try { uri = new Uri(path); }
            catch (UriFormatException) { continue; }
            if (!uri.IsFile) continue;
            string line = uri.AbsoluteUri;
            if (line.Length > MaxPathLength + 16) continue;
            if (sb.Length + line.Length + 2 > MaxUriListLength) break;
            sb.Append(line).Append("\r\n");
            count++;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>文件路径的纯文本回退（每行一个）。</summary>
    public static string? BuildTextFileList(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return null;
        var sb = new StringBuilder();
        int count = 0;
        foreach (var path in paths)
        {
            if (count >= MaxUriListItems || sb.Length >= MaxUriListLength) break;
            if (!X11LaunchRules.IsSafePath(path)) continue;
            sb.Append(path).Append('\n');
            count++;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// 解析 <c>text/uri-list</c>：忽略注释行（<c>#</c>）、空行、非 <c>file</c> URI、远端主机与不安全路径。
    /// 结果去重并保持原顺序。
    /// </summary>
    public static IReadOnlyList<string> ParseUriList(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        string source = text!;
        if (source.Length > MaxUriListLength) source = source.Substring(0, MaxUriListLength);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in source.Split('\n'))
        {
            if (result.Count >= MaxUriListItems) break;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            // 只接受明确的 file: URI；.NET 会把裸路径 "/tmp/x" 也解析为绝对 file URI，这里不要
            if (!line.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            if (HasParentSegment(line)) continue; // 不可信的列表不允许父目录段（Uri 会把 .. 规范化掉）
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
            if (!string.IsNullOrEmpty(uri.Host) && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) continue;
            string path = uri.LocalPath;
            if (path.Length == 0 || path.Length > MaxPathLength) continue;
            if (!X11LaunchRules.IsSafePath(path)) continue;
            if (seen.Add(path)) result.Add(path);
        }
        return result;
    }

    static bool HasParentSegment(string line)
    {
        if (line.IndexOf("/../", StringComparison.Ordinal) >= 0) return true;
        if (line.EndsWith("/..", StringComparison.Ordinal)) return true;
        if (line.IndexOf("/..?", StringComparison.Ordinal) >= 0) return true;
        return line.IndexOf("/..#", StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// 粗略的 HTML → 纯文本转换，用作 <c>text/html</c> 的纯文本回退：去掉标签、把块级标签换成换行、
    /// 解码常见实体并折叠多余空白。结果不超过 <see cref="Clipboard.MaxTextLength"/>。
    /// </summary>
    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string source = html!;
        var sb = new StringBuilder(Math.Min(source.Length, 1 << 16));
        bool inTag = false;
        StringBuilder? tag = null;
        for (int i = 0; i < source.Length && sb.Length < Clipboard.MaxTextLength; i++)
        {
            char c = source[i];
            if (!inTag)
            {
                if (c == '<')
                {
                    inTag = true;
                    tag = new StringBuilder(8);
                }
                else sb.Append(c);
                continue;
            }
            if (c == '>')
            {
                inTag = false;
                string name = TagName(tag);
                tag = null;
                if (name is "br" or "p" or "div" or "li" or "tr" or "td" or "th" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                    sb.Append('\n');
                continue;
            }
            if (tag is not null && tag.Length < 32) tag.Append(c);
        }
        string text = DecodeEntities(sb.ToString());
        return CollapseWhitespace(text);
    }

    static string TagName(StringBuilder? tag)
    {
        if (tag is null) return "";
        int i = 0;
        // 结束标签的 '/' 与空白不影响名字
        while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
        if (i < tag.Length && tag[i] == '/') i++;
        int start = i;
        while (i < tag.Length && (char.IsLetterOrDigit(tag[i]))) i++;
        return tag.ToString(start, i - start).ToLowerInvariant();
    }

    static string DecodeEntities(string text)
    {
        if (text.IndexOf('&') < 0) return text;
        return text
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'")
            .Replace("&nbsp;", " ");
    }

    static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool lastSpace = false;
        int newlines = 0;
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                if (newlines < 2) sb.Append('\n');
                newlines++;
                lastSpace = false;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            sb.Append(c);
            lastSpace = false;
            newlines = 0;
        }
        return sb.ToString().Trim();
    }
}