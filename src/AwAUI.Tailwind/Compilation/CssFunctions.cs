using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

/// <summary>构建期 CSS 函数 <c>--spacing()</c>、<c>--alpha()</c>、<c>--theme()</c>、<c>theme()</c> 的展开（移植自 Tailwind <c>css-functions.ts</c>）。</summary>
internal static class CssFunctions
{
    static bool MightContainFunction(string value)
        => value.IndexOf("--alpha(", StringComparison.Ordinal) >= 0
           || value.IndexOf("--spacing(", StringComparison.Ordinal) >= 0
           || value.IndexOf("--theme(", StringComparison.Ordinal) >= 0
           || value.IndexOf("theme(", StringComparison.Ordinal) >= 0;

    /// <summary>就地展开语法树中的函数；返回是否发生过展开。</summary>
    public static bool Substitute(List<AstNode> ast, DesignSystem ds)
    {
        bool any = false;
        AstNode.Walk(ast, (node, _) =>
        {
            if (node.Kind == AstKind.Declaration && node.Value != null && MightContainFunction(node.Value))
            {
                any = true;
                node.Value = SubstituteValue(node.Value, node, ds);
                return WalkAction<AstNode>.Continue;
            }
            if (node.Kind == AstKind.AtRule && (node.Name == "@media" || node.Name == "@custom-media" || node.Name == "@container" || node.Name == "@supports") && MightContainFunction(node.Params))
            {
                any = true;
                node.Params = SubstituteValue(node.Params, node, ds);
            }
            return WalkAction<AstNode>.Continue;
        });
        return any;
    }

    public static string SubstituteValue(string value, AstNode source, DesignSystem ds)
    {
        var ast = ValueParser.Parse(value);
        int substitutions = 0;
        ValueParser.Walk(ast, (node, _) =>
        {
            if (node.Kind == ValueKind.Function && (node.Value == "--alpha" || node.Value == "--spacing" || node.Value == "--theme" || node.Value == "theme"))
            {
                // 展开结果会被继续展开（与 Tailwind 相同）；主题变量互相引用时 Tailwind 会死循环，这里限制次数
                if (++substitutions > Limits.MaxFunctionSubstitutions)
                    throw new TailwindException($"Too many nested CSS function calls while resolving `{(value.Length > 200 ? value.Substring(0, 200) + "…" : value)}`. Check for theme variables whose `theme(…)` / `--theme(…)` values refer to each other.");
                var args = Js.Segment(ValueParser.ToCss(node.Nodes!).Trim(), ',');
                for (int i = 0; i < args.Count; i++) args[i] = args[i].Trim();
                string replacement = node.Value switch
                {
                    "--alpha" => Alpha(args),
                    "--spacing" => Spacing(ds, args),
                    "--theme" => ThemeFn(ds, source, args),
                    _ => LegacyTheme(ds, args),
                };
                return WalkAction<ValueNode>.Replace(ValueParser.Parse(replacement));
            }
            return WalkAction<ValueNode>.Continue;
        });
        return ValueParser.ToCss(ast);
    }

    static string Alpha(List<string> args)
    {
        var parts = Js.Segment(args[0], '/');
        string color = parts[0].Trim();
        string alpha = parts.Count > 1 ? parts[1].Trim() : "";
        if (color.Length == 0 || alpha.Length == 0)
            throw new TailwindException($"The --alpha(…) function requires a color and an alpha value, e.g.: `--alpha({(color.Length > 0 ? color : "var(--my-color)")} / {(alpha.Length > 0 ? alpha : "50%")})`");
        if (args.Count > 1)
            throw new TailwindException($"The --alpha(…) function only accepts one argument, e.g.: `--alpha({color} / {alpha})`");
        return Colors.WithAlpha(color, alpha);
    }

