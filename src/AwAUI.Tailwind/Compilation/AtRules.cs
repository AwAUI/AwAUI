using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

/// <summary><c>@variant</c>、<c>@slot</c>、<c>@apply</c> 的展开（移植自 Tailwind <c>variants.ts</c> / <c>apply.ts</c>）。</summary>
internal static class AtRules
{
    /// <summary>把 <c>@slot</c> 替换为给定节点；<c>@keyframes</c> 与 <c>@property</c> 提升到根。</summary>
    public static void SubstituteAtSlot(List<AstNode> ast, List<AstNode> nodes)
    {
        AstNode.Walk(ast, (node, _) =>
        {
            if (node.Kind == AstKind.AtRule && node.Name == "@slot") return WalkAction<AstNode>.ReplaceSkip(new List<AstNode>(nodes));
            if (node.Kind == AstKind.AtRule && (node.Name == "@keyframes" || node.Name == "@property"))
            {
                var copy = AstNode.AtRule(node.Name, node.Params, node.Nodes);
                node.AssignFrom(AstNode.AtRoot(new List<AstNode> { copy }));
                return WalkAction<AstNode>.Skip;
            }
            return WalkAction<AstNode>.Continue;
        });
    }

    /// <summary>展开 <c>@variant hover { ... }</c>（可逗号分隔多个，可链式 <c>hover:focus</c>）。</summary>
    public static bool SubstituteAtVariant(List<AstNode> ast, DesignSystem ds)
    {
        bool any = false;
        AstNode.Walk(ast, (node, _) =>
        {
            if (node.Kind != AstKind.AtRule || node.Name != "@variant") return WalkAction<AstNode>.Continue;
            var replacement = new List<AstNode>();
            var variantGroups = Js.Segment(node.Params, ',');
            for (int g = 0; g < variantGroups.Count; g++)
            {
                var body = g == variantGroups.Count - 1 ? node.Nodes : AstNode.CloneList(node.Nodes);
                var rule = AstNode.Rule("&", body);
                var chain = Js.Segment(variantGroups[g], ':');
                for (int i = chain.Count - 1; i >= 0; --i)
                {
                    string name = chain[i].Trim();
                    if (name.Length == 0) throw new TailwindException("Cannot use `@variant` with empty variant");
                    var parsed = ds.ParseVariant(name);
                    if (parsed == null) throw new TailwindException($"Cannot use `@variant` with unknown variant: {name}");
                    if (!CandidateCompiler.ApplyVariant(rule, parsed, ds.Variants, 0)) throw new TailwindException($"Cannot use `@variant` with variant: {name}");
                }
                if (rule.Selector == "&" && rule.Kind == AstKind.Rule) replacement.AddRange(rule.Nodes);
                else replacement.Add(rule);
            }
            any = true;
            return WalkAction<AstNode>.Replace(replacement);
        });
        return any;
    }

    static readonly Regex s_whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    static string[] SplitParams(string p)
    {
        var list = new List<string>();
        foreach (var s in s_whitespace.Split(p))
        {
            if (s.Length > 0) list.Add(s);
        }
        return list.ToArray();
    }

    static IEnumerable<string> ResolveApplyDependencies(AstNode apply, DesignSystem ds)
    {
        foreach (var raw in SplitParams(apply.Params))
        {
            foreach (var c in ds.ParseCandidate(raw))
            {
                if (c.Kind == CandidateKind.Static || c.Kind == CandidateKind.Functional) yield return c.Root;
            }
        }
    }

