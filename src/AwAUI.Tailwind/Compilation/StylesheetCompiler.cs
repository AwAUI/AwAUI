using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Resources;
using AwAUI.Tailwind.Theming;
using AwAUI.Tailwind.Variants;

namespace AwAUI.Tailwind.Compilation;

/// <summary>解析输入样式表后的结果：设计系统 + 待填充工具类的语法树。</summary>
internal sealed class CompiledStylesheet
{
    public DesignSystem DesignSystem = null!;
    public List<AstNode> Ast = null!;
    public AstNode? UtilitiesNode;
    public readonly List<TailwindSource> Sources = new();
    public readonly List<string> InlineCandidates = new();
    public readonly List<string> DesignSystemIgnored = new();
}

/// <summary>
/// 处理输入样式表中的 Tailwind 指令（移植自 Tailwind <c>parseCss</c>）：<c>@import</c>、<c>@theme</c>、<c>@utility</c>、
/// <c>@custom-variant</c> / <c>@variant</c>、<c>@source</c>、<c>@apply</c>、<c>@tailwind utilities</c>、<c>@reference</c>。
/// 不支持 JS 插件：<c>@plugin</c> 与 <c>@config</c> 报错。
/// </summary>
internal static class StylesheetCompiler
{
    static readonly Regex s_variantName = new(@"^@?[a-z0-9][a-zA-Z0-9_-]*$", RegexOptions.CultureInvariant);
    static readonly Regex s_prefix = new("^[a-z]+$", RegexOptions.CultureInvariant);

