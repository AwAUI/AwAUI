using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Media;

/// <summary>Media Queries 4 的语法解析（在组件值上工作）。无效查询变为 <c>not all</c> 并给出诊断。</summary>
internal static class MediaQueryParser
{
    enum ValueType { Length, Ratio, Resolution, Discrete }

    sealed class FeatureDef
    {
        public FeatureDef(ValueType type, params string[] keywords)
        {
            Type = type;
            Keywords = keywords;
        }

        public ValueType Type { get; }
        public string[] Keywords { get; }
        public bool IsRange => Type != ValueType.Discrete;
    }

    static FeatureDef? Lookup(string name) => name switch
    {
        "width" or "height" => new FeatureDef(ValueType.Length),
        "aspect-ratio" => new FeatureDef(ValueType.Ratio),
        "resolution" => new FeatureDef(ValueType.Resolution),
        "orientation" => new FeatureDef(ValueType.Discrete, "portrait", "landscape"),
        "prefers-color-scheme" => new FeatureDef(ValueType.Discrete, "light", "dark"),
        "prefers-reduced-motion" => new FeatureDef(ValueType.Discrete, "no-preference", "reduce"),
        "hover" or "any-hover" => new FeatureDef(ValueType.Discrete, "none", "hover"),
        "pointer" or "any-pointer" => new FeatureDef(ValueType.Discrete, "none", "coarse", "fine"),
        _ => null,
    };

    static readonly string[] KnownMediaTypes = { "all", "screen", "print", "tty", "tv", "projection", "handheld", "braille", "embossed", "aural", "speech" };

    public static MediaQueryList Parse(IReadOnlyList<CssComponentValue> values, List<CssDiagnostic> diagnostics)
    {
        bool any = false;
        foreach (var v in values)
            if (v is not CssToken { Type: CssTokenType.Whitespace }) { any = true; break; }
        if (!any) return MediaQueryList.Empty;

        var queries = new List<MediaQuery>();
        int start = 0;
        for (int i = 0; i <= values.Count; i++)
        {
            if (i == values.Count || values[i] is CssToken { Type: CssTokenType.Comma })
            {
                queries.Add(ParseQuery(values, start, i, diagnostics));
                start = i + 1;
            }
        }
        return new MediaQueryList(queries);
    }

    /// <summary>去掉空白后的组件值游标。</summary>
    struct Cursor
    {
        readonly IReadOnlyList<CssComponentValue> _v;
        readonly int _end;
        int _i;

        public Cursor(IReadOnlyList<CssComponentValue> values, int start, int end)
        {
            _v = values;
            _i = start;
            _end = end;
            SkipWs();
        }

        void SkipWs()
        {
            while (_i < _end && _v[_i] is CssToken { Type: CssTokenType.Whitespace }) _i++;
        }

        public bool AtEnd => _i >= _end;
        public CssComponentValue? Peek => _i < _end ? _v[_i] : null;

        public CssComponentValue? PeekAt(int k)
        {
            int j = _i;
            for (int n = 0; j < _end; j++)
            {
                if (_v[j] is CssToken { Type: CssTokenType.Whitespace }) continue;
                if (n++ == k) return _v[j];
            }
            return null;
        }

        public CssComponentValue Next()
        {
            var v = _v[_i++];
            SkipWs();
            return v;
        }

        public bool PeekIdent(string lower) => Peek is CssToken t && t.IsIdent(lower);
    }

    static MediaQuery ParseQuery(IReadOnlyList<CssComponentValue> values, int start, int end, List<CssDiagnostic> diagnostics)
    {
        var range = RangeOf(values, start, end);
        var c = new Cursor(values, start, end);
        var modifier = MediaQueryModifier.None;
        string? type = null;
        MediaCondition? condition = null;
        bool ok;

        if (c.Peek is CssToken { Type: CssTokenType.Ident } first)
        {
            string lower = CssSerializer.ToAsciiLower(first.Value);
            if ((lower == "not" || lower == "only") && c.PeekAt(1) is CssToken { Type: CssTokenType.Ident })
            {
                c.Next();
                modifier = lower == "not" ? MediaQueryModifier.Not : MediaQueryModifier.Only;
                ok = ParseTypeAndCondition(ref c, ref type, ref condition, diagnostics);
            }
            else if (lower == "not")
            {
                condition = ParseCondition(ref c, allowOr: true, diagnostics);
                ok = condition is not null && c.AtEnd;
            }
            else
            {
                ok = ParseTypeAndCondition(ref c, ref type, ref condition, diagnostics);
            }
        }
        else
        {
            condition = ParseCondition(ref c, allowOr: true, diagnostics);
            ok = condition is not null && c.AtEnd;
        }

        if (!ok)
        {
            diagnostics.Add(new CssDiagnostic(CssDiagnosticCodes.InvalidMediaQuery, CssDiagnosticSeverity.Error,
                "无效的媒体查询，按规范视为 not all（永不成立）。", range));
            return new MediaQuery(MediaQueryModifier.None, null, null, invalid: true, range);
        }
        return new MediaQuery(modifier, type, condition, invalid: false, range);
    }

