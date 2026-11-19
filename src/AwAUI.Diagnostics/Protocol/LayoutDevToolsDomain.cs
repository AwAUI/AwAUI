using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// Layout 域（design/20 <c>Layout / Overlay</c>）。目前只有
/// <c>getBoxModel({ nodeId }) → { content, padding, border, margin: Quad, width, height, scrollOffset?: { x, y }, scrollSize?: { width, height } }</c>，
/// 数据源是 <see cref="Inspection.InspectableTree.GetBoxModel"/>；Quad 为窗口坐标的 8 个数（四个角，顺时针，从左上开始），width / height 为边框盒尺寸。
/// 节点存在但没有布局盒（<c>display: none</c>、尚未布局）时返回 -32602。<c>getLayoutInfo</c> 待布局探针接入后实现。
/// </summary>
public sealed class LayoutDevToolsDomain : DevToolsDomain
{
    internal LayoutDevToolsDomain() : base("Layout") { }

    protected override void RegisterMethods(DevToolsMethodTable methods)
    {
        InspectionProtocol.Add(methods, "getBoxModel", GetBoxModel);
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
}
