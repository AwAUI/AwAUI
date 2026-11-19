using System;
using System.Collections.Generic;

namespace AwAUI.Tailwind;

/// <summary>
/// 候选类名提取（与 Tailwind v4 的扫描器同一思路：按文本切分，不理解语言）。适用于 <c>.awaui</c>、<c>.cs</c>、<c>.css</c>、HTML 等任意文本：
/// <c>class="..."</c> 属性、C# 字符串字面量与插值字符串（<c>$"p-{x} m-4"</c> 中的 <c>m-4</c>）、<c>@apply</c> 参数都能被提取。
/// 提取结果是「可能的类名」，由 <see cref="TailwindCompiler"/> 决定哪些真正生成样式。
/// </summary>
/// <remarks>
/// 单遍线性扫描、不回溯；单个候选超过 <see cref="MaxCandidateLength"/> 个字符即丢弃，任意值内的括号与引号有深度上限，
/// 因此对任意（含恶意）输入的时间与内存都与输入长度成线性关系。
/// </remarks>
public static class CandidateScanner
{
    /// <summary>单个候选的最大长度。</summary>
    public const int MaxCandidateLength = 1024;

    const int MaxNesting = 64;

    /// <summary>提取并去重（保持首次出现的顺序）。</summary>
    public static List<string> Extract(string content)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Extract(content, seen, result);
        return result;
    }

    /// <summary>提取到已有集合中（跨文件去重）；<paramref name="ordered"/> 可为 null。</summary>
    public static void Extract(string content, HashSet<string> seen, List<string>? ordered)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        if (seen == null) throw new ArgumentNullException(nameof(seen));
        int len = content.Length;
        int i = 0;
        var stack = new char[MaxNesting];
        while (i < len)
        {
            char c = content[i];
            if (!CanStart(c))
            {
                i++;
                continue;
            }
            int start = i;
            int depth = 0;
            bool valid = true;
            int j = i;
            while (j < len)
            {
                char ch = content[j];
                if (depth > 0)
                {
                    if (IsWhitespace(ch)) break;
                    if (ch == '\'' || ch == '"' || ch == '`')
                    {
                        // 任意值内的字符串：必须在同一行、遇到空白前闭合
                        int k = j + 1;
                        while (k < len && content[k] != ch && !IsWhitespace(content[k]) && k - start < MaxCandidateLength)
                        {
                            if (content[k] == '\\') k++;
                            k++;
                        }
                        if (k >= len || content[k] != ch)
                        {
                            valid = false;
                            j = k;
                            break;
                        }
                        j = k + 1;
                        continue;
                    }
                    if (ch == '[' || ch == '(')
                    {
                        if (depth >= MaxNesting)
                        {
                            valid = false;
                            break;
                        }
                        stack[depth++] = ch == '[' ? ']' : ')';
                    }
                    else if (ch == ']' || ch == ')')
                    {
                        if (stack[depth - 1] != ch)
                        {
                            valid = false;
                            break;
                        }
                        depth--;
                    }
                    j++;
                }
                else
                {
                    if (ch == '[')
                    {
                        stack[depth++] = ']';
                        j++;
                    }
                    else if (ch == '(')
                    {
                        // 只有 bg-(--x)、/(--x) 这种「-(」「/(」开头的括号属于类名
                        char prev = j > start ? content[j - 1] : '\0';
                        if (prev != '-' && prev != '/') break;
                        stack[depth++] = ')';
                        j++;
                    }
                    else if (IsCandidateChar(ch)) j++;
                    else break;
                }
                if (j - start > MaxCandidateLength)
                {
                    valid = false;
                    break;
                }
            }
            if (depth > 0) valid = false;
            if (valid) Emit(content, start, j, seen, ordered);
            // 丢弃的长候选：跳到下一个边界，避免从其内部重新开始（保持线性）
            if (!valid)
            {
                while (j < len && !IsWhitespace(content[j]) && content[j] != '"' && content[j] != '\'' && content[j] != '`') j++;
            }
            i = j > start ? j : start + 1;
        }
    }

    static void Emit(string content, int start, int end, HashSet<string> seen, List<string>? ordered)
    {
        // 去掉不可能出现在类名末尾的字符（句号、冒号、逗号等）
        while (end > start)
        {
            char last = content[end - 1];
            if (last == ':' || last == '.' || last == ',' || last == '/' || last == '\\') end--;
            else break;
        }
        if (end <= start) return;
        string candidate = content.Substring(start, end - start);
        if (seen.Add(candidate)) ordered?.Add(candidate);
    }

    static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v';

    static bool CanStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '!' || c == '[' || c == '*' || c == '@' || c == '_';

    /// <summary>方括号外允许出现在类名中的字符。</summary>
    static bool IsCandidateChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
        c == '-' || c == '_' || c == ':' || c == '/' || c == '.' || c == '!' || c == '%' || c == '@' || c == '*' || c == '#' || c == '&';
}
