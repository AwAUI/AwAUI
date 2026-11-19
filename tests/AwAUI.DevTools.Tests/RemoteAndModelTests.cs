using System.Diagnostics;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Transport;
using AwAUI.UI;

namespace AwAUI.DevTools.Tests;

/// <summary>同一个面板经 WebSocket 连到「远程」应用（这里是同进程里监听回环端口的应用窗口）。</summary>
public class RemotePanelTests
{
    [Fact]
    public void Panel_attaches_to_a_remote_app_over_websocket()
    {
        using var h = new Harness(attach: false);
        var dt = h.App.EnableDevTools(new UIDevToolsOptions { ListenWebSocket = true, Port = 0, AppName = "RemoteCounter" });
        var transport = Connect(dt.WebSocketEndpoint!);
        using var panel = DevToolsPanel.Connect(transport, h.Platform, h.Backend, h.TextEngine);
        panel.Open();
        var model = panel.Model!;
        // 协议请求在应用的 UI 线程（无窗口平台的调度器）上处理，响应经套接字回到面板的调度器：泵送直到加载完成
        Pump(h, () => model.Loaded);
        Assert.Equal("RemoteCounter", model.AppName);
        var count = h.AppById("count");
        Assert.NotNull(model.Tree.Get(count.UniqueId));

        // 选中 → 应用里高亮；改颜色 → 应用像素变化
        model.Select(model.Tree.Get(count.UniqueId));
        Pump(h, () => ReferenceEquals(dt.HighlightedElement, count) && model.Styles is not null);
        var rule = model.Styles!.Sections[0].Blocks.Single(b => b.Selector == ".count");
        model.BeginEdit(rule, rule.Decls.FindIndex(d => d.Name == "color"));
        model.CommitEdit("#ff0000");
        Pump(h, () => h.CountAppPixels(count.BoundingRect, Harness.IsRed) > 20);

        // 应用侧的变化经事件推送到远程面板
        h.ClickApp(h.AppById("increment"));
        Pump(h, () => model.Tree.Get(count.UniqueId)?.InlineText?.Text == "Count: 1");
    }

    /// <summary>连接不需要 UI 线程（握手在线程池上完成），同步等待即可；测试方法保持同步，始终在调度器的线程上。</summary>
    static WebSocketClientTransport Connect(Uri uri) => WebSocketClientTransport.ConnectAsync(uri).GetAwaiter().GetResult();

    static void Pump(Harness h, Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Remote panel did not reach the expected state.");
            h.Frames();
            Thread.Sleep(2);
        }
        h.Settle();
    }
}

/// <summary>镜像对不可信 / 病态输入的处理：重复与成环的 id、极深的树、节点上限、未知节点的事件。</summary>
public class TreeMirrorTests
{
    static JsonObject Node(long id, string tag = "div", params JsonObject[] children)
    {
        var o = new JsonObject { ["id"] = id, ["kind"] = "element", ["tag"] = tag, ["childCount"] = children.Length };
        if (children.Length > 0)
        {
            var arr = new JsonArray();
            foreach (var c in children) arr.Add(c);
            o["children"] = arr;
        }
        return o;
    }

    [Fact]
    public void Duplicate_ids_are_ignored_so_the_tree_stays_acyclic()
    {
        var m = new TreeMirror();
        m.LoadDocument(Node(1, "root", Node(2, "a", Node(1, "loop")), Node(2, "dup"), Node(3, "b")));
        Assert.Equal(3, m.Count);
        Assert.Equal(new long[] { 2, 3 }, m.Get(1)!.Children!.Select(c => c.Id));
        Assert.Empty(m.Get(2)!.Children!);
        m.AutoExpand(100);
        Assert.Equal(3, m.Rows.Count);
    }

