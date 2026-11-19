using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Theming;

namespace AwAUI.Tailwind.Compilation;

/// <summary>polyfill 选项（AwAUI 目前不输出浏览器 polyfill，保留枚举以与 Tailwind 的接口对应）。</summary>
internal enum Polyfills
{
    None = 0,
}

/// <summary>
/// 输出前的语法树优化（移植自 Tailwind <c>optimizeAst</c> 与 v4.3 的嵌套展开）：
/// 去掉未使用的主题变量与 keyframes、提升 <c>@property</c> 等 at-root 节点、把嵌套规则展开为扁平 CSS 并合并相邻同选择器规则。
/// </summary>
internal static class Optimizer
{
    sealed class State
    {
        public readonly List<AstNode> AtRoots = new();
        public readonly HashSet<string> SeenAtProperties = new(StringComparer.Ordinal);
        public readonly List<KeyValuePair<List<AstNode>, List<AstNode>>> ThemeVariables = new();
        public readonly Dictionary<List<AstNode>, List<AstNode>> ThemeVariablesByParent = new(ReferenceEqualityComparer<List<AstNode>>.Instance);
        public readonly List<AstNode> ThemeKeyframes = new();
        public readonly HashSet<string> UsedKeyframeNames = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> VariableDependencies = new(StringComparer.Ordinal);
    }

    readonly struct Ctx
    {
        public readonly bool Theme;
        public readonly bool Keyframes;

        public Ctx(bool theme, bool keyframes)
        {
            Theme = theme;
            Keyframes = keyframes;
        }
    }

    public static List<AstNode> Optimize(List<AstNode> ast, DesignSystem ds, Polyfills polyfills)
    {
        var state = new State();
        var output = new List<AstNode>();
        foreach (var node in ast) Transform(node, output, new Ctx(false, false), 0, state, ds);

        // 去掉未使用的主题变量
        foreach (var kv in state.ThemeVariables)
        {
            var parent = kv.Key;
            foreach (var decl in kv.Value)
            {
                if (IsVariableUsed(decl.Property, ds.Theme, state.VariableDependencies))
                {
                    if (decl.Property.StartsWith(ds.Theme.PrefixKey("--animate-"), StringComparison.Ordinal))
                    {
                        foreach (var name in KeyframeNames(decl.Value ?? "")) state.UsedKeyframeNames.Add(name);
                    }
                    continue;
                }
                int idx = parent.IndexOf(decl);
                if (idx < 0) continue;
                parent.RemoveAt(idx);
                if (parent.Count == 0) RemoveEmptyAncestors(output, parent);
            }
        }

        // 去掉未使用的主题 keyframes
        foreach (var kf in state.ThemeKeyframes)
        {
            if (!state.UsedKeyframeNames.Contains(kf.Params)) state.AtRoots.Remove(kf);
        }
        output.AddRange(state.AtRoots);
        return Flatten(output);
    }

    static void RemoveEmptyAncestors(List<AstNode> root, List<AstNode> emptyList)
    {
        // 找到 nodes == emptyList 的规则，沿路径向上删除空的祖先
        var path = new List<AstNode>();
        bool found = false;
        AstNode.Walk(root, (n, ctx) =>
        {
            if (n.Kind == AstKind.Rule && ReferenceEquals(n.Nodes, emptyList))
            {
                path = ctx.Path();
                path.Add(n);
                found = true;
                return WalkAction<AstNode>.Stop;
            }
            return WalkAction<AstNode>.Continue;
        });
        if (!found || path.Count == 0) return;
        var rootNode = AstNode.AtRoot(root);
        path.Insert(0, rootNode);
        while (path.Count > 1)
        {
            var node = path[path.Count - 1];
            path.RemoveAt(path.Count - 1);
            var parent = path[path.Count - 1];
            if (parent.Kind != AstKind.AtRoot && parent.Kind != AstKind.AtRule) break;
            int i = parent.Nodes.IndexOf(node);
            if (i == -1) break;
            // 与 Tailwind 一致：沿 at 规则链一直删到根（这些容器只包裹主题变量规则）
            parent.Nodes.RemoveAt(i);
        }
    }

