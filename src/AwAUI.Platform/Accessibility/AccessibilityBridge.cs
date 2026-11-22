using System;
using System.Collections.Generic;

namespace AwAUI.Platform;

/// <summary>
/// 辅助技术可以请求的单个动作（design/10「平台桥」）。平台桥把系统发起的动作以
/// <see cref="AccessibilityActionRequest"/> 回传给框架，框架再通过元素现有的行为执行它。
/// </summary>
public enum AccessibilityActionKind
{
    /// <summary>激活（点击）。</summary>
    Invoke,

    /// <summary>聚焦。</summary>
    Focus,

    /// <summary>切换（复选框、单选、开关、按下状态）。</summary>
    Toggle,

    /// <summary>设置可编辑控件的值。</summary>
    SetValue,

    /// <summary>增加一步（滑块、数值框）。</summary>
    Increment,

    /// <summary>减少一步（滑块、数值框）。</summary>
    Decrement,

    /// <summary>滚动到可见。</summary>
    ScrollIntoView,

    /// <summary>展开。</summary>
    Expand,

    /// <summary>折叠。</summary>
    Collapse,
}

/// <summary>实时区域的播报优先级（ARIA <c>aria-live</c>）。</summary>
public enum AccessibilityPoliteness
{
    /// <summary>不播报。</summary>
    Off,

    /// <summary>等待当前播报结束（<c>polite</c>）。</summary>
    Polite,

    /// <summary>立即打断当前播报（<c>assertive</c>）。</summary>
    Assertive,
}

/// <summary>
/// 可编辑控件的文本快照：值、选区与输入法组合状态。平台桥据此映射到 UIA TextPattern / AT-SPI Text 等。
/// </summary>
public sealed class AccessibilityTextSnapshot
{
    /// <summary>已提交的文本（不含预编辑文本）。</summary>
    public string Value { get; set; } = "";

    /// <summary>选区起点（UTF-16 下标，≤ <see cref="SelectionEnd"/>）。</summary>
    public int SelectionStart { get; set; }

    /// <summary>选区终点（不含）。</summary>
    public int SelectionEnd { get; set; }

    /// <summary>选区的锚点。</summary>
    public int SelectionAnchor { get; set; }

    /// <summary>选区的焦点（插入点所在的一端）。</summary>
    public int SelectionFocus { get; set; }

    /// <summary>选中的文本；密码框为空串。</summary>
    public string SelectedText { get; set; } = "";

    /// <summary>输入法组合是否进行中。</summary>
    public bool IsComposing { get; set; }

    /// <summary>预编辑文本（组合状态为空串）。</summary>
    public string CompositionText { get; set; } = "";

    /// <summary>预编辑文本插入在值中的位置。</summary>
    public int CompositionStart { get; set; }

    /// <summary>预编辑文本内的选区起点。</summary>
    public int CompositionSelectionStart { get; set; }

    /// <summary>预编辑文本内的选区终点。</summary>
    public int CompositionSelectionEnd { get; set; }
}

/// <summary>
/// 语义树节点的平台无关快照。没有对 <c>AwAUI.UI</c> 的依赖：平台桥只消费这些 DTO，
/// 由 UI 侧按需从元素树生成。
/// </summary>
public sealed class AccessibilityNodeSnapshot
{
    /// <summary>稳定标识：元素显式 <c>id</c>，否则由元素唯一序号生成。</summary>
    public string Id { get; set; } = "";

    /// <summary>ARIA 角色名（<c>button</c>、<c>checkbox</c>、<c>textbox</c> ...）。</summary>
    public string Role { get; set; } = "";

    /// <summary>计算得到的可访问名称。</summary>
    public string? Name { get; set; }

    /// <summary>描述。</summary>
    public string? Description { get; set; }

    /// <summary>当前值（输入框、滑块、进度条等）。</summary>
    public string? Value { get; set; }

    /// <summary>状态标记名（<c>disabled</c>、<c>checked</c>、<c>mixed</c>、<c>selected</c> ...）。</summary>
    public IReadOnlyList<string> States { get; set; } = Array.Empty<string>();

    /// <summary>可用动作。</summary>
    public IReadOnlyList<AccessibilityActionKind> Actions { get; set; } = Array.Empty<AccessibilityActionKind>();

    /// <summary>屏幕（窗口客户区）坐标下的边界。</summary>
    public Rect Bounds { get; set; }

    /// <summary>父节点标识；根为 null。</summary>
    public string? ParentId { get; set; }