    /// <summary>展开 <c>@apply</c>：按依赖拓扑序处理（<c>@utility</c> 里也可以 <c>@apply</c> 其它自定义工具），检测循环。</summary>
    public static bool SubstituteAtApply(List<AstNode> ast, DesignSystem ds)
    {
        bool features = false;
        var root = AstNode.Rule("&", ast);
        var parents = new List<AstNode>();
        var parentSet = new HashSet<AstNode>(ReferenceEqualityComparer<AstNode>.Instance);
        var dependencies = new Dictionary<AstNode, HashSet<string>>(ReferenceEqualityComparer<AstNode>.Instance);
        var definitions = new Dictionary<string, List<AstNode>>(StringComparer.Ordinal);

        HashSet<string> Deps(AstNode n)
        {
            if (!dependencies.TryGetValue(n, out var s))
            {
                s = new HashSet<string>(StringComparer.Ordinal);
                dependencies[n] = s;
            }
            return s;
        }

        void AddParent(AstNode n)
        {
            if (parentSet.Add(n)) parents.Add(n);
        }

        AstNode.Walk(new List<AstNode> { root }, (node, ctx) =>
        {
            if (node.Kind != AstKind.AtRule) return WalkAction<AstNode>.Continue;
            if (node.Name == "@keyframes")
            {
                AstNode.Walk(node.Nodes, (child, _) =>
                {
                    if (child.Kind == AstKind.AtRule && child.Name == "@apply") throw new TailwindException("You cannot use `@apply` inside `@keyframes`.");
                    return WalkAction<AstNode>.Continue;
                });
                return WalkAction<AstNode>.Skip;
            }
            if (node.Name == "@utility")
            {
                string name = node.Params.EndsWith("-*", StringComparison.Ordinal) ? node.Params.Substring(0, node.Params.Length - 2) : node.Params;
                if (!definitions.TryGetValue(name, out var defs))
                {
                    defs = new List<AstNode>();
                    definitions[name] = defs;
                }
                defs.Add(node);
                var utilityNode = node;
                AstNode.Walk(node.Nodes, (child, _) =>
                {
                    if (child.Kind != AstKind.AtRule || child.Name != "@apply") return WalkAction<AstNode>.Continue;
                    AddParent(utilityNode);
                    foreach (var dep in ResolveApplyDependencies(child, ds)) Deps(utilityNode).Add(dep);
                    return WalkAction<AstNode>.Continue;
                });
                return WalkAction<AstNode>.Continue;
            }
            if (node.Name == "@apply")
            {
                if (ctx.Parent == null) return WalkAction<AstNode>.Continue;
                features = true;
                AddParent(ctx.Parent);
                var path = ctx.Path();
                foreach (var dep in ResolveApplyDependencies(node, ds))
                {
                    foreach (var p in path)
                    {
                        if (parentSet.Contains(p)) Deps(p).Add(dep);
                    }
                }
            }
            return WalkAction<AstNode>.Continue;
        });

        // 拓扑排序：被依赖的 @utility 先展开
        var visited = new HashSet<AstNode>(ReferenceEqualityComparer<AstNode>.Instance);
        var visiting = new HashSet<AstNode>(ReferenceEqualityComparer<AstNode>.Instance);
        var sorted = new List<AstNode>();

        void Visit(AstNode node, List<AstNode> path)
        {
            if (visited.Contains(node)) return;
            if (visiting.Contains(node))
            {
                var next = path[(path.IndexOf(node) + 1) % path.Count];
                if (node.Kind == AstKind.AtRule && node.Name == "@utility" && next.Kind == AstKind.AtRule && next.Name == "@utility")
                {
                    AstNode.Walk(node.Nodes, (child, _) =>
                    {
                        if (child.Kind != AstKind.AtRule || child.Name != "@apply") return WalkAction<AstNode>.Continue;
                        foreach (var raw in SplitParams(child.Params))
                        {
                            foreach (var c in ds.ParseCandidate(raw))
                            {
                                if ((c.Kind == CandidateKind.Static || c.Kind == CandidateKind.Functional) && (next.Params.EndsWith("-*", StringComparison.Ordinal) ? next.Params.Substring(0, next.Params.Length - 2) : next.Params) == c.Root)
                                    throw new TailwindException($"You cannot `@apply` the `{raw}` utility here because it creates a circular dependency.");
                            }
                        }
                        return WalkAction<AstNode>.Continue;
                    });
                }
                throw new TailwindException("Circular dependency detected:\n\n" + CssPrinter.Print(new List<AstNode> { node }, false) + "\nRelies on:\n\n" + CssPrinter.Print(new List<AstNode> { next }, false));
            }
            if (path.Count > Limits.MaxImportDepth * 10) throw new TailwindException("`@apply` dependency chain is too deep.");
            visiting.Add(node);
            if (dependencies.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (!definitions.TryGetValue(dep, out var defs)) continue;
                    foreach (var def in defs)
                    {
                        path.Add(node);
                        Visit(def, path);
                        path.RemoveAt(path.Count - 1);
                    }
                }
            }
            visited.Add(node);
            visiting.Remove(node);
            sorted.Add(node);
        }

