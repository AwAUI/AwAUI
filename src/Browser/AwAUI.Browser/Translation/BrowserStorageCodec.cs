using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.Browser;

// 本文件属于 Translation/：只用 netstandard2.0 可用的 API，可由 tests/AwAUI.Browser.Tests 以源码链接方式在普通 .NET 上测试。

/// <summary>
/// JS 端 <c>storageOpenFiles</c> / <c>storageSaveFile</c> 返回的一个文件条目。句柄是 JS 端文件注册表里的整数 id；
/// 浏览器没有真正的路径，<see cref="StorageFile.Path"/> 因此为空。
/// </summary>
internal readonly struct BrowserStorageEntry
{
    public BrowserStorageEntry(int handle, string name, long size, string type, bool writable)
    {
        Handle = handle;
        Name = name ?? string.Empty;
        Size = size;
        Type = type ?? string.Empty;
        Writable = writable;
    }

    /// <summary>JS 文件注册表句柄（&gt; 0）。</summary>
    public int Handle { get; }

    /// <summary>不含目录的文件名。</summary>
    public string Name { get; }

    /// <summary>字节数；未知时为 -1。</summary>
    public long Size { get; }

    /// <summary>浏览器报告的 MIME 类型（可能为空）。</summary>
    public string Type { get; }

    /// <summary>是否可写（保存对话框选中的句柄为 true，打开的文件为 false）。</summary>
    public bool Writable { get; }
}

/// <summary>
/// 文件选择器元数据在 C# 与 JS 之间的编码。跨边界只传字符串：每行一个文件，字段用 U+001F 分隔，
/// 行间用 LF；名称与 MIME 类型经 <c>encodeURIComponent</c> / <see cref="Uri.UnescapeDataString(string)"/> 转义，
/// 因此文件名里的分隔符、换行与控制字符不会破坏解析。字段顺序：
/// <c>handle</c>, <c>name</c>, <c>size</c>, <c>type</c>, <c>writable(0/1)</c>。
/// </summary>
internal static class BrowserStorageCodec
{
    public const char FieldSeparator = '\u001f';
    public const char RecordSeparator = '\n';

    /// <summary>解析 JS 端返回的元数据；空串 / null 表示用户取消。格式错误的行被跳过。</summary>
    public static IReadOnlyList<BrowserStorageEntry> Parse(string? payload)
    {
        var entries = new List<BrowserStorageEntry>();
        if (string.IsNullOrEmpty(payload)) return entries;
        foreach (var record in payload!.Split(RecordSeparator))
        {
            if (record.Length == 0) continue;
            var fields = record.Split(FieldSeparator);
            if (fields.Length < 5) continue;
            if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int handle) || handle <= 0) continue;
            long size = long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize) ? parsedSize : -1;
            string name, type;
            try
            {
                name = Uri.UnescapeDataString(fields[1]);
                type = Uri.UnescapeDataString(fields[3]);
            }
            catch (FormatException)
            {
                continue; // 非法百分号转义（Uri.UnescapeDataString 抛出 UriFormatException）
            }
            entries.Add(new BrowserStorageEntry(handle, name, size, type, fields[4] == "1"));
        }
        return entries;
    }

    /// <summary>
    /// 把 <see cref="FilePickerFileType"/> 列表转换为 <c>&lt;input type=file&gt;.accept</c> 的值：
    /// MIME 类型与通配符按声明顺序合并且去重，逗号分隔；没有类型时返回 null（接受全部文件）。
    /// </summary>
    public static string? BuildAccept(FilePickerOptions? options)
    {
        if (options is null || options.FileTypes.Count == 0) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var type in options.FileTypes)
        {
            if (type is null) continue;
            foreach (var mime in type.MimeTypes) Add(mime);
            foreach (var pattern in type.Patterns) Add(pattern);
        }
        return builder.Length > 0 ? builder.ToString() : null;

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value!)) return;
            if (builder.Length > 0) builder.Append(',');
            builder.Append(value);
        }
    }
}