    [Fact]
    public void Very_deep_trees_are_handled_iteratively()
    {
        // 20 万层：建立、展开、摊平、删除都不能递归
        const int depth = 200_000;
        var m = new TreeMirror();
        m.LoadDocument(Node(1, "root"));
        var parent = m.Get(1)!;
        for (long id = 2; id <= depth; id++)
        {
            Assert.True(m.SetChildren(parent.Id, new JsonArray { Node(id) }));
            parent.Expanded = true;
            parent = m.Get(id)!;
        }
        m.ExpansionChanged();
        Assert.Equal(depth, m.Rows.Count);
        Assert.Equal(depth - 1, m.RowOf(depth));
        Assert.True(m.Apply("childrenChanged", new JsonObject { ["parentId"] = 1, ["removed"] = new JsonArray { 2 }, ["inserted"] = new JsonArray() }));
        Assert.Equal(1, m.Count);
        Assert.Single(m.Rows);
    }

    [Fact]
    public void Node_count_is_bounded()
    {
        var m = new TreeMirror { MaxNodes = 100 };
        var kids = Enumerable.Range(2, 500).Select(i => Node(i)).ToArray();
        m.LoadDocument(Node(1, "root", kids));
        Assert.Equal(100, m.Count);
        Assert.True(m.Truncated);
    }

    [Fact]
    public void Events_for_unknown_or_unloaded_nodes_are_ignored_and_known_ones_apply()
    {
        var m = new TreeMirror();
        m.LoadDocument(Node(1, "root", Node(2, "a"), Node(3, "b")));
        Assert.False(m.Apply("childrenChanged", new JsonObject { ["parentId"] = 99, ["removed"] = new JsonArray(), ["inserted"] = new JsonArray() }));
        Assert.False(m.Apply("textChanged", new JsonObject { ["nodeId"] = 42, ["text"] = "x" }));
        Assert.False(m.Apply("bogus", new JsonObject()));
        Assert.False(m.Apply("childMoved", new JsonObject { ["parentId"] = 1, ["nodeId"] = "nope" }));
        // 插入到 3 之前、移动、属性
        Assert.True(m.Apply("childrenChanged", new JsonObject
        {
            ["parentId"] = 1,
            ["removed"] = new JsonArray(),
            ["inserted"] = new JsonArray { new JsonObject { ["node"] = Node(4, "c"), ["beforeId"] = 3 } },
        }));
        Assert.Equal(new long[] { 2, 4, 3 }, m.Get(1)!.Children!.Select(c => c.Id));
        Assert.True(m.Apply("childMoved", new JsonObject { ["parentId"] = 1, ["nodeId"] = 2 }));
        Assert.Equal(new long[] { 4, 3, 2 }, m.Get(1)!.Children!.Select(c => c.Id));
        Assert.True(m.Apply("attributesChanged", new JsonObject
        {
            ["nodeId"] = 4,
            ["attributes"] = new JsonObject { ["data-x"] = "1" },
            ["classes"] = new JsonArray { "k" },
            ["elementId"] = "c4",
        }));
        var n4 = m.Get(4)!;
        Assert.Equal("c4", n4.ElementId);
        Assert.Equal(new[] { "k" }, n4.Classes);
        Assert.Equal("1", n4.Attributes!.Single(a => a.Key == "data-x").Value);
        Assert.True(m.Apply("attributesChanged", new JsonObject { ["nodeId"] = 4, ["attributes"] = new JsonObject { ["data-x"] = null } }));
        Assert.Empty(n4.Attributes!);
        // 已加载的节点不接受 childCountUpdated（它的子节点由 childrenChanged 维护）
        Assert.False(m.Apply("childCountUpdated", new JsonObject { ["nodeId"] = 1, ["childCount"] = 9 }));
    }

    [Fact]
    public void Edited_declaration_blocks_round_trip_disabled_declarations()
    {
        var block = new StyleBlock { RuleId = "s1/0" };
        block.Decls.Add(new StyleDecl { Name = "color", Value = "red" });
        block.Decls.Add(new StyleDecl { Name = "margin", Value = "0", Important = true });
        Assert.Equal("color: blue; margin: 0 !important;", block.BuildText(0, new StyleDecl { Name = "color", Value = "blue" }));
        Assert.Equal("color: red;", block.BuildText(1, null));
        Assert.Equal("color: red; /* margin: 0 !important; */;", block.BuildText(1, new StyleDecl { Name = "margin", Value = "0", Important = true, Disabled = true }));
        Assert.Equal("color: red; margin: 0 !important; gap: 4px;", block.BuildText(-1, new StyleDecl { Name = "gap", Value = "4px" }));
    }
}
