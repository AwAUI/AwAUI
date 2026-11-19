using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Protocol;

namespace AwAUI.Diagnostics.Inspection;

/// <summary>
/// 元素树的检查接缝：<c>Tree</c> 与 <c>Layout</c> 域（design/20）的数据源。UI 层为每个窗口实现一个派生类，
/// 赋给 <see cref="DevToolsTarget.Tree"/>。Diagnostics 不依赖 UI 层，只通过本类看到元素树。
/// </summary>
/// <remarks>
/// <para><b>线程</b>：本类的所有抽象 / 虚方法都只在目标的 UI 线程上被调用 —— 即 <see cref="DevToolsTarget.Dispatcher"/>
/// （未设置时为 <see cref="DevToolsAgentOptions.Dispatcher"/>；两者都未设置时在传输线程上调用，此时实现需自行保证线程安全）。
/// 协议层把方法处理器投递到该调度器上执行并等待结果，实现不需要做任何线程切换。</para>
/// <para><b>节点标识</b>：见 <see cref="InspectableNodeInfo"/>。所有以 id 查找节点的方法在节点不存在（或已从树上移除）时
/// 返回 null / false / 0，协议层据此返回 -32001。</para>
/// <para><b>错误</b>：方法可以抛 <see cref="FormatException"/>（无法解析的选择器、属性名等，协议层返回 -32005）、
/// <see cref="NotSupportedException"/>（-32004）或 <see cref="DevToolsProtocolException"/>（原样返回）。其他异常作为内部错误返回。</para>
/// <para><b>变更通知</b>：UI 层在元素树变化时调用 <c>Notify*</c> 方法（通常从 <c>ElementTreeObserver</c> 转发）。
/// 应在 UI 线程上、在修改发生时同步调用 —— 这样通知与方法处理器在同一线程上按发生顺序交错，前端看到的快照与事件严格一致。
/// 从其他线程调用时通知被投递到 UI 线程，处理时读取的是那时的状态（重复的插入会被识别并忽略，但不保证逐条一致），
/// 只适合不修改树结构的报告。没有会话启用 <c>Tree</c> 域时通知几乎零开销（一次字段判断）。
/// 结构变化（插入 / 移除 / 移动）立即推送；属性、文本、伪状态与子节点数的变化按帧合并，在
/// <see cref="DevToolsAgent.FlushFrame"/> 时发出 —— 宿主的帧调度器必须每帧调用它。</para>
/// </remarks>
public abstract class InspectableTree
{
    internal DevToolsTarget? Owner;

    /// <summary>所属目标（赋给 <see cref="DevToolsTarget.Tree"/> 后设置）。</summary>
    public DevToolsTarget? Target => Owner;

    // ------------------------------------------------------------ 读取

    /// <summary>根节点 id；树还没有根时为 0。</summary>
    public abstract long RootId { get; }

    /// <summary>描述一个节点（不含子节点）；节点不存在时返回 null。每次调用返回新的快照对象。</summary>
    public abstract InspectableNodeInfo? DescribeNode(long nodeId);

    /// <summary>
    /// 按文档顺序把 <paramref name="nodeId"/> 的直接子节点（含文本节点）的 id 追加到 <paramref name="children"/>。
    /// 节点不存在时返回 false。
    /// </summary>
    public abstract bool GetChildren(long nodeId, List<long> children);

    /// <summary>节点是否存在于树上。默认实现调用 <see cref="DescribeNode"/>，实现可以覆盖为更便宜的查找。</summary>
    public virtual bool ContainsNode(long nodeId) => DescribeNode(nodeId) != null;

    /// <summary>
    /// 节点的盒模型（窗口坐标）。节点存在但没有布局盒（<c>display: none</c>、尚未布局、文本节点等）时返回 null。
    /// 协议层先用 <see cref="ContainsNode"/> 区分「节点不存在」。
    /// </summary>
    public abstract BoxModelInfo? GetBoxModel(long nodeId);

    /// <summary>
    /// 在 <paramref name="scopeNodeId"/> 的后代（不含它自身，与 DOM 的 <c>element.querySelectorAll</c> 一致）中按文档顺序
    /// 查找匹配 <paramref name="selector"/> 的元素，把 id 追加到 <paramref name="results"/>。
    /// 选择器无法解析时抛 <see cref="FormatException"/>。协议层已确认 <paramref name="scopeNodeId"/> 存在。
    /// </summary>
    public abstract void QuerySelectorAll(long scopeNodeId, string selector, List<long> results);

