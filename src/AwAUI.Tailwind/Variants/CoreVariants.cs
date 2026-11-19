using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Compilation;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Theming;

namespace AwAUI.Tailwind.Variants;

/// <summary>Tailwind CSS v4 的核心变体（移植自 <c>variants.ts</c>，v4.3.5）。注册顺序即输出顺序。</summary>
internal static class CoreVariants
{
    public static VariantRegistry Create(Theme theme)
    {
        var v = new VariantRegistry();

        // 选择器 / at 规则模板变体（Tailwind 的 staticVariant）
        void Sv(string name, string[] selectors, Compounds? compounds = null)
        {
            var c = compounds ?? VariantRegistry.GetCompounds(selectors);
            v.Static(name, r =>
            {
                var body = r.Nodes;
                var list = new List<AstNode>(selectors.Length);
                foreach (var s in selectors) list.Add(AstNode.RuleOrAtRule(s, body));
                r.Nodes = list;
            }, c);
        }

        Sv("*", new[] { ":is(& > *)" }, Compounds.Never);
        Sv("**", new[] { ":is(& *)" }, Compounds.Never);

        v.Compound("not", Compounds.StyleRules | Compounds.AtRules, (c, d) => ApplyNot(c, d));

        string prefix = theme.Prefix != null ? theme.Prefix + "\\:" : "";
        v.Compound("group", Compounds.StyleRules, (c, d) => ApplyRelational(c, d, ":where(." + prefix + "group" + (d.Modifier != null ? "\\/" + d.Modifier.Value : "") + ")", " &"));
        v.Compound("peer", Compounds.StyleRules, (c, d) => ApplyRelational(c, d, ":where(." + prefix + "peer" + (d.Modifier != null ? "\\/" + d.Modifier.Value : "") + ")", " ~ &"));

        Sv("first-letter", new[] { "&::first-letter" });
        Sv("first-line", new[] { "&::first-line" });
        Sv("marker", new[] { "& *::marker", "&::marker", "& *::-webkit-details-marker", "&::-webkit-details-marker" });
        Sv("selection", new[] { "& *::selection", "&::selection" });
        Sv("file", new[] { "&::file-selector-button" });
        Sv("placeholder", new[] { "&::placeholder" });
        Sv("backdrop", new[] { "&::backdrop" });
        Sv("details-content", new[] { "&::details-content" });

        AstNode ContentProperty() => AstNode.AtRoot(new List<AstNode>
        {
            AstNode.AtRule("@property", "--tw-content", new List<AstNode> { AstNode.Decl("syntax", "\"*\""), AstNode.Decl("initial-value", "\"\""), AstNode.Decl("inherits", "false") }),
        });
        foreach (var pseudo in new[] { "before", "after" })
        {
            string p = pseudo;
            v.Static(p, r =>
            {
                var nodes = new List<AstNode> { ContentProperty(), AstNode.Decl("content", "var(--tw-content)") };
                nodes.AddRange(r.Nodes);
                r.Nodes = new List<AstNode> { AstNode.Rule("&::" + p, nodes) };
            }, Compounds.Never);
        }

        foreach (var (name, sel) in new[]
                 {
                     ("first", "&:first-child"), ("last", "&:last-child"), ("only", "&:only-child"), ("odd", "&:nth-child(odd)"), ("even", "&:nth-child(even)"),
                     ("first-of-type", "&:first-of-type"), ("last-of-type", "&:last-of-type"), ("only-of-type", "&:only-of-type"), ("visited", "&:visited"),
                     ("target", "&:target"), ("open", "&:is([open], :popover-open, :open)"), ("default", "&:default"), ("checked", "&:checked"),
                     ("indeterminate", "&:indeterminate"), ("placeholder-shown", "&:placeholder-shown"), ("autofill", "&:autofill"), ("optional", "&:optional"),
                     ("required", "&:required"), ("valid", "&:valid"), ("invalid", "&:invalid"), ("user-valid", "&:user-valid"), ("user-invalid", "&:user-invalid"),
                     ("in-range", "&:in-range"), ("out-of-range", "&:out-of-range"), ("read-only", "&:read-only"), ("empty", "&:empty"), ("focus-within", "&:focus-within"),
                 })
        {
            Sv(name, new[] { sel });
        }
        v.Static("hover", r =>
        {
            r.Nodes = new List<AstNode> { AstNode.Rule("&:hover", new List<AstNode> { AstNode.AtRule("@media", "(hover: hover)", r.Nodes) }) };
        });
        Sv("focus", new[] { "&:focus" });
        Sv("focus-visible", new[] { "&:focus-visible" });
        Sv("active", new[] { "&:active" });
        Sv("enabled", new[] { "&:enabled" });
        Sv("disabled", new[] { "&:disabled" });
        Sv("inert", new[] { "&:is([inert], [inert] *)" });

        v.Compound("in", Compounds.StyleRules, (c, d) =>
        {
            if (d.Modifier != null) return false;
            return RewriteTopRule(c, s => ":where(" + s.Replace("&", "*") + ") &");
        });
        v.Compound("has", Compounds.StyleRules, (c, d) =>
        {
            if (d.Modifier != null) return false;
            return RewriteTopRule(c, s => "&:has(" + s.Replace("&", "*") + ")");
        });
        v.Functional("aria", (c, d) =>
        {
            if (d.Value == null || d.Modifier != null) return false;
            string attr = d.Value.IsArbitrary ? "[aria-" + QuoteAttributeValue(d.Value.Value) + "]" : "[aria-" + d.Value.Value + "=\"true\"]";
            if (!IsValidAttributeSelector(attr)) return false;
            c.Nodes = new List<AstNode> { AstNode.Rule("&" + attr, c.Nodes) };
            return true;
        });
        v.Functional("data", (c, d) =>
        {
            if (d.Value == null || d.Modifier != null) return false;
            string attr = "[data-" + QuoteAttributeValue(d.Value.Value) + "]";
            if (!IsValidAttributeSelector(attr)) return false;
            c.Nodes = new List<AstNode> { AstNode.Rule("&" + attr, c.Nodes) };
            return true;
        });
        foreach (var (name, pseudo) in new[] { ("nth", "nth-child"), ("nth-last", "nth-last-child"), ("nth-of-type", "nth-of-type"), ("nth-last-of-type", "nth-last-of-type") })
        {
            string ps = pseudo;
            v.Functional(name, (c, d) =>
            {
                if (d.Value == null || d.Modifier != null || (!d.Value.IsArbitrary && !Js.IsPositiveInteger(d.Value.Value))) return false;
                c.Nodes = new List<AstNode> { AstNode.Rule("&:" + ps + "(" + d.Value.Value + ")", c.Nodes) };
                return true;
            });
        }
        v.Functional("supports", (c, d) =>
        {
            if (d.Value == null || d.Modifier != null) return false;
            string value = d.Value.Value;
            if (IsFunctionLike(value))
            {
                bool changed = false;
                var ast = ValueParser.Parse(value);
                ValueParser.Walk(ast, (n, _) =>
                {
                    if (n.Kind != ValueKind.Function) return WalkAction<ValueNode>.Continue;
                    if (n.Value == "selector") return WalkAction<ValueNode>.Skip;
                    if (n.Value == "and" || n.Value == "or" || n.Value == "not")
                    {
                        changed = true;
                        n.Value = " " + n.Value + " ";
                    }
                    return WalkAction<ValueNode>.Continue;
                });
                c.Nodes = new List<AstNode> { AstNode.AtRule("@supports", changed ? ValueParser.ToCss(ast) : value, c.Nodes) };
                return true;
            }
            if (value.IndexOf(':') < 0) value = value + ": var(--tw)";
            if (value[0] != '(' || value[value.Length - 1] != ')') value = "(" + value + ")";
            c.Nodes = new List<AstNode> { AstNode.AtRule("@supports", value, c.Nodes) };
            return true;
        }, Compounds.AtRules);
        Sv("motion-safe", new[] { "@media (prefers-reduced-motion: no-preference)" });
        Sv("motion-reduce", new[] { "@media (prefers-reduced-motion: reduce)" });
        Sv("contrast-more", new[] { "@media (prefers-contrast: more)" });
        Sv("contrast-less", new[] { "@media (prefers-contrast: less)" });

        RegisterBreakpoints(v, theme);

        Sv("portrait", new[] { "@media (orientation: portrait)" });
        Sv("landscape", new[] { "@media (orientation: landscape)" });
        Sv("ltr", new[] { "&:where(:dir(ltr), [dir=\"ltr\"], [dir=\"ltr\"] *)" });
        Sv("rtl", new[] { "&:where(:dir(rtl), [dir=\"rtl\"], [dir=\"rtl\"] *)" });
        Sv("dark", new[] { "@media (prefers-color-scheme: dark)" });
        Sv("starting", new[] { "@starting-style" });
        Sv("print", new[] { "@media print" });
        Sv("forced-colors", new[] { "@media (forced-colors: active)" });
        Sv("inverted-colors", new[] { "@media (inverted-colors: inverted)" });
        Sv("pointer-none", new[] { "@media (pointer: none)" });
        Sv("pointer-coarse", new[] { "@media (pointer: coarse)" });
        Sv("pointer-fine", new[] { "@media (pointer: fine)" });
        Sv("any-pointer-none", new[] { "@media (any-pointer: none)" });
        Sv("any-pointer-coarse", new[] { "@media (any-pointer: coarse)" });
        Sv("any-pointer-fine", new[] { "@media (any-pointer: fine)" });
        Sv("noscript", new[] { "@media (scripting: none)" });
        return v;
    }

