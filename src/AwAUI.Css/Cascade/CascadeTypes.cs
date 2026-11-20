using System;
using System.Collections.Generic;
using AwAUI.Css.Media;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.Cascade;

/// <summary>
/// 样式来源（CSS Cascade 5 的 origin）。内联样式属于 <see cref="Author"/>（以「元素附着」的方式参与排序）；
/// 过渡与动画是级联中的独立层级（见 <see cref="StyleEngine"/>，第 1 期为空槽）。
/// </summary>
public enum CascadeOrigin : byte
{
    /// <summary>默认样式表（相当于浏览器 UA 样式表）。</summary>
    UserAgent,
    /// <summary>应用样式表（全局、组件作用域与运行时样式表）及内联样式。</summary>
    Author,
    /// <summary>DevTools 覆盖（design/08：优先级最高）。</summary>
    DevTools,
}

/// <summary>注册到 <see cref="StyleEngine"/> 的样式表及其来源与值层诊断。</summary>
public sealed class CascadeStyleSheet
{
    internal CascadeStyleSheet(StyleSheet sheet, CascadeOrigin origin)
    {
        Sheet = sheet;
        Origin = origin;
    }

    public StyleSheet Sheet { get; }

    public CascadeOrigin Origin { get; }

    /// <summary>值层诊断（AW2002 不支持的属性、AW2003 无效值），在首次编译时产生。语法层诊断见 <see cref="StyleSheet.Diagnostics"/>。</summary>
    public IReadOnlyList<CssDiagnostic> Diagnostics => _diagnostics;

    internal readonly List<CssDiagnostic> _diagnostics = new();

    /// <summary>每条规则的声明只解析一次（按规则对象缓存）。</summary>
    internal readonly Dictionary<CssRule, PropertyDeclarationBlock> Blocks = new();

    internal bool Parsed;

    public override string ToString() => $"{Origin}: {Sheet.SourceName ?? "(inline)"}";
}

/// <summary>级联层树中的一个节点。根节点代表「未分层」。</summary>
internal sealed class LayerNode
{
    public LayerNode(string? segment, LayerNode? parent)
    {
        Segment = segment;
        Parent = parent;
    }

    /// <summary>
    /// 本级名称（点分名称中的一段）；匿名层与根为 null。完整名称按需由 <see cref="DisplayName"/> 拼出：
    /// 若在创建时就拼接完整名称，<c>@layer a.a.a…</c> 这样的长名称会导致平方级的字符串分配。
    /// </summary>
    public string? Segment { get; }

    public LayerNode? Parent { get; }

    public readonly List<LayerNode> Children = new();

    public Dictionary<string, LayerNode>? Named;

    /// <summary>后序编号：子层在父层的直接规则之前；根（未分层）最大。</summary>
    public int Rank;

    public bool IsRoot => Parent is null;

    public LayerNode GetOrCreate(string dottedName)
    {
        var node = this;
        int start = 0;
        while (start <= dottedName.Length)
        {
            int dot = dottedName.IndexOf('.', start);
            if (dot < 0) dot = dottedName.Length;
            var part = dottedName.Substring(start, dot - start);
            node = node.Child(part);
            start = dot + 1;
        }
        return node;
    }

    LayerNode Child(string part)
    {
        Named ??= new Dictionary<string, LayerNode>(StringComparer.Ordinal);
        if (!Named.TryGetValue(part, out var child))
        {
            child = new LayerNode(part, this);
            Named.Add(part, child);
            Children.Add(child);
        }
        return child;
    }

    public LayerNode CreateAnonymous()
    {
        var child = new LayerNode(null, this) { IsAnonymous = true };
        Children.Add(child);
        return child;
    }

    public bool IsAnonymous { get; private set; }

    /// <summary>
    /// 不挂入层树的节点：条件不成立的 @media 中的层块。其规则不会命中，节点只为让嵌套规则有所归属，不影响其他层的排序。
    /// </summary>
    public LayerNode CreateDetached(string? name) => new(name, this) { IsAnonymous = name is null };

    string? _displayName;

