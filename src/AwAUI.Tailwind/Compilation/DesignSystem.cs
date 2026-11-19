using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Theming;
using AwAUI.Tailwind.Variants;

namespace AwAUI.Tailwind.Compilation;

/// <summary>编译得到的一个节点及其属性排序信息。</summary>
internal sealed class CompiledNode
{
    public AstNode Node = null!;
    public PropertySort Sort;
}

/// <summary>按属性顺序表排序所需的信息。</summary>
internal readonly struct PropertySort
{
    public readonly int[] Order;
    public readonly int Count;

    public PropertySort(int[] order, int count)
    {
        Order = order;
        Count = count;
    }
}

/// <summary>
/// 设计系统：主题 + 工具注册表 + 变体注册表 + 各级缓存（移植自 Tailwind <c>design-system.ts</c>）。
/// 非线程安全；由 <see cref="TailwindCompiler"/> 加锁保护。
/// </summary>
internal sealed class DesignSystem
{
    public readonly Theme Theme;
    public readonly UtilityRegistry Utilities;
    public readonly VariantRegistry Variants;
    public readonly HashSet<string> InvalidCandidates = new(StringComparer.Ordinal);
    public bool Important;

    readonly Dictionary<string, Variant?> _variantCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Candidate>> _candidateCache = new(StringComparer.Ordinal);
    readonly Dictionary<Candidate, List<CompiledNode>>[] _astCache = { new(), new() };
    readonly HashSet<string> _trackedVariables = new(StringComparer.Ordinal);
    Dictionary<Variant, int>? _variantOrder;
    int _variantOrderCount = -1;

    public DesignSystem(Theme theme)
    {
        Theme = theme;
        Utilities = CoreUtilities.Create(theme);
        Variants = CoreVariants.Create(theme);
    }

    /// <summary>缓存条目上限：运行时 JIT 长期运行、遇到大量（可能恶意的）不同类名时，整体清空后重建，内存保持有界。</summary>
    const int MaxCacheEntries = 100_000;

    public List<Candidate> ParseCandidate(string candidate)
    {
        if (!_candidateCache.TryGetValue(candidate, out var list))
        {
            list = CandidateParser.Parse(candidate, this);
            _candidateCache[candidate] = list;
        }
        return list;
    }

    public Variant? ParseVariant(string variant, int depth = 0)
    {
        if (_variantCache.TryGetValue(variant, out var v)) return v;
        v = CandidateParser.ParseVariant(variant, this, depth);
        _variantCache[variant] = v;
        return v;
    }

    /// <summary>
    /// 清空解析与编译缓存。变体对象的身份参与排序，因此三级缓存必须一起清空；正在使用的候选会在下次编译时重新解析。
    /// </summary>
    public void TrimCaches()
    {
        if (_candidateCache.Count + _variantCache.Count + _astCache[0].Count + _astCache[1].Count < MaxCacheEntries && InvalidCandidates.Count < MaxCacheEntries) return;
        _candidateCache.Clear();
        _variantCache.Clear();
        _astCache[0].Clear();
        _astCache[1].Clear();
        _variantOrder = null;
        _variantOrderCount = -1;
        _trackedVariables.Clear();
        if (InvalidCandidates.Count >= MaxCacheEntries) InvalidCandidates.Clear();
        // 注意：只在一次公开操作开始时调用（见 TailwindCompiler），避免同一次编译中新旧变体对象混用
        Variants.ClearCaches();
    }

    /// <summary>编译单个候选（结果缓存）。flags 第 0 位：是否遵守全局 important。</summary>
    public List<CompiledNode> CompileAstNodes(Candidate candidate, int flags)
    {
        var cache = _astCache[flags & 1];
        if (cache.TryGetValue(candidate, out var result)) return result;
        result = CandidateCompiler.CompileAstNodes(candidate, this, flags);
        try
        {
            var nodes = new List<AstNode>(result.Count);
            foreach (var r in result) nodes.Add(r.Node);
            CssFunctions.Substitute(nodes, this);
            AtRules.SubstituteAtVariant(nodes, this);
        }
        catch (TailwindException)
        {
            result = new List<CompiledNode>();
        }
        cache[candidate] = result;
        return result;
    }

