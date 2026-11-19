using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Theming;

namespace AwAUI.Tailwind.Compilation;

/// <summary>颜色与透明度修饰（<c>color-mix(in oklab, ...)</c>）。</summary>
internal static class Colors
{
    /// <summary>Tailwind <c>withAlpha</c>：数字按百分比处理，100% 原样返回。</summary>
    public static string WithAlpha(string value, string? alpha)
    {
        if (alpha == null) return value;
        double n = Js.Number(alpha);
        if (!double.IsNaN(n)) alpha = Js.NumberToString(n * 100) + "%";
        if (alpha == "100%") return value;
        return "color-mix(in oklab, " + value + " " + alpha + ", transparent)";
    }

    /// <summary>Tailwind <c>withAlpha</c> 的相对颜色版本（阴影的 alpha 修饰）。</summary>
    public static string ReplaceAlpha(string value, string alpha)
    {
        double n = Js.Number(alpha);
        if (!double.IsNaN(n)) alpha = Js.NumberToString(n * 100) + "%";
        return "oklab(from " + value + " l a b / " + alpha + ")";
    }

    /// <summary>Tailwind <c>asColor</c>：应用透明度修饰；具名修饰从 <c>--opacity-*</c> 或数字解析，失败返回 null。</summary>
    public static string? AsColor(string value, CandidateModifier? modifier, Theme theme)
    {
        if (modifier == null) return value;
        if (modifier.IsArbitrary) return WithAlpha(value, modifier.Value);
        string? alpha = theme.Resolve(modifier.Value, s_opacity);
        if (alpha != null) return WithAlpha(value, alpha);
        if (!Js.IsMultipleOfQuarter(modifier.Value)) return null;
        return WithAlpha(value, modifier.Value + "%");
    }

    static readonly string[] s_opacity = { "--opacity" };

    /// <summary>Tailwind <c>resolveThemeColor</c>：<c>inherit / transparent / current</c> 与主题颜色。</summary>
    public static string? ResolveThemeColor(Candidate candidate, Theme theme, string[] themeKeys)
    {
        string? value = candidate.Value!.Value switch
        {
            "inherit" => "inherit",
            "transparent" => "transparent",
            "current" => "currentcolor",
            _ => theme.Resolve(candidate.Value.Value, themeKeys),
        };
        return value == null ? null : AsColor(value, candidate.Modifier, theme);
    }
}

/// <summary>从值中提取 <c>var(--x)</c> 引用的变量名。</summary>
internal static class VariableNames
{
    public static List<string> Extract(string value)
    {
        var names = new List<string>();
        if (value.IndexOf("var(", StringComparison.Ordinal) < 0) return names;
        ValueParser.Walk(ValueParser.Parse(value), (n, _) =>
        {
            if (n.Kind != ValueKind.Function || n.Value != "var") return WalkAction<ValueNode>.Continue;
            ValueParser.Walk(n.Nodes!, (inner, _) =>
            {
                if (inner.Kind == ValueKind.Word && inner.Value.Length > 1 && inner.Value[0] == '-' && inner.Value[1] == '-') names.Add(inner.Value);
                return WalkAction<ValueNode>.Continue;
            });
            return WalkAction<ValueNode>.Skip;
        });
        return names;
    }
}