    static string Spacing(DesignSystem ds, List<string> args)
    {
        string value = args[0];
        if (value.Length == 0) throw new TailwindException("The --spacing(…) function requires an argument, but received none.");
        if (args.Count > 1) throw new TailwindException($"The --spacing(…) function only accepts a single argument, but received {args.Count}.");
        string? multiplier = ds.Theme.Resolve(null, new[] { "--spacing" });
        if (multiplier == null) throw new TailwindException("The --spacing(…) function requires that the `--spacing` theme variable exists, but it was not found.");
        var dim = Dimension.Parse(value);
        if (dim != null)
        {
            if (dim.Value.Number == 0) return "0px";
            if (dim.Value.Number == 1) return multiplier;
        }
        return "calc(" + multiplier + " * " + value + ")";
    }

    static string ThemeFn(DesignSystem ds, AstNode source, List<string> args)
    {
        string path = args[0];
        if (!path.StartsWith("--", StringComparison.Ordinal)) throw new TailwindException("The --theme(…) function can only be used with CSS variables from your theme.");
        bool inline = false;
        if (path.EndsWith(" inline", StringComparison.Ordinal))
        {
            inline = true;
            path = path.Substring(0, path.Length - 7);
        }
        if (source.Kind == AstKind.AtRule) inline = true;
        string? resolved = ds.ResolveThemeValue(path, inline);
        if (resolved == null)
        {
            if (args.Count > 1) return string.Join(", ", args.GetRange(1, args.Count - 1));
            throw new TailwindException($"Could not resolve value for theme function: `theme({path})`. Consider checking if the variable name is correct or provide a fallback value to silence this error.");
        }
        if (args.Count == 1) return resolved;
        string fallback = string.Join(", ", args.GetRange(1, args.Count - 1));
        if (fallback == "initial") return resolved;
        if (resolved == "initial") return fallback;
        if (resolved.StartsWith("var(", StringComparison.Ordinal) || resolved.StartsWith("theme(", StringComparison.Ordinal) || resolved.StartsWith("--theme(", StringComparison.Ordinal))
        {
            var ast = ValueParser.Parse(resolved);
            InjectFallback(ast, fallback);
            return ValueParser.ToCss(ast);
        }
        return resolved;
    }

    static void InjectFallback(List<ValueNode> ast, string fallback)
    {
        ValueParser.Walk(ast, (node, _) =>
        {
            if (node.Kind != ValueKind.Function) return WalkAction<ValueNode>.Continue;
            if (node.Value != "var" && node.Value != "theme" && node.Value != "--theme") return WalkAction<ValueNode>.Continue;
            if (node.Nodes!.Count == 1) node.Nodes.Add(ValueNode.Word(", " + fallback));
            else
            {
                var last = node.Nodes[node.Nodes.Count - 1];
                if (last.Kind == ValueKind.Word && last.Value == "initial") last.Value = fallback;
            }
            return WalkAction<ValueNode>.Continue;
        });
    }

    static string LegacyTheme(DesignSystem ds, List<string> args)
    {
        string path = Unquote(args[0]);
        string? resolved = path.StartsWith("--", StringComparison.Ordinal) ? ds.ResolveThemeValue(path) : ResolveLegacyPath(ds, path);
        if (resolved == null && args.Count > 1) return string.Join(", ", args.GetRange(1, args.Count - 1));
        if (resolved == null)
            throw new TailwindException($"Could not resolve value for theme function: `theme({path})`. Consider checking if the path is correct or provide a fallback value to silence this error.");
        return resolved;
    }

    static readonly System.Collections.Generic.Dictionary<string, string> s_legacyNamespaces = new(StringComparer.Ordinal)
    {
        ["colors"] = "--color", ["borderRadius"] = "--radius", ["fontSize"] = "--text", ["fontFamily"] = "--font", ["fontWeight"] = "--font-weight",
        ["letterSpacing"] = "--tracking", ["lineHeight"] = "--leading", ["screens"] = "--breakpoint", ["boxShadow"] = "--shadow", ["maxWidth"] = "--container",
        ["containers"] = "--container", ["animation"] = "--animate", ["aspectRatio"] = "--aspect", ["blur"] = "--blur", ["dropShadow"] = "--drop-shadow",
        ["transitionTimingFunction"] = "--ease", ["perspective"] = "--perspective", ["insetShadow"] = "--inset-shadow",
    };

