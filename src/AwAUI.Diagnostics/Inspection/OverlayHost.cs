using AwAUI.Diagnostics.Protocol;

namespace AwAUI.Diagnostics.Inspection;

/// <summary>检查模式（design/20 <c>Overlay.setInspectMode</c>）。</summary>
public enum InspectMode
{
    /// <summary>关闭：输入照常路由进应用。</summary>
    None,
    /// <summary>拾取：悬停高亮元素，点击报告 <see cref="OverlayHost.NotifyNodePicked"/>；输入在路由进应用之前被截获。</summary>
    Pick,
    /// <summary>测量：显示标尺与间距。</summary>
    Measure,
}

/// <summary>高亮的外观（<c>HighlightConfig</c>，design/20 未定义形状，见 <see cref="OverlayDevToolsDomain"/>）。</summary>
public sealed class HighlightConfig
{
    /// <summary>显示标签、尺寸等信息提示。</summary>
    public bool ShowInfo { get; set; } = true;
    /// <summary>显示标尺。</summary>
    public bool ShowRulers { get; set; }
    /// <summary>把边框盒的边延长到窗口边缘。</summary>
    public bool ShowExtensionLines { get; set; }
    public Color ContentColor { get; set; } = Color.FromRgba(111, 168, 220, 168);
    public Color PaddingColor { get; set; } = Color.FromRgba(147, 196, 125, 140);
    public Color BorderColor { get; set; } = Color.FromRgba(255, 229, 153, 168);
    public Color MarginColor { get; set; } = Color.FromRgba(246, 178, 107, 168);
}

/// <summary>
/// 叠加层接缝：<c>Overlay</c> 域（design/20）的执行者。UI 层在应用内容之上的独立叠加层里绘制高亮与拾取器，
/// 不参与应用的布局与命中测试（design/06「与被调试 UI 的隔离」）。赋给 <see cref="DevToolsTarget.Overlay"/>。
/// </summary>
/// <remarks>
/// 线程与错误约定同 <see cref="InspectableTree"/>。叠加层在目标上是全局的（多个会话共享同一个高亮与检查模式，后设置者生效）；
/// 设置它的会话断开或停用 Overlay 域时，协议层调用 <see cref="HideHighlight"/> / <c>SetInspectMode(None)</c> 撤销。
/// </remarks>
public abstract class OverlayHost
{
    internal DevToolsTarget? Owner;

    /// <summary>所属目标（赋给 <see cref="DevToolsTarget.Overlay"/> 后设置）。</summary>
    public DevToolsTarget? Target => Owner;

    /// <summary>高亮节点的盒模型（替换当前高亮），并保持跟随节点的布局变化。节点不存在返回 false。</summary>
    public abstract bool HighlightNode(long nodeId, HighlightConfig config);

    /// <summary>隐藏高亮。</summary>
    public abstract void HideHighlight();

    /// <summary>切换检查模式。</summary>
    public abstract void SetInspectMode(InspectMode mode);

    /// <summary>
    /// 拾取模式下用户选中了节点。推送 <c>Overlay.nodePicked</c>；不会自动退出拾取模式（前端收到后自行
    /// <c>setInspectMode("none")</c>，或继续拾取）。
    /// </summary>
    public void NotifyNodePicked(long nodeId)
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnNodePicked(target, nodeId);
    }

    /// <summary>用户在应用里取消了检查模式（例如按 Esc），UI 层已回到 <see cref="InspectMode.None"/>。推送 <c>Overlay.inspectModeCanceled</c>。</summary>
    public void NotifyInspectModeCanceled()
    {
        if (TryGetDomain(out var target, out var domain)) domain.OnInspectModeCanceled(target);
    }

    bool TryGetDomain(out DevToolsTarget target, out OverlayDevToolsDomain domain)
    {
        target = Owner!;
        domain = null!;
        if (!DiagnosticsFeature.IsEnabled || target?.Agent?.Overlay is not { IsEnabled: true } d) return false;
        domain = d;
        return true;
    }
}