    static bool ParseTypeAndCondition(ref Cursor c, ref string? type, ref MediaCondition? condition, List<CssDiagnostic> diagnostics)
    {
        if (c.Peek is not CssToken { Type: CssTokenType.Ident } t) return false;
        string lower = CssSerializer.ToAsciiLower(t.Value);
        if (lower is "not" or "only" or "and" or "or" or "layer") return false;
        c.Next();
        type = lower;
        if (Array.IndexOf(KnownMediaTypes, lower) < 0)
        {
            diagnostics.Add(new CssDiagnostic(CssDiagnosticCodes.InvalidMediaQuery, CssDiagnosticSeverity.Warning,
                $"未知的媒体类型 '{t.Value}'，该查询永不成立。", t.Range));
        }
        if (c.AtEnd) return true;
        if (!c.PeekIdent("and")) return false;
        c.Next();
        condition = ParseCondition(ref c, allowOr: false, diagnostics);
        return condition is not null && c.AtEnd;
    }

    static MediaCondition? ParseCondition(ref Cursor c, bool allowOr, List<CssDiagnostic> diagnostics)
    {
        if (c.PeekIdent("not"))
        {
            c.Next();
            var operand = ParseInParens(ref c, diagnostics);
            return operand is null ? null : new MediaNotCondition(operand);
        }
        var first = ParseInParens(ref c, diagnostics);
        if (first is null) return null;
        bool isAnd;
        if (c.PeekIdent("and")) isAnd = true;
        else if (c.PeekIdent("or")) { if (!allowOr) return null; isAnd = false; }
        else return first;

        var list = new List<MediaCondition> { first };
        string op = isAnd ? "and" : "or";
        while (c.PeekIdent(op))
        {
            c.Next();
            var next = ParseInParens(ref c, diagnostics);
            if (next is null) return null;
            list.Add(next);
        }
        if (c.PeekIdent(isAnd ? "or" : "and")) return null; // 不能混用 and 与 or
        return new MediaCombinedCondition(isAnd, list);
    }

    static MediaCondition? ParseInParens(ref Cursor c, List<CssDiagnostic> diagnostics)
    {
        var v = c.Peek;
        if (v is CssSimpleBlock { Open: '(' } block)
        {
            c.Next();
            // ( <media-condition> )
            var inner = new Cursor(block.Values, 0, block.Values.Count);
            if (!inner.AtEnd && (inner.Peek is CssSimpleBlock { Open: '(' } || inner.PeekIdent("not") || inner.Peek is CssFunction))
            {
                var probe = new List<CssDiagnostic>();
                var cond = ParseCondition(ref inner, allowOr: true, probe);
                if (cond is not null && inner.AtEnd)
                {
                    diagnostics.AddRange(probe);
                    return cond;
                }
            }
            return ParseFeature(block, diagnostics);
        }
        if (v is CssFunction f)
        {
            c.Next();
            diagnostics.Add(new CssDiagnostic(CssDiagnosticCodes.InvalidMediaQuery, CssDiagnosticSeverity.Warning,
                $"无法识别的媒体条件 '{f.ToCss()}'，按规范视为未知（不成立）。", f.Range));
            return new MediaGeneralEnclosed(f.ToCss());
        }
        return null;
    }

