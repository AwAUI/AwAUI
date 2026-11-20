using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Compilation;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Variants;

/// <summary>变体可与哪些规则复合（位标志，与 Tailwind 的 <c>Compounds</c> 一致）。</summary>
[Flags]
internal enum Compounds
{
    Never = 0,
    AtRules = 1 << 0,
    StyleRules = 1 << 1,
}

/// <summary>把变体应用到节点：修改 <paramref name="node"/> 的子节点或节点本身；返回 false 表示变体无效。</summary>
internal delegate bool VariantApplyFn(AstNode node, Variant variant);

internal sealed class VariantEntry
{
    public VariantKind Kind;
    public VariantApplyFn ApplyFn = null!;
    public int Order;
    public Compounds CompoundsWith;
    public Compounds Compounds;
}

/// <summary>变体注册表（移植自 Tailwind <c>Variants</c>），负责变体的排序比较。</summary>
internal sealed class VariantRegistry
{
    readonly Dictionary<int, Comparison<Variant>> _compareFns = new();
    readonly Dictionary<string, VariantEntry> _variants = new(StringComparer.Ordinal);
    readonly List<string> _keys = new();
    int? _groupOrder;
    int _lastOrder;

    public void Static(string name, Action<AstNode> applyFn, Compounds? compounds = null, int? order = null)
    {
        Set(name, VariantKind.Static, (n, _) =>
        {
            applyFn(n);
            return true;
        }, compounds ?? Compounds.StyleRules, Compounds.Never, order);
    }

    public void StaticChecked(string name, VariantApplyFn applyFn, Compounds? compounds = null, int? order = null)
        => Set(name, VariantKind.Static, applyFn, compounds ?? Compounds.StyleRules, Compounds.Never, order);

    public void Functional(string name, VariantApplyFn applyFn, Compounds? compounds = null, int? order = null)
        => Set(name, VariantKind.Functional, applyFn, compounds ?? Compounds.StyleRules, Compounds.Never, order);

    public void Compound(string name, Compounds compoundsWith, VariantApplyFn applyFn, Compounds? compounds = null, int? order = null)
        => Set(name, VariantKind.Compound, applyFn, compounds ?? Compounds.StyleRules, compoundsWith, order);

    /// <summary>从 <c>@custom-variant name { ... @slot ... }</c> 的主体注册静态变体。</summary>
    public void FromAst(string name, List<AstNode> ast, DesignSystem ds)
    {
        var selectors = new List<string>();
        bool usesAtVariant = false;
        AstNode.Walk(ast, (n, _) =>
        {
            if (n.Kind == AstKind.Rule) selectors.Add(n.Selector);
            else if (n.Kind == AstKind.AtRule && n.Name == "@variant") usesAtVariant = true;
            else if (n.Kind == AstKind.AtRule && n.Name != "@slot") selectors.Add(n.Name + " " + n.Params);
            return WalkAction<AstNode>.Continue;
        });
        StaticChecked(name, (r, _) =>
        {
            var body = AstNode.CloneList(ast);
            if (usesAtVariant)
            {
                AtRules.SubstituteAtVariant(body, ds);
                // 主体里的 @variant 可以引用其它自定义变体，多次嵌套时按层数指数增长
                if (CandidateCompiler.ExceedsSelectorBudget(AstNode.Rule("&", body))) return false;
            }
            AtRules.SubstituteAtSlot(body, r.Nodes);
            r.Nodes = body;
            return true;
        }, GetCompounds(selectors));
    }

    public void Group(Action body, Comparison<Variant>? compareFn = null)
    {
        _groupOrder = NextOrder();
        if (compareFn != null) _compareFns[_groupOrder.Value] = compareFn;
        body();
        _groupOrder = null;
    }

    public bool Has(string name) => _variants.ContainsKey(name);

    public VariantEntry? Get(string name) => _variants.TryGetValue(name, out var e) ? e : null;

    public VariantKind? Kind(string name) => _variants.TryGetValue(name, out var e) ? e.Kind : null;

    public IReadOnlyList<string> Keys => _keys;

    public bool CompoundsWith(string parent, Variant child)
    {
        if (!_variants.TryGetValue(parent, out var p)) return false;
        Compounds childCompounds;
        if (child.Kind == VariantKind.Arbitrary) childCompounds = GetCompounds(new[] { child.Selector });
        else if (_variants.TryGetValue(child.Root, out var c)) childCompounds = c.Compounds;
        else return false;
        if (p.Kind != VariantKind.Compound) return false;
        if (childCompounds == Compounds.Never) return false;
        if (p.CompoundsWith == Compounds.Never) return false;
        return (p.CompoundsWith & childCompounds) != 0;
    }