        foreach (var p in parents) Visit(p, new List<AstNode>());

        int expanded = 0;
        foreach (var parent in sorted)
        {
            if (!parent.HasChildren) continue;
            AstNode.Walk(parent.Nodes, (child, _) =>
            {
                if (child.Kind != AstKind.AtRule || child.Name != "@apply") return WalkAction<AstNode>.Continue;
                var candidates = SplitParams(child.Params);
                var utilities = new List<string>();
                var mixins = new List<string>();
                foreach (var c in candidates)
                {
                    if (c.Length > 1 && c[0] == '-' && c[1] == '-') mixins.Add(c);
                    else utilities.Add(c);
                }
                if (mixins.Count > 0)
                {
                    if (utilities.Count == 0) return WalkAction<AstNode>.Skip;
                    throw new TailwindException($"You cannot use `@apply` with both mixins and utilities. Please move `@apply {string.Join(" ", mixins)}` into a separate rule.");
                }
                if (child.Nodes.Count > 0 && utilities.Count > 0) throw new TailwindException($"The rule `@apply {string.Join(" ", utilities)}` must not have a body.");
                var compiled = CandidateCompiler.CompileCandidates(utilities, ds, invalid => throw UnknownApply(invalid, ds), respectImportant: false);
                var newNodes = new List<AstNode>();
                foreach (var node in compiled.AstNodes)
                {
                    var clone = node.Clone();
                    if (clone.Kind == AstKind.Rule) newNodes.AddRange(clone.Nodes);
                    else newNodes.Add(clone);
                }
                expanded += CountNodes(newNodes, Limits.MaxApplyExpansionNodes - expanded + 1);
                if (expanded > Limits.MaxApplyExpansionNodes) throw new TailwindException("`@apply` expands to too much CSS (more than " + Limits.MaxApplyExpansionNodes + " nodes). Check for utilities that `@apply` each other repeatedly.");
                return WalkAction<AstNode>.Replace(newNodes);
            });
        }
        return features;
    }

    /// <summary>节点总数（含所有后代），数到 <paramref name="limit"/> 即停止。</summary>
    internal static int CountNodes(List<AstNode> nodes, int limit)
    {
        int count = 0;
        AstNode.Walk(nodes, (_, _) => ++count >= limit ? WalkAction<AstNode>.Stop : WalkAction<AstNode>.Continue);
        return count;
    }

    static TailwindException UnknownApply(string candidate, DesignSystem ds)
    {
        if (ds.Theme.Prefix != null && !candidate.StartsWith(ds.Theme.Prefix, StringComparison.Ordinal))
            return new TailwindException($"Cannot apply unprefixed utility class `{candidate}`. Did you mean `{ds.Theme.Prefix}:{candidate}`?");
        if (ds.InvalidCandidates.Contains(candidate))
            return new TailwindException($"Cannot apply utility class `{candidate}` because it has been explicitly disabled: https://tailwindcss.com/docs/detecting-classes-in-source-files#explicitly-excluding-classes");
        var parts = Js.Segment(candidate, ':');
        if (parts.Count > 1)
        {
            string utility = parts[parts.Count - 1];
            parts.RemoveAt(parts.Count - 1);
            if (ds.CandidatesToCss(new[] { utility })[0] != null)
            {
                var checks = new List<string>();
                foreach (var v in parts) checks.Add(v + ":[--tw-variant-check:1]");
                var css = ds.CandidatesToCss(checks);
                var missing = new List<string>();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (css[i] == null) missing.Add("`" + parts[i] + "`");
                }
                if (missing.Count == 1) return new TailwindException($"Cannot apply utility class `{candidate}` because the {missing[0]} variant does not exist.");
                if (missing.Count > 1)
                {
                    string list = missing.Count == 2 ? missing[0] + " and " + missing[1] : string.Join(", ", missing.GetRange(0, missing.Count - 1)) + ", and " + missing[missing.Count - 1];
                    return new TailwindException($"Cannot apply utility class `{candidate}` because the {list} variants do not exist.");
                }
            }
        }
        if (ds.Theme.Count == 0)
            return new TailwindException($"Cannot apply unknown utility class `{candidate}`. Are you using CSS modules or similar and missing `@reference`? https://tailwindcss.com/docs/functions-and-directives#reference-directive");
        return new TailwindException($"Cannot apply unknown utility class `{candidate}`");
    }
}