/// <summary>阴影值中的颜色替换（移植自 Tailwind <c>replaceShadowColors</c>）。</summary>
internal static class ShadowColors
{
    static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal) { "inset", "inherit", "initial", "revert", "unset" };
    static readonly HashSet<string> s_mathFns = new(StringComparer.Ordinal) { "calc", "clamp", "max", "min", "--spacing" };
    static readonly HashSet<string> s_colorFns = new(StringComparer.Ordinal)
    {
        "color", "color-mix", "contrast-color", "device-cmyk", "hsl", "hsla", "hwb", "lab", "lch", "light-dark", "oklab", "oklch", "rgb", "rgba", "--alpha",
    };
    static readonly Regex s_length = new(@"^-?(\d+|\.\d+)(.*?)$", RegexOptions.CultureInvariant);

    public static string Replace(string input, Func<string, string> replacement)
    {
        var parts = Js.Segment(input, ',');
        var outParts = new List<string>(parts.Count);
        foreach (var raw in parts)
        {
            string shadow = raw.Trim();
            var ast = ValueParser.Parse(shadow);
            ValueNode? lastWord = null;
            int otherWords = 0;
            int lengths = 0;
            bool replaced = false;
            ValueParser.Walk(ast, (node, _) =>
            {
                switch (node.Kind)
                {
                    case ValueKind.Word:
                    {
                        string lower = node.Value.ToLowerInvariant();
                        if (s_keywords.Contains(lower)) return WalkAction<ValueNode>.Continue;
                        if (s_length.IsMatch(lower))
                        {
                            lengths++;
                            return WalkAction<ValueNode>.Continue;
                        }
                        if ((node.Value.Length > 0 && node.Value[0] == '#') || DataTypes.IsNamedColor(node.Value))
                        {
                            replaced = true;
                            return WalkAction<ValueNode>.ReplaceStop(Replacement(node));
                        }
                        lastWord = node;
                        otherWords++;
                        break;
                    }
                    case ValueKind.Function:
                    {
                        string lower = node.Value.ToLowerInvariant();
                        if (s_colorFns.Contains(lower))
                        {
                            replaced = true;
                            return WalkAction<ValueNode>.ReplaceStop(Replacement(node));
                        }
                        if (s_mathFns.Contains(lower))
                        {
                            lengths++;
                            return WalkAction<ValueNode>.Skip;
                        }
                        lastWord = node;
                        otherWords++;
                        return WalkAction<ValueNode>.Skip;
                    }
                }
                return WalkAction<ValueNode>.Continue;
            });
            if (replaced)
            {
                outParts.Add(ValueParser.ToCss(ast));
                continue;
            }
            if (lengths < 2)
            {
                outParts.Add(shadow);
                continue;
            }
            if (otherWords == 0)
            {
                outParts.Add(shadow + " " + replacement("currentcolor"));
                continue;
            }
            if (otherWords == 1)
            {
                var target = lastWord;
                ValueParser.Walk(ast, (node, _) =>
                {
                    if (ReferenceEquals(node, target))
                    {
                        replaced = true;
                        return WalkAction<ValueNode>.ReplaceStop(Replacement(node));
                    }
                    return WalkAction<ValueNode>.Skip;
                });
            }
            outParts.Add(replaced ? ValueParser.ToCss(ast) : shadow);
        }
        return string.Join(", ", outParts);

        List<ValueNode> Replacement(ValueNode node) => ValueParser.Parse(replacement(ValueParser.ToCss(new List<ValueNode> { node })));
    }

    /// <summary>Tailwind <c>alphaReplacedShadowProperties</c>：生成阴影声明（必要时带 <c>@supports (color: lab(from red l a b))</c> 的版本）。</summary>
    public static List<AstNode> AlphaReplacedShadow(string property, string value, string? alpha, Func<string, string> varInjector, string prefix = "")
    {
        bool requiresFallback = false;
        string replacedValue = Replace(value, color =>
        {
            if (alpha == null) return varInjector(color);
            if (color.StartsWith("current", StringComparison.Ordinal)) return varInjector(Colors.WithAlpha(color, alpha));
            if (color.StartsWith("var(", StringComparison.Ordinal) || alpha.StartsWith("var(", StringComparison.Ordinal)) requiresFallback = true;
            return varInjector(Colors.ReplaceAlpha(color, alpha));
        });
        string ApplyPrefix(string v)
        {
            if (prefix.Length == 0) return v;
            var parts = Js.Segment(v, ',');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(prefix.Trim()).Append(' ').Append(parts[i].Trim());
            }
            return sb.ToString();
        }
        if (requiresFallback)
        {
            return new List<AstNode>
            {
                AstNode.Decl(property, ApplyPrefix(Replace(value, varInjector))),
                AstNode.RuleOrAtRule("@supports (color: lab(from red l a b))", new List<AstNode> { AstNode.Decl(property, ApplyPrefix(replacedValue)) }),
            };
        }
        return new List<AstNode> { AstNode.Decl(property, ApplyPrefix(replacedValue)) };
    }

    /// <summary>Tailwind <c>alphaReplacedDropShadowProperties</c>。</summary>
    public static List<AstNode> AlphaReplacedDropShadow(string property, string value, string? alpha, Func<string, string> varInjector, string prefix = "")
    {
        bool requiresFallback = false;
        var segs = Js.Segment(value, ',');
        var replaced = new List<string>();
        foreach (var seg in segs)
        {
            string r = Replace(seg, color =>
            {
                if (alpha == null) return varInjector(color);
                if (color.StartsWith("current", StringComparison.Ordinal)) return varInjector(Colors.WithAlpha(color, alpha));
                if (color.StartsWith("var(", StringComparison.Ordinal) || alpha.StartsWith("var(", StringComparison.Ordinal)) requiresFallback = true;
                return varInjector(Colors.ReplaceAlpha(color, alpha));
            });
            replaced.Add("drop-shadow(" + r + ")");
        }
        string replacedValue = string.Join(" ", replaced);
        if (requiresFallback)
        {
            var plain = new List<string>();
            foreach (var seg in segs) plain.Add("drop-shadow(" + Replace(seg, varInjector) + ")");
            return new List<AstNode>
            {
                AstNode.Decl(property, prefix + string.Join(" ", plain)),
                AstNode.RuleOrAtRule("@supports (color: lab(from red l a b))", new List<AstNode> { AstNode.Decl(property, prefix + replacedValue) }),
            };
        }
        return new List<AstNode> { AstNode.Decl(property, prefix + replacedValue) };
    }
}
