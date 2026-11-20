using System;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.Markup.Generator.TailwindIntegration;

/// <summary>
/// Tailwind 扫描范围的路径工具：路径规范化（统一为 <c>/</c>、消去 <c>.</c> 与 <c>..</c>）与 <c>@source</c> 的 glob 匹配。
/// 路径比较不区分大小写（与 Windows / macOS 默认文件系统一致；在区分大小写的系统上只会多扫描，不会漏）。
/// </summary>
internal static class SourcePaths
{
    /// <summary>单个 glob 模式（展开花括号后）的最大长度；更长的模式不匹配任何文件。</summary>
    public const int MaxPatternLength = 1024;

    /// <summary>花括号展开的最大分支数（<c>{a,b}{c,d}...</c> 按乘积增长）。</summary>
    public const int MaxBraceExpansions = 64;

    /// <summary>
    /// 一个入口的全部 glob 模式（展开花括号后）的总长度上限。匹配的代价是「模式总长 × 路径长 × 文件数」，
    /// 样式表是不可信输入，超出预算的模式不匹配任何文件（正常项目的 <c>@source</c> 远低于此值）。
    /// </summary>
    public const int GlobBudget = 4096;

    /// <summary>规范化：分隔符统一为 <c>/</c>，消去 <c>.</c>、<c>..</c> 与重复的分隔符，去掉末尾的 <c>/</c>（根除外）。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string p = path.Replace('\\', '/');
        bool rooted = p.StartsWith("/", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                // 盘符（C:）之上没有父目录
                if (parts.Count > 0 && parts[parts.Count - 1] != ".." && !(parts.Count == 1 && IsDrive(parts[0]))) parts.RemoveAt(parts.Count - 1);
                else if (!rooted && !(parts.Count == 1 && IsDrive(parts[0]))) parts.Add("..");
                continue;
            }
            parts.Add(seg);
        }
        string joined = string.Join("/", parts);
        return rooted ? "/" + joined : joined;
    }

    static bool IsDrive(string seg) => seg.Length == 2 && seg[1] == ':' && char.IsLetter(seg[0]);

    /// <summary>文件所在目录（规范化后）；没有目录部分时为空串。</summary>
    public static string DirectoryOf(string path)
    {
        string p = Normalize(path);
        int i = p.LastIndexOf('/');
        return i < 0 ? "" : i == 0 ? "/" : p.Substring(0, i);
    }

    /// <summary>把相对路径接到 <paramref name="baseDirectory"/> 上（<paramref name="relative"/> 已是绝对路径时原样规范化）。</summary>
    public static string Combine(string? baseDirectory, string relative)
    {
        string r = relative.Replace('\\', '/');
        if (r.StartsWith("/", StringComparison.Ordinal) || (r.Length >= 2 && r[1] == ':' && char.IsLetter(r[0])) || string.IsNullOrEmpty(baseDirectory))
            return Normalize(r);
        return Normalize(baseDirectory + "/" + r);
    }

    /// <summary><paramref name="path"/> 是否等于目录 <paramref name="directory"/> 或在其下（两者均已规范化）。</summary>
    public static bool IsUnder(string path, string directory)
    {
        if (directory.Length == 0) return true;
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == directory.Length || directory.EndsWith("/", StringComparison.Ordinal) || path[directory.Length] == '/';
    }

    static bool HasGlob(string pattern) => pattern.IndexOfAny(new[] { '*', '?', '{', '[' }) >= 0;

    /// <summary>
    /// 编译 <c>@source</c> 模式（已接到样式表目录上并规范化）：不含通配符时按文件或目录匹配（目录匹配其下全部文件），
    /// 含通配符时按 glob 匹配：<c>**</c> 跨目录、<c>*</c> 与 <c>?</c> 不跨 <c>/</c>、<c>{a,b}</c> 展开、<c>[...]</c> 按字面处理。
    /// </summary>
    /// <param name="fullPattern">模式。</param>
    /// <param name="budget">剩余的 glob 预算（见 <see cref="GlobBudget"/>），用掉的长度从中扣除。</param>
    public static SourceMatcher CompilePattern(string fullPattern, ref int budget)
    {
        if (!HasGlob(fullPattern)) return new SourceMatcher(fullPattern, null);
        var expanded = new List<string>();
        ExpandBraces(fullPattern, expanded);
        var kept = new List<string>(expanded.Count);
        foreach (var g in expanded)
        {
            if (g.Length > budget) break;
            budget -= g.Length;
            kept.Add(g);
        }
        return new SourceMatcher(null, kept);
    }

    /// <summary>花括号展开（不支持嵌套花括号中的嵌套，按字面处理），结果数不超过 <see cref="MaxBraceExpansions"/>。</summary>
    static void ExpandBraces(string pattern, List<string> output)
    {
        var work = new Queue<string>();
        work.Enqueue(pattern);
        while (work.Count > 0)
        {
            string p = work.Dequeue();
            int open = p.IndexOf('{');
            int close = open < 0 ? -1 : p.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                if (p.Length <= MaxPatternLength) output.Add(p);
                if (output.Count >= MaxBraceExpansions) return;
                continue;
            }
            string head = p.Substring(0, open), tail = p.Substring(close + 1);
            foreach (var alt in p.Substring(open + 1, close - open - 1).Split(','))
            {
                if (output.Count + work.Count >= MaxBraceExpansions) break;
                work.Enqueue(head + alt + tail);
            }
        }
    }

    /// <summary>
    /// glob 匹配（动态规划，时间 O(模式长度 × 路径长度)，空间 O(路径长度)）。<c>**/</c> 匹配零个或多个目录。
    /// </summary>
    public static bool GlobMatch(string pattern, string path)
    {
        int n = path.Length;
        // cur[j]：模式已处理部分能否匹配 path[0..j)
        var cur = new bool[n + 1];
        var next = new bool[n + 1];
        cur[0] = true;
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            Array.Clear(next, 0, next.Length);
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // ** 或 **/：匹配任意字符（含 /）；**/ 还可以匹配空
                bool slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                bool any = false;
                for (int j = 0; j <= n; j++)
                {
                    if (cur[j]) any = true;
                    if (!any) continue;
                    if (slash)
                    {
                        // 匹配空，或匹配以 / 结尾的若干目录
                        if (cur[j] || (j > 0 && path[j - 1] == '/')) next[j] = true;
                    }
                    else next[j] = true;
                }
                i += slash ? 3 : 2;
            }
            else if (c == '*')
            {
                bool run = false;
                for (int j = 0; j <= n; j++)
                {
                    if (cur[j]) run = true;
                    else if (j > 0 && path[j - 1] == '/') run = false;
                    next[j] = run;
                }
                i++;
            }
            else
            {
                for (int j = 0; j < n; j++)
                {
                    if (!cur[j]) continue;
                    char pc = path[j];
                    if (c == '?' ? pc != '/' : char.ToUpperInvariant(pc) == char.ToUpperInvariant(c)) next[j + 1] = true;
                }
                i++;
            }
            var t = cur;
            cur = next;
            next = t;
            bool alive = false;
            foreach (bool b in cur)
            {
                if (b)
                {
                    alive = true;
                    break;
                }
            }
            if (!alive) return false;
        }
        return cur[n];
    }
}

/// <summary>编译好的 <c>@source</c> 模式。</summary>
internal sealed class SourceMatcher
{
    readonly string? _path;
    readonly List<string>? _globs;

    public SourceMatcher(string? path, List<string>? globs)
    {
        _path = path;
        _globs = globs;
    }

    /// <summary><paramref name="normalizedPath"/>（规范化后的完整路径）是否在本模式内。</summary>
    public bool Matches(string normalizedPath)
    {
        if (_path != null) return SourcePaths.IsUnder(normalizedPath, _path);
        foreach (var g in _globs!)
            if (SourcePaths.GlobMatch(g, normalizedPath)) return true;
        return false;
    }
}

/// <summary>生成代码用的 C# 字面量。</summary>
internal static class CSharpLiterals
{
    /// <summary>普通字符串字面量（转义引号、反斜杠与控制字符）。</summary>
    public static string String(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ' || c == '\u2028' || c == '\u2029' || c == '\u0085') sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>逐字字符串字面量（<c>@"..."</c>，引号加倍），适合多行的大段文本。</summary>
    public static string Verbatim(string value) => "@\"" + value.Replace("\"", "\"\"") + "\"";
}
