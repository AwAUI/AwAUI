using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Variants;

namespace AwAUI.Tailwind.Compilation;

/// <summary>编译结果：排好序的规则与每条规则的排序信息。</summary>
internal sealed class CompileResult
{
    public List<AstNode> AstNodes = new();
    public Dictionary<AstNode, NodeSorting> Sorting = new(ReferenceEqualityComparer<AstNode>.Instance);
}

internal sealed class NodeSorting
{
    public PropertySort Properties;
    public BitSet Variants;
    public string Candidate = "";
}

/// <summary>候选 → CSS 规则（移植自 Tailwind <c>compile.ts</c>）。</summary>
internal static class CandidateCompiler
{
    /// <summary>编译一组候选并按 v4 的规则排序：变体顺序 → 属性顺序 → 属性数量 → 类名自然序。</summary>
    public static CompileResult CompileCandidates(IEnumerable<string> rawCandidates, DesignSystem ds, Action<string>? onInvalidCandidate = null, bool respectImportant = true)
    {
        var result = new CompileResult();
        var matches = new List<KeyValuePair<string, List<Candidate>>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawCandidates)
        {
            if (!seen.Add(raw)) continue;
            if (ds.InvalidCandidates.Contains(raw))
            {
                onInvalidCandidate?.Invoke(raw);
                continue;
            }
            var candidates = ds.ParseCandidate(raw);
            if (candidates.Count == 0)
            {
                onInvalidCandidate?.Invoke(raw);
                continue;
            }
            matches.Add(new KeyValuePair<string, List<Candidate>>(raw, candidates));
        }

        int flags = respectImportant ? 1 : 0;
        Dictionary<Variant, int>? variantOrder = matches.Count > 1 ? ds.GetVariantOrder() : null;

        foreach (var kv in matches)
        {
            bool found = false;
            foreach (var candidate in kv.Value)
            {
                var compiled = ds.CompileAstNodes(candidate, flags);
                if (compiled.Count == 0) continue;
                found = true;
                foreach (var c in compiled)
                {
                    var bits = new BitSet();
                    if (variantOrder != null)
                    {
                        foreach (var v in candidate.Variants)
                        {
                            if (variantOrder.TryGetValue(v, out int idx)) bits.Set(idx);
                        }
                    }
                    result.Sorting[c.Node] = new NodeSorting { Properties = c.Sort, Variants = bits, Candidate = kv.Key };
                    result.AstNodes.Add(c.Node);
                }
            }
            if (!found) onInvalidCandidate?.Invoke(kv.Key);
        }