    /// <summary>解析 <c>( &lt;media-feature&gt; )</c>；语法不是特性时作为 general-enclosed（未知）。</summary>
    static MediaCondition ParseFeature(CssSimpleBlock block, List<CssDiagnostic> diagnostics)
    {
        string text = block.ToCss();
        var items = new List<CssComponentValue>();
        var wsBefore = new List<bool>();
        bool ws = false;
        foreach (var v in block.Values)
        {
            if (v is CssToken { Type: CssTokenType.Whitespace }) { ws = true; continue; }
            items.Add(v);
            wsBefore.Add(ws);
            ws = false;
        }

        if (TryParseFeatureSyntax(items, wsBefore, out string? rawName, out var constraints))
        {
            string name = CssSerializer.ToAsciiLower(rawName!);
            string? prefix = null;
            if (name.StartsWith("min-", StringComparison.Ordinal)) { prefix = "min"; name = name.Substring(4); }
            else if (name.StartsWith("max-", StringComparison.Ordinal)) { prefix = "max"; name = name.Substring(4); }

            var def = Lookup(name);
            string? problem = null;
            if (def is null)
            {
                problem = $"不支持的媒体特性 '{rawName}'";
            }
            else if (prefix is not null)
            {
                // min-/max- 只用于范围型特性的普通形式
                if (!def.IsRange || constraints.Count != 1 || constraints[0].Comparison != MediaComparison.Equal || items.Count < 2
                    || items[1] is not CssToken { Type: CssTokenType.Colon })
                {
                    problem = $"'{rawName}' 只能以 '{rawName}: 值' 的形式使用";
                }
                else
                {
                    constraints[0] = new MediaFeatureConstraint(prefix == "min" ? MediaComparison.GreaterOrEqual : MediaComparison.LessOrEqual,
                        constraints[0].Value);
                }
            }
            if (problem is null && def is not null)
            {
                foreach (var con in constraints)
                {
                    if (!def.IsRange && con.Comparison != MediaComparison.Equal) { problem = $"离散特性 '{name}' 不能使用范围比较"; break; }
                    if (!IsValidValue(def, con.Value)) { problem = $"媒体特性 '{name}' 的值 '{con.Value}' 无效"; break; }
                }
            }
            if (problem is null)
                return new MediaFeature(name, constraints, text);

            diagnostics.Add(new CssDiagnostic(CssDiagnosticCodes.InvalidMediaQuery, CssDiagnosticSeverity.Warning,
                problem + "，该条件按规范视为未知（不成立）。支持：width、height、aspect-ratio、orientation、resolution、prefers-color-scheme、prefers-reduced-motion、hover、pointer、any-hover、any-pointer。",
                block.Range));
            return new MediaGeneralEnclosed(text);
        }

        diagnostics.Add(new CssDiagnostic(CssDiagnosticCodes.InvalidMediaQuery, CssDiagnosticSeverity.Warning,
            $"无法识别的媒体条件 '{text}'，按规范视为未知（不成立）。", block.Range));
        return new MediaGeneralEnclosed(text);
    }

    static bool IsValidValue(FeatureDef def, MediaValue v)
    {
        switch (def.Type)
        {
            case ValueType.Length:
                return (v.Kind == MediaValueKind.Number && v.Number == 0)
                    || (v.Kind == MediaValueKind.Dimension && !double.IsNaN(MediaFeature.LengthFactor(v.Unit, null)));
            case ValueType.Resolution:
                return (v.Kind == MediaValueKind.Dimension && !double.IsNaN(MediaFeature.ResolutionFactor(v.Unit)) && v.Number >= 0)
                    || (v.Kind == MediaValueKind.Ident && v.Ident == "infinite");
            case ValueType.Ratio:
                return (v.Kind == MediaValueKind.Number && v.Number >= 0)
                    || (v.Kind == MediaValueKind.Ratio && v.Number >= 0 && v.Denominator >= 0);
            default:
                return v.Kind == MediaValueKind.Ident && Array.IndexOf(def.Keywords, v.Ident) >= 0;
        }
    }