    /// <summary>DevTools 显示用的完整点分名称：匿名层的一段显示为 <c>&lt;anonymous&gt;</c>；根为空串。只在记录模式下使用，结果缓存。</summary>
    public string DisplayName
    {
        get
        {
            if (_displayName is not null) return _displayName;
            if (IsRoot) return _displayName = string.Empty;
            // 迭代拼接（层的嵌套深度可以很大，不用递归）
            var parts = new List<string>();
            for (var n = this; n is not null && !n.IsRoot; n = n.Parent) parts.Add(n.Segment ?? "<anonymous>");
            parts.Reverse();
            return _displayName = string.Join(".", parts);
        }
    }

    /// <summary>为整棵树计算后序编号。</summary>
    public void AssignRanks()
    {
        int next = 0;
        // 迭代后序遍历（层嵌套深度受解析器的规则深度限制，但仍避免递归）
        var stack = new Stack<(LayerNode Node, int Child)>();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            var (node, i) = stack.Pop();
            if (i < node.Children.Count)
            {
                stack.Push((node, i + 1));
                stack.Push((node.Children[i], 0));
            }
            else node.Rank = next++;
        }
    }
}

/// <summary>@media 条件（可嵌套），按环境代数缓存求值结果。</summary>
internal sealed class MediaGate
{
    public MediaGate(MediaQueryList media, MediaGate? parent)
    {
        Media = media;
        Parent = parent;
    }

    public MediaQueryList Media { get; }

    public MediaGate? Parent { get; }

    /// <summary>条件内含 @layer 规则：条件结果变化时层的排序随之变化，需要重新编译。</summary>
    public bool AffectsLayerOrder;

    int _generation = -1;
    bool _value;

    public bool Value => _value;

    public bool Evaluate(int generation, MediaEnvironment environment)
    {
        if (_generation == generation) return _value;
        _value = (Parent is null || Parent.Evaluate(generation, environment)) && Media.Matches(environment);
        _generation = generation;
        return _value;
    }

    public void CollectChain(List<MediaQueryList> into)
    {
        Parent?.CollectChain(into);
        into.Add(Media);
    }
}

/// <summary>编译后的规则：样式规则或嵌套声明规则 + 其级联上下文。</summary>
internal sealed class CompiledRule
{
    public CompiledRule(CssRule source, SelectorList selectors, PropertyDeclarationBlock block, CascadeStyleSheet sheet, LayerNode layer, MediaGate? media)
    {
        Source = source;
        Selectors = selectors;
        Block = block;
        Sheet = sheet;
        Layer = layer;
        Media = media;
    }

    public CssRule Source { get; }
    public SelectorList Selectors { get; }
    public PropertyDeclarationBlock Block { get; }
    public CascadeStyleSheet Sheet { get; }
    public LayerNode Layer { get; }
    public MediaGate? Media { get; }
    public CascadeOrigin Origin => Sheet.Origin;

    /// <summary>所在的 <c>@container</c> 条件链；没有为 null。</summary>
    public ContainerGate? Container;

    /// <summary>全局出现顺序（跨样式表）。</summary>
    public int Order;
}

/// <summary><c>@container</c> 条件（可嵌套），按元素求值。</summary>
internal sealed class ContainerGate
{
    public ContainerGate(CssContainerRule rule, ContainerGate? parent)
    {
        Rule = rule;
        Parent = parent;
        Axes = rule.Condition.RequiredAxes;
    }

    public CssContainerRule Rule { get; }

    public ContainerGate? Parent { get; }

    public ContainerAxes Axes { get; }

    /// <summary>
    /// 找到元素的查询容器并求值条件。查询容器：最近的祖先（伪元素从原始元素开始）中 <c>container-type</c> 支持所需轴、且
    /// <c>container-name</c> 含所需名称的那个。找不到、或容器尚未布局（尺寸未知）时条件不成立。
    /// </summary>
    public bool Evaluate(StyleElement element, bool includeSelf, CssEnvironment environment)
    {
        bool needWidth = (Axes & ContainerAxes.Inline) != 0, needHeight = (Axes & ContainerAxes.Block) != 0;
        if (!TryFindContainer(element, includeSelf, Rule.Name, needWidth, needHeight, out var style, out var w, out var h)) return false;
        var input = new ContainerQueryInput(w, h, style!.FontSize, style.RootFontSize, environment.Media);
        return Rule.Condition.EvaluateContainer(input) == MediaTri.True;
    }