    static bool IsFunctionLike(string value)
    {
        // /^[\w-]*\s*\(/
        int i = 0;
        while (i < value.Length && (char.IsLetterOrDigit(value[i]) || value[i] == '_' || value[i] == '-')) i++;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
        return i < value.Length && value[i] == '(';
    }

    /// <summary>group-* / peer-*：把内层变体的选择器改写为 <c>:is(:where(.group) sel &amp;)</c>。</summary>
    static bool ApplyRelational(AstNode c, Variant d, string marker, string combinatorAndAmp)
    {
        if (d.Inner!.Kind == VariantKind.Arbitrary && d.Inner.Relative) return false;
        bool ok = false;
        AstNode.Walk(new List<AstNode> { c }, (h, w) =>
        {
            if (h.Kind != AstKind.Rule) return WalkAction<AstNode>.Continue;
            foreach (var k in w.Path())
            {
                if (k.Kind == AstKind.Rule)
                {
                    ok = false;
                    return WalkAction<AstNode>.Stop;
                }
            }
            string b = h.Selector.Replace("&", marker);
            if (Js.Segment(b, ',').Count > 1) b = ":is(" + b + ")";
            h.Selector = ":is(" + b + combinatorAndAmp + ")";
            ok = true;
            return WalkAction<AstNode>.Continue;
        });
        return ok;
    }

