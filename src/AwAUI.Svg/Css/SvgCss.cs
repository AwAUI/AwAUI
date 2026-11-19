using System;
using System.Collections.Generic;
using AwAUI.Svg.Model;

namespace AwAUI.Svg.Css;

/// <summary>一条样式声明。</summary>
internal readonly struct CssDeclaration
{
    public CssDeclaration(SvgProperty property, string value, bool important)
    {
        Property = property;
        Value = value;
        Important = important;
    }

    public SvgProperty Property { get; }
    public string Value { get; }
    public bool Important { get; }
}

/// <summary>style="" 与规则块中的声明列表解析。未知属性静默跳过。</summary>
internal static class CssDeclarations
{
    public static void Parse(string text, List<CssDeclaration> output, DiagnosticBag? diag = null, int offset = -1) =>
        Parse(text, 0, text.Length, output, diag, offset);

    /// <summary>
    /// 解析 [start, end) 中的声明。值在语法上无效的声明按 CSS 规则在这里就丢弃（记录诊断），
    /// 不参与层叠——否则它会盖掉同一属性的有效表现属性。
    /// </summary>
    public static void Parse(string text, int start, int end, List<CssDeclaration> output, DiagnosticBag? diag = null, int offset = -1)
    {
        if (text.IndexOf("/*", start, end - start, StringComparison.Ordinal) >= 0)
        {
            // 注释在 CSS 中等同于空白（可能出现在声明之间，也可能夹在值里）：先整体替换掉再解析
            text = StripComments(text, start, end);
            start = 0;
            end = text.Length;
        }
        int i = start;
        while (i < end)
        {
            // 找本条声明的结尾 ';'（跳过括号与字符串内部）
            int declStart = i;
            int depth = 0;
            while (i < end)
            {
                char c = text[i];
                if (c is '"' or '\'')
                {
                    int q = text.IndexOf(c, i + 1, end - i - 1);
                    i = q < 0 ? end : q + 1;
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ';' && depth == 0) break;
                i++;
            }
            AddDeclaration(text, declStart, i, output, diag, offset);
            i++; // ';'
        }
    }

    /// <summary>把 [start, end) 中字符串之外的 /* … */ 注释替换为一个空格（未闭合的注释延伸到结尾）。线性。</summary>
    internal static string StripComments(string text, int start, int end)
    {
        var sb = new System.Text.StringBuilder(end - start);
        int i = start;
        while (i < end)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int q = text.IndexOf(c, i + 1, end - i - 1);
                int stop = q < 0 ? end : q + 1;
                sb.Append(text, i, stop - i);
                i = stop;
                continue;
            }
            if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                int close = i + 2 < end ? text.IndexOf("*/", i + 2, end - i - 2, StringComparison.Ordinal) : -1;
                i = close < 0 ? end : close + 2;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    static void AddDeclaration(string text, int start, int end, List<CssDeclaration> output, DiagnosticBag? diag, int offset)
    {
        int colon = text.IndexOf(':', start, end - start);
        if (colon < 0) return;
        var name = text.Substring(start, colon - start).Trim().ToLowerInvariant();
        if (!SvgProperties.TryGet(name, out var prop)) return;
        var value = text.Substring(colon + 1, end - colon - 1).Trim();
        bool important = false;
        int bang = value.LastIndexOf('!');
        if (bang >= 0 && string.Equals(value.Substring(bang + 1).Trim(), "important", StringComparison.OrdinalIgnoreCase))
        {
            important = true;
            value = value.Substring(0, bang).Trim();
        }
        if (value.Length == 0) return;
        if (!Rendering.ComputedStyle.IsValidDeclaration(prop, value))
        {
            diag?.Add(SvgDiagnosticCode.InvalidValue,
                "Invalid CSS value '" + (value.Length > 40 ? value.Substring(0, 40) + "…" : value) + "' for '" + name + "' was ignored.", offset);
            return;
        }
        output.Add(new CssDeclaration(prop, value, important));
    }
}

internal enum CssCombinator : byte { Descendant, Child }

