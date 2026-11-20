using System;
using System.Collections.Generic;
using AwAUI.Css.Media;
using AwAUI.Css.Selectors;
using AwAUI.Css.Syntax;

namespace AwAUI.Css;

/// <summary>规则种类。</summary>
public enum CssRuleKind
{
    Style,
    NestedDeclarations,
    Media,
    LayerBlock,
    LayerStatement,
    Keyframes,
    Keyframe,
    FontFace,
    Import,
    /// <summary>未知或暂不支持的 at 规则（如 <c>@scope</c>），内容按原样保留但不生效。</summary>
    UnknownAtRule,
    /// <summary><c>@supports</c>。</summary>
    Supports,
    /// <summary><c>@container</c>。</summary>
    Container,
    /// <summary><c>@starting-style</c>。</summary>
    StartingStyle,
    /// <summary><c>@property</c>。</summary>
    Property,
}

/// <summary>规则基类。规则树是不可变的解析结果。</summary>
public abstract class CssRule
{
    internal static readonly IReadOnlyList<CssRule> NoRules = Array.Empty<CssRule>();
    internal static readonly IReadOnlyList<CssDeclaration> NoDeclarations = Array.Empty<CssDeclaration>();

    private protected CssRule(SourceRange range) => Range = range;

    public abstract CssRuleKind Kind { get; }

    /// <summary>整条规则（含块）在原始文本中的区间。</summary>
    public SourceRange Range { get; internal set; }

    /// <summary>包含本规则的规则；顶层规则为 null。</summary>
    public CssRule? Parent { get; internal set; }

    /// <summary>子规则（嵌套规则、分组规则内的规则、关键帧）。</summary>
    public virtual IReadOnlyList<CssRule> ChildRules => NoRules;
}

/// <summary>一条声明，如 <c>color: red !important</c>。值以组件值形式保留，值解析属于后续的值层。</summary>
public sealed class CssDeclaration
{
    internal CssDeclaration(string name, IReadOnlyList<CssComponentValue> value, bool important, string valueText,
        SourceRange range, SourceRange nameRange, SourceRange valueRange)
    {
        Name = name;
        Value = value;
        Important = important;
        ValueText = valueText;
        Range = range;
        NameRange = nameRange;
        ValueRange = valueRange;
    }

    /// <summary>属性名。普通属性为 ASCII 小写；自定义属性（<c>--*</c>）保留原样（大小写敏感）。</summary>
    public string Name { get; }

    /// <summary>是否为自定义属性。</summary>
    public bool IsCustomProperty => Name.Length >= 2 && Name[0] == '-' && Name[1] == '-';

    /// <summary>值的组件值（不含 <c>!important</c>，首尾空白已去除）。</summary>
    public IReadOnlyList<CssComponentValue> Value { get; }

    public bool Important { get; }

    /// <summary>值在原始文本中的原样文本（不含 <c>!important</c>）。DevTools 展示用。</summary>
    public string ValueText { get; }

    /// <summary>从属性名到值（含 <c>!important</c>）末尾的区间，不含分号。</summary>
    public SourceRange Range { get; }

    public SourceRange NameRange { get; }

    public SourceRange ValueRange { get; }

    public override string ToString() => Important ? $"{Name}: {ValueText} !important" : $"{Name}: {ValueText}";
}

/// <summary>样式规则：选择器 + 声明，可含嵌套规则（CSS Nesting）。</summary>
public sealed class CssStyleRule : CssRule
{
    internal List<CssDeclaration> DeclarationList = new();
    internal List<CssRule> ChildList = new();

    internal CssStyleRule(IReadOnlyList<CssComponentValue> prelude, string selectorText, SelectorList selectors,
        CssStyleRule? parentStyleRule, SourceRange preludeRange, SourceRange range) : base(range)
    {
        Prelude = prelude;
        SelectorText = selectorText;
        Selectors = selectors;
        ParentStyleRule = parentStyleRule;
        PreludeRange = preludeRange;
    }

    public override CssRuleKind Kind => CssRuleKind.Style;

    /// <summary>序言的组件值（原样）。</summary>
    public IReadOnlyList<CssComponentValue> Prelude { get; }

    /// <summary>选择器的原始文本（首尾空白已去除）。</summary>
    public string SelectorText { get; }

