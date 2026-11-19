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

    /// <summary>全局出现顺序（跨样式表）。</summary>
    public int Order;
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