    /// <summary>
    /// 查找查询容器（深度至多 4096 层）。返回 false 表示没有合适的容器，或容器尚未布局。只支持行内轴的容器（inline-size）的高度为 NaN。
    /// </summary>
    public static bool TryFindContainer(StyleElement? element, bool includeSelf, string? name, bool needWidth, bool needHeight,
        out ComputedStyle? style, out double width, out double height)
    {
        style = null;
        width = height = double.NaN;
        int guard = 0;
        for (var e = includeSelf ? element : element?.Parent; e is not null && guard++ < 4096; e = e.Parent)
        {
            var s = e.CurrentStyle;
            if (s is null) continue;
            var type = s.ContainerType;
            if (type == CssContainerType.Normal) continue;
            bool hasHeight = type == CssContainerType.Size;
            if (needHeight && !hasHeight) continue;
            if (name is not null && !Contains(s.ContainerName, name)) continue;
            if (!e.TryGetContainerSize(out var w, out var h)) return false;
            style = s;
            width = w;
            height = hasHeight ? h : double.NaN;
            return true;
        }
        return false;

        static bool Contains(CssList<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}

/// <summary>
/// <c>:has()</c> 的失效特征集：从全部样式表的 <c>:has()</c> 参数中收集的 class、id、属性、伪状态与结构依赖。
/// <para>
/// 宿主（UI 层）的用法：某元素 E 的这些输入变化（或 E 被插入、移除）时，<c>:has()</c> 的结果可能改变的锚点是 E 的祖先
/// （<see cref="HasSiblingRelative"/> 时还有 E 及其祖先的前面的兄弟）。这些锚点要重算样式；若 <see cref="HasNonSubjectHas"/>，
/// 锚点的子树也要重算（如 <c>.group:has(:focus) .item</c>）。只需检查带 <see cref="ComputedStyleFlags.DependsOnHas"/> 的锚点。
/// 没有 <c>:has()</c> 时（<see cref="UsesHas"/> 为假）无需任何额外工作。
/// </para>
/// </summary>
public sealed class HasInvalidationSet
{
    public static readonly HasInvalidationSet Empty = new(false, false, false, false, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), PseudoStates.None, false);

    readonly HashSet<string> _classes, _ids, _attributes;

    HasInvalidationSet(bool uses, bool sibling, bool nonSubject, bool structural, HashSet<string> classes, HashSet<string> ids, HashSet<string> attributes,
        PseudoStates states, bool hookedStates)
    {
        UsesHas = uses;
        HasSiblingRelative = sibling;
        HasNonSubjectHas = nonSubject;
        UsesStructural = structural;
        _classes = classes;
        _ids = ids;
        _attributes = attributes;
        States = states;
        UsesStatePseudoClasses = hookedStates;
    }

    /// <summary>样式表中有 <c>:has()</c>。</summary>
    public bool UsesHas { get; }

    /// <summary>有以 <c>+</c> / <c>~</c> 开头的相对选择器：变化也影响前面的兄弟。</summary>
    public bool HasSiblingRelative { get; }

    /// <summary><c>:has()</c> 出现在非主体位置（或 <c>&amp;</c> 引用的父选择器中）：锚点的后代也依赖它的结果。</summary>
    public bool HasNonSubjectHas { get; }

    /// <summary><c>:has()</c> 的参数中有结构伪类（<c>:first-child :nth-*() :empty</c> 等）。</summary>
    public bool UsesStructural { get; }

    /// <summary><c>:has()</c> 参数中出现的伪状态（悬停、焦点…）。</summary>
    public PseudoStates States { get; }

    /// <summary><c>:has()</c> 参数中有表单 / 文档状态伪类（<c>:invalid</c> 等）。</summary>
    public bool UsesStatePseudoClasses { get; }

    public bool IsRelevantClass(string name) => _classes.Contains(name);

    public bool IsRelevantId(string? id) => id is not null && _ids.Contains(id);

    /// <summary>属性名按 ASCII 小写比较。</summary>
    public bool IsRelevantAttribute(string name) => _attributes.Contains(Syntax.CssSerializer.ToAsciiLower(name));

    internal sealed class Builder
    {
        bool _uses, _sibling, _nonSubject, _structural, _hooked;
        PseudoStates _states;
        readonly HashSet<string> _classes = new(StringComparer.Ordinal), _ids = new(StringComparer.Ordinal), _attributes = new(StringComparer.Ordinal);

        public void Add(SelectorList list)
        {
            foreach (var s in list.Selectors)
                if (s.ContainsHas) Walk(s, nonSubject: false, inHas: false, 0);
        }

        void Walk(ComplexSelector s, bool nonSubject, bool inHas, int depth)
        {
            if (depth > 64) { _nonSubject = _structural = true; return; }
            var compounds = s.Compounds;
            for (int i = 0; i < compounds.Count; i++)
            {
                bool ns = nonSubject || i < compounds.Count - 1;
                foreach (var x in compounds[i].Selectors) Simple(x, ns, inHas, depth);
                foreach (var x in compounds[i].PseudoElementStates) Simple(x, ns, inHas, depth);
            }
        }

        void Simple(SimpleSelector x, bool nonSubject, bool inHas, int depth)
        {
            switch (x)
            {
                case HasSelector h:
                    _uses = true;
                    if (nonSubject) _nonSubject = true;
                    foreach (var rel in h.Arguments.Selectors)
                    {
                        if (rel.Combinators.Count > 0 && rel.Combinators[0] is Combinator.NextSibling or Combinator.SubsequentSibling) _sibling = true;
                        Walk(rel, nonSubject, inHas: true, depth + 1);
                    }
                    break;
                case LogicalSelector l:
                    foreach (var c in l.Arguments.Selectors) Walk(c, nonSubject, inHas, depth + 1);
                    break;
                case NthSelector n:
                    if (inHas) _structural = true;
                    if (n.Of is { } of) foreach (var c in of.Selectors) Walk(c, nonSubject, inHas, depth + 1);
                    break;
                case NestingSelector { Parent: { } p }:
                    foreach (var c in p.Selectors) Walk(c, true, inHas, depth + 1);
                    break;
                case ClassSelector c when inHas: _classes.Add(c.Name); break;
                case IdSelector id when inHas: _ids.Add(id.Id); break;
                case AttributeSelector a when inHas: _attributes.Add(Syntax.CssSerializer.ToAsciiLower(a.Name)); break;
                case PseudoClassSelector pc when inHas:
                    switch (pc.PseudoClass)
                    {
                        case PseudoClass.Hover: _states |= PseudoStates.Hover; break;
                        case PseudoClass.Active: _states |= PseudoStates.Active; break;
                        case PseudoClass.Focus: _states |= PseudoStates.Focus; break;
                        case PseudoClass.FocusVisible: _states |= PseudoStates.FocusVisible; break;
                        case PseudoClass.FocusWithin: _states |= PseudoStates.FocusWithin; break;
                        case PseudoClass.Disabled:
                        case PseudoClass.Enabled: _states |= PseudoStates.Disabled; break;
                        case PseudoClass.Checked: _states |= PseudoStates.Checked; break;
                        case PseudoClass.PlaceholderShown: _states |= PseudoStates.PlaceholderShown; break;
                        case PseudoClass.Root:
                        case PseudoClass.Scope:
                        case PseudoClass.Host: break;
                        default:
                            if (pc.IsHookedState) _hooked = true;
                            else _structural = true;
                            break;
                    }
                    break;
            }
        }

        public HasInvalidationSet Build()
            => !_uses ? Empty : new HasInvalidationSet(true, _sibling, _nonSubject, _structural, _classes, _ids, _attributes, _states, _hooked);
    }
}

/// <summary>一条命中的规则（或内联样式），DevTools 样式面板的数据源（design/20 的 RuleMatch）。</summary>
public sealed class MatchedRule
{
    internal MatchedRule(CssRule? rule, string selectorText, ComplexSelector? matchedSelector, Specificity specificity,
        CascadeOrigin origin, bool isInline, string? layerName, IReadOnlyList<MediaQueryList> media, CascadeStyleSheet? sheet, int order)
    {
        Rule = rule;
        SelectorText = selectorText;
        MatchedSelector = matchedSelector;
        Specificity = specificity;
        Origin = origin;
        IsInline = isInline;
        LayerName = layerName;
        Media = media;
        StyleSheet = sheet;
        SourceOrder = order;
    }

