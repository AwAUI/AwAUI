using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.Parsing;

/// <summary>
/// C# 边界扫描器（design/17「C# 边界规则」）：不解析 C#，只识别足以确定边界的原子 ——
/// 字符串（普通、逐字、内插、原始、内插原始，内插洞可任意嵌套）、字符字面量、注释、预处理行，
/// 并据此找到配对的 <c>() [] {}</c>。任何输入都不会抛异常；嵌套过深按未终止处理。
/// </summary>
internal static class CSharpScanner
{
    const int MaxNesting = 128;

    /// <summary>
    /// 若 <paramref name="pos"/> 处开始一个字符串、字符字面量、注释或预处理行，返回其结束位置（不含）；否则返回 <paramref name="pos"/>。
    /// </summary>
    public static int SkipAtom(string s, int pos) => SkipAtom(s, pos, out _, 0);

    /// <inheritdoc cref="SkipAtom(string, int)"/>
    public static int SkipAtom(string s, int pos, out bool unterminated) => SkipAtom(s, pos, out unterminated, 0);

    /// <summary>pos 处的原子是否是注释或预处理行（它们不改变语句位置）。</summary>
    public static bool IsTriviaAtom(string s, int pos) =>
        (pos + 1 < s.Length && s[pos] == '/' && (s[pos + 1] == '/' || s[pos + 1] == '*'))
        || (pos < s.Length && s[pos] == '#' && IsAtLineStart(s, pos));

    /// <summary>
    /// 从 <paramref name="openPos"/>（必须是 <c>( [ {</c> 之一）扫描到配对的结束定界符，返回其位置；不配对返回 -1。
    /// 不同种类的定界符错配时按「就近配对」容错：结束符与栈中更深处匹配则弹出到那里；
    /// 栈中根本没有对应的开定界符（如括号内出现多余的 }）则立即判为不配对，避免越过外层代码体的边界。
    /// 同一文本需要反复查询时用 <see cref="DelimiterMatcher"/>，它缓存扫描结果，总代价是线性的。
    /// </summary>
    public static int FindClose(string s, int openPos) => new DelimiterMatcher(s, memoize: false).FindClose(openPos);

