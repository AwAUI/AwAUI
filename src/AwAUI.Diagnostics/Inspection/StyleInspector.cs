using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Protocol;

namespace AwAUI.Diagnostics.Inspection;

/// <summary>声明在级联中的结果（design/20 <c>Declaration.status</c>）。</summary>
public enum StyleDeclarationStatus
{
    /// <summary>参与级联并胜出。</summary>
    Active,
    /// <summary>被更高优先级的声明覆盖。</summary>
    Overridden,
    /// <summary>值无效。</summary>
    Invalid,
    /// <summary>属性不被支持。</summary>
    Unsupported,
    /// <summary>胜出但不生效（例如父元素不是 flex 容器时的 <c>flex-grow</c>），原因见 <see cref="StyleDeclarationInfo.Reason"/>。</summary>
    Inactive,
}

/// <summary>规则来源（design/20 <c>Rule.origin</c>）。</summary>
public enum StyleOrigin
{
    /// <summary>框架默认样式表（相当于 UA 样式表）。</summary>
    Default,
    /// <summary>应用样式表与内联样式。</summary>
    Author,
    /// <summary>DevTools 覆盖（级联中最高的来源，可一键还原）。</summary>
    DevTools,
}

/// <summary>一条声明（design/20 <c>Declaration</c>）。</summary>
public sealed class StyleDeclarationInfo
{
    public StyleDeclarationInfo(string name, string value)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Name { get; }
    /// <summary>值的源文本（不含 <c>!important</c>）。</summary>
    public string Value { get; }
    public bool Important { get; set; }
    public StyleDeclarationStatus Status { get; set; }
    /// <summary>无效或不生效的原因（面向人的文本）。</summary>
    public string? Reason { get; set; }
    /// <summary>被注释掉的声明（<c>/* name: value */</c>，DevTools 的「切换」），不参与级联。</summary>
    public bool Disabled { get; set; }
    /// <summary>在所属声明块文本（<see cref="StyleInspector.SetStyleText"/> 编辑的文本）中的 UTF-16 区间 [Start, End)；未知为 null。</summary>
    public (int Start, int End)? Range { get; set; }
}

/// <summary>选择器特异性 (a, b, c)。</summary>
public readonly struct StyleSpecificity : IEquatable<StyleSpecificity>
{
    public StyleSpecificity(int ids, int classes, int types)
    {
        Ids = ids; Classes = classes; Types = types;
    }

    /// <summary>a：ID 选择器数。</summary>
    public int Ids { get; }
    /// <summary>b：class、属性、伪类选择器数。</summary>
    public int Classes { get; }
    /// <summary>c：类型与伪元素选择器数。</summary>
    public int Types { get; }

    public bool Equals(StyleSpecificity o) => Ids == o.Ids && Classes == o.Classes && Types == o.Types;
    public override bool Equals(object? obj) => obj is StyleSpecificity s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(Ids, Classes, Types);
    public override string ToString() => "(" + Ids + "," + Classes + "," + Types + ")";
}

/// <summary>一条样式规则（design/20 <c>Rule</c>）。</summary>
public sealed class StyleRuleInfo
{
    public StyleRuleInfo(string id, string selectorText, StyleOrigin origin, string sheetId)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        SelectorText = selectorText ?? throw new ArgumentNullException(nameof(selectorText));
        Origin = origin;
        SheetId = sheetId ?? throw new ArgumentNullException(nameof(sheetId));
    }

    /// <summary>规则的稳定标识（<see cref="StyleTextTarget.ForRule"/> 用它引用规则）。样式表文本被替换后旧规则 id 可以失效。</summary>
    public string Id { get; }
    public string SelectorText { get; }
    public StyleOrigin Origin { get; }
    public string SheetId { get; }
    /// <summary>所在级联层的点分名称；未分层为 null。</summary>
    public string? Layer { get; set; }
    /// <summary>外层 @media 条件文本（多层时以 <c> and </c> 连接）；没有为 null。</summary>
    public string? Media { get; set; }
    public SourceLocation? Source { get; set; }
    /// <summary>声明（含被注释掉的），按书写顺序。</summary>
    public List<StyleDeclarationInfo> Declarations { get; } = new();
}