    public static CompiledStylesheet Compile(string css, TailwindOptions options)
    {
        var parsed = CssParser.Parse(css);
        var ast = new List<AstNode> { AstNode.CreateContext(new AstContext { Base = options.BaseDirectory ?? "" }, parsed) };
        ResolveImports(ast, options.BaseDirectory, options, 0);

        var result = new CompiledStylesheet();
        bool important = false;
        var theme = new Theme();
        var customVariants = new Dictionary<string, Action<DesignSystem>>(StringComparer.Ordinal);
        var customVariantOrder = new List<string>();
        var customVariantDeps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var customUtilities = new List<Action<DesignSystem>>();
        AstNode? firstThemeRule = null;
        AstNode? utilitiesNode = null;

        AstNode.Walk(ast, (node, ctx) =>
        {
            if (node.Kind != AstKind.AtRule) return WalkAction<AstNode>.Continue;
            var context = CollectContext(ctx);
            var parent = NonContextParent(ctx);

            if (node.Name == "@tailwind" && (node.Params == "utilities" || node.Params.StartsWith("utilities", StringComparison.Ordinal)))
            {
                if (utilitiesNode != null) return WalkAction<AstNode>.Replace(new List<AstNode>());
                if (context.Reference) return WalkAction<AstNode>.Replace(new List<AstNode>());
                foreach (var part in Js.Segment(node.Params, ' '))
                {
                    if (!part.StartsWith("source(", StringComparison.Ordinal)) continue;
                    string path = part.Slice(7, -1);
                    if (path == "none") continue;
                    if (!IsQuoted(path)) throw new TailwindException("`source(…)` paths must be quoted.");
                }
                utilitiesNode = node;
            }

            if (node.Name == "@utility")
            {
                if (parent != null) throw new TailwindException("`@utility` cannot be nested.");
                if (node.Nodes.Count == 0) throw new TailwindException($"`@utility {node.Params}` is empty. Utilities should include at least one property.");
                var register = CustomUtilities.Create(node);
                if (register == null)
                {
                    if (!node.Params.EndsWith("-*", StringComparison.Ordinal))
                    {
                        if (node.Params.EndsWith("*", StringComparison.Ordinal)) throw new TailwindException($"`@utility {node.Params}` defines an invalid utility name. A functional utility must end in `-*`.");
                        if (node.Params.IndexOf('*') >= 0) throw new TailwindException($"`@utility {node.Params}` defines an invalid utility name. The dynamic portion marked by `-*` must appear once at the end.");
                    }
                    throw new TailwindException($"`@utility {node.Params}` defines an invalid utility name. Utilities should be alphanumeric and start with a lowercase letter.");
                }
                customUtilities.Add(register);
            }

            if (node.Name == "@source")
            {
                if (node.Nodes.Count > 0) throw new TailwindException("`@source` cannot have a body.");
                if (parent != null) throw new TailwindException("`@source` cannot be nested.");
                bool negated = false, inline = false;
                string path = node.Params;
                if (path.StartsWith("not ", StringComparison.Ordinal))
                {
                    negated = true;
                    path = path.Substring(4);
                }
                if (path.StartsWith("inline(", StringComparison.Ordinal))
                {
                    inline = true;
                    path = path.Slice(7, -1).Trim();
                }
                if (!IsQuoted(path)) throw new TailwindException("`@source` paths must be quoted.");
                string pattern = path.Slice(1, -1);
                if (inline)
                {
                    foreach (var part in Js.Segment(pattern, ' '))
                    {
                        foreach (var expanded in BraceExpansion.Expand(part))
                        {
                            if (negated) result.DesignSystemIgnored.Add(expanded);
                            else result.InlineCandidates.Add(expanded);
                        }
                    }
                }
                else result.Sources.Add(new TailwindSource(context.Base ?? "", pattern, negated));
                return WalkAction<AstNode>.ReplaceSkip(new List<AstNode>());
            }

            if (node.Name == "@variant")
            {
                if (parent == null)
                {
                    if (node.Nodes.Count == 0) node.Name = "@custom-variant";
                    else
                    {
                        AstNode.Walk(node.Nodes, (n, _) =>
                        {
                            if (n.Kind == AstKind.AtRule && n.Name == "@slot")
                            {
                                node.Name = "@custom-variant";
                                return WalkAction<AstNode>.Stop;
                            }
                            return WalkAction<AstNode>.Continue;
                        });
                    }
                }
            }

            if (node.Name == "@custom-variant")
            {
                if (parent != null) throw new TailwindException("`@custom-variant` cannot be nested.");
                var parts = Js.Segment(node.Params, ' ');
                string name = parts[0];
                string? selector = parts.Count > 1 ? string.Join(" ", parts.GetRange(1, parts.Count - 1)) : null;
                if (!IsValidVariantName(name)) throw new TailwindException($"`@custom-variant {name}` defines an invalid variant name. Variants should only contain alphanumeric, dashes, or underscore characters and start with a lowercase letter or number.");
                if (node.Nodes.Count > 0 && selector != null) throw new TailwindException($"`@custom-variant {name}` cannot have both a selector and a body.");
                if (!customVariants.ContainsKey(name)) customVariantOrder.Add(name);
                if (node.Nodes.Count == 0)
                {
                    if (selector == null) throw new TailwindException($"`@custom-variant {name}` has no selector or body.");
                    var selectors = Js.Segment(selector.Slice(1, -1), ',');
                    if (selectors.Count == 0 || selectors.Exists(s => s.Trim().Length == 0)) throw new TailwindException($"`@custom-variant {name} ({string.Join(",", selectors)})` selector is invalid.");
                    var atRules = new List<string>();
                    var styleRules = new List<string>();
                    foreach (var s in selectors)
                    {
                        string t = s.Trim();
                        if (t.Length > 0 && t[0] == '@') atRules.Add(t);
                        else styleRules.Add(t);
                    }
                    var all = new List<string>(styleRules);
                    all.AddRange(atRules);
                    var compounds = VariantRegistry.GetCompounds(all);
                    customVariants[name] = ds => ds.Variants.Static(name, r =>
                    {
                        var body = r.Nodes;
                        var list = new List<AstNode>();
                        if (styleRules.Count > 0) list.Add(AstNode.Rule(string.Join(", ", styleRules), body));
                        foreach (var at in atRules) list.Add(AstNode.RuleOrAtRule(at, body));
                        r.Nodes = list;
                    }, compounds);
                    customVariantDeps[name] = new HashSet<string>(StringComparer.Ordinal);
                }
                else
                {
                    var deps = new HashSet<string>(StringComparer.Ordinal);
                    AstNode.Walk(node.Nodes, (n, _) =>
                    {
                        if (n.Kind == AstKind.AtRule && n.Name == "@variant") deps.Add(n.Params);
                        return WalkAction<AstNode>.Continue;
                    });
                    var body = node.Nodes;
                    customVariants[name] = ds => ds.Variants.FromAst(name, body, ds);
                    customVariantDeps[name] = deps;
                }
                return WalkAction<AstNode>.ReplaceSkip(new List<AstNode>());
            }

            if (node.Name == "@media")
            {
                var tokens = Js.Segment(node.Params, ' ');
                var unknown = new List<string>();
                foreach (var t in tokens)
                {
                    if (t.StartsWith("source(", StringComparison.Ordinal))
                    {
                        string path = t.Slice(7, -1);
                        AstNode.Walk(node.Nodes, (n, _) =>
                        {
                            if (n.Kind == AstKind.AtRule && n.Name == "@tailwind" && n.Params == "utilities")
                            {
                                n.Params += " source(" + path + ")";
                                return WalkAction<AstNode>.ReplaceStop(AstNode.CreateContext(new AstContext { SourceBase = context.Base }, new List<AstNode> { n }));
                            }
                            return WalkAction<AstNode>.Continue;
                        });
                    }
                    else if (t.StartsWith("theme(", StringComparison.Ordinal))
                    {
                        string themeParams = t.Slice(6, -1);
                        bool isReference = themeParams.IndexOf("reference", StringComparison.Ordinal) >= 0;
                        AstNode.Walk(node.Nodes, (n, _) =>
                        {
                            if (n.Kind == AstKind.Context) return WalkAction<AstNode>.Continue;
                            if (n.Kind != AstKind.AtRule)
                            {
                                if (isReference) throw new TailwindException("Files imported with `@import \"…\" theme(reference)` must only contain `@theme` blocks.\nUse `@reference \"…\";` instead.");
                                return WalkAction<AstNode>.Continue;
                            }
                            if (n.Name == "@theme")
                            {
                                n.Params += " " + themeParams;
                                return WalkAction<AstNode>.Skip;
                            }
                            return WalkAction<AstNode>.Continue;
                        });
                    }
                    else if (t.StartsWith("prefix(", StringComparison.Ordinal))
                    {
                        string p = t.Slice(7, -1);
                        AstNode.Walk(node.Nodes, (n, _) =>
                        {
                            if (n.Kind == AstKind.AtRule && n.Name == "@theme")
                            {
                                n.Params += " prefix(" + p + ")";
                                return WalkAction<AstNode>.Skip;
                            }
                            return WalkAction<AstNode>.Continue;
                        });
                    }
                    else if (t == "important") important = true;
                    else if (t == "reference") node.Nodes = new List<AstNode> { AstNode.CreateContext(new AstContext { Reference = true }, node.Nodes) };
                    else unknown.Add(t);
                }
                if (unknown.Count > 0) node.Params = string.Join(" ", unknown);
                else if (tokens.Count > 0) return WalkAction<AstNode>.Replace(node.Nodes);
                return WalkAction<AstNode>.Continue;
            }

            if (node.Name == "@theme")
            {
                var (themeOptions, prefix) = ParseThemeOptions(node.Params);
                if (context.Reference) themeOptions |= ThemeOptions.Reference;
                if (prefix != null)
                {
                    if (!s_prefix.IsMatch(prefix)) throw new TailwindException($"The prefix \"{prefix}\" is invalid. Prefixes must be lowercase ASCII letters (a-z) only.");
                    theme.Prefix = prefix;
                }
                AstNode.Walk(node.Nodes, (child, _) =>
                {
                    if (child.Kind == AstKind.AtRule && child.Name == "@keyframes")
                    {
                        theme.AddKeyframes(child);
                        return WalkAction<AstNode>.Skip;
                    }
                    if (child.Kind == AstKind.Comment) return WalkAction<AstNode>.Continue;
                    if (child.Kind == AstKind.Declaration && child.Property.StartsWith("--", StringComparison.Ordinal))
                    {
                        theme.Add(Js.Unescape(child.Property), child.Value ?? "", themeOptions);
                        return WalkAction<AstNode>.Continue;
                    }
                    throw new TailwindException("`@theme` blocks must only contain custom properties or `@keyframes`.\n\n" + CssPrinter.Print(new List<AstNode> { AstNode.AtRule(node.Name, node.Params, new List<AstNode> { child }) }, false));
                });
                if (firstThemeRule != null) return WalkAction<AstNode>.ReplaceSkip(new List<AstNode>());
                firstThemeRule = AstNode.Rule(options.ThemeRootSelector);
                return WalkAction<AstNode>.ReplaceSkip(firstThemeRule);
            }

            if (node.Name == "@plugin" || node.Name == "@config")
                throw new TailwindException($"`{node.Name}` is not supported by AwAUI.Tailwind (JavaScript plugins and configs cannot run here). Use `@utility`, `@custom-variant` and `@theme` instead.");

            return WalkAction<AstNode>.Continue;
        });

        var ds = new DesignSystem(theme);
        if (important) ds.Important = true;
        foreach (var c in result.DesignSystemIgnored) ds.InvalidCandidates.Add(c);

        // 自定义变体：先全部登记为空的静态变体（使互相引用时可以解析），再按依赖顺序真正注册
        foreach (var name in customVariantOrder) ds.Variants.Static(name, _ => { });
        foreach (var name in TopologicalOrder(customVariantOrder, customVariantDeps)) customVariants[name](ds);
        foreach (var register in customUtilities) register(ds);

        if (firstThemeRule != null)
        {
            var decls = new List<AstNode>();
            foreach (var entry in theme.Entries())
            {
                if ((entry.Options & ThemeOptions.Reference) != 0) continue;
                decls.Add(AstNode.Decl(Js.Escape(theme.PrefixKey(entry.Key)), entry.Value));
            }
            foreach (var kf in theme.GetKeyframes())
            {
                ast.Add(AstNode.CreateContext(new AstContext { Theme = true }, new List<AstNode> { AstNode.AtRoot(new List<AstNode> { kf }) }));
            }
            firstThemeRule.Nodes = new List<AstNode> { AstNode.CreateContext(new AstContext { Theme = true }, decls) };
        }

        AtRules.SubstituteAtVariant(ast, ds);
        CssFunctions.Substitute(ast, ds);
        AtRules.SubstituteAtApply(ast, ds);

        if (utilitiesNode != null)
        {
            utilitiesNode.Kind = AstKind.Context;
            utilitiesNode.Context = new AstContext();
            utilitiesNode.Nodes = new List<AstNode>();
        }

        AstNode.Walk(ast, (n, _) =>
        {
            if (n.Kind == AstKind.AtRule) return n.Name == "@utility" ? WalkAction<AstNode>.Replace(new List<AstNode>()) : WalkAction<AstNode>.Skip;
            return WalkAction<AstNode>.Continue;
        });

        result.DesignSystem = ds;
        result.Ast = ast;
        result.UtilitiesNode = utilitiesNode;
        return result;
    }