    /// <summary>in-* / has-*：只允许一层规则，改写其选择器。</summary>
    static bool RewriteTopRule(AstNode c, Func<string, string> rewrite)
    {
        bool ok = false;
        AstNode.Walk(new List<AstNode> { c }, (f, h) =>
        {
            if (f.Kind != AstKind.Rule) return WalkAction<AstNode>.Continue;
            foreach (var w in h.Path())
            {
                if (w.Kind == AstKind.Rule)
                {
                    ok = false;
                    return WalkAction<AstNode>.Stop;
                }
            }
            f.Selector = rewrite(f.Selector);
            ok = true;
            return WalkAction<AstNode>.Continue;
        });
        return ok;
    }

    static bool ApplyNot(AstNode c, Variant d)
    {
        if ((d.Inner!.Kind == VariantKind.Arbitrary && d.Inner.Relative) || d.Modifier != null) return false;
        bool ok = false;
        AstNode.Walk(new List<AstNode> { c }, (f, h) =>
        {
            if (f.Kind != AstKind.Rule && f.Kind != AstKind.AtRule) return WalkAction<AstNode>.Continue;
            if (f.Nodes.Count > 0) return WalkAction<AstNode>.Continue;
            var atRules = new List<AstNode>();
            var rules = new List<AstNode>();
            var path = h.Path();
            path.Add(f);
            foreach (var n in path)
            {
                if (n.Kind == AstKind.AtRule) atRules.Add(n);
                else if (n.Kind == AstKind.Rule) rules.Add(n);
            }
            if (atRules.Count > 1) return WalkAction<AstNode>.Stop;
            if (rules.Count > 1) return WalkAction<AstNode>.Stop;
            var replacement = new List<AstNode>();
            foreach (var r in rules)
            {
                string? sel = NegateSelector(r.Selector);
                if (sel == null)
                {
                    ok = false;
                    return WalkAction<AstNode>.Stop;
                }
                replacement.Add(AstNode.Rule(sel, new List<AstNode>()));
            }
            foreach (var a in atRules)
            {
                var neg = NegateAtRule(a);
                if (neg == null)
                {
                    ok = false;
                    return WalkAction<AstNode>.Stop;
                }
                replacement.Add(neg);
            }
            c.AssignFrom(AstNode.Rule("&", replacement));
            ok = true;
            return WalkAction<AstNode>.Skip;
        });
        if (c.Kind == AstKind.Rule && c.Selector == "&" && c.Nodes.Count == 1) c.AssignFrom(c.Nodes[0]);
        return ok;
    }