        var sorting = result.Sorting;
        Sorting.StableSort(result.AstNodes, (a, z) => Compare(sorting[a], sorting[z]));
        return result;
    }

    public static int Compare(NodeSorting a, NodeSorting z)
    {
        int v = a.Variants.CompareTo(z.Variants);
        if (v != 0) return v;
        var ao = a.Properties.Order;
        var zo = z.Properties.Order;
        int offset = 0;
        while (offset < ao.Length && offset < zo.Length && ao[offset] == zo[offset]) offset++;
        bool aHas = offset < ao.Length, zHas = offset < zo.Length;
        if (aHas && zHas)
        {
            int d = ao[offset] - zo[offset];
            if (d != 0) return d;
        }
        else if (aHas) return -1;
        else if (zHas) return 1;
        int count = z.Properties.Count - a.Properties.Count;
        if (count != 0) return count;
        return Js.NaturalCompare(a.Candidate, z.Candidate);
    }

    /// <summary>编译一个已解析的候选：生成声明、应用 important、按变体从内到外包裹选择器。</summary>
    public static List<CompiledNode> CompileAstNodes(Candidate candidate, DesignSystem ds, int flags)
    {
        var results = new List<CompiledNode>();
        var asts = CompileBaseCandidate(candidate, ds);
        if (asts.Count == 0) return results;
        bool important = ds.Important && (flags & 1) != 0;
        string selector = "." + Js.Escape(candidate.Raw);
        foreach (var nodes in asts)
        {
            var sort = GetPropertySort(nodes);
            if (candidate.Important || important) ApplyImportant(nodes);
            var node = AstNode.Rule(selector, nodes);
            foreach (var variant in candidate.Variants)
            {
                if (!ApplyVariant(node, variant, ds.Variants, 0)) return new List<CompiledNode>();
            }
            results.Add(new CompiledNode { Node = node, Sort = sort });
        }
        return results;
    }

    /// <summary>把变体应用到规则（Tailwind <c>applyVariant</c>）。返回 false 表示变体在此处无效。</summary>
    public static bool ApplyVariant(AstNode node, Variant variant, VariantRegistry variants, int depth)
    {
        if (depth > Limits.MaxCompoundDepth) return false;
        if (variant.Kind == VariantKind.Arbitrary)
        {
            if (variant.Relative && depth == 0) return false;
            var wrapped = AstNode.RuleOrAtRule(variant.Selector, node.Nodes);
            node.Nodes = new List<AstNode> { wrapped };
            return true;
        }
        var entry = variants.Get(variant.Root);
        if (entry == null) return false;
        if (variant.Kind == VariantKind.Compound)
        {
            var isolated = AstNode.AtRule("@slot");
            if (!ApplyVariant(isolated, variant.Inner!, variants, depth + 1)) return false;
            if (variant.Root == "not" && isolated.Nodes.Count > 1) return false;
            foreach (var child in isolated.Nodes)
            {
                if (child.Kind != AstKind.Rule && child.Kind != AstKind.AtRule) return false;
                if (!entry.ApplyFn(child, variant)) return false;
            }
            var bodyNodes = node.Nodes;
            AstNode.Walk(isolated.Nodes, (n, _) =>
            {
                if ((n.Kind == AstKind.Rule || n.Kind == AstKind.AtRule) && n.Nodes.Count <= 0)
                {
                    n.Nodes = bodyNodes;
                    return WalkAction<AstNode>.Skip;
                }
                return WalkAction<AstNode>.Continue;
            });
            node.Nodes = isolated.Nodes;
            return true;
        }
        return entry.ApplyFn(node, variant);
    }

    /// <summary>调用工具注册表生成声明（Tailwind <c>compileBaseUtility</c>）。</summary>
    static List<List<AstNode>> CompileBaseCandidate(Candidate candidate, DesignSystem ds)
    {
        var asts = new List<List<AstNode>>();
        if (candidate.Kind == CandidateKind.Arbitrary)
        {
            string? value = candidate.ArbitraryValue;
            if (candidate.Modifier != null) value = Colors.AsColor(value, candidate.Modifier, ds.Theme);
            if (value == null) return asts;
            asts.Add(new List<AstNode> { AstNode.Decl(candidate.Property, value) });
            return asts;
        }
        var utils = ds.Utilities.Get(candidate.Root);
        var kind = candidate.Kind == CandidateKind.Static ? UtilityKind.Static : UtilityKind.Functional;
        foreach (var u in utils)
        {
            if (u.Kind != kind) continue;
            var nodes = u.CompileFn(candidate);
            if (nodes == null) continue;
            asts.Add(nodes);
        }
        return asts;
    }

    static void ApplyImportant(List<AstNode> ast)
    {
        foreach (var node in ast)
        {
            if (node.Kind == AstKind.AtRoot) continue;
            if (node.Kind == AstKind.Declaration) node.Important = true;
            else if (node.Kind == AstKind.Rule || node.Kind == AstKind.AtRule) ApplyImportant(node.Nodes);
        }
    }

    /// <summary>计算属性排序信息：出现的属性在排序表中的位置（去重、升序）与声明数量。<c>--tw-sort</c> 声明可覆盖排序。</summary>
    static PropertySort GetPropertySort(List<AstNode> nodes)
    {
        var order = new SortedSet<int>();
        int count = 0;
        var queue = new Queue<AstNode>(nodes);
        bool seenTwSort = false;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Kind == AstKind.Declaration)
            {
                if (node.Value == null) continue;
                count++;
                if (seenTwSort) continue;
                if (node.Property == "--tw-sort")
                {
                    int idx = PropertyOrder.IndexOf(node.Value ?? "");
                    if (idx != -1)
                    {
                        order.Add(idx);
                        seenTwSort = true;
                        continue;
                    }
                }
                int i = PropertyOrder.IndexOf(node.Property);
                if (i != -1) order.Add(i);
            }
            else if (node.Kind == AstKind.Rule || node.Kind == AstKind.AtRule)
            {
                foreach (var c in node.Nodes) queue.Enqueue(c);
            }
        }
        var arr = new int[order.Count];
        order.CopyTo(arr);
        return new PropertySort(arr, count);
    }
}

/// <summary>变体排名的位集合，比较时按「最高位优先」的大整数语义（对应 Tailwind 的 BigInt）。</summary>
internal struct BitSet
{
    ulong[]? _words;

    public void Set(int index)
    {
        int w = index >> 6;
        if (_words == null) _words = new ulong[w + 1];
        else if (_words.Length <= w) Array.Resize(ref _words, w + 1);
        _words[w] |= 1UL << (index & 63);
    }

    public int CompareTo(BitSet other)
    {
        var a = _words;
        var b = other._words;
        int aLen = a?.Length ?? 0, bLen = b?.Length ?? 0;
        int max = aLen > bLen ? aLen : bLen;
        for (int i = max - 1; i >= 0; i--)
        {
            ulong x = i < aLen ? a![i] : 0;
            ulong y = i < bLen ? b![i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }
}