/// <summary>复合选择器：类型（或 *）+ 若干 .class / #id。</summary>
internal sealed class CssCompound
{
    public string? Type;
    public string? Id;
    public List<string>? Classes;

    public bool Matches(SvgNode node)
    {
        if (Type != null && Type != node.Name) return false;
        if (Id != null && Id != node.Id) return false;
        if (Classes != null)
        {
            foreach (var c in Classes)
                if (!node.HasClass(c)) return false;
        }
        return true;
    }

    /// <summary>一次 <see cref="Matches"/> 的工作量（计入选择器预算）。</summary>
    public int Cost => 1 + (Classes?.Count ?? 0);
}

/// <summary>复杂选择器：复合选择器序列（最右侧为主体）与组合符。</summary>
internal sealed class CssSelector
{
    public CssSelector(CssCompound[] compounds, CssCombinator[] combinators)
    {
        Compounds = compounds;
        Combinators = combinators;
        int ids = 0, classes = 0, types = 0;
        foreach (var c in compounds)
        {
            if (c.Id != null) ids++;
            classes += c.Classes?.Count ?? 0;
            if (c.Type != null) types++;
        }
        Specificity = Math.Min(ids, 255) << 16 | Math.Min(classes, 255) << 8 | Math.Min(types, 255);
    }

    public CssCompound[] Compounds { get; }

    /// <summary>Combinators[i] 位于 Compounds[i] 与 Compounds[i + 1] 之间。</summary>
    public CssCombinator[] Combinators { get; }
    public int Specificity { get; }
    public CssCompound Subject => Compounds[Compounds.Length - 1];
}

internal sealed class CssRule
{
    public CssRule(CssSelector selector, CssDeclaration[] declarations, int order)
    {
        Selector = selector;
        Declarations = declarations;
        Order = order;
    }

    public CssSelector Selector { get; }
    public CssDeclaration[] Declarations { get; }
    public int Order { get; }
}

/// <summary>
/// &lt;style&gt; 的最小实现：选择器列表，复合选择器只含类型 / * / .class / #id，组合符只有后代与子代。
/// 属性选择器、伪类、兄弟组合符和全部 @ 规则（含 @media）不支持，整条规则被忽略并记录诊断。
/// 匹配按最右复合选择器建索引，并受全局工作量预算约束。
/// </summary>
internal sealed class SvgStyleSheet
{
    const int MaxCompounds = 32;

    readonly List<CssRule> _rules = new();
    readonly Dictionary<string, List<CssRule>> _byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<CssRule>> _byClass = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<CssRule>> _byType = new(StringComparer.Ordinal);
    readonly List<CssRule> _universal = new();
    readonly List<CssRule> _matched = new();
    long _budget;
    bool _budgetReported;

    public SvgStyleSheet(long workBudget) => _budget = workBudget;

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>解析一段样式表文本并追加到本表。</summary>
    public void Add(string text, DiagnosticBag diag, int offset)
    {
        int i = 0, n = text.Length;
        var decls = new List<CssDeclaration>();
        while (i < n)
        {
            SkipTrivia(text, ref i);
            if (i >= n) break;
            if (text[i] == '@')
            {
                int end = FindAny(text, i, ";{");
                diag.Add(SvgDiagnosticCode.UnsupportedCss, "At-rules in <style> are not supported and were ignored.", offset);
                if (end < 0) break;
                i = text[end] == '{' ? SkipBlock(text, end) : end + 1;
                continue;
            }
            int brace = FindAny(text, i, "{");
            if (brace < 0) break;
            int blockEnd = SkipBlock(text, brace); // 指向 '}' 之后
            var prelude = text.IndexOf("/*", i, brace - i, StringComparison.Ordinal) >= 0
                ? CssDeclarations.StripComments(text, i, brace)
                : text.Substring(i, brace - i);
            int bodyEnd = blockEnd > brace + 1 && text[blockEnd - 1] == '}' ? blockEnd - 1 : blockEnd;
            decls.Clear();
            CssDeclarations.Parse(text, brace + 1, bodyEnd, decls, diag, offset);
            i = blockEnd;

            var selectors = ParseSelectorList(prelude);
            if (selectors == null)
            {
                diag.Add(SvgDiagnosticCode.UnsupportedCss,
                    "Unsupported selector '" + Truncate(prelude.Trim()) + "'; the rule was ignored.", offset);
                continue;
            }
            if (decls.Count == 0) continue;
            var declArray = Compact(decls);
            foreach (var sel in selectors) AddRule(new CssRule(sel, declArray, _rules.Count));
        }
    }