    /// <summary>样式规则或嵌套声明规则；内联样式为 null。</summary>
    public CssRule? Rule { get; }

    /// <summary>规则的选择器文本（嵌套声明规则取外层样式规则的选择器）；内联样式为空串。</summary>
    public string SelectorText { get; }

    /// <summary>命中的（特异性最高的）那个选择器。</summary>
    public ComplexSelector? MatchedSelector { get; }

    public Specificity Specificity { get; }

    public CascadeOrigin Origin { get; }

    public bool IsInline { get; }

    /// <summary>所在级联层的点分名称；未分层为 null。</summary>
    public string? LayerName { get; }

    /// <summary>外层到内层的 @media 条件。</summary>
    public IReadOnlyList<MediaQueryList> Media { get; }

    public CascadeStyleSheet? StyleSheet { get; }

    /// <summary>全局出现顺序。</summary>
    public int SourceOrder { get; }

    /// <summary>规则在源文本中的区间；内联样式为默认值。</summary>
    public SourceRange SourceRange => Rule?.Range ?? default;

    public override string ToString() => IsInline ? "style attribute" : SelectorText;
}

/// <summary>一条声明（简写展开后的一个长属性）在级联中的结果。</summary>
public sealed class DeclarationMatch
{
    internal DeclarationMatch(MatchedRule rule, CssDeclaration declaration, string propertyName, CssPropertyId? propertyId,
        DeclarationStatus status, string? reason)
    {
        Rule = rule;
        Declaration = declaration;
        PropertyName = propertyName;
        PropertyId = propertyId;
        Status = status;
        Reason = reason;
    }

