using System.Collections.Generic;
using System.Threading.Tasks;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>
/// <c>Layout.layoutPass</c> 事件：只在启用 Layout 域后推送，形状为 <c>{ frameId, durationMs, layoutRan, nodeCount }</c>，
/// 带目标 id。使用只提供布局信息的内存树，不依赖 UI 层。
/// </summary>
public class LayoutPassEventTests
{
    sealed class LayoutTree : InspectableTree
    {
        public override long RootId => 1;
        public override InspectableNodeInfo? DescribeNode(long nodeId) =>
            nodeId == 1 ? new InspectableNodeInfo(1, InspectableNodeKind.Element) { Tag = "div" } : null;
        public override bool GetChildren(long nodeId, List<long> children) => nodeId == 1;
        public override BoxModelInfo? GetBoxModel(long nodeId) =>
            nodeId == 1 ? new BoxModelInfo(new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10)) : null;
        public override void QuerySelectorAll(long scopeNodeId, string selector, List<long> results) { }
        public override LayoutInfo? GetLayoutInfo(long nodeId) => nodeId == 1 ? new LayoutInfo() : null;
    }

    static (DevToolsAgent Agent, RpcClient Client, DevToolsTarget Target) Create()
    {
        var agent = new DevToolsAgent();
        var target = new DevToolsTarget("w1", "Main", new Size(100, 100)) { Tree = new LayoutTree() };
        agent.AddTarget(target);
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        return (agent, new RpcClient(c), target);
    }

    [Fact]
    public async Task Layout_pass_is_only_sent_after_layout_is_enabled_and_has_the_minimal_shape()
    {
        var (agent, client, target) = Create();

        // 未启用：即使完成布局也不推送
        agent.Layout.NotifyLayoutPass(target, 5, 1.5, layoutRan: true, nodeCount: 10);
        Assert.Empty(client.Events("Layout.layoutPass"));

        await client.OkAsync("Layout.enable");
        agent.Layout.NotifyLayoutPass(target, 6, 2.25, layoutRan: true, nodeCount: 42);

        var e = Assert.Single(client.Events("Layout.layoutPass"));
        Assert.Equal("w1", e["target"]!.GetString());
        var p = e["params"]!;
        Assert.Equal(6, p["frameId"]!.GetInt64());
        Assert.Equal(2.25, p["durationMs"]!.GetDouble());
        Assert.True(p["layoutRan"]!.GetBoolean());
        Assert.Equal(42, p["nodeCount"]!.GetInt64());
    }

    [Fact]
    public async Task Layout_pass_is_not_sent_to_a_session_that_disabled_again()
    {
        var (agent, client, target) = Create();
        await client.OkAsync("Layout.enable");
        await client.OkAsync("Layout.disable");
        agent.Layout.NotifyLayoutPass(target, 7, 0.5, layoutRan: true, nodeCount: 1);
        Assert.Empty(client.Events("Layout.layoutPass"));
    }
}
