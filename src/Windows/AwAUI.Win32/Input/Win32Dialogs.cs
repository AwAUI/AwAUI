using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 纯逻辑：comdlg32 过滤串与多选缓冲的解析。不引用互操作，可由跨平台测试验证。

/// <summary>
/// comdlg32 对话框的数据准备：过滤串（「名称\0模式\0…\0\0」）与 OFN_ALLOWMULTISELECT 的结果缓冲
/// （「目录\0文件1\0文件2\0\0」；只选一个时是完整路径）。所有解析都以实际 NUL 为界，限制条目数，
/// 并拒绝空名与非法路径。
/// </summary>
internal static class Win32Dialogs
{
    /// <summary>一次多选最多接受的条目数。</summary>
    public const int MaxPickedFiles = 4096;

    /// <summary>构造过滤串：每个类型「名称\0模式;模式\0」，整体以额外 NUL 结束。</summary>
    public static string BuildFilter(IList<FilePickerFileType> types)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            string name = Sanitize(type.Name);
            if (name.Length == 0) name = "Files";
            sb.Append(name).Append('\0');
            bool any = false;
            for (int p = 0; p < type.Patterns.Count; p++)
            {
                string pattern = Sanitize(type.Patterns[p]);
                if (pattern.Length == 0) continue;
                if (any) sb.Append(';');
                sb.Append(pattern);
                any = true;
            }
            if (!any) sb.Append("*.*");
            sb.Append('\0');
        }
        if (sb.Length == 0) sb.Append("All files").Append('\0').Append("*.*").Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    /// <summary>解析 GetOpenFileNameW 的缓冲为绝对路径列表。</summary>
    public static IReadOnlyList<string> ParseOpenPaths(char[] buffer, bool multiple)
    {
        if (buffer is null) return Array.Empty<string>();
        int first = IndexOfNul(buffer, 0);
        if (first <= 0) return Array.Empty<string>();
        string firstToken = new string(buffer, 0, first);

        // 单选，或多选时只有一个文件（其完整路径后面直接是双 NUL）。
        if (!multiple || first + 1 >= buffer.Length || buffer[first + 1] == '\0')
        {
            return Win32Path.IsSafeAbsolutePath(firstToken)
                ? new[] { firstToken }
                : Array.Empty<string>();
        }

        var paths = new List<string>();
        int at = first + 1;
        while (at < buffer.Length && paths.Count < MaxPickedFiles)
        {
            int end = IndexOfNul(buffer, at);
            if (end < 0 || end == at) break; // 双 NUL 结束
            string name = new string(buffer, at, end - at);
            string? path = Win32Path.Combine(firstToken, name);
            if (path is not null) paths.Add(path);
            at = end + 1;
        }
        return paths;
    }

    static int IndexOfNul(char[] buffer, int start) =>
        start < 0 || start >= buffer.Length ? -1 : Array.IndexOf(buffer, '\0', start);

    static string Sanitize(string? text)
    {
        if (text is null || text.Length == 0) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (c != '\0' && !char.IsControl(c)) sb.Append(c);
        return sb.ToString();
    }
}