    /// <summary>
    /// 解析后的选择器列表。嵌套规则中的 <c>&amp;</c> 已绑定到父规则的选择器，
    /// 不含 <c>&amp;</c> 的相对选择器已补上隐式的 <c>&amp;</c>，可直接用于匹配。
    /// </summary>
    public SelectorList Selectors { get; }

    /// <summary>最近的外层样式规则（嵌套时）。</summary>
    public CssStyleRule? ParentStyleRule { get; }

    public SourceRange PreludeRange { get; }

    /// <summary>出现在任何嵌套规则之前的声明。</summary>
    public IReadOnlyList<CssDeclaration> Declarations => DeclarationList;

    /// <summary>嵌套的规则；嵌套规则之后出现的声明以 <see cref="CssNestedDeclarationsRule"/> 表示。</summary>
    public override IReadOnlyList<CssRule> ChildRules => ChildList;
}

/// <summary>
/// 嵌套声明规则（CSS Nesting 的 CSSNestedDeclarations）：出现在嵌套规则之后、或嵌套分组规则（如 <c>@media</c>）中的声明，
/// 按外层样式规则的选择器匹配。
/// </summary>
public sealed class CssNestedDeclarationsRule : CssRule
{
    internal CssNestedDeclarationsRule(IReadOnlyList<CssDeclaration> declarations, CssStyleRule parentStyleRule, SourceRange range)
        : base(range)
    {
        Declarations = declarations;
        ParentStyleRule = parentStyleRule;
    }

    public override CssRuleKind Kind => CssRuleKind.NestedDeclarations;

    public IReadOnlyList<CssDeclaration> Declarations { get; }

    /// <summary>决定匹配与特异性的外层样式规则。</summary>
    public CssStyleRule ParentStyleRule { get; }

    /// <summary>用于匹配的选择器（即外层样式规则的选择器）。</summary>
    public SelectorList Selectors => ParentStyleRule.Selectors;
}

/// <summary><c>@media</c> 规则。</summary>
public sealed class CssMediaRule : CssRule
{
    internal CssMediaRule(IReadOnlyList<CssComponentValue> prelude, MediaQueryList media, IReadOnlyList<CssRule> rules, SourceRange range)
        : base(range)
    {
        Prelude = prelude;
        Media = media;
        Rules = rules;
    }

    public override CssRuleKind Kind => CssRuleKind.Media;

    /// <summary>序言的组件值（原样）。</summary>
    public IReadOnlyList<CssComponentValue> Prelude { get; }

    /// <summary>解析后的媒体查询列表。</summary>
    public MediaQueryList Media { get; }

    public IReadOnlyList<CssRule> Rules { get; }

    public override IReadOnlyList<CssRule> ChildRules => Rules;
}

/// <summary>
/// <c>@supports</c> 规则。条件按本引擎的实际支持情况在解析时求值（CSS Conditional 3/4）：声明测试 <c>(prop: value)</c> 检查属性
/// 是否受支持且值能被解析，<c>selector()</c> 检查选择器能否解析，<c>font-tech()</c>、<c>font-format()</c> 与无法识别的条件不成立。
/// </summary>
public sealed class CssSupportsRule : CssRule
{
    internal CssSupportsRule(IReadOnlyList<CssComponentValue> prelude, string conditionText, bool matches, IReadOnlyList<CssRule> rules, SourceRange range)
        : base(range)
    {
        Prelude = prelude;
        ConditionText = conditionText;
        Matches = matches;
        Rules = rules;
    }

    public override CssRuleKind Kind => CssRuleKind.Supports;

    public IReadOnlyList<CssComponentValue> Prelude { get; }

    /// <summary>条件原文。</summary>
    public string ConditionText { get; }

    /// <summary>条件是否成立（解析时求值；本引擎的支持情况在运行期不变）。</summary>
    public bool Matches { get; }

    public IReadOnlyList<CssRule> Rules { get; }

    public override IReadOnlyList<CssRule> ChildRules => Rules;
}