    /// <summary>
    /// 同一规则内每个（属性, 重要性）只有最后一条声明有效：压缩后每条规则至多 2 × 属性数 条声明，
    /// 层叠时一个元素的代价与规则长度无关（否则「* { 一百万条声明 }」× 每个元素是二次方的）。
    /// </summary>
    static CssDeclaration[] Compact(List<CssDeclaration> decls)
    {
        if (decls.Count <= 1) return decls.ToArray();
        var last = new int[SvgProperties.Count * 2];
        for (int i = 0; i < decls.Count; i++) last[(int)decls[i].Property * 2 + (decls[i].Important ? 1 : 0)] = i + 1;
        var result = new List<CssDeclaration>();
        for (int i = 0; i < decls.Count; i++)
            if (last[(int)decls[i].Property * 2 + (decls[i].Important ? 1 : 0)] == i + 1) result.Add(decls[i]);
        return result.ToArray();
    }

    static string Truncate(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

    void AddRule(CssRule rule)
    {
        _rules.Add(rule);
        var subject = rule.Selector.Subject;
        if (subject.Id != null) Bucket(_byId, subject.Id).Add(rule);
        else if (subject.Classes is { Count: > 0 }) Bucket(_byClass, subject.Classes[0]).Add(rule);
        else if (subject.Type != null) Bucket(_byType, subject.Type).Add(rule);
        else _universal.Add(rule);
    }

    static List<CssRule> Bucket(Dictionary<string, List<CssRule>> map, string key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<CssRule>();
        return list;
    }

    /// <summary>
    /// 返回匹配 <paramref name="node"/> 的规则，按（特异性，出现顺序）升序。
    /// 返回的列表在下次调用前有效。预算耗尽后不再匹配任何规则。
    /// </summary>
    public List<CssRule> Match(SvgNode node, DiagnosticBag diag)
    {
        _matched.Clear();
        if (_rules.Count == 0) return _matched;
        if (node.Id != null && _byId.TryGetValue(node.Id, out var l)) Collect(l, node);
        foreach (var c in node.Classes)
            if (_byClass.TryGetValue(c, out l)) Collect(l, node);
        if (_byType.TryGetValue(node.Name, out l)) Collect(l, node);
        Collect(_universal, node);
        if (_budget < 0 && !_budgetReported)
        {
            _budgetReported = true;
            diag.Add(SvgDiagnosticCode.LimitExceeded, "Selector matching budget exhausted; remaining style rules were not applied.", node.Offset);
        }
        if (_matched.Count > 1)
            _matched.Sort((a, b) => a.Selector.Specificity != b.Selector.Specificity
                ? a.Selector.Specificity.CompareTo(b.Selector.Specificity)
                : a.Order.CompareTo(b.Order));
        return _matched;
    }

    void Collect(List<CssRule> rules, SvgNode node)
    {
        foreach (var r in rules)
        {
            if (--_budget < 0) return;
            if (MatchFrom(r.Selector, r.Selector.Compounds.Length - 1, node))
            {
                _matched.Add(r);
                _budget -= r.Declarations.Length; // 层叠时要逐条应用
            }
        }
    }

    bool MatchFrom(CssSelector sel, int index, SvgNode node)
    {
        var compound = sel.Compounds[index];
        _budget -= compound.Cost;
        if (_budget < 0 || !compound.Matches(node)) return false;
        if (index == 0) return true;
        if (sel.Combinators[index - 1] == CssCombinator.Child)
            return node.Parent != null && MatchFrom(sel, index - 1, node.Parent);
        for (var p = node.Parent; p != null; p = p.Parent)
        {
            if (--_budget < 0) return false;
            if (MatchFrom(sel, index - 1, p)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 选择器解析

    static List<CssSelector>? ParseSelectorList(string prelude)
    {
        var result = new List<CssSelector>();
        foreach (var part in prelude.Split(','))
        {
            var sel = ParseComplex(part);
            if (sel == null) return null;
            result.Add(sel);
        }
        return result;
    }

    static CssSelector? ParseComplex(string s)
    {
        var compounds = new List<CssCompound>();
        var combinators = new List<CssCombinator>();
        int i = 0, n = s.Length;
        SkipWs(s, ref i);
        if (i >= n) return null;
        while (i < n)
        {
            var compound = ParseCompound(s, ref i);
            if (compound == null || compounds.Count >= MaxCompounds) return null;
            compounds.Add(compound);
            bool ws = SkipWs(s, ref i);
            if (i >= n) break;
            if (s[i] == '>')
            {
                i++;
                SkipWs(s, ref i);
                combinators.Add(CssCombinator.Child);
            }
            else if (ws) combinators.Add(CssCombinator.Descendant);
            else return null;
            if (i >= n) return null;
        }
        if (combinators.Count != compounds.Count - 1) return null;
        return new CssSelector(compounds.ToArray(), combinators.ToArray());
    }

    static CssCompound? ParseCompound(string s, ref int i)
    {
        var c = new CssCompound();
        bool any = false;
        if (i < s.Length && s[i] == '*') { i++; any = true; }
        else if (i < s.Length && IsIdentStart(s[i]))
        {
            c.Type = ReadIdent(s, ref i);
            any = true;
        }
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch == '.' || ch == '#')
            {
                i++;
                if (i >= s.Length || !IsIdentStart(s[i]) && s[i] != '-') return null;
                var ident = ReadIdent(s, ref i);
                if (ch == '.') (c.Classes ??= new List<string>()).Add(ident);
                else if (c.Id != null && c.Id != ident) c.Id = "\0"; // 两个不同 id：永不匹配
                else c.Id = ident;
                any = true;
            }
            else if (IsSeparator(ch)) break;
            else return null; // [ : + ~ \ 等不支持
        }
        return any ? c : null;
    }

    static bool IsSeparator(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '>';
    static bool IsIdentStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || c >= 0x80;
    static bool IsIdentChar(char c) => IsIdentStart(c) || c is >= '0' and <= '9' or '-';

    static string ReadIdent(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && IsIdentChar(s[i])) i++;
        return s.Substring(start, i - start);
    }

    static bool SkipWs(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && s[i] is ' ' or '\t' or '\r' or '\n' or '\f') i++;
        return i > start;
    }

    // ---------------------------------------------------------------- 词法辅助

    /// <summary>跳过空白、注释与 CDO/CDC。</summary>
    static void SkipTrivia(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (string.CompareOrdinal(s, i, "/*", 0, 2) == 0)
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                continue;
            }
            if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0) { i += 4; continue; }
            if (string.CompareOrdinal(s, i, "-->", 0, 3) == 0) { i += 3; continue; }
            break;
        }
    }

    /// <summary>从 i 起找第一个出现在 <paramref name="chars"/> 中的字符（跳过字符串与注释）。</summary>
    static int FindAny(string s, int i, string chars)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                int q = s.IndexOf(c, i + 1);
                if (q < 0) return -1;
                i = q + 1;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + 2;
                continue;
            }
            if (chars.IndexOf(c) >= 0) return i;
            i++;
        }
        return -1;
    }

    /// <summary>从 '{' 开始跳过一个平衡的块，返回 '}' 之后的位置（未闭合时返回文本末尾）。</summary>
    static int SkipBlock(string s, int open)
    {
        int depth = 0;
        int i = open;
        while (i < s.Length)
        {
            int next = FindAny(s, i, "{}");
            if (next < 0) return s.Length;
            if (s[next] == '{') depth++;
            else if (--depth == 0) return next + 1;
            i = next + 1;
        }
        return s.Length;
    }
}
