using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.Headless;

/// <summary>
/// 无窗口无障碍桥：记录最近一版语义树快照，并让测试驱动辅助技术一侧的动作。
/// <see cref="Perform"/> 产生的 <see cref="AccessibilityActionRequest"/> 与真实平台桥一样经
/// <see cref="AccessibilityBridge.ActionRequested"/> 回传，通常由
/// <c>AccessibilityTree.ConnectBridge</c> 转交给元素动作。
/// </summary>
public sealed class HeadlessAccessibilityBridge : AccessibilityBridge
{
    readonly List<AccessibilityActionRequest> _requests = new();

    /// <summary>最近一次 <see cref="Update"/> 收到的快照；尚未更新时为 null。</summary>
    public AccessibilitySnapshot? LastSnapshot { get; private set; }

    /// <summary><see cref="Update"/> 被调用的次数。</summary>
    public int UpdateCount { get; private set; }

    /// <summary>辅助技术一侧发起过的动作请求（按调用顺序）。</summary>
    public IReadOnlyList<AccessibilityActionRequest> Requests => _requests;

    /// <inheritdoc />
    public override void Update(AccessibilitySnapshot snapshot)
    {
        LastSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        UpdateCount++;
    }

    /// <summary>在最近一版快照中按标识查找节点。</summary>
    public AccessibilityNodeSnapshot? Find(string id) => LastSnapshot?.Find(id);

    /// <summary>
    /// 模拟辅助技术激活一个节点（VoiceOver 双击、TalkBack 双击、UIA Invoke ...）：
    /// 记录请求并触发 <see cref="AccessibilityBridge.ActionRequested"/> / <see cref="AccessibilityBridge.ActionHandler"/>。
    /// 返回动作是否被框架成功执行（已连接 <c>AccessibilityTree.ConnectBridge</c> 时即实际的执行结果）。
    /// </summary>
    public bool Perform(AccessibilityActionKind action, string nodeId, string? value = null)
    {
        if (nodeId is null) return false;
        var request = new AccessibilityActionRequest(nodeId, action, value);
        _requests.Add(request);
        return RaiseActionRequested(request);
    }

    /// <summary>清空记录的动作请求（快照保留）。</summary>
    public void ClearRequests() => _requests.Clear();
}
