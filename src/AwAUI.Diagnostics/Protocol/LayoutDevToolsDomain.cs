using System.Collections.Generic;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// Layout 域（design/20 <c>Layout / Overlay</c>）：
/// <c>getBoxModel({ nodeId }) → { content, padding, border, margin: Quad, width, height, scrollOffset?: { x, y }, scrollSize?: { width, height } }</c>
/// 的数据源是 <see cref="Inspection.InspectableTree.GetBoxModel"/>；Quad 为窗口坐标的 8 个数（四个角，顺时针，从左上开始），
/// width / height 为边框盒尺寸。
/// <para><c>getLayoutInfo({ nodeId }) → { display, constraints: { minWidth, maxWidth, minHeight, maxHeight }, flex?,
/// grid?, isLayoutBoundary, lastLayoutFrame, invalidatedBy? }</c> 的数据源是 <see cref="Inspection.InspectableTree.GetLayoutInfo"/>。
/// 约束是计算样式中的固定像素值；<c>auto</c> / <c>none</c> / 百分比等无法表示为固定像素的约束写成 null。
/// <c>flex</c> / <c>grid</c> 只在对应容器上出现：<c>flex = { tracks: { x, y } }</c>（主轴项目的已用尺寸，不含间距），
/// <c>grid = { columns, rows }</c>（各轨道已用尺寸，不含间距）。<c>invalidatedBy</c> 由 UI 层适配器记录最近一次
/// 标脏来源（来源节点 id 与属性名），是 UI 侧的近似而非布局引擎内部的失效原因；来源被清除后省略。</para>
/// <para><b>事件</b>（发给启用了 Layout 的会话，带 <c>target</c>）：<c>layoutPass { frameId, durationMs, layoutRan, nodeCount }</c>。
/// 只在帧管线真的完成一次布局计算时推送（缓存的帧没有事件）：<c>frameId</c> 是 UI 根单调递增的帧号
/// （<c>UIRoot.FrameNumber</c>，不是 <c>lastLayoutFrame</c> 的 DisplayList 版本），<c>durationMs</c> 是本帧布局阶段墙钟耗时，
/// <c>layoutRan</c> 恒为 true（保留字段以便前端按统一形状处理），<c>nodeCount</c> 是本次布局节点树的节点数（含生成内容）。</para>
/// 节点存在但没有布局盒（<c>display: none</c>、尚未布局、文本节点）时两个方法都返回 -32602。
/// </summary>
public sealed class LayoutDevToolsDomain : DevToolsDomain
{
    internal LayoutDevToolsDomain() : base("Layout") { }

    /// <summary>
    /// 帧管线完成一次布局计算后由 UI 层调用：向启用了 Layout 的会话推送 <c>Layout.layoutPass</c>
    /// （形状 <c>{ frameId, durationMs, layoutRan, nodeCount }</c>）。
    /// <para><paramref name="frameId"/> 用 UI 根单调递增的帧号（不是 DisplayList 版本）；<paramref name="durationMs"/> 是本帧布局阶段墙钟耗时；
    /// <paramref name="layoutRan"/> 由调用方报告是否真的运行了布局引擎；<paramref name="nodeCount"/> 是布局节点树节点数（含生成内容）。
    /// 目标不属于本 Agent、或没有会话启用 Layout 时静默丢弃。</para>
    /// </summary>
    public void NotifyLayoutPass(DevToolsTarget target, long frameId, double durationMs, bool layoutRan, int nodeCount)
    {
        if (target.Agent != Agent) return;
        Emit("layoutPass", new JsonObject
        {
            ["frameId"] = frameId,
            ["durationMs"] = JsonValue.Create(durationMs),
            ["layoutRan"] = layoutRan,
            ["nodeCount"] = nodeCount,
        }, target.Id);
    }

    protected override void RegisterMethods(DevToolsMethodTable methods)
    {
        InspectionProtocol.Add(methods, "getBoxModel", GetBoxModel);
        InspectionProtocol.Add(methods, "getLayoutInfo", GetLayoutInfo);
    }

