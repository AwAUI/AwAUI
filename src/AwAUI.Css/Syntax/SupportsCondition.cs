using System.Collections.Generic;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.Syntax;

/// <summary>
/// <c>@supports</c> 条件（CSS Conditional 3/4）按本引擎的实际支持情况求值：
/// <list type="bullet">
/// <item><c>(prop: value)</c>：属性受支持（自定义属性总是受支持）且值能按其语法解析；</item>
/// <item><c>selector(S)</c>：S 是本引擎能解析的单个复杂选择器；</item>
/// <item><c>font-tech()</c>、<c>font-format()</c> 与其他无法识别的形式（general-enclosed）不成立。</item>
/// </list>
/// 语法错误返回 null（整条规则按不成立处理）。
/// </summary>
internal static class SupportsCondition
{
    const int MaxDepth = 32;

    public static bool? Evaluate(IReadOnlyList<CssComponentValue> values, int start = 0)
    {
        int i = start;
        var r = Condition(values, ref i, 0);
        SkipWs(values, ref i);
        return r is not null && i == values.Count ? r : null;
    }

    static void SkipWs(IReadOnlyList<CssComponentValue> v, ref int i)
    {
        while (i < v.Count && v[i] is CssToken { Type: CssTokenType.Whitespace }) i++;
    }

    static bool IsIdent(IReadOnlyList<CssComponentValue> v, int i, string lower) => i < v.Count && v[i] is CssToken t && t.IsIdent(lower);

    static bool? Condition(IReadOnlyList<CssComponentValue> v, ref int i, int depth)
    {
        if (depth > MaxDepth) return null;
        SkipWs(v, ref i);
        if (IsIdent(v, i, "not"))
        {
            i++;
            SkipWs(v, ref i);
            var x = InParens(v, ref i, depth);
            return x is null ? null : !x.Value;
        }
        var first = InParens(v, ref i, depth);
        if (first is null) return null;
        bool result = first.Value;
        string? op = null;
        while (true)
        {
            int save = i;
            SkipWs(v, ref i);
            string? cur = IsIdent(v, i, "and") ? "and" : IsIdent(v, i, "or") ? "or" : null;
            if (cur is null) { i = save; break; }
            if (op is not null && op != cur) return null; // 不能混用 and 与 or
            op = cur;
            i++;
            SkipWs(v, ref i);
            var next = InParens(v, ref i, depth);
            if (next is null) return null;
            result = cur == "and" ? result && next.Value : result || next.Value;
        }
        return result;
    }

    static bool? InParens(IReadOnlyList<CssComponentValue> v, ref int i, int depth)
    {
        if (i >= v.Count) return null;
        var c = v[i];
        if (c is CssSimpleBlock { Open: '(' } block)
        {
            i++;
            var inner = block.Values;
            // ( <supports-condition> )
            int k = 0;
            SkipWs(inner, ref k);
            if (k < inner.Count && (inner[k] is CssSimpleBlock { Open: '(' } || IsIdent(inner, k, "not") || inner[k] is CssFunction))
            {
                int j = 0;
                var cond = Condition(inner, ref j, depth + 1);
                SkipWs(inner, ref j);
                if (cond is not null && j == inner.Count) return cond;
            }
            // ( <declaration> )
            return Declaration(inner);
        }
        if (c is CssFunction f)
        {
            i++;
            switch (CssSerializer.ToAsciiLower(f.Name))
            {
                case "selector":
                {
                    var diags = new List<CssDiagnostic>();
                    var range = f.Arguments.Count > 0 ? new SourceRange(f.Arguments[0].Range.Start, f.Arguments[f.Arguments.Count - 1].Range.End) : default;
                    var list = SelectorParser.ParseRulePrelude(f.Arguments, null, false, diags, range);
                    return list is { Count: 1 };
                }
                default:
                    // font-tech()、font-format() 等：本引擎不支持字体技术查询
                    return false;
            }
        }
        // <general-enclosed> 之外的记号是语法错误
        return null;
    }

    /// <summary><c>(name: value)</c>：属性已知且值有效；不是声明形式时按 general-enclosed（不成立）。</summary>
    static bool? Declaration(IReadOnlyList<CssComponentValue> inner)
    {
        int k = 0;
        SkipWs(inner, ref k);
        if (k >= inner.Count || inner[k] is not CssToken { Type: CssTokenType.Ident } name) return false;
        k++;
        SkipWs(inner, ref k);
        if (k >= inner.Count || inner[k] is not CssToken { Type: CssTokenType.Colon }) return false;
        k++;
        var value = new List<CssComponentValue>();
        for (; k < inner.Count; k++) value.Add(inner[k]);
        // 去掉首尾空白与 !important
        Trim(value);
        if (value.Count >= 2 && value[value.Count - 1] is CssToken imp && imp.IsIdent("important"))
        {
            int b = value.Count - 2;
            while (b >= 0 && value[b] is CssToken { Type: CssTokenType.Whitespace }) b--;
            if (b >= 0 && value[b] is CssToken bang && bang.IsDelim('!')) value.RemoveRange(b, value.Count - b);
            Trim(value);
        }
        if (name.Value.StartsWith("--", System.StringComparison.Ordinal)) return true;
        if (value.Count == 0) return false;
        if (!CssProperties.TryGet(name.Value, out var property)) return false;
        return PropertyParsers.TryParseValue(property, value, new List<PropertyValue>());
    }

    static void Trim(List<CssComponentValue> list)
    {
        while (list.Count > 0 && list[list.Count - 1] is CssToken { Type: CssTokenType.Whitespace }) list.RemoveAt(list.Count - 1);
        int n = 0;
        while (n < list.Count && list[n] is CssToken { Type: CssTokenType.Whitespace }) n++;
        if (n > 0) list.RemoveRange(0, n);
    }
}