    internal static char CloserOf(char c) => c switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };

    static int SkipAtom(string s, int pos, out bool unterminated, int nesting)
    {
        unterminated = false;
        if (pos >= s.Length) return pos;
        char c = s[pos];
        char n = pos + 1 < s.Length ? s[pos + 1] : '\0';
        if (nesting > MaxNesting)
        {
            // 嵌套过深：保守地视为一直延续到文本末尾
            unterminated = true;
            return s.Length;
        }
        switch (c)
        {
            case '/' when n == '/':
                return LineEnd(s, pos);
            case '/' when n == '*':
            {
                int end = s.IndexOf("*/", pos + 2, System.StringComparison.Ordinal);
                if (end < 0) { unterminated = true; return s.Length; }
                return end + 2;
            }
            case '#' when IsAtLineStart(s, pos):
                return LineEnd(s, pos);
            case '\'':
                return SkipChar(s, pos, out unterminated);
            case '"':
                return SkipStringAt(s, pos, 0, false, out unterminated, nesting);
            case '@' when n == '"':
                return SkipVerbatim(s, pos + 2, false, out unterminated, nesting);
            case '@' when n == '$' && pos + 2 < s.Length && s[pos + 2] == '"':
                return SkipVerbatim(s, pos + 3, true, out unterminated, nesting);
            case '$':
            {
                int d = pos;
                while (d < s.Length && s[d] == '$') d++;
                int dollars = d - pos;
                if (d < s.Length && s[d] == '"') return SkipStringAt(s, d, dollars, false, out unterminated, nesting);
                if (dollars == 1 && d + 1 < s.Length && s[d] == '@' && s[d + 1] == '"')
                    return SkipVerbatim(s, d + 2, true, out unterminated, nesting);
                return pos;
            }
            default:
                return pos;
        }
    }

    internal static bool IsAtLineStart(string s, int pos)
    {
        for (int i = pos - 1; i >= 0; i--)
        {
            char c = s[i];
            if (c == '\n' || c == '\r') return true;
            if (c != ' ' && c != '\t') return false;
        }
        return true;
    }

    static int LineEnd(string s, int pos)
    {
        int i = pos;
        while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
        return i;
    }

    static int SkipChar(string s, int pos, out bool unterminated)
    {
        // 严格匹配字符字面量：'x'、代理对、'\n'、'\u1234'、'\U0001F600'、'\x41'。
        // 不符合时只吞掉这个 '，避免把后面的括号当成字面量内容
        unterminated = true;
        int i = pos + 1;
        if (i >= s.Length) return pos + 1;
        char c = s[i];
        int j;
        if (c == '\\')
        {
            char n = i + 1 < s.Length ? s[i + 1] : '\0';
            int hexStart = i + 2;
            if (n == 'u') j = SkipHex(s, hexStart, 4, 4);
            else if (n == 'U') j = SkipHex(s, hexStart, 8, 8);
            else if (n == 'x') j = SkipHex(s, hexStart, 1, 4);
            else j = n == '\0' || n == '\n' || n == '\r' ? -1 : i + 2;
        }
        else if (c == '\'' || c == '\n' || c == '\r')
        {
            j = -1;
        }
        else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            j = i + 2;
        }
        else
        {
            j = i + 1;
        }
        if (j >= 0 && j < s.Length && s[j] == '\'')
        {
            unterminated = false;
            return j + 1;
        }
        return pos + 1;
    }

    static int SkipHex(string s, int at, int min, int max)
    {
        int n = 0;
        while (n < max && at + n < s.Length && IsHex(s[at + n])) n++;
        return n >= min ? at + n : -1;
    }

    static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>pos 处是 <c>"</c>：普通、内插或原始字符串。dollars 为前缀 $ 的个数。</summary>
    static int SkipStringAt(string s, int pos, int dollars, bool verbatim, out bool unterminated, int nesting)
    {
        int q = pos;
        while (q < s.Length && s[q] == '"') q++;
        int quotes = q - pos;
        if (quotes >= 3) return SkipRaw(s, q, quotes, dollars, out unterminated, nesting);
        if (quotes == 2)
        {
            // 空字符串 "" 或 $""
            unterminated = false;
            return pos + 2;
        }
        return dollars > 0
            ? SkipInterpolated(s, pos + 1, verbatim, out unterminated, nesting)
            : SkipRegular(s, pos + 1, out unterminated);
    }

    static int SkipRegular(string s, int i, out bool unterminated)
    {
        unterminated = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '"') return i + 1;
            if (c == '\n' || c == '\r') break;
            i++;
        }
        unterminated = true;
        return System.Math.Min(i, s.Length);
    }

    static int SkipVerbatim(string s, int i, bool interpolated, out bool unterminated, int nesting)
    {
        if (interpolated) return SkipInterpolated(s, i, true, out unterminated, nesting);
        unterminated = false;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        unterminated = true;
        return s.Length;
    }

    static int SkipInterpolated(string s, int i, bool verbatim, out bool unterminated, int nesting)
    {
        unterminated = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (c == '"')
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            if (c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }
                i = SkipHole(s, i + 1, 1, out bool bad, nesting + 1);
                if (bad) { unterminated = true; return i; }
                continue;
            }
            if (c == '}' && i + 1 < s.Length && s[i + 1] == '}') { i += 2; continue; }
            if (!verbatim && (c == '\n' || c == '\r')) break;
            i++;
        }
        unterminated = true;
        return System.Math.Min(i, s.Length);
    }

    static int SkipRaw(string s, int i, int quotes, int dollars, out bool unterminated, int nesting)
    {
        unterminated = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                int q = i;
                while (q < s.Length && s[q] == '"') q++;
                if (q - i >= quotes) return q;
                i = q;
                continue;
            }
            if (dollars > 0 && c == '{')
            {
                int b = i;
                while (b < s.Length && s[b] == '{') b++;
                int run = b - i;
                if (run >= dollars)
                {
                    // 前 run - dollars 个 { 是内容，后 dollars 个开启内插洞
                    i = SkipHole(s, b, dollars, out bool bad, nesting + 1);
                    if (bad) { unterminated = true; return i; }
                    continue;
                }
                i = b;
                continue;
            }
            i++;
        }
        unterminated = true;
        return s.Length;
    }

    /// <summary>
    /// 扫描内插洞的内容直到深度 0 的 <c>}</c>（再吞掉其后共 <paramref name="closeBraces"/> 个 }），返回其后位置。
    /// 深度 0 的单个 <c>:</c> 之后是格式说明，原样跳到 }。
    /// </summary>
    static int SkipHole(string s, int i, int closeBraces, out bool unterminated, int nesting)
    {
        unterminated = false;
        int depth = 0;
        while (i < s.Length)
        {
            int atomEnd = SkipAtom(s, i, out bool bad, nesting);
            if (atomEnd > i)
            {
                if (bad && atomEnd >= s.Length) { unterminated = true; return s.Length; }
                i = atomEnd;
                continue;
            }
            char c = s[i];
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']') { if (depth > 0) depth--; }
            else if (c == '}')
            {
                if (depth > 0) depth--;
                else
                {
                    int e = i;
                    int count = 0;
                    while (e < s.Length && s[e] == '}' && count < closeBraces) { e++; count++; }
                    return e;
                }
            }
            else if (c == ':' && depth == 0)
            {
                if (i + 1 < s.Length && s[i + 1] == ':') { i += 2; continue; }
                // 格式说明：跳到 }
                int e = i + 1;
                while (e < s.Length && s[e] != '}') e++;
                i = e;
                continue;
            }
            i++;
        }
        unterminated = true;
        return s.Length;
    }
}

