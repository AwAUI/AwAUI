using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>文件对话框类型。</summary>
internal enum X11FileDialogKind
{
    Open,
    Save,
    Folder,
}

/// <summary>一个外部对话框命令：可执行文件与逐个参数（不经过 shell）。</summary>
internal sealed class X11FileDialogCommand
{
    public X11FileDialogCommand(string fileName, IReadOnlyList<string> arguments)
    {
        FileName = fileName;
        Arguments = arguments;
    }

    public string FileName { get; }
    public IReadOnlyList<string> Arguments { get; }
}

/// <summary>
/// 把 <see cref="FilePickerOptions"/> 转换为 zenity / kdialog 的参数（纯逻辑），并解析它们的输出。
/// 过滤器名称与模式里的控制字符、zenity 的分隔符 <c>|</c> 与 kdialog 的括号会被去掉，避免改变命令的语义。
/// </summary>
internal static class X11FileDialogArguments
{
    /// <summary>读取对话框输出的上限（字符）。</summary>
    public const int MaxOutputLength = 1 << 20;

    static readonly char[] WhitespaceChars = { ' ', '\t' };

    public static X11FileDialogCommand Build(string tool, X11FileDialogKind kind, FilePickerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.Equals(tool, "zenity", StringComparison.Ordinal)) return BuildZenity(kind, options);
        if (string.Equals(tool, "kdialog", StringComparison.Ordinal)) return BuildKdialog(kind, options);
        throw new ArgumentException($"Unsupported dialog tool '{tool}'.", nameof(tool));
    }

    static X11FileDialogCommand BuildZenity(X11FileDialogKind kind, FilePickerOptions options)
    {
        var args = new List<string> { "--file-selection" };
        switch (kind)
        {
            case X11FileDialogKind.Open:
                if (options.AllowMultiple)
                {
                    // zenity 多选时用默认的 '|' 分隔（ParseOutput 同时接受 '|' 与换行）
                    args.Add("--multiple");
                }
                break;
            case X11FileDialogKind.Save:
                args.Add("--save");
                if (IsSafeFileName(options.SuggestedFileName))
                    args.Add("--filename=" + options.SuggestedFileName);
                break;
            case X11FileDialogKind.Folder:
                args.Add("--directory");
                break;
        }
        AddTitle(args, options.Title);
        foreach (var filter in options.FileTypes)
        {
            string pattern = ZenityPatterns(filter);
            if (pattern.Length == 0) continue;
            args.Add("--file-filter=" + SanitizeName(filter.Name) + " | " + pattern);
        }
        return new X11FileDialogCommand("zenity", args);
    }

    static X11FileDialogCommand BuildKdialog(X11FileDialogKind kind, FilePickerOptions options)
    {
        var args = new List<string>();
        switch (kind)
        {
            case X11FileDialogKind.Open:
                args.Add("--getopenfilename");
                args.Add(".");
                AddKdialogFilters(args, options);
                if (options.AllowMultiple)
                {
                    args.Add("--multiple");
                    args.Add("--separate-output");
                }
                break;
            case X11FileDialogKind.Save:
                args.Add("--getsavefilename");
                args.Add(IsSafeFileName(options.SuggestedFileName) ? "./" + options.SuggestedFileName : ".");
                AddKdialogFilters(args, options);
                break;
            case X11FileDialogKind.Folder:
                args.Add("--getexistingdirectory");
                args.Add(".");
                break;
        }
        AddTitle(args, options.Title);
        return new X11FileDialogCommand("kdialog", args);
    }

    static void AddKdialogFilters(List<string> args, FilePickerOptions options)
    {
        if (options.FileTypes.Count == 0)
        {
            args.Add("*");
            return;
        }
        var parts = new List<string>();
        foreach (var filter in options.FileTypes)
        {
            string pattern = PlainPatterns(filter);
            if (pattern.Length == 0) continue;
            parts.Add(SanitizeName(filter.Name) + " (" + pattern + ")");
        }
        args.Add(parts.Count > 0 ? string.Join("|", parts) : "*");
    }

    static void AddTitle(List<string> args, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        string clean = StripControl(title!).Trim();
        if (clean.Length == 0) return;
        if (clean.Length > 256) clean = clean.Substring(0, 256);
        args.Add("--title=" + clean);
    }

    /// <summary>zenity 的过滤器模式：<c>*.png *.jpg</c>；没有模式时用 <c>*</c>。</summary>
    public static string ZenityPatterns(FilePickerFileType filter)
    {
        var patterns = new List<string>();
        foreach (var raw in filter.Patterns)
        {
            string p = SanitizePattern(raw);
            if (p.Length > 0) patterns.Add(p);
        }
        return patterns.Count == 0 ? "*" : string.Join(" ", patterns);
    }

    /// <summary>kdialog 的过滤器模式：<c>*.png *.jpg</c>；没有模式时用 <c>*</c>。</summary>
    public static string PlainPatterns(FilePickerFileType filter) => ZenityPatterns(filter);

    /// <summary>去掉过滤器显示名里的控制字符与 zenity / kdialog 的分隔符。</summary>
    public static string SanitizeName(string? name)
    {
        string clean = StripControl(name ?? "").Replace('|', ' ').Replace('(', ' ').Replace(')', ' ').Trim();
        clean = string.Join(" ", clean.Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) return "Files";
        return clean.Length > 128 ? clean.Substring(0, 128) : clean;
    }

    /// <summary>去掉模式里的控制字符、空白与 zenity 的 <c>|</c>；空结果由调用方换成 <c>*</c>。</summary>
    public static string SanitizePattern(string? pattern)
    {
        if (pattern is null) return "";
        string clean = StripControl(pattern).Trim();
        if (clean.Length == 0) return "";
        if (clean.IndexOf('|') >= 0) return "";
        if (clean.Length > 256) clean = clean.Substring(0, 256);
        foreach (var c in clean)
            if (char.IsWhiteSpace(c)) return ""; // 模式之间以空格分隔，模式自身不能含空白
        return clean;
    }

    /// <summary>解析对话框输出：按换行、回车或 zenity 的 <c>|</c> 分隔；去掉空项，限制数量。</summary>
    public static IReadOnlyList<string> ParseOutput(string? output, bool allowMultiple, int maxItems = 1024)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(output) || maxItems <= 0) return result;
        string source = output!;
        if (source.Length > MaxOutputLength) source = source.Substring(0, MaxOutputLength);
        int start = 0;
        for (int i = 0; i <= source.Length; i++)
        {
            bool end = i == source.Length;
            char c = end ? '\0' : source[i];
            if (!end && c != '\n' && c != '\r' && c != '|') continue;
            if (i > start)
            {
                string item = source.Substring(start, i - start).Trim();
                if (item.Length > 0)
                {
                    result.Add(item);
                    if (!allowMultiple || result.Count >= maxItems) return result;
                }
            }
            start = i + 1;
        }
        return result;
    }

    /// <summary>
    /// 从文本读取最多 <paramref name="maxChars"/> 个字符（防止对话框输出无界增长）。
    /// 到达上限后继续读取并丢弃剩余内容，避免子进程因管道写满而阻塞。
    /// </summary>
    public static string ReadCapped(TextReader reader, int maxChars)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));
        if (maxChars <= 0) maxChars = 0;
        var sb = new StringBuilder(Math.Min(Math.Max(maxChars, 0), 4096));
        var buffer = new char[4096];
        while (true)
        {
            int n = reader.Read(buffer, 0, buffer.Length);
            if (n <= 0) break;
            if (sb.Length >= maxChars) continue;
            int take = Math.Min(n, maxChars - sb.Length);
            sb.Append(buffer, 0, take);
        }
        return sb.ToString();
    }

    static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string value = name!;
        if (value.Length > 255) return false;
        if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0) return false;
        foreach (var c in value)
            if (c < 0x20 || c == 0x7F) return false;
        return value != "." && value != "..";
    }

    static string StripControl(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c >= 0x20 && c != 0x7F) sb.Append(c);
        return sb.ToString();
    }
}