/// <summary>一条命中的规则（design/20 <c>RuleMatch</c>）。</summary>
public sealed class StyleRuleMatch
{
    public StyleRuleMatch(StyleRuleInfo rule, string matchedSelector, StyleSpecificity specificity)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        MatchedSelector = matchedSelector ?? throw new ArgumentNullException(nameof(matchedSelector));
        Specificity = specificity;
    }

    public StyleRuleInfo Rule { get; }
    /// <summary>选择器列表中实际命中的（特异性最高的）那个选择器。</summary>
    public string MatchedSelector { get; }
    public StyleSpecificity Specificity { get; }
}

/// <summary>从祖先继承来的规则。</summary>
public sealed class InheritedStyleInfo
{
    public InheritedStyleInfo(long fromNodeId) => FromNodeId = fromNodeId;
    public long FromNodeId { get; }
    /// <summary>该祖先命中的、含可继承属性的规则，按级联优先级从高到低。</summary>
    public List<StyleRuleMatch> Matched { get; } = new();
}

/// <summary>伪元素（<c>::before</c> / <c>::after</c>）命中的规则。</summary>
public sealed class PseudoElementStyleInfo
{
    public PseudoElementStyleInfo(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));
    /// <summary>伪元素名，如 <c>before</c>。</summary>
    public string Name { get; }
    public List<StyleRuleMatch> Matched { get; } = new();
}

/// <summary>一个节点的匹配样式（design/20 <c>Css.getMatchedRules</c> 的结果）。</summary>
public sealed class MatchedStylesInfo
{
    /// <summary>内联样式的声明。</summary>
    public List<StyleDeclarationInfo> Inline { get; } = new();
    /// <summary>命中的规则，按级联优先级从高到低。</summary>
    public List<StyleRuleMatch> Matched { get; } = new();
    /// <summary>从近到远的祖先。</summary>
    public List<InheritedStyleInfo> Inherited { get; } = new();
    public List<PseudoElementStyleInfo> PseudoElements { get; } = new();
}

/// <summary>一个计算值（design/20 <c>ComputedProperty</c>）。</summary>
public sealed class ComputedStyleProperty
{
    public ComputedStyleProperty(string name, string value)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Name { get; }
    public string Value { get; }
    /// <summary>胜出声明所在规则；取自内联样式、继承或初始值时为 null。</summary>
    public string? FromRuleId { get; set; }
    /// <summary>值继承自的祖先节点；不是继承来的为 0。</summary>
    public long FromNodeId { get; set; }
    /// <summary>胜出声明的来源；初始值为 null。</summary>
    public StyleOrigin? FromOrigin { get; set; }
}

/// <summary>样式表信息（<c>StyleSheetInfo</c>，design/20 未定义形状，见 <see cref="CssDevToolsDomain"/>）。</summary>
public sealed class StyleSheetInfo
{
    public StyleSheetInfo(string id, StyleOrigin origin)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Origin = origin;
    }

    public string Id { get; }
    public StyleOrigin Origin { get; }
    /// <summary>来源文件或 URL（组件作用域样式为 <c>.awaui</c> 文件路径）。</summary>
    public string? SourceUrl { get; set; }
    public string? Title { get; set; }
    public bool Disabled { get; set; }
    /// <summary>样式表文本能否通过 <see cref="StyleInspector.SetStyleSheetText"/> 替换。</summary>
    public bool Editable { get; set; } = true;
    /// <summary>拥有该样式表的节点（组件作用域样式的宿主元素）；没有为 0。</summary>
    public long OwnerNodeId { get; set; }
}