/// <summary>
/// 带缓存的定界符配对（语义同 <see cref="CSharpScanner.FindClose(string, int)"/>），供解析器对同一文本反复查询。
/// </summary>
/// <remarks>
/// 对每个「代码位置」p（不在字符串、注释等原子内部、被扫描实际访问到的位置）定义
/// N(p) = 从 p 起、跳过配对的嵌套定界符后遇到的第一个结束符的位置（到文本末尾仍没有则为 -1）：
/// 开定界符 o 的 N(o + 1) 就是结束 o 这一层的结束符 —— 与 o 配对，或错配后由外层继续处理（就近配对）。
/// N 只取决于文本与 p，因此扫描途中访问的每个位置都记入缓存，以后任何从这些位置出发或经过它们的查询都直接取用。
/// 从字符串内部等不同「相位」出发的扫描一旦与已缓存的扫描在同一位置汇合，后面就不再重复。
/// 这样一行一个未配对的 <c>@(</c>、属性值中的 <c>"@("</c> 等成千上万次查询，总代价仍与文本长度成正比。
/// </remarks>
internal sealed class DelimiterMatcher
{
    readonly string _s;
    readonly bool _memoize;
    // N(p) + 2；0 表示尚未计算
    int[]? _next;

    public DelimiterMatcher(string s, bool memoize = true)
    {
        _s = s;
        _memoize = memoize;
    }

    /// <inheritdoc cref="CSharpScanner.FindClose(string, int)"/>
    public int FindClose(int openPos)
    {
        if (openPos < 0 || openPos >= _s.Length) return -1;
        char closer = CSharpScanner.CloserOf(_s[openPos]);
        if (closer == '\0') return -1;
        int v = Next(openPos + 1);
        return v >= 0 && _s[v] == closer ? v : -1;
    }

    /// <summary>计算 N(start)。用显式栈代替递归，任意深的嵌套都不会栈溢出。</summary>
    int Next(int start)
    {
        string s = _s;
        int[]? memo = null;
        if (_memoize) memo = _next ??= new int[s.Length + 1];
        var openers = new List<int> { -1 };   // 每一层对应的开定界符位置；根层为 -1
        var pathStart = new List<int> { 0 };  // 每一层访问过的位置在 path 中的起点
        var path = memo != null ? new List<int>() : null;
        int p = start;
        while (true)
        {
            int v;
            if (p >= s.Length)
            {
                v = -1;
            }
            else if (memo != null && memo[p] != 0)
            {
                v = memo[p] - 2;
            }
            else
            {
                int atomEnd = CSharpScanner.SkipAtom(s, p);
                if (atomEnd > p)
                {
                    path?.Add(p);
                    p = atomEnd;
                    continue;
                }
                char c = s[p];
                if (CSharpScanner.CloserOf(c) != '\0')
                {
                    path?.Add(p);
                    openers.Add(p);
                    pathStart.Add(path?.Count ?? 0);
                    p++;
                    continue;
                }
                if (c != ')' && c != ']' && c != '}')
                {
                    path?.Add(p);
                    p++;
                    continue;
                }
                v = p;
            }

            // v 是当前层的 N 值：记入缓存并结束这一层；与该层的开定界符配对则在外层从 v + 1 继续，否则外层的值也是 v
            while (true)
            {
                int top = openers.Count - 1;
                int o = openers[top];
                if (path != null)
                {
                    int from = pathStart[top];
                    for (int k = from; k < path.Count; k++) memo![path[k]] = v + 2;
                    path.RemoveRange(from, path.Count - from);
                }
                openers.RemoveAt(top);
                pathStart.RemoveAt(top);
                if (o < 0) return v;
                if (v >= 0 && s[v] == CSharpScanner.CloserOf(s[o]))
                {
                    p = v + 1;
                    break;
                }
            }
        }
    }
}
