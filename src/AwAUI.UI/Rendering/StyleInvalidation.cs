using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;

namespace AwAUI.UI;

/// <summary>
/// 样式失效的特征集（浏览器「invalidation set」的简化版）：扫描全部样式表的选择器，记录哪些 class / id / 属性 / 伪状态
/// 出现在<b>非主体</b>的复合选择器里（祖先或兄弟位置）。某个元素的这些输入变化时，后代（或兄弟）的匹配结果才可能改变；
/// 其余变化只需要重算元素自身。
/// <para>
/// 例：样式表只有 <c>button:hover {}</c> 时，悬停只重算按钮自身；有 <c>.card:hover .title {}</c> 时，
/// <c>.card</c> 上的悬停变化重算整个子树。逻辑伪类（<c>:is() :not() :where()</c>）与 <c>:nth-child(of …)</c> 的参数中非主体的部分、
/// 嵌套规则的父选择器（<c>&amp;</c>）以及 <c>::part()</c> 的宿主部分一律按非主体处理（保守）。
/// </para>
/// </summary>
internal sealed class StyleInvalidation
{
    readonly HashSet<string> _deepClasses = new(StringComparer.Ordinal);
    readonly HashSet<string> _deepIds = new(StringComparer.Ordinal);
    readonly HashSet<string> _deepAttributes = new(StringComparer.Ordinal);

    /// <summary>出现在非主体位置的伪状态。</summary>
    public PseudoStates DeepStates { get; private set; }

    /// <summary>样式表用到了兄弟组合器（<c>+</c> / <c>~</c>）。</summary>
    public bool HasSiblingCombinator { get; private set; }

    /// <summary>样式表用到了依赖兄弟位置的伪类（<c>:first-child</c>、<c>:nth-*</c> ...）。</summary>
    public bool HasStructuralPseudo { get; private set; }

    /// <summary>结构伪类出现在非主体位置（后代的匹配依赖祖先的兄弟位置）。</summary>
    public bool HasDeepStructuralPseudo { get; private set; }

    /// <summary>样式表用到了 <c>:empty</c>。</summary>
    public bool UsesEmpty { get; private set; }

    public bool IsDeepClass(string name) => _deepClasses.Contains(name);

    public bool IsDeepId(string? id) => id is not null && _deepIds.Contains(id);

    public bool IsDeepAttribute(string name) => _deepAttributes.Contains(name.ToLowerInvariant());

    public void Rebuild(IReadOnlyList<CascadeStyleSheet> sheets)
    {
        _deepClasses.Clear();
        _deepIds.Clear();
        _deepAttributes.Clear();
        DeepStates = PseudoStates.None;
        HasSiblingCombinator = HasStructuralPseudo = HasDeepStructuralPseudo = UsesEmpty = false;
        foreach (var cs in sheets)
        {
            foreach (var rule in cs.Sheet.EnumerateRules())
            {
                if (rule is CssStyleRule style && style.Selectors is { } list)
                    CollectList(list, forceDeep: false, depth: 0);
            }
        }
    }

    void CollectList(SelectorList list, bool forceDeep, int depth)
    {
        // 选择器解析器把函数式伪类的嵌套限制在 32 层；这里再设一道上限以防万一
        if (depth > 64) return;
        foreach (var complex in list.Selectors) Collect(complex, forceDeep, depth);
    }

    void Collect(ComplexSelector complex, bool forceDeep, int depth)
    {
        foreach (var c in complex.Combinators)
            if (c is Combinator.NextSibling or Combinator.SubsequentSibling) HasSiblingCombinator = true;
        var compounds = complex.Compounds;
        for (int i = 0; i < compounds.Count; i++)
        {
            var compound = compounds[i];
            // x::part(name) 中 x 匹配的是部件的宿主（祖先），所以整段按非主体处理
            bool deep = forceDeep || i < compounds.Count - 1 || compound.PseudoElement?.PseudoElement == PseudoElementKind.Part;
            foreach (var simple in compound.Selectors) Add(simple, deep, depth);
            foreach (var simple in compound.PseudoElementStates) Add(simple, deep, depth);
        }
    }

    void Add(SimpleSelector simple, bool deep, int depth)
    {
        switch (simple)
        {
            case ClassSelector c:
                if (deep) _deepClasses.Add(c.Name);
                break;
            case IdSelector id:
                if (deep) _deepIds.Add(id.Id);
                break;
            case AttributeSelector a:
                if (deep) _deepAttributes.Add(a.Name.ToLowerInvariant());
                break;
            case PseudoClassSelector pc:
                var state = MapState(pc.PseudoClass);
                if (state != PseudoStates.None)
                {
                    if (deep) DeepStates |= state;
                }
                else if (pc.PseudoClass is not (PseudoClass.Root or PseudoClass.Scope))
                {
                    HasStructuralPseudo = true;
                    if (deep) HasDeepStructuralPseudo = true;
                    if (pc.PseudoClass == PseudoClass.Empty) UsesEmpty = true;
                }
                break;
            case NthSelector nth:
                HasStructuralPseudo = true;
                if (deep) HasDeepStructuralPseudo = true;
                if (nth.Of is { } of) CollectList(of, deep, depth + 1);
                break;
            case LogicalSelector logical:
                CollectList(logical.Arguments, deep, depth + 1);
                break;
            case NestingSelector nesting:
                // & 代表父规则的选择器：它整体位于嵌套选择器的祖先/复合位置，保守地全部按非主体处理
                if (nesting.Parent is { } parent) CollectList(parent, true, depth + 1);
                break;
        }
    }

    internal static PseudoStates MapState(PseudoClass pc) => pc switch
    {
        PseudoClass.Hover => PseudoStates.Hover,
        PseudoClass.Active => PseudoStates.Active,
        PseudoClass.Focus => PseudoStates.Focus,
        PseudoClass.FocusVisible => PseudoStates.FocusVisible,
        PseudoClass.FocusWithin => PseudoStates.FocusWithin,
        PseudoClass.Disabled or PseudoClass.Enabled => PseudoStates.Disabled,
        PseudoClass.Checked => PseudoStates.Checked,
        PseudoClass.PlaceholderShown => PseudoStates.PlaceholderShown,
        _ => PseudoStates.None,
    };
}