/// <summary>诊断的严重程度。</summary>
public enum StyleDiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>替换样式表文本后的诊断（<c>Diagnostic</c>）。</summary>
public sealed class StyleDiagnostic
{
    public StyleDiagnostic(StyleDiagnosticSeverity severity, string message)
    {
        Severity = severity;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public StyleDiagnosticSeverity Severity { get; }
    public string Message { get; }
    /// <summary>诊断码，如 <c>AW2002</c>。</summary>
    public string? Code { get; set; }
    /// <summary>1 起的行号；未知为 0。</summary>
    public int Line { get; set; }
    /// <summary>1 起的列号；未知为 0。</summary>
    public int Column { get; set; }
}

/// <summary><see cref="StyleInspector.SetStyleText"/> 编辑的声明块：节点的内联样式，或一条规则。</summary>
public readonly struct StyleTextTarget
{
    StyleTextTarget(long nodeId, string? ruleId)
    {
        NodeId = nodeId; RuleId = ruleId;
    }

    /// <summary>内联样式所属节点；编辑规则时为 0。</summary>
    public long NodeId { get; }
    /// <summary>规则 id；编辑内联样式时为 null。</summary>
    public string? RuleId { get; }
    public bool IsInline => RuleId is null;

    public static StyleTextTarget ForNode(long nodeId) => new(nodeId, null);
    public static StyleTextTarget ForRule(string ruleId) => new(0, ruleId ?? throw new ArgumentNullException(nameof(ruleId)));

    public override string ToString() => IsInline ? "node " + NodeId : "rule " + RuleId;
}

/// <summary>
/// 样式的检查与编辑接缝：<c>Css</c> 域（design/20）的数据源。UI 层基于 CSS 引擎的 <c>StyleMatchRecord</c> 实现派生类，
/// 赋给 <see cref="DevToolsTarget.Styles"/>。节点 id 与同一目标的 <see cref="InspectableTree"/> 一致。
/// </summary>
/// <remarks>
/// <para>线程与错误约定同 <see cref="InspectableTree"/>：方法只在目标的 UI 线程上调用；以 id 查找的对象不存在时返回 null / false；
/// CSS 文本无法解析抛 <see cref="FormatException"/>（-32005）；不支持的操作抛 <see cref="NotSupportedException"/>（-32004）。</para>
/// <para>编辑：增删改与「切换」声明都通过 <see cref="SetStyleText"/> 替换整个声明块的文本完成（前端在本地改好文本后提交），
/// 被注释掉的声明 <c>/* name: value */</c> 应报告为 <see cref="StyleDeclarationInfo.Disabled"/>。
/// DevTools 的修改应写入级联中独立的 DevTools 来源，组件重建不会冲掉它们，<see cref="ClearOverrides"/> 一键还原（design/06）。
/// 修改在下一帧生效；实现照常通过 <see cref="NotifyStyleSheetChanged"/> 等报告变化。</para>
/// </remarks>
public abstract class StyleInspector
{
    /// <summary><see cref="ForcePseudoState"/> 接受的伪状态名（与 CSS 引擎的 <c>PseudoStates</c> 对应）。</summary>
    public static IReadOnlyList<string> ForceablePseudoStates { get; } = new[]
    {
        "hover", "active", "focus", "focus-visible", "focus-within", "disabled", "checked", "placeholder-shown",
    };

    internal DevToolsTarget? Owner;

    /// <summary>所属目标（赋给 <see cref="DevToolsTarget.Styles"/> 后设置）。</summary>
    public DevToolsTarget? Target => Owner;

    // ------------------------------------------------------------ 读取

    /// <summary>节点命中的样式；节点不存在返回 null。文本节点返回空结果。</summary>
    public abstract MatchedStylesInfo? GetMatchedRules(long nodeId);

    /// <summary>节点的计算值（按属性名排序）；节点不存在返回 null。</summary>
    public abstract IReadOnlyList<ComputedStyleProperty>? GetComputedStyle(long nodeId);

    /// <summary>节点的内联样式声明；节点不存在返回 null。默认取 <see cref="GetMatchedRules"/> 的 <see cref="MatchedStylesInfo.Inline"/>。</summary>
    public virtual IReadOnlyList<StyleDeclarationInfo>? GetInlineStyle(long nodeId) => GetMatchedRules(nodeId)?.Inline;

