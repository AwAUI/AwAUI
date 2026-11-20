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
        => Matches(selector, element, pseudoElement, null);

    /// <summary>
    /// 带 <c>:has()</c> 结果缓存的匹配（样式引擎在一次重算批次内共享 <paramref name="hasCache"/>）。
    /// </summary>
    public static bool Matches(ComplexSelector selector, StyleElement element, PseudoElementKind pseudoElement, HasMatchCache? hasCache)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        if (element is null) throw new ArgumentNullException(nameof(element));
        var pe = selector.PseudoElement?.PseudoElement ?? PseudoElementKind.None;
        if (pe == PseudoElementKind.Part) pe = PseudoElementKind.None;
        if (pe != pseudoElement) return false;
        var context = new MatchContext { HasCache = hasCache };
        return MatchFrom(selector, selector.Compounds.Count - 1, element, ref context) == Result.Matched;
    }

    /// <summary><c>:has()</c> 的候选：相对选择器从主体开始匹配，第一个复合选择器（锚点）只匹配当前锚点。</summary>
    internal static bool MatchRelativeCandidate(ComplexSelector relative, int last, StyleElement candidate, ref MatchContext context)
        => MatchFrom(relative, last, candidate, ref context) == Result.Matched;

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
            if (context.HasBudgetExceeded) return false; // 未完成的匹配不能记忆
        }
        if (list.NeedsMemo) context.Set(list, element, result);
        return result;
    }

    static bool MatchSimple(IReadOnlyList<SimpleSelector> list, StyleElement e, ref MatchContext context)
    {
        for (int i = 0; i < list.Count; i++)
            if (!list[i].Matches(e, ref context) || context.HasBudgetExceeded) return false;
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
                return context.HasBudgetExceeded ? Result.NotMatchedGlobally : Result.RestartFromClosestLaterSibling;
            var host = element.PartHost;
            if (host is null) return Result.RestartFromClosestLaterSibling;
            subject = host;
        }
        else if (compound.PseudoElement is not null && compound.PseudoElementStates.Count > 0)
        {
            // 其他伪元素之后的状态伪类（::file-selector-button:hover）：没有独立的伪元素状态，按原始元素近似
            if (!MatchSimple(compound.PseudoElementStates, element, ref context))
                return context.HasBudgetExceeded ? Result.NotMatchedGlobally : Result.RestartFromClosestLaterSibling;
        }
        if (!MatchSimple(compound.Selectors, subject, ref context))
            return context.HasBudgetExceeded ? Result.NotMatchedGlobally : Result.RestartFromClosestLaterSibling;
        if (index == 0) return Result.Matched;

        var combinator = s.Combinators[index - 1];
        bool sibling = combinator is Combinator.NextSibling or Combinator.SubsequentSibling;
        var candidateNotFound = sibling ? Result.RestartFromClosestDescendant : Result.NotMatchedGlobally;
        var next = subject;
        bool relative = context.HasRelative is { } rel && ReferenceEquals(rel, s);
        bool insideHas = context.InsideHas;
        while (true)
        {
            next = sibling ? next.PreviousSibling : next.Parent;
            if (next is null) return candidateNotFound;
            if (relative)
            {
                // 锚点边界是选择器语义的一部分，必须先于预算判断；锚点外的元素既不能参与匹配，也不消耗预算。
                if (ReferenceEquals(next, context.HasAnchor) ? index - 1 > 0 : ReferenceEquals(next, context.HasAnchorParent))
                    return Result.NotMatchedGlobally;
            }
            // :is() / :not() 等内部的组合器匹配也属于当前 :has() 求值，不能绕过它的工作上限。
            if (insideHas && !context.VisitHasStep()) return Result.NotMatchedGlobally;
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

    /// <summary>正在求值的 <c>:has()</c> 的锚点（<see cref="HasAnchorSelector"/> 只匹配它）。</summary>
    public StyleElement? HasAnchor;

    /// <summary>正在匹配的 <c>:has()</c> 相对选择器（只有它的回溯受锚点限制，参数中的 <c>:is()</c> 等不受限制）。</summary>
    public ComplexSelector? HasRelative;

    /// <summary>锚点的父元素（相对选择器回溯的界）。</summary>
    public StyleElement? HasAnchorParent;

    /// <summary><c>:has()</c> 的结果缓存与访问预算；顶层匹配没有提供时，第一次求值 <c>:has()</c> 时创建一个（只在这次匹配内有效）。</summary>
    public HasMatchCache? HasCache;

    /// <summary>正在求值 <c>:has()</c>。内部的组合器与伪类遍历也使用同一个预算。</summary>
    public bool InsideHas => HasRelative is not null;

    /// <summary>本次顶层选择器匹配因预算中止；这不是普通的不匹配，不能被 <c>:not()</c> 取反。</summary>
    public bool HasBudgetExceeded { get; private set; }

    /// <summary>计一步 <c>:has()</c> 的访问；预算耗尽时返回 false。</summary>
    public bool VisitHasStep()
    {
        if ((HasCache ??= new HasMatchCache()).Visit()) return true;
        HasBudgetExceeded = true;
        return false;
    }

    /// <summary>CSS 匹配器内的遍历只在 <c>:has()</c> 求值期间计费。</summary>
    public bool VisitSelectorWalkStep() => !InsideHas || VisitHasStep();

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

/// <summary>
/// <c>:has()</c> 的结果缓存：按 (选择器, 锚点元素) 记录结果。只在元素树不变的期间有效——
/// 样式引擎在一次 <see cref="Cascade.StyleEngine.BeginRecalcBatch"/> 批次内共享一个实例，批次外每次解析一个。
/// 同时限制每次元素解析中 <c>:has()</c> 的候选与匹配遍历步数。
/// </summary>
public sealed class HasMatchCache
{
    /// <summary>
    /// 一次元素解析中 <c>:has()</c> 至多访问的步数（候选元素与内部的选择器遍历）；超出后中止当前选择器匹配。
    /// </summary>
    public const int MaxVisitsPerResolve = 1_000_000;

    /// <summary>
    /// 一个重算批次（<see cref="Cascade.StyleEngine.BeginRecalcBatch"/>）内 <c>:has()</c> 至多访问的总步数。
    /// 批次缓存使正常页面的总开销为 O(Σ 锚点子树)，远低于此；恶意的「深树 × 每个元素都是锚点 × 候选都要回溯」组合（O(n³)）在这里截断。
    /// </summary>
    public const int MaxVisitsPerBatch = 32_000_000;

    readonly Dictionary<Key, bool> _results = new();
    readonly int _maxVisitsPerResolve;
    readonly int _maxVisitsPerBatch;
    int _budget;
    int _batchBudget;

    public HasMatchCache() : this(MaxVisitsPerResolve, MaxVisitsPerBatch)
    {
    }

    internal HasMatchCache(int maxVisitsPerResolve, int maxVisitsPerBatch)
    {
        if (maxVisitsPerResolve < 0) throw new ArgumentOutOfRangeException(nameof(maxVisitsPerResolve));
        if (maxVisitsPerBatch < 0) throw new ArgumentOutOfRangeException(nameof(maxVisitsPerBatch));
        _maxVisitsPerResolve = maxVisitsPerResolve;
        _maxVisitsPerBatch = maxVisitsPerBatch;
        _budget = maxVisitsPerResolve;
        _batchBudget = maxVisitsPerBatch;
    }

    /// <summary>缓存的结果数。</summary>
    public int Count => _results.Count;

    /// <summary>因超出预算而按不匹配处理的次数（诊断用）。</summary>
    public int BudgetExceededCount { get; private set; }

    internal bool TryGet(HasSelector selector, StyleElement element, out bool result) => _results.TryGetValue(new Key(selector, element), out result);

    internal void Set(HasSelector selector, StyleElement element, bool result)
    {
        // 预算耗尽后的结果不可信，不缓存
        if (_budget >= 0) _results[new Key(selector, element)] = result;
    }

    internal bool Visit()
    {
        if (_budget < 0) return false;
        if (_batchBudget > 0 && _budget > 0)
        {
            _batchBudget--;
            _budget--;
            return true;
        }
        _budget = -1;
        BudgetExceededCount++;
        return false;
    }

    /// <summary>开始解析一个新元素：重置单次解析的访问预算（缓存的结果与批次总预算保留）。</summary>
    public void ResetBudget() => _budget = _maxVisitsPerResolve;

    /// <summary>清空缓存（元素树或状态变化后），并重置全部预算。</summary>
    public void Clear()
    {
        _results.Clear();
        _budget = _maxVisitsPerResolve;
        _batchBudget = _maxVisitsPerBatch;
    }

    readonly struct Key : IEquatable<Key>
    {
        readonly HasSelector _selector;
        readonly StyleElement _element;

        public Key(HasSelector selector, StyleElement element)
        {
            _selector = selector;
            _element = element;
        }

        public bool Equals(Key other) => ReferenceEquals(_selector, other._selector) && ReferenceEquals(_element, other._element);
        public override bool Equals(object? obj) => obj is Key k && Equals(k);
        public override int GetHashCode() => unchecked(RuntimeHelpers.GetHashCode(_selector) * -1521134295 + RuntimeHelpers.GetHashCode(_element));
    }
}
