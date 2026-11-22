using System;
using System.Collections.Generic;
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
/// 叠加层持续显示的辅助内容（design/20 <c>Overlay.setShowOptions</c>）。默认全部关闭 / 为空。
/// </summary>
/// <remarks>
/// 这些选项与 <see cref="HighlightConfig"/> 无关：它们不依赖某个特定的高亮节点。UI 层把它们绘制在应用帧之上的独立叠加层里：
/// 标尺（视口顶边与左边的刻度）、当前高亮元素的命中区域、布局网格（<see cref="LayoutGrids"/> 指定的 flex / grid 容器的
/// 轨道带）与活动触摸点（圆 + 十字准星；鼠标不绘制）。触摸点位置由 UI 层的输入管理器提供。
/// </remarks>
public sealed class OverlayShowOptions
{
    /// <summary>显示布局网格叠加的节点 id（空表示不显示）。</summary>
    public IReadOnlyList<long> LayoutGrids { get; set; } = Array.Empty<long>();

    /// <summary>显示标尺。</summary>
    public bool Rulers { get; set; }

    /// <summary>显示命中目标区域。</summary>
    public bool HitTargets { get; set; }

    /// <summary>显示触摸点。</summary>
    public bool Touches { get; set; }
}

/// <summary>
/// 叠加层接缝：<c>Overlay</c> 域（design/20）的执行者。UI 层在应用内容之上的独立叠加层里绘制高亮与拾取器，
/// 不参与应用的布局与命中测试（design/06「与被调试 UI 的隔离」）。赋给 <see cref="DevToolsTarget.Overlay"/>。
/// </summary>
/// <remarks>
/// 线程与错误约定同 <see cref="InspectableTree"/>。叠加层在目标上是全局的（多个会话共享同一个高亮与检查模式，后设置者生效）；
/// 设置它的会话断开或停用 Overlay 域时，协议层调用 <see cref="HideHighlight"/> / <c>SetInspectMode(None)</c> 撤销。
/// 显示选项（<see cref="SetShowOptions"/>）同样由设置它的会话拥有，撤销时恢复为全关闭的空选项。
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
    /// 设置持续显示的辅助内容（替换之前的设置；默认实现不支持，协议层返回 -32004）。实现应保存选项并在下一次合成时
    /// 按新选项绘制（见 <see cref="OverlayShowOptions"/>）。
    /// </summary>
    public virtual void SetShowOptions(OverlayShowOptions options) => throw new NotSupportedException("Showing overlay options is not supported by this overlay.");

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