    static IEnumerable<string> KeyframeNames(string value)
    {
        foreach (var s in value.Split(new[] { ' ', '\t', '\n', '\r', '\f', ',' }, StringSplitOptions.None)) yield return s;
    }

    /// <summary>主题变量是否需要输出：被使用 / static，或者引用它的其它主题变量需要输出。图中出现回边时视为需要（与 Tailwind 一致）。</summary>
    static bool IsVariableUsed(string variable, Theme theme, Dictionary<string, HashSet<string>> deps)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(variable);
        visited.Add(variable);
        while (stack.Count > 0)
        {
            string v = stack.Pop();
            if ((theme.GetOptions(v) & (ThemeOptions.Static | ThemeOptions.Used)) != 0) return true;
            if (!deps.TryGetValue(v, out var users)) continue;
            foreach (var u in users)
            {
                if (!visited.Add(u)) return true;
                stack.Push(u);
            }
        }
        return false;
    }

    static void Transform(AstNode node, List<AstNode> parent, Ctx ctx, int depth, State state, DesignSystem ds)
    {
        switch (node.Kind)
        {
            case AstKind.Declaration:
            {
                if (node.Property == "--tw-sort" || node.Value == null) return;
                bool isCustom = node.Property.Length > 1 && node.Property[0] == '-' && node.Property[1] == '-';
                if (ctx.Theme && isCustom)
                {
                    if (node.Value == "initial")
                    {
                        node.Value = null;
                        return;
                    }
                    if (!ctx.Keyframes)
                    {
                        if (!state.ThemeVariablesByParent.TryGetValue(parent, out var list))
                        {
                            list = new List<AstNode>();
                            state.ThemeVariablesByParent[parent] = list;
                            state.ThemeVariables.Add(new KeyValuePair<List<AstNode>, List<AstNode>>(parent, list));
                        }
                        if (!list.Contains(node)) list.Add(node);
                    }
                }
                if (node.Value.IndexOf("var(", StringComparison.Ordinal) >= 0)
                {
                    if (ctx.Theme && isCustom)
                    {
                        foreach (var name in VariableNames.Extract(node.Value))
                        {
                            if (!state.VariableDependencies.TryGetValue(name, out var set))
                            {
                                set = new HashSet<string>(StringComparer.Ordinal);
                                state.VariableDependencies[name] = set;
                            }
                            set.Add(node.Property);
                        }
                    }
                    else ds.TrackUsedVariables(node.Value);
                }
                if (node.Property == "animation")
                {
                    foreach (var name in KeyframeNames(node.Value)) state.UsedKeyframeNames.Add(name);
                }
                parent.Add(node);
                break;
            }
            case AstKind.Rule:
            {
                var children = new List<AstNode>();
                foreach (var c in node.Nodes) Transform(c, children, ctx, depth + 1, state, ds);
                if (children.Count > 0)
                {
                    var copy = node.Clone0();
                    copy.Nodes = children;
                    parent.Add(copy);
                }
                break;
            }
            case AstKind.AtRule when node.Name == "@property" && depth == 0:
            {
                if (!state.SeenAtProperties.Add(node.Params)) return;
                var copy = node.Clone0();
                copy.Nodes = new List<AstNode>();
                foreach (var c in node.Nodes) Transform(c, copy.Nodes, ctx, depth + 1, state, ds);
                parent.Add(copy);
                break;
            }
            case AstKind.AtRule:
            {
                if (node.Name == "@keyframes") ctx = new Ctx(ctx.Theme, true);
                var copy = node.Clone0();
                copy.Nodes = new List<AstNode>();
                foreach (var c in node.Nodes) Transform(c, copy.Nodes, ctx, depth + 1, state, ds);
                if (node.Name == "@keyframes" && ctx.Theme) state.ThemeKeyframes.Add(copy);
                if (copy.Nodes.Count > 0 || copy.Name == "@layer" || copy.Name == "@charset" || copy.Name == "@custom-media" || copy.Name == "@namespace" || copy.Name == "@import" || copy.Name == "@apply")
                    parent.Add(copy);
                break;
            }
            case AstKind.AtRoot:
                foreach (var c in node.Nodes)
                {
                    var list = new List<AstNode>();
                    Transform(c, list, ctx, 0, state, ds);
                    state.AtRoots.AddRange(list);
                }
                break;
            case AstKind.Context:
            {
                if (node.Context!.Reference) return;
                var merged = new Ctx(ctx.Theme || node.Context.Theme, ctx.Keyframes || node.Context.Keyframes);
                foreach (var c in node.Nodes) Transform(c, parent, merged, depth, state, ds);
                break;
            }
            case AstKind.Comment:
                parent.Add(node);
                break;
        }
    }

    static AstNode Clone0(this AstNode n)
    {
        var c = new AstNode();
        c.AssignFrom(n);
        return c;
    }

    // ------------------------------------------------------------------
    // 嵌套展开（Tailwind v4.3 在输出前把 & 嵌套展开为普通选择器）
    // ------------------------------------------------------------------

    static readonly HashSet<string> s_groupingAtRules = new(StringComparer.Ordinal)
    {
        "@container", "@layer", "@media", "@page", "@scope", "@starting-style", "@supports", "@view-transition",
    };

    static readonly HashSet<string> s_conditionalAtRules = new(StringComparer.Ordinal)
    {
        "@container", "@media", "@page", "@scope", "@starting-style", "@supports", "@view-transition",
    };

    sealed class SelectorEntry
    {
        public string Selector = "";
        public bool Invalid;
    }

    sealed class FlattenState
    {
        public readonly List<SelectorEntry> Selectors = new();
        public readonly List<KeyValuePair<string, string>> AtRules = new();
        public List<AstNode>? Group;
        public readonly HashSet<string> GroupProperties = new(StringComparer.Ordinal);
        public readonly List<List<AstNode>> NeedsDedup = new();
        public readonly HashSet<List<AstNode>> NeedsDedupSet = new(ReferenceEqualityComparer<List<AstNode>>.Instance);
        public readonly List<AstNode> Output = new();
        public readonly HashSet<AstNode> Skipped = new(ReferenceEqualityComparer<AstNode>.Instance);
        public readonly Dictionary<string, List<SelectorNode>> SelectorCache = new(StringComparer.Ordinal);

        public List<SelectorNode> ParseSelector(string s)
        {
            if (!SelectorCache.TryGetValue(s, out var ast))
            {
                ast = SelectorParser.Parse(s);
                SelectorCache[s] = ast;
            }
            return ast;
        }
    }

    public static List<AstNode> Flatten(List<AstNode> ast)
    {
        var st = new FlattenState();
        AstNode.Walk(ast, (k, _) =>
        {
            switch (k.Kind)
            {
                case AstKind.Rule:
                {
                    st.Group = null;
                    if (st.Selectors.Count == 0)
                    {
                        if (k.Selector.IndexOf('&') >= 0)
                        {
                            var parsed = SelectorParser.Parse(k.Selector);
                            bool replaced = false;
                            SelectorParser.Walk(parsed, (n, _) =>
                            {
                                if (n.Kind == SelectorKind.Selector && n.Value == "&")
                                {
                                    replaced = true;
                                    n.Value = ":scope";
                                }
                                return WalkAction<SelectorNode>.Continue;
                            });
                            st.Selectors.Add(new SelectorEntry { Selector = replaced ? SelectorParser.ToCss(parsed) : k.Selector });
                        }
                        else st.Selectors.Add(new SelectorEntry { Selector = k.Selector });
                    }
                    else
                    {
                        if (k.Selector == "&")
                        {
                            st.Skipped.Add(k);
                            return WalkAction<AstNode>.Continue;
                        }
                        string parentSelector = st.Selectors[st.Selectors.Count - 1].Selector;
                        var resolved = new List<string>();
                        foreach (var part in Js.Segment(k.Selector, ',')) resolved.Add(ResolveNested(part, parentSelector, st));
                        var valid = new List<string>();
                        foreach (var r in resolved)
                        {
                            if (!IsInvalidNesting(st.ParseSelector(r))) valid.Add(r);
                        }
                        if (valid.Count == 0)
                        {
                            st.Selectors.Add(new SelectorEntry { Selector = string.Join(", ", resolved), Invalid = true });
                            break;
                        }
                        st.Selectors.Add(new SelectorEntry { Selector = string.Join(", ", valid) });
                    }
                    bool hasDecl = false;
                    foreach (var c in k.Nodes)
                    {
                        if (c.Kind == AstKind.Declaration)
                        {
                            hasDecl = true;
                            break;
                        }
                    }
                    if (hasDecl)
                    {
                        foreach (var c in k.Nodes) Emit(c, st);
                        return WalkAction<AstNode>.Skip;
                    }
                    break;
                }
                case AstKind.AtRule:
                {
                    st.Group = null;
                    var top = st.Selectors.Count > 0 ? st.Selectors[st.Selectors.Count - 1] : null;
                    if (top != null && top.Invalid)
                    {
                        if (k.Name == "@layer" && k.Nodes.Count == 0)
                        {
                            var saved = new List<SelectorEntry>(st.Selectors);
                            st.Selectors.Clear();
                            Emit(k, st);
                            st.Selectors.AddRange(saved);
                            st.Skipped.Add(k);
                            return WalkAction<AstNode>.Skip;
                        }
                        if (k.Nodes.Count == 0 || !s_groupingAtRules.Contains(k.Name))
                        {
                            st.Skipped.Add(k);
                            return WalkAction<AstNode>.Skip;
                        }
                    }
                    if (k.Nodes.Count == 0 && !s_conditionalAtRules.Contains(k.Name))
                    {
                        Emit(k, st);
                        st.Skipped.Add(k);
                        return WalkAction<AstNode>.Skip;
                    }
                    if (s_groupingAtRules.Contains(k.Name))
                    {
                        st.AtRules.Add(new KeyValuePair<string, string>(k.Name, k.Params));
                    }
                    else
                    {
                        Emit(k, st);
                        st.Skipped.Add(k);
                        return WalkAction<AstNode>.Skip;
                    }
                    break;
                }
                case AstKind.Declaration:
                case AstKind.Comment:
                {
                    var top = st.Selectors.Count > 0 ? st.Selectors[st.Selectors.Count - 1] : null;
                    if (top != null && top.Invalid) break;
                    Emit(k, st);
                    break;
                }
            }
            return WalkAction<AstNode>.Continue;
        }, (k, _) =>
        {
            if (st.Skipped.Remove(k)) return WalkAction<AstNode>.Continue;
            if (k.Kind == AstKind.Rule)
            {
                st.Group = null;
                st.Selectors.RemoveAt(st.Selectors.Count - 1);
            }
            else if (k.Kind == AstKind.AtRule)
            {
                st.Group = null;
                st.AtRules.RemoveAt(st.AtRules.Count - 1);
            }
            return WalkAction<AstNode>.Continue;
        });

        foreach (var list in st.NeedsDedup)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = list.Count - 1; i >= 0; --i)
            {
                var d = list[i];
                if (d.Kind != AstKind.Declaration) continue;
                string key = d.Property + "\0" + d.Value + "\0" + (d.Important ? "1" : "0");
                if (!seen.Add(key)) list.RemoveAt(i);
            }
        }
        return st.Output;
    }

    static void MarkDedup(FlattenState st, List<AstNode> list)
    {
        if (st.NeedsDedupSet.Add(list)) st.NeedsDedup.Add(list);
    }

    static void Emit(AstNode k, FlattenState st)
    {
        if (st.Group != null)
        {
            if (k.Kind == AstKind.Declaration)
            {
                if (!st.GroupProperties.Add(k.Property)) MarkDedup(st, st.Group);
            }
            st.Group.Add(k);
            return;
        }
        if (st.Selectors.Count == 0 && st.AtRules.Count == 0)
        {
            var last = st.Output.Count > 0 ? st.Output[st.Output.Count - 1] : null;
            if (last != null && last.Kind == AstKind.AtRule && k.Kind == AstKind.AtRule && last.Nodes.Count == 0 && k.Nodes.Count == 0 && last.Name == k.Name && last.Params == k.Params) return;
            st.Output.Add(k);
            return;
        }
        st.Group = new List<AstNode> { k };
        st.GroupProperties.Clear();
        if (k.Kind == AstKind.Declaration) st.GroupProperties.Add(k.Property);

        AstNode? wrapped = null;
        var container = st.Output;
        int matched = 0;
        {
            var last = container.Count > 0 ? container[container.Count - 1] : null;
            if (last != null && last.Kind == AstKind.AtRule)
            {
                for (int i = 0; i < st.AtRules.Count; i++)
                {
                    var at = st.AtRules[i];
                    if (last == null || last.Kind != AstKind.AtRule || last.Name != at.Key || last.Params != at.Value) break;
                    matched++;
                    container = last.Nodes;
                    last = last.Nodes.Count > 0 ? last.Nodes[last.Nodes.Count - 1] : null;
                }
            }
        }
        if (st.Selectors.Count > 0)
        {
            string selector = st.Selectors[st.Selectors.Count - 1].Selector;
            if (st.AtRules.Count - matched <= 0)
            {
                var prev = container.Count > 0 ? container[container.Count - 1] : null;
                if (prev != null && prev.Kind == AstKind.Rule && prev.Selector == selector)
                {
                    prev.Nodes.AddRange(st.Group);
                    st.Group = prev.Nodes;
                    MarkDedup(st, st.Group);
                    return;
                }
            }
            wrapped = AstNode.RuleOrAtRule(selector, st.Group);
        }
        for (int i = st.AtRules.Count - 1; i >= matched; --i)
        {
            var at = st.AtRules[i];
            wrapped = AstNode.AtRule(at.Key, at.Value, wrapped != null ? new List<AstNode> { wrapped } : st.Group);
        }
        if (wrapped != null) container.Add(wrapped);
        else container.AddRange(st.Group);
    }

    /// <summary>把嵌套选择器 <paramref name="selector"/> 相对父选择器 <paramref name="parent"/> 展开（处理 &amp; 与 <c>:is()</c>）。</summary>
    static string ResolveNested(string selector, string parent, FlattenState st)
    {
        if (selector.IndexOf('&') >= 0)
        {
            var ast = SelectorParser.Parse(selector);
            bool found = false;
            SelectorParser.Walk(ast, (o, p) =>
            {
                if (o.Kind != SelectorKind.Selector || o.Value != "&") return WalkAction<SelectorNode>.Continue;
                found = true;
                o.Value = ":is(" + parent + ")";
                if (p.Parent == null) return WalkAction<SelectorNode>.Continue;
                var parentAst = st.ParseSelector(parent);
                if (parentAst.Count == 1 && parentAst[0].Kind == SelectorKind.List) return WalkAction<SelectorNode>.Continue;
                var first = parentAst.Count > 0 ? parentAst[0] : null;
                bool firstIsComplex = first != null && first.Kind == SelectorKind.Complex;
                if (p.Parent.Kind == SelectorKind.Complex)
                {
                    if (p.Index == 0)
                    {
                        o.Value = parent;
                        return WalkAction<SelectorNode>.Continue;
                    }
                    if (firstIsComplex) return WalkAction<SelectorNode>.Continue;
                    o.Value = parent;
                    return WalkAction<SelectorNode>.Continue;
                }
                if (p.Parent.Kind == SelectorKind.Compound)
                {
                    if (firstIsComplex)
                    {
                        var path = p.Path();
                        var grand = path.Count >= 2 ? path[path.Count - 2] : null;
                        if (grand != null && grand.Kind == SelectorKind.Complex && !ReferenceEquals(grand.Nodes![0], p.Parent)) return WalkAction<SelectorNode>.Continue;
                    }
                    for (int i = p.Index + 1; i < p.Siblings.Count; i++)
                    {
                        var r = p.Siblings[i];
                        if (SelectorParser.IsUniversal(r) || SelectorParser.IsTypeSelector(r)) return WalkAction<SelectorNode>.Continue;
                    }
                    if (p.Index == 0)
                    {
                        o.Value = parent;
                        return WalkAction<SelectorNode>.Continue;
                    }
                    if (first != null && (firstIsComplex || SelectorParser.IsUniversal(first) || SelectorParser.IsTypeSelector(first))) return WalkAction<SelectorNode>.Continue;
                    o.Value = parent;
                    return WalkAction<SelectorNode>.Continue;
                }
                if (p.Parent.Kind == SelectorKind.Function)
                {
                    o.Value = parent;
                }
                return WalkAction<SelectorNode>.Continue;
            }, (o, p) =>
            {
                if (p.Index == 0 && p.Siblings.Count > 1 && p.Parent != null && p.Parent.Kind == SelectorKind.Compound && SelectorParser.IsUniversal(o))
                {
                    var next = p.Siblings[1];
                    if (next.Kind == SelectorKind.Selector && next.Value.Length > 0 && next.Value[0] == '|') return WalkAction<SelectorNode>.Continue;
                    return WalkAction<SelectorNode>.ReplaceSkip(new List<SelectorNode>());
                }
                return WalkAction<SelectorNode>.Continue;
            });
            if (found) return SelectorParser.ToCss(ast);
        }
        var parentParsed = st.ParseSelector(parent);
        bool isList = parentParsed.Count == 1 && parentParsed[0].Kind == SelectorKind.List;
        return (isList ? ":is(" + parent + ")" : parent) + " " + selector;
    }

    static readonly HashSet<string> s_knownPseudoElements = new(StringComparer.Ordinal)
    {
        "::after", "::backdrop", "::before", "::checkmark", "::column", "::cue", "::cue-region", "::details-content", "::file-selector-button",
        "::first-letter", "::first-line", "::grammar-error", "::highlight", "::marker", "::part", "::picker", "::picker-icon", "::placeholder",
        "::scroll-button", "::scroll-marker", "::scroll-marker-group", "::search-text", "::selection", "::slotted", "::spelling-error",
        "::target-text", "::view-transition", "::view-transition-group", "::view-transition-group-children", "::view-transition-image-pair",
        "::view-transition-new", "::view-transition-old",
    };

    static bool IsKnownPseudoElement(SelectorNode e)
    {
        if ((e.Kind != SelectorKind.Selector && e.Kind != SelectorKind.Function) || !SelectorParser.IsPseudoElement(e)) return false;
        string v = e.Value.ToLowerInvariant();
        return !v.StartsWith("::", StringComparison.Ordinal) || v.StartsWith("::-", StringComparison.Ordinal) || s_knownPseudoElements.Contains(v);
    }

    /// <summary>选择器列表中每一项都在伪元素后接组合符（无法合法嵌套）。</summary>
    static bool IsInvalidNesting(List<SelectorNode> ast)
    {
        if (ast.Count == 0) return false;
        foreach (var n in ast)
        {
            if (!HasPseudoElementCombinator(n, 0)) return false;
        }
        return true;
    }

    static bool HasPseudoElementCombinator(SelectorNode e, int depth)
    {
        if (depth > Limits.MaxValueNesting) return false;
        switch (e.Kind)
        {
            case SelectorKind.List:
                foreach (var n in e.Nodes!)
                {
                    if (!HasPseudoElementCombinator(n, depth + 1)) return false;
                }
                return true;
            case SelectorKind.Complex:
                for (int i = 0; i < e.Nodes!.Count; i++)
                {
                    var r = e.Nodes[i];
                    if (r.Kind == SelectorKind.Combinator)
                    {
                        if (i == 0) continue;
                        var prev = e.Nodes[i - 1];
                        if (IsKnownPseudoElement(prev)) return true;
                        if (prev.Kind == SelectorKind.Compound)
                        {
                            foreach (var x in prev.Nodes!)
                            {
                                if (IsKnownPseudoElement(x)) return true;
                            }
                        }
                    }
                    else if (HasPseudoElementCombinator(r, depth + 1)) return true;
                }
                return false;
            case SelectorKind.Compound:
                foreach (var n in e.Nodes!)
                {
                    if (HasPseudoElementCombinator(n, depth + 1)) return true;
                }
                return false;
            case SelectorKind.Function:
                if (e.Value != ":is" && e.Value != ":where") return false;
                return IsInvalidNesting(e.Nodes!);
            default:
                return false;
        }
    }
}
