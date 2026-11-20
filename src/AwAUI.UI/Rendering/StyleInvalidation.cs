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

    /// <summary>样式表用到了表单 / 文档状态伪类（<c>:required :invalid :read-write :open</c> ...，见 <see cref="StatePseudoClasses"/>）。</summary>
    public bool UsesStatePseudoClasses { get; private set; }

    /// <summary>状态伪类出现在非主体位置：相关属性变化时连带子树（或兄弟）。</summary>
    public bool HasDeepStatePseudoClasses { get; private set; }

    /// <summary>样式表用到了 <c>:dir()</c> / <c>:lang()</c>：<c>dir</c> / <c>lang</c> 属性变化影响整棵子树。</summary>
    public bool UsesDirOrLang { get; private set; }

    /// <summary><c>:has()</c> 的失效特征（来自 <see cref="StyleEngine.HasInvalidation"/>）。</summary>
    public HasInvalidationSet Has { get; private set; } = HasInvalidationSet.Empty;

    /// <summary>
    /// 带 <c>content</c> 声明的 <c>::before / ::after</c> 规则的主体键（见 <see cref="MayGenerateContent"/>）：
    /// 只有可能匹配这些规则的元素才需要解析伪元素样式。
    /// </summary>
    readonly HashSet<string> _generatedClasses = new(StringComparer.Ordinal);
    readonly HashSet<string> _generatedIds = new(StringComparer.Ordinal);
    readonly HashSet<string> _generatedTags = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _generatedAttributes = new(StringComparer.OrdinalIgnoreCase);
    bool _generatedUniversal;

    /// <summary>样式表中有可能生成 <c>::before / ::after</c> 盒的规则。</summary>
    public bool UsesGeneratedContent { get; private set; }

    /// <summary>样式表中有 <c>::placeholder</c> 规则（输入框的占位文本按它着色）。</summary>
    public bool UsesPlaceholder { get; private set; }

    /// <summary>样式表中有 <c>::selection</c> 规则（输入框的选区高亮按它着色）。</summary>
    public bool UsesSelection { get; private set; }

    public bool IsDeepClass(string name) => _deepClasses.Contains(name);

    public bool IsDeepId(string? id) => id is not null && _deepIds.Contains(id);

    public bool IsDeepAttribute(string name) => _deepAttributes.Contains(name.ToLowerInvariant());

    public void Rebuild(StyleEngine engine)
    {
        _deepClasses.Clear();
        _deepIds.Clear();
        _deepAttributes.Clear();
        _generatedClasses.Clear();
        _generatedIds.Clear();
        _generatedTags.Clear();
        _generatedAttributes.Clear();
        _generatedUniversal = false;
        DeepStates = PseudoStates.None;
        HasSiblingCombinator = HasStructuralPseudo = HasDeepStructuralPseudo = UsesEmpty = false;
        UsesStatePseudoClasses = HasDeepStatePseudoClasses = UsesDirOrLang = UsesGeneratedContent = UsesPlaceholder = UsesSelection = false;
        Has = engine.HasInvalidation;
        foreach (var cs in engine.StyleSheets)
        {
            foreach (var rule in cs.Sheet.EnumerateRules())
            {
                if (rule is CssStyleRule style && style.Selectors is { } list)
                {
                    CollectList(list, forceDeep: false, depth: 0);
                    CollectPseudoElements(list, DeclaresContent(style));
                }
            }
        }
    }

    static bool DeclaresContent(CssStyleRule rule)
    {
        foreach (var d in rule.Declarations)
            if (string.Equals(d.Name, "content", StringComparison.Ordinal) || string.Equals(d.Name, "all", StringComparison.Ordinal)) return true;
        foreach (var c in rule.ChildRules)
            if (c is CssNestedDeclarationsRule nested)
                foreach (var d in nested.Declarations)
                    if (string.Equals(d.Name, "content", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 记录主体带伪元素的规则：<c>::placeholder</c> / <c>::selection</c> 只记是否用到；带 <c>content</c> 的 <c>::before / ::after</c>
    /// 记录主体复合选择器中最有选择性的键（id &gt; class &gt; 属性 &gt; 类型），没有键时按通配。
    /// </summary>
    void CollectPseudoElements(SelectorList list, bool declaresContent)
    {
        foreach (var complex in list.Selectors)
        {
            var compounds = complex.Compounds;
            if (compounds.Count == 0) continue;
            var subject = compounds[compounds.Count - 1];
            var kind = subject.PseudoElement?.PseudoElement ?? PseudoElementKind.None;
            if (kind == PseudoElementKind.Placeholder) UsesPlaceholder = true;
            else if (kind == PseudoElementKind.Selection) UsesSelection = true;
            if (!declaresContent || kind is not (PseudoElementKind.Before or PseudoElementKind.After)) continue;
            UsesGeneratedContent = true;
            string? id = null, cls = null, attr = null, tag = null;
            foreach (var simple in subject.Selectors)
            {
                switch (simple)
                {
                    case IdSelector i: id ??= i.Id; break;
                    case ClassSelector c: cls ??= c.Name; break;
                    case AttributeSelector a: attr ??= a.Name; break;
                    case TypeSelector t: tag ??= t.Name; break;
                }
            }
            if (id is not null) _generatedIds.Add(id);
            else if (cls is not null) _generatedClasses.Add(cls);
            else if (attr is not null) _generatedAttributes.Add(attr);
            else if (tag is not null) _generatedTags.Add(tag);
            else _generatedUniversal = true;
        }
    }

    /// <summary>
    /// 元素是否可能匹配某条带 <c>content</c> 的 <c>::before / ::after</c> 规则（保守：为 true 时还要解析伪元素样式才能确定）。
    /// <c>content</c> 不继承、初始值 normal（不生成盒），所以没有这样的规则匹配时伪元素一定不存在。
    /// </summary>
    public bool MayGenerateContent(Element e)
    {
        if (!UsesGeneratedContent || e.IsTextNode) return false;
        if (_generatedUniversal) return true;
        if (e.Id is { } id && _generatedIds.Contains(id)) return true;
        if (_generatedClasses.Count > 0)
        {
            var classes = e.Classes;
            for (int i = 0; i < classes.Count; i++)
                if (_generatedClasses.Contains(classes[i])) return true;
        }
        if (_generatedTags.Count > 0 && _generatedTags.Contains(e.Tag)) return true;
        if (_generatedAttributes.Count > 0)
            foreach (var name in _generatedAttributes)
                if (e.GetAttribute(name) is not null) return true;
        return false;
    }

    /// <summary>与状态伪类有关的属性：变化时状态伪类的结果可能改变。</summary>
    public static bool IsStateAttribute(string name) => name.ToLowerInvariant() switch
    {
        "required" or "readonly" or "disabled" or "open" or "href" or "checked" or "selected" or "indeterminate" or "aria-checked"
            or "contenteditable" or "value" => true,
        _ => false,
    };

    // ------------------------------------------------------------ :has()

    /// <summary>
    /// <c>:has()</c> 的失效：<paramref name="changed"/> 的选择器输入变化（或它被插入）时，可能把它当作 <c>:has()</c> 参数匹配的锚点
    /// 是它的祖先（后代形式），以及它和各祖先之前的兄弟（<c>+</c> / <c>~</c> 形式）。只重算其中求值过 <c>:has()</c>
    /// 的元素（<see cref="ComputedStyleFlags.DependsOnHas"/>）；<c>:has()</c> 位于非主体位置时连带锚点的子树。
    /// <paramref name="inclusive"/> 时从 <paramref name="changed"/> 自身开始（子节点被移除时传父元素：被移除者已不在树中），
    /// 此时它的全部子元素都按「之前的兄弟」处理。
    /// </summary>
    public void InvalidateHas(Element changed, bool inclusive)
    {
        var set = Has;
        if (!set.UsesHas) return;
        bool siblings = set.HasSiblingRelative;
        if (inclusive && siblings)
        {
            var ch = changed.Children;
            for (int i = 0; i < ch.Count; i++) MarkHasAnchor(ch[i], set);
        }
        Element? child = inclusive ? null : changed;
        for (var a = inclusive ? changed : changed.Parent; a is not null; child = a, a = a.Parent)
        {
            MarkHasAnchor(a, set);
            if (siblings && child is not null)
                for (var s = child.PreviousElementSibling; s is not null; s = s.PreviousElementSibling) MarkHasAnchor(s, set);
        }
    }

    static void MarkHasAnchor(Element e, HasInvalidationSet set)
    {
        if (e.IsTextNode || e.Render?.Style is not { } style || (style.Flags & ComputedStyleFlags.DependsOnHas) == 0) return;
        e.MarkDirty(ElementDirtyFlags.Style);
        if (set.HasNonSubjectHas) RenderBox.Get(e).RestyleSubtree = true;
    }

    /// <summary>class 变化是否与 <c>:has()</c> 的参数有关。</summary>
    public bool HasRelevantClassChange(IReadOnlyList<string>? old, IReadOnlyList<string> now)
    {
        var set = Has;
        if (!set.UsesHas) return false;
        if (old is not null)
            for (int i = 0; i < old.Count; i++)
                if (set.IsRelevantClass(old[i])) return true;
        for (int i = 0; i < now.Count; i++)
            if (set.IsRelevantClass(now[i])) return true;
        return false;
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
                else if (pc.IsHookedState)
                {
                    // 表单 / 文档状态（:required :invalid :open ...）：由元素属性与值决定，不是结构伪类
                    UsesStatePseudoClasses = true;
                    if (deep) HasDeepStatePseudoClasses = true;
                }
                else if (pc.PseudoClass is not (PseudoClass.Root or PseudoClass.Scope or PseudoClass.Host))
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
            case DirSelector or LangSelector:
                UsesDirOrLang = true;
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