    /// <summary>全部样式表。</summary>
    public abstract IReadOnlyList<StyleSheetInfo> GetStyleSheets();

    /// <summary>样式表的源文本；样式表不存在返回 null。</summary>
    public abstract string? GetStyleSheetText(string sheetId);

    // ------------------------------------------------------------ 编辑

    /// <summary>
    /// 用 <paramref name="text"/>（声明块内容，不含花括号）替换内联样式或规则的全部声明，返回新的声明列表。
    /// 目标不存在返回 null；文本无法解析抛 <see cref="FormatException"/>（单条无效声明不算无法解析，应以 Invalid 状态返回）。
    /// </summary>
    public abstract IReadOnlyList<StyleDeclarationInfo>? SetStyleText(StyleTextTarget target, string text);

    /// <summary>替换整个样式表的文本，返回诊断。样式表不存在返回 null；不可编辑时抛 <see cref="NotSupportedException"/>。</summary>
    public abstract IReadOnlyList<StyleDiagnostic>? SetStyleSheetText(string sheetId, string text);

    /// <summary>在 DevTools 专用样式表中新增一条规则并返回它。选择器无法解析抛 <see cref="FormatException"/>。默认不支持。</summary>
    public virtual StyleRuleInfo AddRule(string selector, string? text) => throw new NotSupportedException("Adding rules is not supported.");

    /// <summary>
    /// 强制节点处于给定伪状态（替换之前强制的集合；空集合取消强制）。<paramref name="states"/> 中的名字已由协议层校验
    /// 属于 <see cref="ForceablePseudoStates"/>。节点不存在返回 false。会话断开或停用 Css 域时协议层自动以空集合撤销。
    /// </summary>
    public abstract bool ForcePseudoState(long nodeId, IReadOnlyList<string> states);

    /// <summary>
    /// 撤销 DevTools 的样式修改：<paramref name="nodeId"/> 非 null 时只撤销该节点的内联修改与强制伪状态，否则撤销全部。
    /// 节点不存在返回 false。默认不支持。
    /// </summary>
    public virtual bool ClearOverrides(long? nodeId) => throw new NotSupportedException("Clearing overrides is not supported.");

    // ------------------------------------------------------------ 变更通知（任意线程）

    /// <summary>样式表的内容变化（包括 DevTools 自己的编辑）。立即推送 <c>Css.styleSheetChanged</c>。</summary>
    public void NotifyStyleSheetChanged(string sheetId)
    {
        if (sheetId is null) throw new ArgumentNullException(nameof(sheetId));
        if (TryGetDomain(out var target, out var domain)) domain.OnStyleSheetChanged(target, sheetId);
    }

    /// <summary>新增样式表。立即推送 <c>Css.styleSheetAdded</c>。</summary>
    public void NotifyStyleSheetAdded(StyleSheetInfo sheet)
    {
        if (sheet is null) throw new ArgumentNullException(nameof(sheet));
        if (TryGetDomain(out var target, out var domain)) domain.OnStyleSheetAdded(target, sheet);
    }

    /// <summary>样式表被移除。立即推送 <c>Css.styleSheetRemoved</c>。</summary>
    public void NotifyStyleSheetRemoved(string sheetId)
    {
        if (sheetId is null) throw new ArgumentNullException(nameof(sheetId));
        if (TryGetDomain(out var target, out var domain)) domain.OnStyleSheetRemoved(target, sheetId);
    }

    /// <summary>
    /// 节点的计算值变了（重算样式后报告）。按帧合并为 <c>Css.computedStyleInvalidated</c>，
    /// 只报告给已通过 <c>Tree</c> 域拿到该节点的会话。
    /// </summary>
    public void NotifyComputedStyleInvalidated(long nodeId)
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnComputedStyleInvalidated(target, nodeId);
    }

    bool TryGetDomain(out DevToolsTarget target, out CssDevToolsDomain domain)
    {
        target = Owner!;
        domain = null!;
        if (!DiagnosticsFeature.IsEnabled || target?.Agent?.Css is not { IsEnabled: true } d) return false;
        domain = d;
        return true;
    }
}