    public bool CompoundsWith(string parent, string child)
    {
        if (!_variants.TryGetValue(parent, out var p) || !_variants.TryGetValue(child, out var c)) return false;
        if (p.Kind != VariantKind.Compound || c.Compounds == Compounds.Never || p.CompoundsWith == Compounds.Never) return false;
        return (p.CompoundsWith & c.Compounds) != 0;
    }

    /// <summary>变体排序比较（决定输出顺序）。</summary>
    public int Compare(Variant? a, Variant? z)
    {
        if (ReferenceEquals(a, z)) return 0;
        if (a == null) return -1;
        if (z == null) return 1;
        if (a.Kind == VariantKind.Arbitrary && z.Kind == VariantKind.Arbitrary) return string.CompareOrdinal(a.Selector, z.Selector) < 0 ? -1 : 1;
        if (a.Kind == VariantKind.Arbitrary) return 1;
        if (z.Kind == VariantKind.Arbitrary) return -1;
        int aOrder = _variants[a.Root].Order;
        int zOrder = _variants[z.Root].Order;
        int orderedByVariant = aOrder - zOrder;
        if (orderedByVariant != 0) return orderedByVariant;
        if (a.Kind == VariantKind.Compound && z.Kind == VariantKind.Compound)
        {
            int order = Compare(a.Inner, z.Inner);
            if (order != 0) return order;
            if (a.Modifier != null && z.Modifier != null) return string.CompareOrdinal(a.Modifier.Value, z.Modifier.Value) < 0 ? -1 : 1;
            if (a.Modifier != null) return 1;
            if (z.Modifier != null) return -1;
            return 0;
        }
        if (_compareFns.TryGetValue(aOrder, out var fn)) return fn(a, z);
        if (a.Root != z.Root) return string.CompareOrdinal(a.Root, z.Root) < 0 ? -1 : 1;
        var aValue = a.Value;
        var zValue = z.Value;
        if (aValue == null) return -1;
        if (zValue == null) return 1;
        if (aValue.IsArbitrary && !zValue.IsArbitrary) return 1;
        if (!aValue.IsArbitrary && zValue.IsArbitrary) return -1;
        return string.CompareOrdinal(aValue.Value, zValue.Value) < 0 ? -1 : 1;
    }

    void Set(string name, VariantKind kind, VariantApplyFn applyFn, Compounds compounds, Compounds compoundsWith, int? order)
    {
        if (_variants.TryGetValue(name, out var existing))
        {
            existing.Kind = kind;
            existing.ApplyFn = applyFn;
            existing.Compounds = compounds;
            return;
        }
        if (order == null)
        {
            _lastOrder = NextOrder();
            order = _lastOrder;
        }
        _variants[name] = new VariantEntry { Kind = kind, ApplyFn = applyFn, Order = order.Value, CompoundsWith = compoundsWith, Compounds = compounds };
        _keys.Add(name);
    }

    int NextOrder() => _groupOrder ?? _lastOrder + 1;

    readonly List<Action> _cacheClearers = new();

    /// <summary>登记变体实现内部按变体对象缓存的清理动作（断点 / 容器查询的值解析缓存）。</summary>
    public void RegisterCache(Action clear) => _cacheClearers.Add(clear);

    public void ClearCaches()
    {
        foreach (var c in _cacheClearers) c();
    }

    /// <summary>由选择器 / at 规则推断复合能力（Tailwind <c>compoundsForSelectors</c>）。</summary>
    public static Compounds GetCompounds(IEnumerable<string> selectors)
    {
        var compounds = Compounds.Never;
        foreach (var sel in selectors)
        {
            if (sel.Length > 0 && sel[0] == '@')
            {
                if (!sel.StartsWith("@media", StringComparison.Ordinal) && !sel.StartsWith("@supports", StringComparison.Ordinal) && !sel.StartsWith("@container", StringComparison.Ordinal))
                    return Compounds.Never;
                compounds |= Compounds.AtRules;
                continue;
            }
            if (sel.IndexOf("::", StringComparison.Ordinal) >= 0) return Compounds.Never;
            compounds |= Compounds.StyleRules;
        }
        return compounds;
    }
}