    static JsonValue? GetBoxModel(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        if (!tree.ContainsNode(nodeId)) throw InspectionProtocol.NodeNotFound(nodeId);
        var box = tree.GetBoxModel(nodeId) ?? throw DevToolsProtocolException.InvalidParams(
            $"Node {nodeId} has no layout box.", new JsonObject { ["nodeId"] = nodeId });
        var o = new JsonObject
        {
            ["content"] = InspectionProtocol.QuadJson(box.Content),
            ["padding"] = InspectionProtocol.QuadJson(box.Padding),
            ["border"] = InspectionProtocol.QuadJson(box.Border),
            ["margin"] = InspectionProtocol.QuadJson(box.Margin),
            ["width"] = InspectionProtocol.Number(box.Width),
            ["height"] = InspectionProtocol.Number(box.Height),
        };
        if (box.ScrollOffset is { } so)
            o["scrollOffset"] = new JsonObject { ["x"] = InspectionProtocol.Number(so.X), ["y"] = InspectionProtocol.Number(so.Y) };
        if (box.ScrollSize is { } ss)
            o["scrollSize"] = new JsonObject { ["width"] = InspectionProtocol.Number(ss.Width), ["height"] = InspectionProtocol.Number(ss.Height) };
        return o;
    }

    static JsonValue? GetLayoutInfo(DevToolsCallContext ctx)
    {
        var tree = InspectionProtocol.RequireTree(ctx);
        long nodeId = ctx.GetInt64("nodeId");
        if (!tree.ContainsNode(nodeId)) throw InspectionProtocol.NodeNotFound(nodeId);
        var info = tree.GetLayoutInfo(nodeId) ?? throw DevToolsProtocolException.InvalidParams(
            $"Node {nodeId} has no layout box.", new JsonObject { ["nodeId"] = nodeId });
        var o = new JsonObject
        {
            ["display"] = info.Display ?? "block",
            ["constraints"] = new JsonObject
            {
                ["minWidth"] = NullableNumber(info.MinWidth),
                ["maxWidth"] = NullableNumber(info.MaxWidth),
                ["minHeight"] = NullableNumber(info.MinHeight),
                ["maxHeight"] = NullableNumber(info.MaxHeight),
            },
            ["isLayoutBoundary"] = info.IsLayoutBoundary,
            ["lastLayoutFrame"] = info.LastLayoutFrame,
        };
        if (info.InvalidatedByNodeId != 0)
        {
            var by = new JsonObject { ["nodeId"] = info.InvalidatedByNodeId };
            if (info.InvalidatedByProperty != null) by["property"] = info.InvalidatedByProperty;
            o["invalidatedBy"] = by;
        }
        if (info.FlexTracksX is not null || info.FlexTracksY is not null)
            o["flex"] = new JsonObject
            {
                ["tracks"] = new JsonObject
                {
                    ["x"] = NumberArray(info.FlexTracksX),
                    ["y"] = NumberArray(info.FlexTracksY),
                },
            };
        if (info.GridColumns is not null || info.GridRows is not null)
            o["grid"] = new JsonObject
            {
                ["columns"] = NumberArray(info.GridColumns),
                ["rows"] = NumberArray(info.GridRows),
            };
        return o;
    }

    /// <summary>轨道尺寸数组；null 视为空数组（对应轴上没有轨道）。</summary>
    static JsonValue NumberArray(IReadOnlyList<float>? values)
    {
        var array = new JsonArray();
        if (values is not null)
            for (int i = 0; i < values.Count; i++) array.Add(InspectionProtocol.Number(values[i]));
        return array;
    }

    /// <summary>无法表示为固定像素的约束写成 null。</summary>
    static JsonValue NullableNumber(float? value) => value is { } v ? InspectionProtocol.Number(v) : JsonValue.Null;
}