/// <summary>
/// <c>@container [&lt;container-name&gt;]? &lt;container-condition&gt;</c>（CSS Containment 3 的尺寸查询）。条件按元素求值：
/// 查询容器是最近的、<c>container-type</c> 支持所需轴且名称匹配的祖先，尺寸由宿主经 <see cref="Selectors.StyleElement.TryGetContainerSize"/> 提供。
/// </summary>
public sealed class CssContainerRule : CssRule
{
    internal CssContainerRule(IReadOnlyList<CssComponentValue> prelude, string? name, MediaCondition condition, IReadOnlyList<CssRule> rules, SourceRange range)
        : base(range)
    {
        Prelude = prelude;
        Name = name;
        Condition = condition;
        Rules = rules;
    }

    public override CssRuleKind Kind => CssRuleKind.Container;

    public IReadOnlyList<CssComponentValue> Prelude { get; }

    /// <summary>容器名（区分大小写）；省略为 null（匹配任何查询容器）。</summary>
    public string? Name { get; }

    /// <summary>条件（与媒体条件同一棵树，特性为容器尺寸特性）。</summary>
    public MediaCondition Condition { get; }

    public IReadOnlyList<CssRule> Rules { get; }

    public override IReadOnlyList<CssRule> ChildRules => Rules;
}

/// <summary>
/// <c>@starting-style</c>（CSS Transitions 2）：其中的规则不参与普通级联，只在计算元素的「起始样式」时生效
/// （元素首次得到样式或从 <c>display: none</c> 变为显示时，作为过渡的起点，见 <see cref="Cascade.StyleEngine.ResolveStartingStyle"/>）。
/// </summary>
public sealed class CssStartingStyleRule : CssRule
{
    internal CssStartingStyleRule(IReadOnlyList<CssRule> rules, SourceRange range) : base(range) => Rules = rules;

    public override CssRuleKind Kind => CssRuleKind.StartingStyle;

    public IReadOnlyList<CssRule> Rules { get; }

    public override IReadOnlyList<CssRule> ChildRules => Rules;
}

/// <summary>
/// <c>@property --name { syntax: …; inherits: …; initial-value: …; }</c>（CSS Properties and Values API 1）。
/// 无效的规则（缺少描述符、语法字符串无效、初始值不符合语法或不是计算期独立的）在解析时报告并丢弃，不会出现在规则树中。
/// </summary>
public sealed class CssPropertyRule : CssRule
{
    internal CssPropertyRule(string name, Values.CssPropertySyntax syntax, bool inherits, IReadOnlyList<CssComponentValue>? initialValue,
        IReadOnlyList<CssDeclaration> declarations, SourceRange range) : base(range)
    {
        Name = name;
        Syntax = syntax;
        Inherits = inherits;
        InitialValue = initialValue;
        Declarations = declarations;
    }

    public override CssRuleKind Kind => CssRuleKind.Property;

    /// <summary>自定义属性名（含 <c>--</c>，区分大小写）。</summary>
    public string Name { get; }

    public Values.CssPropertySyntax Syntax { get; }

    public bool Inherits { get; }

    /// <summary>初始值的组件值；语法为 <c>*</c> 且省略时为 null（初始值为「保证无效」）。</summary>
    public IReadOnlyList<CssComponentValue>? InitialValue { get; }

    /// <summary>描述符原样（DevTools）。</summary>
    public IReadOnlyList<CssDeclaration> Declarations { get; }
}

/// <summary>块形式的 <c>@layer</c>：<c>@layer name { … }</c> 或匿名 <c>@layer { … }</c>。</summary>
public sealed class CssLayerBlockRule : CssRule
{
    internal CssLayerBlockRule(string? name, IReadOnlyList<CssRule> rules, SourceRange range) : base(range)
    {
        Name = name;
        Rules = rules;
    }

    public override CssRuleKind Kind => CssRuleKind.LayerBlock;

    /// <summary>层名（点分形式，如 <c>base.reset</c>）；匿名层为 null。</summary>
    public string? Name { get; }

    public IReadOnlyList<CssRule> Rules { get; }

    public override IReadOnlyList<CssRule> ChildRules => Rules;
}

/// <summary>语句形式的 <c>@layer a, b.c;</c>，只声明层的顺序。</summary>
public sealed class CssLayerStatementRule : CssRule
{
    internal CssLayerStatementRule(IReadOnlyList<string> names, SourceRange range) : base(range) => Names = names;

