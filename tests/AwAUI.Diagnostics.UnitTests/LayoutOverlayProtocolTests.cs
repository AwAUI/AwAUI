using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>
/// <c>Layout.getLayoutInfo</c> 的轨道字段与省略规则，以及 <c>Overlay.setShowOptions</c> 的完整形状。
/// 使用只提供布局信息的内存树，不依赖 UI 层。
/// </summary>
public class LayoutOverlayProtocolTests
{
    sealed class LayoutTree : InspectableTree
    {
        public LayoutInfo Info = new();
        public override long RootId => 1;
        public override InspectableNodeInfo? DescribeNode(long nodeId) =>
            nodeId == 1 ? new InspectableNodeInfo(1, InspectableNodeKind.Element) { Tag = "div" } : null;
        public override bool GetChildren(long nodeId, List<long> children) => nodeId == 1;
        public override BoxModelInfo? GetBoxModel(long nodeId) =>
            nodeId == 1 ? new BoxModelInfo(new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10)) : null;
        public override void QuerySelectorAll(long scopeNodeId, string selector, List<long> results) { }
        public override LayoutInfo? GetLayoutInfo(long nodeId) => nodeId == 1 ? Info : null;
    }

    sealed class LayoutOverlay : OverlayHost
    {
        public OverlayShowOptions Last = new();
        public int Calls;
        public override bool HighlightNode(long nodeId, HighlightConfig config) => true;
        public override void HideHighlight() { }
        public override void SetInspectMode(InspectMode mode) { }
        public override void SetShowOptions(OverlayShowOptions options)
        {
            Last = options;
            Calls++;
        }
    }

    static (RpcClient Client, LayoutTree Tree, LayoutOverlay Overlay) Create()
    {
        var agent = new DevToolsAgent();
        var tree = new LayoutTree();
        var overlay = new LayoutOverlay();
        agent.AddTarget(new DevToolsTarget("w1", "Main", new Size(100, 100)) { Tree = tree, Overlay = overlay });
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        return (new RpcClient(c), tree, overlay);
    }

    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    [Fact]
    public async Task GetLayoutInfo_reports_flex_tracks_and_omits_grid()
    {
        var (c, tree, _) = Create();
        tree.Info = new LayoutInfo
        {
            Display = "flex",
            FlexTracksX = new[] { 100f, 200f },
            FlexTracksY = Array.Empty<float>(),
        };

        var r = await c.OkAsync("Layout.getLayoutInfo", Node(1));
        Assert.Equal("flex", r["display"]!.GetString());
        Assert.Equal(new double[] { 100, 200 }, r["flex"]!["tracks"]!["x"]!.AsArray().Select(v => v.GetDouble()));
        Assert.Empty(r["flex"]!["tracks"]!["y"]!.AsArray());
        // 不是 grid 容器：省略 grid；没有可靠的 invalidatedBy 数据：省略
        Assert.Null(r["grid"]);
        Assert.Null(r["invalidatedBy"]);
    }

    [Fact]
    public async Task GetLayoutInfo_reports_grid_tracks_and_invalidated_by()
    {
        var (c, tree, _) = Create();
        tree.Info = new LayoutInfo
        {
            Display = "grid",
            GridColumns = new[] { 50f, 70f },
            GridRows = new[] { 10f },
            InvalidatedByNodeId = 7,
            InvalidatedByProperty = "width",
        };

        var r = await c.OkAsync("Layout.getLayoutInfo", Node(1));
        Assert.Equal(new double[] { 50, 70 }, r["grid"]!["columns"]!.AsArray().Select(v => v.GetDouble()));
        Assert.Equal(new double[] { 10 }, r["grid"]!["rows"]!.AsArray().Select(v => v.GetDouble()));
        Assert.Null(r["flex"]);
        Assert.Equal(7, r["invalidatedBy"]!["nodeId"]!.GetInt64());
        Assert.Equal("width", r["invalidatedBy"]!["property"]!.GetString());

        // 只有节点 id、没有属性名时 property 省略
        tree.Info = new LayoutInfo { Display = "block", InvalidatedByNodeId = 9 };
        r = await c.OkAsync("Layout.getLayoutInfo", Node(1));
        Assert.Equal(9, r["invalidatedBy"]!["nodeId"]!.GetInt64());
        Assert.Null(r["invalidatedBy"]!["property"]);
        Assert.Null(r["flex"]);
        Assert.Null(r["grid"]);
    }

    [Fact]
    public async Task SetShowOptions_replaces_the_complete_option_shape()
    {
        var (c, _, overlay) = Create();
        await c.FailAsync("Overlay.setShowOptions", new JsonObject { ["rulers"] = true }, DevToolsErrorCodes.DomainNotEnabled);
        await c.OkAsync("Overlay.enable");

        await c.OkAsync("Overlay.setShowOptions", new JsonObject
        {
            ["rulers"] = true,
            ["hitTargets"] = true,
            ["touches"] = true,
            ["layoutGrids"] = new JsonArray { 3L, 4L },
        });
        Assert.True(overlay.Last.Rulers);
        Assert.True(overlay.Last.HitTargets);
        Assert.True(overlay.Last.Touches);
        Assert.Equal(new long[] { 3, 4 }, overlay.Last.LayoutGrids.ToArray());

        // 省略的字段取默认值（全部关闭 / 空数组）
        await c.OkAsync("Overlay.setShowOptions", new JsonObject { ["hitTargets"] = true });
        Assert.False(overlay.Last.Rulers);
        Assert.True(overlay.Last.HitTargets);
        Assert.False(overlay.Last.Touches);
        Assert.Empty(overlay.Last.LayoutGrids);
        Assert.True(overlay.Calls >= 2);
    }
}