    static bool IsQuoted(string p) => p.Length >= 2 && ((p[0] == '"' && p[p.Length - 1] == '"') || (p[0] == '\'' && p[p.Length - 1] == '\''));

    static bool IsValidVariantName(string name) => s_variantName.IsMatch(name) && name[name.Length - 1] != '_' && name[name.Length - 1] != '-';

    static (ThemeOptions, string?) ParseThemeOptions(string p)
    {
        var options = ThemeOptions.None;
        string? prefix = null;
        foreach (var t in Js.Segment(p, ' '))
        {
            if (t == "reference") options |= ThemeOptions.Reference;
            else if (t == "inline") options |= ThemeOptions.Inline;
            else if (t == "default") options |= ThemeOptions.Default;
            else if (t == "static") options |= ThemeOptions.Static;
            else if (t.StartsWith("prefix(", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal)) prefix = t.Slice(7, -1);
        }
        return (options, prefix);
    }

    static AstContext CollectContext(WalkContext<AstNode> ctx)
    {
        var result = new AstContext();
        foreach (var n in ctx.Path())
        {
            if (n.Kind == AstKind.Context) result = result.Merge(n.Context!);
        }
        return result;
    }

    static AstNode? NonContextParent(WalkContext<AstNode> ctx)
    {
        var path = ctx.Path();
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (path[i].Kind != AstKind.Context) return path[i];
        }
        return null;
    }

    static List<string> TopologicalOrder(List<string> names, Dictionary<string, HashSet<string>> deps)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        void Visit(string name, List<string> path)
        {
            if (!deps.ContainsKey(name) || done.Contains(name)) return;
            if (visiting.Contains(name))
            {
                var chain = new List<string>(path) { name };
                throw new TailwindException("Circular dependency detected in custom variants:\n\n" + string.Join(" -> ", chain));
            }
            if (path.Count > 1000) throw new TailwindException("Custom variant dependency chain is too deep.");
            visiting.Add(name);
            foreach (var d in deps[name])
            {
                path.Add(name);
                Visit(d, path);
                path.RemoveAt(path.Count - 1);
            }
            done.Add(name);
            visiting.Remove(name);
            order.Add(name);
        }
        foreach (var n in names) Visit(n, new List<string>());
        return order;
    }

    // ------------------------------------------------------------------
    // @import
    // ------------------------------------------------------------------

    static void ResolveImports(List<AstNode> ast, string? baseDir, TailwindOptions options, int depth)
    {
        AstNode.Walk(ast, (node, _) =>
        {
            if (node.Kind != AstKind.AtRule || (node.Name != "@import" && node.Name != "@reference")) return WalkAction<AstNode>.Continue;
            var import = ParseImportParams(ValueParser.Parse(node.Params));
            if (import == null) return WalkAction<AstNode>.Continue;
            var (uri, layer, media, supports) = import.Value;
            if (node.Name == "@reference") media = "reference";
            if (uri.StartsWith("data:", StringComparison.Ordinal) || uri.StartsWith("http://", StringComparison.Ordinal) || uri.StartsWith("https://", StringComparison.Ordinal)) return WalkAction<AstNode>.Continue;
            if (depth > Limits.MaxImportDepth) throw new TailwindException($"Exceeded maximum recursion depth while resolving `{uri}` in `{baseDir}`)");
            var (content, childBase) = LoadStylesheet(uri, baseDir, options);
            var parsed = CssParser.Parse(content);
            ResolveImports(parsed, childBase, options, depth + 1);
            List<AstNode> nodes = new() { AstNode.CreateContext(new AstContext { Base = childBase ?? "" }, parsed) };
            if (layer != null) nodes = new List<AstNode> { AstNode.AtRule("@layer", layer, nodes) };
            if (media != null) nodes = new List<AstNode> { AstNode.AtRule("@media", media, nodes) };
            if (supports != null) nodes = new List<AstNode> { AstNode.AtRule("@supports", supports.Length > 0 && supports[0] == '(' ? supports : "(" + supports + ")", nodes) };
            return WalkAction<AstNode>.ReplaceSkip(AstNode.CreateContext(new AstContext(), nodes));
        });
    }

    static (string Content, string? Base) LoadStylesheet(string id, string? baseDir, TailwindOptions options)
    {
        var builtin = LoadBuiltin(id, options);
        if (builtin != null)
        {
            // 内嵌资源在 Windows 上可能以 CRLF 检出（.gitattributes 为 text=auto）；统一为 LF，使输出与平台无关
            return (builtin.IndexOf('\r') >= 0 ? builtin.Replace("\r\n", "\n") : builtin, baseDir);
        }
        var resolved = options.Resolver?.Resolve(id, baseDir);
        if (resolved == null) throw new TailwindException($"Could not resolve `@import \"{id}\"`" + (baseDir != null && baseDir.Length > 0 ? $" from `{baseDir}`" : "") + ".");
        return (resolved.Content, resolved.BaseDirectory ?? baseDir);
    }

    static string? LoadBuiltin(string id, TailwindOptions options)
    {
        switch (id)
        {
            case "tailwindcss":
            case "tailwindcss/index.css":
                return options.Preflight == PreflightKind.Tailwind ? EmbeddedCss.Index : EmbeddedCss.IndexAwAUI;
            case "tailwindcss/theme":
            case "tailwindcss/theme.css":
                return EmbeddedCss.TailwindTheme;
            case "tailwindcss/preflight":
            case "tailwindcss/preflight.css":
                return options.Preflight == PreflightKind.Tailwind ? EmbeddedCss.TailwindPreflight : EmbeddedCss.AwAUIPreflight;
            case "tailwindcss/utilities":
            case "tailwindcss/utilities.css":
                return EmbeddedCss.Utilities;
            case "tw-animate-css":
            case "tw-animate-css/dist/tw-animate.css":
                return EmbeddedCss.TwAnimate;
        }
        return null;
    }

    static (string Uri, string? Layer, string? Media, string? Supports)? ParseImportParams(List<ValueNode> nodes)
    {
        string? uri = null, layer = null, media = null, supports = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.Kind == ValueKind.Separator) continue;
            if (node.Kind == ValueKind.Word && uri == null)
            {
                if (node.Value.Length < 2 || (node.Value[0] != '"' && node.Value[0] != '\'')) return null;
                uri = node.Value.Slice(1, -1);
                continue;
            }
            if ((node.Kind == ValueKind.Function && node.Value.ToLowerInvariant() == "url") || uri == null) return null;
            if ((node.Kind == ValueKind.Word || node.Kind == ValueKind.Function) && node.Value.ToLowerInvariant() == "layer")
            {
                if (layer != null) return null;
                if (supports != null) throw new TailwindException("`layer(…)` in an `@import` should come before any other functions or conditions");
                layer = node.Kind == ValueKind.Function ? ValueParser.ToCss(node.Nodes!) : "";
                continue;
            }
            if (node.Kind == ValueKind.Function && node.Value.ToLowerInvariant() == "supports")
            {
                if (supports != null) return null;
                supports = ValueParser.ToCss(node.Nodes!);
                continue;
            }
            media = ValueParser.ToCss(nodes.GetRange(i, nodes.Count - i));
            break;
        }
        return uri == null ? null : (uri, layer, media, supports);
    }
}