    static string? NegateSelector(string selector)
    {
        if (selector.IndexOf("::", StringComparison.Ordinal) >= 0) return null;
        var parts = Js.Segment(selector, ',');
        for (int i = 0; i < parts.Count; i++) parts[i] = parts[i].Replace("&", "*");
        return "&:not(" + string.Join(", ", parts) + ")";
    }

    static AstNode? NegateAtRule(AstNode rule)
    {
        if (rule.Name != "@media" && rule.Name != "@supports" && rule.Name != "@container") return null;
        var conditions = Js.Segment(rule.Params, ',');
        if (conditions.Count > 1) return null;
        var negated = new List<string>();
        foreach (var m in conditions) negated.Add(NegateCondition(rule.Name, m));
        return AstNode.AtRule(rule.Name, string.Join(", ", negated));
    }

    static string NegateCondition(string ruleName, string m)
    {
        if (ruleName == "@container")
        {
            var f = ValueParser.Parse(m.Trim());
            if (f.Count >= 1 && f[0].Kind == ValueKind.Function) return "not " + m;
            if (f.Count >= 3 && f[0].Kind == ValueKind.Word && f[0].Value == "not" && f[2].Kind == ValueKind.Function)
            {
                f.RemoveRange(0, 2);
                return ValueParser.ToCss(f);
            }
            if (f.Count >= 5 && f[0].Kind == ValueKind.Word && f[2].Kind == ValueKind.Word && f[2].Value == "not" && f[4].Kind == ValueKind.Function)
            {
                f.RemoveRange(2, 2);
                return ValueParser.ToCss(f);
            }
            if (f.Count >= 3 && f[0].Kind == ValueKind.Word && f[0].Value != "not" && f[2].Kind == ValueKind.Function)
            {
                f.InsertRange(1, new[] { ValueNode.Separator(" "), ValueNode.Word("not") });
                return ValueParser.ToCss(f);
            }
            return "not " + m;
        }
        m = m.Trim();
        var parts = Js.Segment(m, ' ');
        if (parts[0] == "not") return string.Join(" ", parts.GetRange(1, parts.Count - 1));
        return "not " + m;
    }

    /// <summary>属性选择器值加引号（Tailwind <c>quoteAttributeValue</c>）。</summary>
    static string QuoteAttributeValue(string input)
    {
        if (input.IndexOf('=') < 0) return input;
        var parts = Js.Segment(input, '=');
        string attribute = parts[0];
        string value = string.Join("=", parts.GetRange(1, parts.Count - 1)).Trim();
        if (value.Length > 0 && (value[0] == '\'' || value[0] == '"')) return input;
        if (value.Length > 1)
        {
            char last = value[value.Length - 1];
            if (value[value.Length - 2] == ' ' && (last == 'i' || last == 'I' || last == 's' || last == 'S'))
                return attribute + "=\"" + value.Substring(0, value.Length - 2) + "\" " + last;
        }
        return attribute + "=\"" + value + "\"";
    }

    static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r';