    public MatchedRule Rule { get; }

    /// <summary>源声明（简写展开出的多个长属性共享同一条）。</summary>
    public CssDeclaration Declaration { get; }

    /// <summary>长属性名或自定义属性名。</summary>
    public string PropertyName { get; }

    /// <summary>长属性 ID；自定义属性与不支持的属性为 null。</summary>
    public CssPropertyId? PropertyId { get; }

    public bool Important => Declaration.Important;

    public DeclarationStatus Status { get; internal set; }

    /// <summary>无效或不生效的原因。</summary>
    public string? Reason { get; internal set; }

    public override string ToString() => $"{PropertyName} [{Status}] from {Rule}";
}

/// <summary>
/// 一个元素的匹配记录：命中的规则（按级联优先级从高到低）与每条声明的结果（胜出 / 被覆盖 / 无效 / 不支持）。
/// 只在调用方传入时收集，不影响常规解析路径的性能。可复用：每次解析前自动清空。
/// </summary>
public sealed class StyleMatchRecord
{
    internal readonly List<MatchedRule> Rules = new();
    internal readonly List<DeclarationMatch> Decls = new();

    /// <summary>命中的规则（含内联样式），按级联优先级从高到低。</summary>
    public IReadOnlyList<MatchedRule> MatchedRules => Rules;

    /// <summary>全部声明的结果，按级联优先级从高到低。</summary>
    public IReadOnlyList<DeclarationMatch> Declarations => Decls;

    /// <summary>某个长属性的胜出声明；没有则为 null（取继承值或初始值）。</summary>
    public DeclarationMatch? GetWinner(CssPropertyId id)
    {
        foreach (var d in Decls)
            if (d.PropertyId == id && d.Status == DeclarationStatus.Active) return d;
        return null;
    }

    /// <summary>某个自定义属性的胜出声明。</summary>
    public DeclarationMatch? GetWinner(string customPropertyName)
    {
        foreach (var d in Decls)
            if (d.PropertyId is null && d.PropertyName == customPropertyName && d.Status == DeclarationStatus.Active) return d;
        return null;
    }

    internal void Clear()
    {
        Rules.Clear();
        Decls.Clear();
    }
}
