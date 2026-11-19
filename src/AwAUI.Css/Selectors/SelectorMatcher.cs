using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AwAUI.Css.Selectors;

/// <summary>
/// 选择器匹配：从主体复合选择器开始自右向左，组合器回溯采用浏览器引擎通用的「失败分级」剪枝，
/// 避免后代/兄弟组合器的指数级回溯；<c>:is()</c>、<c>&amp;</c> 等参数中含组合器时，一次匹配内按 (列表, 元素) 记忆结果，
/// 避免嵌套选择器在祖先链上重复求值。
/// </summary>
public static class SelectorMatcher
{
    enum Result : byte
    {
        Matched,
        /// <summary>从最近的后续兄弟组合器重新尝试。</summary>
        RestartFromClosestLaterSibling,
        /// <summary>从最近的后代组合器重新尝试。</summary>
        RestartFromClosestDescendant,
        /// <summary>任何候选都不可能匹配。</summary>
        NotMatchedGlobally,
    }

    /// <summary>
    /// 选择器是否匹配元素。<paramref name="pseudoElement"/> 指定要匹配的伪元素：带 <c>::before</c> 等的选择器
    /// 只在请求相同伪元素时匹配其原始元素；<c>::part()</c> 选择器匹配部件元素本身（请求 <see cref="PseudoElementKind.None"/>）。
    /// </summary>
    public static bool Matches(ComplexSelector selector, StyleElement element, PseudoElementKind pseudoElement = PseudoElementKind.None)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        if (element is null) throw new ArgumentNullException(nameof(element));
        var pe = selector.PseudoElement?.PseudoElement ?? PseudoElementKind.None;
        if (pe == PseudoElementKind.Part) pe = PseudoElementKind.None;
        if (pe != pseudoElement) return false;
        var context = default(MatchContext);
        return MatchFrom(selector, selector.Compounds.Count - 1, element, ref context) == Result.Matched;
    }

    /// <summary>列表中任一选择器匹配即匹配。</summary>
    public static bool Matches(SelectorList list, StyleElement element, PseudoElementKind pseudoElement = PseudoElementKind.None)
    {
        if (list is null) throw new ArgumentNullException(nameof(list));
        foreach (var s in list.Selectors)
            if (Matches(s, element, pseudoElement)) return true;
        return false;
    }

    /// <summary>
    /// 供 <c>:is()</c> / <c>:not()</c> / <c>of S</c> / <c>&amp;</c> 内部使用：参数中不允许伪元素，但嵌套规则的父选择器可能带伪元素，
    /// 按规范这种父选择器不匹配任何元素。
    /// </summary>
    internal static bool MatchesAny(SelectorList list, StyleElement element, ref MatchContext context)
    {
        if (list.NeedsMemo && context.TryGet(list, element, out bool cached)) return cached;
        bool result = false;
        var selectors = list.Selectors;
        for (int i = 0; i < selectors.Count; i++)
        {
            var s = selectors[i];
            if (s.PseudoElement is not null) continue;
            if (MatchFrom(s, s.Compounds.Count - 1, element, ref context) == Result.Matched) { result = true; break; }
        }
        if (list.NeedsMemo) context.Set(list, element, result);
        return result;
    }

    static bool MatchSimple(IReadOnlyList<SimpleSelector> list, StyleElement e, ref MatchContext context)
    {
        for (int i = 0; i < list.Count; i++)
            if (!list[i].Matches(e, ref context)) return false;
        return true;
    }

    static Result MatchFrom(ComplexSelector s, int index, StyleElement element, ref MatchContext context)
    {
        var compound = s.Compounds[index];
        var subject = element;
        if (compound.PseudoElement is { PseudoElement: PseudoElementKind.Part } part)
        {
            // ::part()：伪元素及其后的伪类作用于部件元素本身，前面的简单选择器作用于宿主
            if (!part.Matches(element, ref context) || !MatchSimple(compound.PseudoElementStates, element, ref context))
                return Result.RestartFromClosestLaterSibling;
            var host = element.PartHost;
            if (host is null) return Result.RestartFromClosestLaterSibling;
            subject = host;
        }
        if (!MatchSimple(compound.Selectors, subject, ref context)) return Result.RestartFromClosestLaterSibling;
        if (index == 0) return Result.Matched;

        var combinator = s.Combinators[index - 1];
        bool sibling = combinator is Combinator.NextSibling or Combinator.SubsequentSibling;
        var candidateNotFound = sibling ? Result.RestartFromClosestDescendant : Result.NotMatchedGlobally;
        var next = subject;
        while (true)
        {
            next = sibling ? next.PreviousSibling : next.Parent;
            if (next is null) return candidateNotFound;
            var r = MatchFrom(s, index - 1, next, ref context);
            if (r == Result.Matched || r == Result.NotMatchedGlobally || combinator == Combinator.NextSibling) return r;
            if (combinator == Combinator.Child) return Result.RestartFromClosestDescendant;
            if (r == Result.RestartFromClosestDescendant && combinator == Combinator.SubsequentSibling) return r;
            // 后代组合器：继续尝试更远的祖先；后续兄弟组合器：继续尝试更前的兄弟
        }
    }
}

/// <summary>一次顶层匹配的上下文：(选择器列表, 元素) → 结果的记忆表，首次需要时才分配。</summary>
internal struct MatchContext
{
    Dictionary<Key, bool>? _memo;

    public bool TryGet(SelectorList list, StyleElement element, out bool result)
    {
        result = false;
        return _memo is not null && _memo.TryGetValue(new Key(list, element), out result);
    }

    public void Set(SelectorList list, StyleElement element, bool result)
        => (_memo ??= new Dictionary<Key, bool>())[new Key(list, element)] = result;

    /// <summary>按引用比较的键（元素适配器可能重写了 Equals）。</summary>
    readonly struct Key : IEquatable<Key>
    {
        readonly SelectorList _list;
        readonly StyleElement _element;

        public Key(SelectorList list, StyleElement element)
        {
            _list = list;
            _element = element;
        }

        public bool Equals(Key other) => ReferenceEquals(_list, other._list) && ReferenceEquals(_element, other._element);

        public override bool Equals(object? obj) => obj is Key k && Equals(k);

        public override int GetHashCode()
            => unchecked(RuntimeHelpers.GetHashCode(_list) * -1521134295 + RuntimeHelpers.GetHashCode(_element));
    }
}