    /// <summary>属性选择器合法性检查（Tailwind <c>parseAttributeSelector</c> 的成功与否）。</summary>
    static bool IsValidAttributeSelector(string s)
    {
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']') return false;
        int i = 1;
        int end = s.Length - 1;
        while (i < end && IsWs(s[i])) i++;
        int start = i;
        for (; i < end; i++)
        {
            char c = s[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c >= 128)) break;
        }
        if (start == i) return false;
        while (i < end && IsWs(s[i])) i++;
        if (i == end) return true;
        char op = s[i];
        if (op == '=') i++;
        else if ((op == '~' || op == '|' || op == '^' || op == '$' || op == '*') && i + 1 < s.Length && s[i + 1] == '=') i += 2;
        else return false;
        while (i < end && IsWs(s[i])) i++;
        if (i == end) return false;
        char q = s[i];
        if (q == '\'' || q == '"')
        {
            i++;
            int j = i;
            for (int d = i; d < end; d++)
            {
                char m = s[d];
                if (m == q) j = d + 1;
                else if (m == '\\') d++;
            }
            i = j;
        }
        else
        {
            while (i < end && !IsWs(s[i])) i++;
        }
        while (i < end && IsWs(s[i])) i++;
        if (i == end) return true;
        char sens = s[i];
        if (sens == 'i' || sens == 'I' || sens == 's' || sens == 'S') i++;
        else return false;
        while (i < end && IsWs(s[i])) i++;
        return i == end;
    }

    static void RegisterBreakpoints(VariantRegistry v, Theme theme)
    {
        int Compare(Variant a, Variant z, bool ascending, Func<Variant, string?> resolve)
        {
            if (ReferenceEquals(a, z)) return 0;
            string? aValue = resolve(a);
            if (aValue == null) return ascending ? -1 : 1;
            string? zValue = resolve(z);
            if (zValue == null) return ascending ? 1 : -1;
            return Js.CompareBreakpoints(aValue, zValue, ascending);
        }

        var bpCache = new Dictionary<Variant, string?>(ReferenceEqualityComparer<Variant>.Instance);
        v.RegisterCache(bpCache.Clear);
        string? ResolveBreakpoint(Variant variant)
        {
            if (bpCache.TryGetValue(variant, out var cached)) return cached;
            string? result = null;
            switch (variant.Kind)
            {
                case VariantKind.Static:
                    result = theme.ResolveValue(variant.Root, new[] { "--breakpoint" });
                    break;
                case VariantKind.Functional:
                {
                    if (variant.Value == null || variant.Modifier != null) break;
                    string? value = variant.Value.IsArbitrary ? variant.Value.Value : theme.ResolveValue(variant.Value.Value, new[] { "--breakpoint" });
                    result = value == null || value.IndexOf("var(", StringComparison.Ordinal) >= 0 ? null : value;
                    break;
                }
            }
            bpCache[variant] = result;
            return result;
        }

        v.Group(() =>
        {
            v.Functional("max", (c, d) =>
            {
                if (d.Modifier != null) return false;
                string? value = ResolveBreakpoint(d);
                if (value == null) return false;
                c.Nodes = new List<AstNode> { AstNode.AtRule("@media", "(width < " + value + ")", c.Nodes) };
                return true;
            }, Compounds.AtRules);
        }, (a, z) => Compare(a, z, false, ResolveBreakpoint));

        v.Group(() =>
        {
            foreach (var kv in theme.Namespace("--breakpoint"))
            {
                if (kv.Key == null) continue;
                string bp = kv.Value;
                v.Static(kv.Key, r => r.Nodes = new List<AstNode> { AstNode.AtRule("@media", "(width >= " + bp + ")", r.Nodes) }, Compounds.AtRules);
            }
            v.Functional("min", (c, d) =>
            {
                if (d.Modifier != null) return false;
                string? value = ResolveBreakpoint(d);
                if (value == null) return false;
                c.Nodes = new List<AstNode> { AstNode.AtRule("@media", "(width >= " + value + ")", c.Nodes) };
                return true;
            }, Compounds.AtRules);
        }, (a, z) => Compare(a, z, true, ResolveBreakpoint));

        var cqCache = new Dictionary<Variant, string?>(ReferenceEqualityComparer<Variant>.Instance);
        v.RegisterCache(cqCache.Clear);
        string? ResolveContainer(Variant variant)
        {
            if (cqCache.TryGetValue(variant, out var cached)) return cached;
            string? result = null;
            if (variant.Kind == VariantKind.Functional && variant.Value != null)
            {
                string? value = variant.Value.IsArbitrary ? variant.Value.Value : theme.ResolveValue(variant.Value.Value, new[] { "--container" });
                result = value == null || value.IndexOf("var(", StringComparison.Ordinal) >= 0 ? null : value;
            }
            cqCache[variant] = result;
            return result;
        }

        bool Container(AstNode c, Variant d, string op)
        {
            string? value = ResolveContainer(d);
            if (value == null) return false;
            string query = d.Modifier != null ? d.Modifier.Value + " (width " + op + " " + value + ")" : "(width " + op + " " + value + ")";
            c.Nodes = new List<AstNode> { AstNode.AtRule("@container", query, c.Nodes) };
            return true;
        }

        v.Group(() => v.Functional("@max", (c, d) => Container(c, d, "<"), Compounds.AtRules), (a, z) => Compare(a, z, false, ResolveContainer));
        v.Group(() =>
        {
            v.Functional("@", (c, d) => Container(c, d, ">="), Compounds.AtRules);
            v.Functional("@min", (c, d) => Container(c, d, ">="), Compounds.AtRules);
        }, (a, z) => Compare(a, z, true, ResolveContainer));
    }
}