    /// <summary>子节点标识，按树序。</summary>
    public IReadOnlyList<string> ChildIds { get; set; } = Array.Empty<string>();

    /// <summary><c>aria-labelledby</c> 引用的节点标识。</summary>
    public IReadOnlyList<string> LabelledBy { get; set; } = Array.Empty<string>();

    /// <summary><c>aria-controls</c> 引用的节点标识。</summary>
    public IReadOnlyList<string> Controls { get; set; } = Array.Empty<string>();

    /// <summary><c>aria-describedby</c> 引用的节点标识。</summary>
    public IReadOnlyList<string> DescribedBy { get; set; } = Array.Empty<string>();

    /// <summary>实时区域的播报优先级。</summary>
    public AccessibilityPoliteness LiveRegion { get; set; }

    /// <summary>标题层级（<c>heading</c>），未知时为 null。</summary>
    public int? HeadingLevel { get; set; }

    /// <summary>虚拟化列表中的位置（1 基，<c>aria-posinset</c>）。</summary>
    public int? PosInSet { get; set; }

    /// <summary>虚拟化列表的总数（<c>aria-setsize</c>）。</summary>
    public int? SetSize { get; set; }

    /// <summary>可编辑控件的文本信息；非可编辑控件为 null。</summary>
    public AccessibilityTextSnapshot? Text { get; set; }
}

/// <summary>某一版本语义树的完整快照。</summary>
public sealed class AccessibilitySnapshot
{
    /// <summary>生成快照时的语义树版本（见 <c>AccessibilityTree.Version</c>）。</summary>
    public long Version { get; set; }

    /// <summary>根节点标识。</summary>
    public string? RootId { get; set; }

    /// <summary>全部节点（树序）。</summary>
    public IReadOnlyList<AccessibilityNodeSnapshot> Nodes { get; set; } = Array.Empty<AccessibilityNodeSnapshot>();

    /// <summary>按标识查找节点；不存在时返回 null。</summary>
    public AccessibilityNodeSnapshot? Find(string id)
    {
        if (id is null) return null;
        var nodes = Nodes;
        for (int i = 0; i < nodes.Count; i++)
            if (string.Equals(nodes[i].Id, id, StringComparison.Ordinal)) return nodes[i];
        return null;
    }
}

/// <summary>平台桥回传给框架的动作请求。</summary>
public sealed class AccessibilityActionRequest
{
    public AccessibilityActionRequest(string nodeId, AccessibilityActionKind action, string? value = null)
    {
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        Action = action;
        Value = value;
    }

    /// <summary>目标节点标识。</summary>
    public string NodeId { get; }

    /// <summary>请求的动作。</summary>
    public AccessibilityActionKind Action { get; }

    /// <summary>动作参数（<see cref="AccessibilityActionKind.SetValue"/> 的新值）。</summary>
    public string? Value { get; }
}

/// <summary>
/// 平台无障碍桥的基类（design/10）：接收语义树快照的更新（增量由调用方决定），
/// 并在辅助技术发起动作时通过 <see cref="ActionRequested"/> 把请求回传给框架。
/// 具体平台（UI Automation、NSAccessibility、AT-SPI2、Web ARIA DOM ...）派生它。
/// </summary>
public abstract class AccessibilityBridge
{
    /// <summary>接收一版语义树快照。平台桥应把它映射到原生无障碍元素。</summary>
    public abstract void Update(AccessibilitySnapshot snapshot);

    /// <summary>辅助技术请求执行动作（在平台线程上触发；框架应封送到 UI 线程执行）。</summary>
    public event Action<AccessibilityActionRequest>? ActionRequested;

    /// <summary>
    /// 框架设置的动作处理器：返回动作是否成功执行。<see cref="ActionRequested"/> 仍会先触发，
    /// 供 DevTools / 日志等旁路观察。与事件相比，它让平台桥能向辅助技术报告 Invoke/Toggle 的成功与否。
    /// </summary>
    public Func<AccessibilityActionRequest, bool>? ActionHandler { get; set; }

    /// <summary>
    /// 派生类把辅助技术发起的动作交给 <see cref="ActionRequested"/> 的订阅者与 <see cref="ActionHandler"/>；
    /// 返回动作是否被成功处理（没有处理器时返回是否有事件订阅者）。
    /// </summary>
    protected bool RaiseActionRequested(AccessibilityActionRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var listeners = ActionRequested;
        bool any = listeners is not null;
        listeners?.Invoke(request);
        var handler = ActionHandler;
        if (handler is not null) return handler(request);
        return any;
    }
}