    /// <summary>第一个匹配的后代元素；没有返回 0。默认实现取 <see cref="QuerySelectorAll"/> 的第一个结果。</summary>
    public virtual long QuerySelector(long scopeNodeId, string selector)
    {
        var results = new List<long>();
        QuerySelectorAll(scopeNodeId, selector, results);
        return results.Count > 0 ? results[0] : 0;
    }

    /// <summary>
    /// 窗口坐标 <paramref name="point"/> 处最上层的节点（命中测试）；没有返回 0。
    /// <paramref name="ignorePointerEvents"/> 为 true 时也考虑 <c>pointer-events: none</c> 的元素。默认不支持。
    /// </summary>
    public virtual long GetNodeAt(Point point, bool ignorePointerEvents) => throw new NotSupportedException("Hit testing is not supported by this tree.");

    // ------------------------------------------------------------ 编辑（可选）

    /// <summary>
    /// 设置（<paramref name="value"/> 非 null）或移除（null）一个属性。<paramref name="name"/> 为 <c>id</c> / <c>class</c> 时改元素 id / class 文本。
    /// 节点不存在返回 false；属性名非法抛 <see cref="FormatException"/>。默认不支持。
    /// 修改应照常触发变更通知（协议层不会替 UI 层发事件）。
    /// </summary>
    public virtual bool SetAttribute(long nodeId, string name, string? value) => throw new NotSupportedException("Editing attributes is not supported by this tree.");

    /// <summary>替换元素的 class 列表。节点不存在返回 false。默认不支持。</summary>
    public virtual bool SetClasses(long nodeId, IReadOnlyList<string> classes) => throw new NotSupportedException("Editing classes is not supported by this tree.");

    // ------------------------------------------------------------ 变更通知

    /// <summary>
    /// <paramref name="childId"/> 被插入到 <paramref name="parentId"/>，位于 <paramref name="beforeId"/> 之前（0 表示末尾）。
    /// 新插入的子树只需报告其顶层节点。跨父节点的移动报告为 <see cref="NotifyChildRemoved"/> + 本方法，最好先报告移除；
    /// 先报告插入时，协议层会先替已展开旧父节点的会话补发一条旧位置的移除，随后迟到的移除通知被忽略。
    /// </summary>
    public void NotifyChildInserted(long parentId, long childId, long beforeId = 0)
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnChildInserted(target, parentId, childId, beforeId);
    }

    /// <summary><paramref name="childId"/>（连同其子树）从 <paramref name="parentId"/> 移除。</summary>
    public void NotifyChildRemoved(long parentId, long childId)
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnChildRemoved(target, parentId, childId);
    }

    /// <summary><paramref name="childId"/> 在同一父节点内移动到 <paramref name="beforeId"/> 之前（0 表示末尾），子树保持不变。</summary>
    public void NotifyChildMoved(long parentId, long childId, long beforeId = 0)
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnChildMoved(target, parentId, childId, beforeId);
    }

    /// <summary>属性 <paramref name="name"/> 被设置或移除。元素 id 变化报告为 <c>"id"</c>，class 列表变化报告为 <c>"class"</c>。</summary>
    public void NotifyAttributeChanged(long nodeId, string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (TryGetDomain(out var target, out var domain)) domain.OnNodeChanged(target, nodeId, TreeDevToolsDomain.ChangeKind.Attribute, name);
    }

    /// <summary>文本节点的内容变化。</summary>
    public void NotifyTextChanged(long nodeId)
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnNodeChanged(target, nodeId, TreeDevToolsDomain.ChangeKind.Text, null);
    }

    /// <summary>节点的伪状态（hover、focus ...）变化。</summary>
    public void NotifyPseudoStatesChanged(long nodeId)
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnNodeChanged(target, nodeId, TreeDevToolsDomain.ChangeKind.PseudoStates, null);
    }

    /// <summary>整棵树被替换（根节点变化等）：所有已发送的节点失效，前端需要重新 <c>Tree.getDocument</c>。</summary>
    public void NotifyDocumentUpdated()
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnDocumentUpdated(target);
    }

    bool TryGetDomain(out DevToolsTarget target, out TreeDevToolsDomain domain)
    {
        target = Owner!;
        domain = null!;
        if (!DiagnosticsFeature.IsEnabled || target?.Agent?.Tree is not { IsEnabled: true } d) return false;
        domain = d;
        return true;
    }
}