    /// <summary>
    /// v3 风格的 <c>theme(colors.red.500)</c> 点路径：命名空间映射到 v4 主题变量（返回内联值）；<c>spacing.N</c> 按 v3 默认刻度（N × 0.25rem）。
    /// Tailwind v4 通过完整的 v3 兼容配置层解析这类路径，这里只覆盖常见命名空间。
    /// </summary>
    static string? ResolveLegacyPath(DesignSystem ds, string path)
    {
        string? modifier = null;
        int slash = path.LastIndexOf('/');
        if (slash != -1)
        {
            modifier = path.Substring(slash + 1).Trim();
            path = path.Substring(0, slash).Trim();
        }
        var segments = path.Split('.');
        if (segments.Length == 0) return null;
        string? value = null;
        if (segments[0] == "spacing" && segments.Length >= 2)
        {
            string key = string.Join(".", segments, 1, segments.Length - 1);
            if (key == "px") value = "1px";
            else if (key == "0") value = "0px";
            else if (Js.IsMultipleOfQuarter(key)) value = Js.NumberToString(Js.Number(key) * 0.25) + "rem";
        }
        else if (s_legacyNamespaces.TryGetValue(segments[0], out var ns))
        {
            var rest = new System.Collections.Generic.List<string>();
            for (int i = 1; i < segments.Length; i++)
            {
                if (segments[i] != "DEFAULT") rest.Add(segments[i]);
            }
            string key = rest.Count == 0 ? ns : ns + "-" + string.Join("-", rest);
            value = ds.Theme.ResolveValue(null, new[] { key });
        }
        if (value == null) return null;
        return modifier != null && modifier.Length > 0 ? Colors.WithAlpha(value, modifier) : value;
    }

    static string Unquote(string value)
    {
        if (value.Length == 0 || (value[0] != '\'' && value[0] != '"')) return value;
        var sb = new StringBuilder();
        char quote = value[0];
        for (int i = 1; i < value.Length - 1; i++)
        {
            char c = value[i];
            char next = value[i + 1];
            if (c == '\\' && (next == quote || next == '\\'))
            {
                sb.Append(next);
                i++;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>数值 + 单位（Tailwind <c>dimensions</c>：<c>/^([-+]?(?:\d*\.)?\d+)([a-z]+|%)?$/i</c>）。</summary>
internal readonly struct Dimension
{
    public readonly double Number;
    public readonly string? Unit;

    Dimension(double n, string? unit)
    {
        Number = n;
        Unit = unit;
    }

    public static Dimension? Parse(string input)
    {
        int i = 0;
        int len = input.Length;
        if (i < len && (input[i] == '+' || input[i] == '-')) i++;
        int numStart = i;
        int lastDigitRunStart = -1;
        bool sawDot = false;
        while (i < len)
        {
            char c = input[i];
            if (c >= '0' && c <= '9')
            {
                if (lastDigitRunStart == -1) lastDigitRunStart = i;
                i++;
            }
            else if (c == '.' && !sawDot)
            {
                sawDot = true;
                lastDigitRunStart = -1;
                i++;
            }
            else break;
        }
        // 必须以数字结尾（"1." 不合法）
        if (i == numStart || input[i - 1] == '.' || lastDigitRunStart == -1) return null;
        string numText = input.Substring(0, i);
        string? unit = null;
        if (i < len)
        {
            if (input[i] == '%' && i == len - 1) unit = "%";
            else
            {
                for (int j = i; j < len; j++)
                {
                    char c = input[j];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return null;
                }
                unit = input.Substring(i);
            }
        }
        double n = Js.Number(numText);
        if (double.IsNaN(n)) return null;
        return new Dimension(n, unit);
    }
}