    public override CssRuleKind Kind => CssRuleKind.LayerStatement;

    public IReadOnlyList<string> Names { get; }
}

/// <summary><c>@keyframes</c> 规则。</summary>
public sealed class CssKeyframesRule : CssRule
{
    internal CssKeyframesRule(string name, IReadOnlyList<CssKeyframeRule> keyframes, SourceRange range) : base(range)
    {
        Name = name;
        Keyframes = keyframes;
    }

    public override CssRuleKind Kind => CssRuleKind.Keyframes;

    public string Name { get; }

    public IReadOnlyList<CssKeyframeRule> Keyframes { get; }

    public override IReadOnlyList<CssRule> ChildRules => Keyframes;
}

/// <summary><c>@keyframes</c> 中的一帧，如 <c>from, 50% { … }</c>。</summary>
public sealed class CssKeyframeRule : CssRule
{
    internal CssKeyframeRule(IReadOnlyList<double> offsets, string keyText, IReadOnlyList<CssDeclaration> declarations, SourceRange range)
        : base(range)
    {
        Offsets = offsets;
        KeyText = keyText;
        Declarations = declarations;
    }

    public override CssRuleKind Kind => CssRuleKind.Keyframe;

    /// <summary>关键帧位置，0–1（<c>from</c> = 0，<c>to</c> = 1）。</summary>
    public IReadOnlyList<double> Offsets { get; }

    /// <summary>序言原文。</summary>
    public string KeyText { get; }

    /// <summary>声明（带 <c>!important</c> 的声明按规范被忽略，不在此列表中）。</summary>
    public IReadOnlyList<CssDeclaration> Declarations { get; }
}

/// <summary><c>@font-face</c> 规则。描述符以声明形式保留。</summary>
public sealed class CssFontFaceRule : CssRule
{
    internal CssFontFaceRule(IReadOnlyList<CssDeclaration> declarations, SourceRange range) : base(range) => Declarations = declarations;

    public override CssRuleKind Kind => CssRuleKind.FontFace;

    public IReadOnlyList<CssDeclaration> Declarations { get; }
}

/// <summary><c>@import url layer(name) supports(…) media;</c></summary>
public sealed class CssImportRule : CssRule
{
    internal CssImportRule(string url, bool hasLayer, string? layerName, string? supportsText, MediaQueryList media, SourceRange range)
        : base(range)
    {
        Url = url;
        HasLayer = hasLayer;
        LayerName = layerName;
        SupportsText = supportsText;
        Media = media;
    }

    public override CssRuleKind Kind => CssRuleKind.Import;

    public string Url { get; }

    /// <summary>是否带 <c>layer</c> 或 <c>layer(…)</c>。</summary>
    public bool HasLayer { get; }

    /// <summary>层名；<see cref="HasLayer"/> 为真且本属性为 null 表示导入到匿名层。</summary>
    public string? LayerName { get; }

    /// <summary><c>supports(…)</c> 条件的原文。</summary>
    public string? SupportsText { get; }

    /// <summary><c>supports(…)</c> 条件是否成立（没有条件时为真）；不成立时导入的样式表与层都不生效。</summary>
    public bool SupportsMatches { get; internal set; } = true;

    public MediaQueryList Media { get; }
}

/// <summary>未知或暂不支持的 at 规则；序言与块按原样保留，不参与级联。</summary>
public sealed class CssUnknownAtRule : CssRule
{
    internal CssUnknownAtRule(string name, IReadOnlyList<CssComponentValue> prelude, CssSimpleBlock? block, bool isKnown, SourceRange range)
        : base(range)
    {
        Name = name;
        Prelude = prelude;
        Block = block;
        IsKnownUnsupported = isKnown;
    }

    public override CssRuleKind Kind => CssRuleKind.UnknownAtRule;

    /// <summary>at 规则名（ASCII 小写，不含 @）。</summary>
    public string Name { get; }

    public IReadOnlyList<CssComponentValue> Prelude { get; }

    /// <summary>原样保留的块；语句形式为 null。</summary>
    public CssSimpleBlock? Block { get; }

    /// <summary>是否为规范中已知、但本引擎暂不支持的规则（如 <c>@supports</c>、<c>@container</c>）。</summary>
    public bool IsKnownUnsupported { get; }
}