    /// <summary>所有已解析变体的排名（相等的变体同名次）。</summary>
    public Dictionary<Variant, int> GetVariantOrder()
    {
        if (_variantOrder != null && _variantOrderCount == _variantCache.Count) return _variantOrder;
        var variants = new List<Variant>();
        foreach (var v in _variantCache.Values)
        {
            if (v != null) variants.Add(v);
        }
        Sorting.StableSort(variants, Variants.Compare);
        var order = new Dictionary<Variant, int>(ReferenceEqualityComparer<Variant>.Instance);
        Variant? prev = null;
        int idx = 0;
        foreach (var v in variants)
        {
            if (prev != null && Variants.Compare(prev, v) != 0) idx++;
            order[v] = idx;
            prev = v;
        }
        _variantOrder = order;
        _variantOrderCount = _variantCache.Count;
        return order;
    }

    /// <summary>解析 <c>--theme(--x)</c> / <c>theme(--x / 50%)</c> 的主题值。</summary>
    public string? ResolveThemeValue(string path, bool forceInline = true)
    {
        int slash = path.LastIndexOf('/');
        string? modifier = null;
        if (slash != -1)
        {
            modifier = path.Substring(slash + 1).Trim();
            path = path.Substring(0, slash).Trim();
        }
        string? value = Theme.Resolve(null, new[] { path }, forceInline ? ThemeOptions.Inline : ThemeOptions.None);
        return modifier != null && modifier.Length > 0 && value != null ? Colors.WithAlpha(value, modifier) : value;
    }

    /// <summary>记录值中引用的主题变量为「已使用」。</summary>
    public void TrackUsedVariables(string value)
    {
        if (!_trackedVariables.Add(value)) return;
        foreach (var name in VariableNames.Extract(value)) Theme.MarkUsedVariable(name);
    }

    /// <summary>逐个候选生成 CSS（每个候选单独优化），无效候选为 null。对应 Tailwind 的 <c>candidatesToCss</c>。</summary>
    public string?[] CandidatesToCss(IReadOnlyList<string> candidates, bool minify = false)
    {
        var result = new string?[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            bool valid = true;
            var compiled = CandidateCompiler.CompileCandidates(new[] { candidates[i] }, this, _ => valid = false);
            if (!valid || compiled.AstNodes.Count == 0)
            {
                result[i] = null;
                continue;
            }
            var optimized = Optimizer.Optimize(compiled.AstNodes, this, Polyfills.None);
            // 与 Tailwind 一致：优化后为空（例如自定义工具的声明全部被丢弃）视为无输出
            result[i] = optimized.Count == 0 ? null : CssPrinter.Print(optimized, minify);
        }
        return result;
    }
}

/// <summary>引用相等比较器（netstandard2.0 没有内置）。</summary>
internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

/// <summary>稳定排序（JavaScript 的 <c>Array.prototype.sort</c> 是稳定的，<see cref="List{T}.Sort()"/> 不是）。</summary>
internal static class Sorting
{
    public static void StableSort<T>(List<T> list, Comparison<T> comparison)
    {
        if (list.Count < 2) return;
        var arr = list.ToArray();
        var tmp = new T[arr.Length];
        MergeSort(arr, tmp, 0, arr.Length, comparison);
        for (int i = 0; i < arr.Length; i++) list[i] = arr[i];
    }

    static void MergeSort<T>(T[] a, T[] tmp, int lo, int hi, Comparison<T> cmp)
    {
        if (hi - lo < 2) return;
        if (hi - lo <= 12)
        {
            for (int i = lo + 1; i < hi; i++)
            {
                T x = a[i];
                int j = i - 1;
                while (j >= lo && cmp(a[j], x) > 0)
                {
                    a[j + 1] = a[j];
                    j--;
                }
                a[j + 1] = x;
            }
            return;
        }
        int mid = (lo + hi) / 2;
        MergeSort(a, tmp, lo, mid, cmp);
        MergeSort(a, tmp, mid, hi, cmp);
        if (cmp(a[mid - 1], a[mid]) <= 0) return;
        int l = lo, r = mid, k = lo;
        while (l < mid && r < hi) tmp[k++] = cmp(a[r], a[l]) < 0 ? a[r++] : a[l++];
        while (l < mid) tmp[k++] = a[l++];
        while (r < hi) tmp[k++] = a[r++];
        Array.Copy(tmp, lo, a, lo, hi - lo);
    }
}