/// <summary><c>@source inline("...")</c> 的花括号展开（<c>p-{1,2}</c>、<c>w-{1..4}</c>），带数量上限。</summary>
internal static class BraceExpansion
{
    const int MaxResults = 10_000;
    static readonly Regex s_sequence = new(@"^(-?\d+)\.\.(-?\d+)(?:\.\.(-?\d+))?$", RegexOptions.CultureInvariant);

    public static List<string> Expand(string pattern)
    {
        var result = Expand(pattern, 0);
        if (result.Count > MaxResults) throw new TailwindException($"The pattern `{pattern}` expands to too many candidates.");
        return result;
    }

    static List<string> Expand(string pattern, int depth)
    {
        if (depth > 32) throw new TailwindException($"The pattern `{pattern}` is nested too deeply.");
        int open = pattern.IndexOf('{');
        if (open == -1) return new List<string> { pattern };
        string pre = pattern.Substring(0, open);
        string rest = pattern.Substring(open);
        int level = 0;
        int close = rest.LastIndexOf('}');
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '{') level++;
            else if (c == '}')
            {
                level--;
                if (level == 0)
                {
                    close = i;
                    break;
                }
            }
        }
        if (close == -1) throw new TailwindException($"The pattern `{pattern}` is not balanced.");
        string inner = rest.Substring(1, close - 1);
        string post = rest.Substring(close + 1);
        var parts = new List<string>();
        var seq = s_sequence.Match(inner);
        if (seq.Success)
        {
            long start = long.Parse(seq.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            long end = long.Parse(seq.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            long step = seq.Groups[3].Success ? long.Parse(seq.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : (start <= end ? 1 : -1);
            if (step == 0) throw new TailwindException("Step cannot be zero in sequence expansion.");
            bool up = start < end;
            if (up && step < 0) step = -step;
            if (!up && step > 0) step = -step;
            for (long i = start; up ? i <= end : i >= end; i += step)
            {
                parts.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (parts.Count > MaxResults) throw new TailwindException($"The pattern `{pattern}` expands to too many candidates.");
                if (start == end) break;
            }
        }
        else parts.AddRange(Js.Segment(inner, ','));
        var expandedParts = new List<string>();
        foreach (var p in parts) expandedParts.AddRange(Expand(p, depth + 1));
        var expandedPost = Expand(post, depth + 1);
        var results = new List<string>();
        foreach (var a in expandedPost)
        {
            foreach (var b in expandedParts)
            {
                results.Add(pre + b + a);
                if (results.Count > MaxResults) throw new TailwindException($"The pattern `{pattern}` expands to too many candidates.");
            }
        }
        return results;
    }
}