    static bool TryParseFeatureSyntax(List<CssComponentValue> items, List<bool> wsBefore, out string? name, out List<MediaFeatureConstraint> constraints)
    {
        name = null;
        constraints = new List<MediaFeatureConstraint>();
        if (items.Count == 0) return false;

        // <mf-boolean>
        if (items.Count == 1)
        {
            if (items[0] is CssToken { Type: CssTokenType.Ident } b) { name = b.Value; return true; }
            return false;
        }

        // <mf-plain>
        if (items[0] is CssToken { Type: CssTokenType.Ident } n0 && items[1] is CssToken { Type: CssTokenType.Colon })
        {
            int k = 2;
            if (!TryValue(items, ref k, out var value) || k != items.Count) return false;
            name = n0.Value;
            constraints.Add(new MediaFeatureConstraint(MediaComparison.Equal, value));
            return true;
        }

        // <mf-name> <mf-comparison> <mf-value>
        if (items[0] is CssToken { Type: CssTokenType.Ident } n1)
        {
            int k = 1;
            if (TryComparison(items, wsBefore, ref k, out var op) && TryValue(items, ref k, out var value) && k == items.Count)
            {
                name = n1.Value;
                constraints.Add(new MediaFeatureConstraint(op, value));
                return true;
            }
        }

        // <mf-value> <mf-comparison> <mf-name> [<mf-comparison> <mf-value>]?
        {
            int k = 0;
            if (!TryValue(items, ref k, out var left)) return false;
            if (!TryComparison(items, wsBefore, ref k, out var op1)) return false;
            if (k >= items.Count || items[k] is not CssToken { Type: CssTokenType.Ident } nm) return false;
            k++;
            name = nm.Value;
            constraints.Add(new MediaFeatureConstraint(Flip(op1), left));
            if (k == items.Count) return true;
            if (!TryComparison(items, wsBefore, ref k, out var op2)) return false;
            if (!TryValue(items, ref k, out var right) || k != items.Count) return false;
            bool lt1 = op1 is MediaComparison.Less or MediaComparison.LessOrEqual;
            bool lt2 = op2 is MediaComparison.Less or MediaComparison.LessOrEqual;
            bool gt1 = op1 is MediaComparison.Greater or MediaComparison.GreaterOrEqual;
            bool gt2 = op2 is MediaComparison.Greater or MediaComparison.GreaterOrEqual;
            if (!((lt1 && lt2) || (gt1 && gt2))) return false;
            constraints.Add(new MediaFeatureConstraint(op2, right));
            return true;
        }
    }

    static MediaComparison Flip(MediaComparison op) => op switch
    {
        MediaComparison.Less => MediaComparison.Greater,
        MediaComparison.LessOrEqual => MediaComparison.GreaterOrEqual,
        MediaComparison.Greater => MediaComparison.Less,
        MediaComparison.GreaterOrEqual => MediaComparison.LessOrEqual,
        _ => MediaComparison.Equal,
    };

    static bool TryComparison(List<CssComponentValue> items, List<bool> wsBefore, ref int k, out MediaComparison op)
    {
        op = MediaComparison.Equal;
        if (k >= items.Count || items[k] is not CssToken { Type: CssTokenType.Delim } d) return false;
        bool eqFollows = k + 1 < items.Count && items[k + 1] is CssToken e && e.IsDelim('=') && !wsBefore[k + 1];
        switch (d.Delim)
        {
            case '=':
                k++;
                op = MediaComparison.Equal;
                return true;
            case '<':
                op = eqFollows ? MediaComparison.LessOrEqual : MediaComparison.Less;
                k += eqFollows ? 2 : 1;
                return true;
            case '>':
                op = eqFollows ? MediaComparison.GreaterOrEqual : MediaComparison.Greater;
                k += eqFollows ? 2 : 1;
                return true;
            default:
                return false;
        }
    }

    static bool TryValue(List<CssComponentValue> items, ref int k, out MediaValue value)
    {
        value = default;
        if (k >= items.Count || items[k] is not CssToken t) return false;
        switch (t.Type)
        {
            case CssTokenType.Number:
                if (k + 2 < items.Count && items[k + 1] is CssToken slash && slash.IsDelim('/')
                    && items[k + 2] is CssToken { Type: CssTokenType.Number } den)
                {
                    value = new MediaValue(MediaValueKind.Ratio, t.Number, string.Empty, string.Empty, den.Number);
                    k += 3;
                    return true;
                }
                value = new MediaValue(MediaValueKind.Number, t.Number, string.Empty, string.Empty, 1);
                k++;
                return true;
            case CssTokenType.Dimension:
                value = new MediaValue(MediaValueKind.Dimension, t.Number, CssSerializer.ToAsciiLower(t.Unit), string.Empty, 1);
                k++;
                return true;
            case CssTokenType.Ident:
                value = new MediaValue(MediaValueKind.Ident, 0, string.Empty, CssSerializer.ToAsciiLower(t.Value), 1);
                k++;
                return true;
            default:
                return false;
        }
    }

    static SourceRange RangeOf(IReadOnlyList<CssComponentValue> values, int start, int end)
    {
        int a = start, b = end - 1;
        while (a <= b && values[a] is CssToken { Type: CssTokenType.Whitespace }) a++;
        while (b >= a && values[b] is CssToken { Type: CssTokenType.Whitespace }) b--;
        if (a > b)
        {
            if (start < values.Count) return new SourceRange(values[start].Range.Start, values[start].Range.Start);
            return values.Count > 0 ? new SourceRange(values[values.Count - 1].Range.End, values[values.Count - 1].Range.End) : default;
        }
        return new SourceRange(values[a].Range.Start, values[b].Range.End);
    }
}
