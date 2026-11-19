using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;

namespace AwAUI.Diagnostics.UnitTests;

public class TreeDomainTests
{
    static JsonObject P(string name, JsonValue value) => new() { [name] = value };
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    [Fact]
    public async Task Inspection_domains_are_built_in_and_listed_in_hello()
    {
        var f = new InspectionFixture();
        var client = f.Connect();
        var domains = (await client.HelloAsync())["result"]!["domains"]!.AsArray().Select(d => d.GetString()).ToArray();
        Assert.Equal(new[] { "App", "Console", "Tree", "Css", "Layout", "Overlay" }, domains);
        Assert.Same(f.Target, f.Tree.Target);
        Assert.Same(f.Agent, f.Target.Agent);
    }

    [Fact]
    public async Task GetDocument_returns_root_with_two_levels_by_default()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        var root = (await c.OkAsync("Tree.getDocument"))["root"]!;
        Assert.Equal(f.Tree.Root.Id, root.Id());
        Assert.Null(root["parentId"]);
        Assert.Equal("element", root["kind"]!.GetString());
        Assert.Equal("app", root["tag"]!.GetString());
        Assert.Equal("root", root["elementId"]!.GetString());
        Assert.Equal(3, root["childCount"]!.GetInt64());

        var kids = root["children"]!;
        Assert.Equal(new[] { f.Header.Id, f.Main.Id, f.Footer.Id }, kids.Ids());
        var header = kids[0];
        Assert.Equal(f.Tree.Root.Id, header["parentId"]!.GetInt64());
        Assert.Equal("a", header["classes"]![0].GetString());
        Assert.Equal(7, header["componentId"]!.GetInt64());
        Assert.Equal("Header", header["componentName"]!.GetString());
        Assert.Equal("Header.awaui", header["source"]!["file"]!.GetString());
        Assert.Equal(3, header["source"]!["line"]!.GetInt64());
        Assert.Equal(5, header["source"]!["column"]!.GetInt64());
        // 第二层：header 的文本子节点
        var title = header["children"]![0];
        Assert.Equal("text", title["kind"]!.GetString());
        Assert.Equal("Title", title["text"]!.GetString());
        Assert.Null(title["tag"]);
        Assert.Null(title["children"]);                 // 第三层不再展开
        var button = kids[1]["children"]![0];
        Assert.Equal("submit", button["attributes"]!["type"]!.GetString());
        Assert.Equal(new[] { "primary", "a" }, button["classes"]!.AsArray().Select(x => x.GetString()));
        Assert.Equal(1, button["childCount"]!.GetInt64());
        Assert.Null(button["children"]);
        // 叶子元素在可展开的层上得到空的 children（「已展开」）
        Assert.Empty(kids[2]["children"]!.AsArray());
    }

    [Fact]
    public async Task GetDocument_depth_zero_and_whole_tree()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        var root = (await c.OkAsync("Tree.getDocument", P("depth", 0)))["root"]!;
        Assert.Null(root["children"]);
        Assert.Equal(3, root["childCount"]!.GetInt64());

        root = (await c.OkAsync("Tree.getDocument", P("depth", -1)))["root"]!;
        Assert.Equal("OK", root["children"]![1]["children"]![0]["children"]![0]["text"]!.GetString());
    }

    [Theory]
    [InlineData("{\"depth\":-2}")]
    [InlineData("{\"depth\":\"x\"}")]
    [InlineData("{\"depth\":1.5}")]
    [InlineData("{\"depth\":4294967296}")]
    public async Task GetDocument_rejects_invalid_depth(string p)
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.FailAsync("Tree.getDocument", JsonValue.Parse(p).AsObject(), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Mirror_building_methods_require_enable()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.FailAsync("Tree.getDocument", null, DevToolsErrorCodes.DomainNotEnabled);
        await c.FailAsync("Tree.requestChildren", Node(f.Main.Id), DevToolsErrorCodes.DomainNotEnabled);
        await c.FailAsync("Tree.pushNodePath", Node(f.Ok.Id), DevToolsErrorCodes.DomainNotEnabled);
        // 只读方法不需要
        await c.OkAsync("Tree.describeNode", Node(f.Main.Id));
    }

    [Fact]
    public async Task Targets_without_a_tree_or_without_any_target()
    {
        var agent = new DevToolsAgent();
        var (a, cl) = Transport.InProcTransport.CreatePair();
        agent.Attach(a);
        var c = new RpcClient(cl);
        await c.OkAsync("Tree.enable");
        await c.FailAsync("Tree.getDocument", null, DevToolsErrorCodes.TargetNotFound);
        agent.AddTarget(new DevToolsTarget("w1", "Bare", new Size(1, 1)));
        var e = await c.FailAsync("Tree.getDocument", null, DevToolsErrorCodes.NotSupported);
        Assert.Equal("w1", e["data"]!["target"]!.GetString());
        await c.FailAsync("Css.getComputedStyle", Node(1), DevToolsErrorCodes.NotSupported);
        await c.OkAsync("Overlay.enable");
        await c.FailAsync("Overlay.hideHighlight", null, DevToolsErrorCodes.NotSupported);
        await c.FailAsync("Tree.describeNode", Node(1), DevToolsErrorCodes.TargetNotFound, target: "nope");
    }

    [Fact]
    public async Task RequestChildren_expands_sent_nodes()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", 1));
        var kids = (await c.OkAsync("Tree.requestChildren", Node(f.Main.Id)))["children"]!;
        Assert.Equal(new[] { f.Button.Id, f.Span.Id }, kids.Ids());
        Assert.Null(kids[0]["children"]);

        kids = (await c.OkAsync("Tree.requestChildren", new JsonObject { ["nodeId"] = f.Main.Id, ["depth"] = -1 }))["children"]!;
        Assert.Equal(f.Ok.Id, kids[0]["children"]![0].Id());

        // 新发送的按钮也可以直接请求
        Assert.Single((await c.OkAsync("Tree.requestChildren", Node(f.Button.Id)))["children"]!.AsArray());
    }

    [Fact]
    public async Task RequestChildren_errors()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        // 还没有 getDocument：节点未发送
        var e = await c.FailAsync("Tree.requestChildren", Node(f.Main.Id), DevToolsErrorCodes.InvalidParams);
        Assert.Equal(f.Main.Id, e["data"]!["nodeId"]!.GetInt64());
        await c.OkAsync("Tree.getDocument", P("depth", 1));
        await c.FailAsync("Tree.requestChildren", Node(f.Button.Id), DevToolsErrorCodes.InvalidParams);   // 存在但未发送
        e = await c.FailAsync("Tree.requestChildren", Node(999_999), DevToolsErrorCodes.NodeNotFound);
        Assert.Equal(999_999, e["data"]!["nodeId"]!.GetInt64());
        await c.FailAsync("Tree.requestChildren", new JsonObject { ["nodeId"] = f.Main.Id, ["depth"] = 0 }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Tree.requestChildren", null, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Tree.requestChildren", P("nodeId", "12"), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task DescribeNode_and_errors()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        var n = (await c.OkAsync("Tree.describeNode", Node(f.Button.Id)))["node"]!;
        Assert.Equal("button", n["tag"]!.GetString());
        Assert.Equal(f.Main.Id, n["parentId"]!.GetInt64());
        Assert.Null(n["children"]);
        await c.FailAsync("Tree.describeNode", Node(424242), DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Tree.describeNode", null, DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Long_text_is_truncated_and_marked()
    {
        var f = new InspectionFixture(new DevToolsAgentOptions { MaxNodeTextLength = 4 });
        f.Title.Text = "abc\U0001F600xyz";   // 截断点落在代理对中间
        var c = f.Connect();
        var n = (await c.OkAsync("Tree.describeNode", Node(f.Title.Id)))["node"]!;
        Assert.Equal("abc", n["text"]!.GetString());
        Assert.True(n["textTruncated"]!.GetBoolean());
        Assert.Null((await c.OkAsync("Tree.describeNode", Node(f.Ok.Id)))["node"]!["textTruncated"]);
    }

    [Fact]
    public async Task PushNodePath_expands_ancestors_once()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.FailAsync("Tree.pushNodePath", Node(f.Ok.Id), DevToolsErrorCodes.InvalidParams);     // 先要 getDocument
        await c.OkAsync("Tree.getDocument", P("depth", 0));
        var r = await c.OkAsync("Tree.pushNodePath", Node(f.Ok.Id));
        Assert.Equal(new[] { f.Tree.Root.Id, f.Main.Id, f.Button.Id, f.Ok.Id }, r["path"]!.Longs());
        var groups = r["children"]!.AsArray();
        Assert.Equal(new[] { f.Tree.Root.Id, f.Main.Id, f.Button.Id }, groups.Select(g => g["parentId"]!.GetInt64()));
        Assert.Equal(new[] { f.Button.Id, f.Span.Id }, groups[1]["children"]!.Ids());

        // 路径已展开：再推一次不返回任何子节点；推兄弟节点只需要展开 header
        Assert.Empty((await c.OkAsync("Tree.pushNodePath", Node(f.Ok.Id)))["children"]!.AsArray());
        r = await c.OkAsync("Tree.pushNodePath", Node(f.Title.Id));
        Assert.Equal(f.Header.Id, Assert.Single(r["children"]!.AsArray())["parentId"]!.GetInt64());

        await c.FailAsync("Tree.pushNodePath", Node(31337), DevToolsErrorCodes.NodeNotFound);
        // 路径上的节点现在都会收到事件
        f.Tree.SetText(f.Ok, "Done");
        f.Agent.FlushFrame();
        Assert.Equal("Done", c.Events("Tree.textChanged").Single()["params"]!["text"]!.GetString());
    }

    [Fact]
    public async Task QuerySelector_and_QuerySelectorAll()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        var root = f.Tree.Root.Id;
        Assert.Equal(f.Button.Id, (await c.OkAsync("Tree.querySelector", new JsonObject { ["nodeId"] = root, ["selector"] = "button.primary" }))["nodeId"]!.GetInt64());
        Assert.Null((await c.OkAsync("Tree.querySelector", new JsonObject { ["nodeId"] = root, ["selector"] = "nav" }))["nodeId"]);
        var all = await c.OkAsync("Tree.querySelectorAll", new JsonObject { ["nodeId"] = root, ["selector"] = ".a" });
        Assert.Equal(new[] { f.Header.Id, f.Button.Id }, all["nodeIds"]!.Longs());
        Assert.Null(all["truncated"]);
        // 作用域只含后代
        Assert.Empty((await c.OkAsync("Tree.querySelectorAll", new JsonObject { ["nodeId"] = f.Header.Id, ["selector"] = ".a" }))["nodeIds"]!.AsArray());

        await c.FailAsync("Tree.querySelector", new JsonObject { ["nodeId"] = root, ["selector"] = "div >" }, DevToolsErrorCodes.ValueParseError);
        await c.FailAsync("Tree.querySelectorAll", new JsonObject { ["nodeId"] = root, ["selector"] = "" }, DevToolsErrorCodes.ValueParseError);
        await c.FailAsync("Tree.querySelector", new JsonObject { ["nodeId"] = 5, ["selector"] = "*" }, DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Tree.querySelectorAll", new JsonObject { ["nodeId"] = 5, ["selector"] = "*" }, DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Tree.querySelectorAll", Node(root), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task QuerySelectorAll_is_bounded()
    {
        var f = new InspectionFixture(new DevToolsAgentOptions { MaxTreeNodesPerResponse = 3 });
        var c = f.Connect();
        var all = await c.OkAsync("Tree.querySelectorAll", new JsonObject { ["nodeId"] = f.Tree.Root.Id, ["selector"] = "*" });
        Assert.Equal(3, all["nodeIds"]!.AsArray().Count);
        Assert.True(all["truncated"]!.GetBoolean());
    }

    [Fact]
    public async Task GetNodeAt_hit_tests()
    {
        var f = new InspectionFixture();
        f.Button.Attributes.Add(new("pointer-events", "none"));
        var c = f.Connect();
        Assert.Equal(f.Main.Id, (await c.OkAsync("Tree.getNodeAt", new JsonObject { ["x"] = 20, ["y"] = 40 }))["nodeId"]!.GetInt64());
        Assert.Equal(f.Button.Id, (await c.OkAsync("Tree.getNodeAt", new JsonObject { ["x"] = 20.5, ["y"] = 40, ["ignorePointerEvents"] = true }))["nodeId"]!.GetInt64());
        Assert.Null((await c.OkAsync("Tree.getNodeAt", new JsonObject { ["x"] = 5000, ["y"] = 5000 }))["nodeId"]);
        await c.FailAsync("Tree.getNodeAt", P("x", 1), DevToolsErrorCodes.InvalidParams);
        await c.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":77,\"method\":\"Tree.getNodeAt\",\"params\":{\"x\":1e999,\"y\":0}}");
        Rpc.Error(await c.WaitForAsync(m => m["id"] is JsonNumber n && n.GetInt64() == 77), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Optional_tree_methods_are_not_supported_by_default()
    {
        var agent = new DevToolsAgent();
        agent.AddTarget(new DevToolsTarget("w", "W", new Size(1, 1)) { Tree = new MinimalTree() });
        var (a, cl) = Transport.InProcTransport.CreatePair();
        agent.Attach(a);
        var c = new RpcClient(cl);
        await c.FailAsync("Tree.getNodeAt", new JsonObject { ["x"] = 1, ["y"] = 1 }, DevToolsErrorCodes.NotSupported);
        await c.FailAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = 1, ["name"] = "a", ["value"] = "b" }, DevToolsErrorCodes.NotSupported);
        await c.FailAsync("Tree.removeAttribute", new JsonObject { ["nodeId"] = 1, ["name"] = "a" }, DevToolsErrorCodes.NotSupported);
        await c.FailAsync("Tree.setClasses", new JsonObject { ["nodeId"] = 1, ["classes"] = new JsonArray() }, DevToolsErrorCodes.NotSupported);
        // 默认的 ContainsNode / QuerySelector 由抽象成员实现
        Assert.Null((await c.OkAsync("Tree.querySelector", new JsonObject { ["nodeId"] = 1, ["selector"] = "x" }))["nodeId"]);
        await c.FailAsync("Tree.querySelector", new JsonObject { ["nodeId"] = 2, ["selector"] = "x" }, DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Layout.getBoxModel", Node(1), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Attribute_editing_round_trips_through_events()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", -1));
        await c.OkAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Button.Id, ["name"] = "title", ["value"] = "Go" });
        await c.OkAsync("Tree.removeAttribute", new JsonObject { ["nodeId"] = f.Button.Id, ["name"] = "type" });
        await c.OkAsync("Tree.setClasses", new JsonObject { ["nodeId"] = f.Button.Id, ["classes"] = new JsonArray { "x", "y" } });
        await c.OkAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Button.Id, ["name"] = "id", ["value"] = "go" });
        Assert.Empty(c.Events("Tree.attributesChanged"));          // 按帧合并
        f.Agent.FlushFrame();
        var e = c.Events("Tree.attributesChanged").Single()["params"]!;
        Assert.Equal(f.Button.Id, e["nodeId"]!.GetInt64());
        Assert.Equal("Go", e["attributes"]!["title"]!.GetString());
        Assert.True(e["attributes"]!["type"]!.IsNull);
        Assert.Equal(new[] { "x", "y" }, e["classes"]!.AsArray().Select(x => x.GetString()));
        Assert.Equal("go", e["elementId"]!.GetString());
        Assert.Null(e["attributes"]!["id"]);

        await c.FailAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = 9, ["name"] = "a", ["value"] = "b" }, DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Button.Id, ["name"] = "", ["value"] = "b" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Button.Id, ["name"] = "a b", ["value"] = "b" }, DevToolsErrorCodes.ValueParseError);
        await c.FailAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Button.Id, ["name"] = "a" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Tree.removeAttribute", new JsonObject { ["nodeId"] = 9, ["name"] = "a" }, DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Tree.setClasses", new JsonObject { ["nodeId"] = 9, ["classes"] = new JsonArray() }, DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Tree.setClasses", new JsonObject { ["nodeId"] = f.Button.Id, ["classes"] = new JsonArray { 1 } }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Tree.setClasses", new JsonObject { ["nodeId"] = f.Button.Id, ["classes"] = "x" }, DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task GetOuterText_renders_indented_markup()
    {
        var f = new InspectionFixture();
        f.Ok.Text = "O\"K\n";
        var c = f.Connect();
        string text = (await c.OkAsync("Tree.getOuterText", Node(f.Main.Id)))["text"]!.GetString();
        Assert.Equal(
            "<main>\n" +
            "  <button class=\"primary a\" type=\"submit\">\n" +
            "    \"O\\\"K\\n\"\n" +
            "  <span>\n", text);

        text = (await c.OkAsync("Tree.getOuterText", new JsonObject { ["nodeId"] = f.Footer.Id, ["includeStyles"] = "key" }))["text"]!.GetString();
        Assert.Equal("<footer style=\"color: #ff0000; display: block\">\n", text);
        text = (await c.OkAsync("Tree.getOuterText", new JsonObject { ["nodeId"] = f.Footer.Id, ["includeStyles"] = "all" }))["text"]!.GetString();
        Assert.Contains("z-index: auto", text);

        await c.FailAsync("Tree.getOuterText", Node(1), DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Tree.getOuterText", new JsonObject { ["nodeId"] = f.Main.Id, ["includeStyles"] = "some" }, DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task GetOuterText_is_bounded()
    {
        var f = new InspectionFixture(new DevToolsAgentOptions { MaxTreeNodesPerResponse = 2 });
        var c = f.Connect();
        string text = (await c.OkAsync("Tree.getOuterText", Node(f.Tree.Root.Id)))["text"]!.GetString();
        Assert.Equal("<app id=\"root\">\n  <header class=\"a\">\n… (truncated)\n", text);
    }

    // ================================================================ 事件

    [Fact]
    public async Task Insertions_under_expanded_parents_push_childrenChanged()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", 2));
        c.ClearMessages();

        var nav = f.Tree.Create("nav");
        nav.Classes.Add("top");
        f.Tree.Insert(f.Tree.Root, nav, before: f.Main);
        var e = c.Events("Tree.childrenChanged").Single();
        Assert.Equal("w1", e["target"]!.GetString());
        var p = e["params"]!;
        Assert.Equal(f.Tree.Root.Id, p["parentId"]!.GetInt64());
        Assert.Empty(p["removed"]!.AsArray());
        var ins = p["inserted"]![0];
        Assert.Equal(nav.Id, ins["node"]!.Id());
        Assert.Equal("top", ins["node"]!["classes"]![0].GetString());
        Assert.Equal(f.Main.Id, ins["beforeId"]!.GetInt64());

        // 插到末尾不带 beforeId；新节点之下的插入也推送（它已发送，且没有子节点前不算展开 → childCountUpdated）
        var last = f.Tree.Insert(f.Tree.Root, f.Tree.Create("aside"));
        Assert.Null(c.Events("Tree.childrenChanged").Last()["params"]!["inserted"]![0]["beforeId"]);
        f.Tree.Insert(last, f.Tree.CreateText("hi"));
        Assert.Equal(2, c.Events("Tree.childrenChanged").Count);
        f.Agent.FlushFrame();
        var cc = c.Events("Tree.childCountUpdated").Single()["params"]!;
        Assert.Equal(last.Id, cc["nodeId"]!.GetInt64());
        Assert.Equal(1, cc["childCount"]!.GetInt64());

        // 未发送的父节点（button 的文本节点之下不存在；button 本身在 depth 2 时已发送但未展开）
        f.Tree.Insert(f.Ok.Parent!, f.Tree.CreateText("!"));
        f.Agent.FlushFrame();
        Assert.Equal(2, c.Events("Tree.childCountUpdated").Count);
        Assert.Equal(2, c.Events("Tree.childrenChanged").Count);
        // 完全未发送的父节点：没有任何事件
        var deep = f.Tree.Add(f.Ok.Parent!, f.Tree.Create("i"));
        f.Tree.Insert(deep, f.Tree.Create("b"));
        f.Agent.FlushFrame();
        Assert.Equal(2, c.Events("Tree.childCountUpdated").Count);
    }

    [Fact]
    public async Task Removal_drops_the_subtree_and_its_pending_changes()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", -1));
        f.Tree.SetText(f.Ok, "changed");          // 排队
        f.Tree.Remove(f.Main);
        var e = c.Events("Tree.childrenChanged").Single()["params"]!;
        Assert.Equal(new[] { f.Main.Id }, e["removed"]!.Longs());
        Assert.Empty(e["inserted"]!.AsArray());
        f.Agent.FlushFrame();
        Assert.Empty(c.Events("Tree.textChanged"));
        // 被移除的节点不再被跟踪：重新插入时以新节点（无 children）发送
        f.Tree.Insert(f.Tree.Root, f.Main);
        var node = c.Events("Tree.childrenChanged").Last()["params"]!["inserted"]![0]["node"]!;
        Assert.Equal(f.Main.Id, node.Id());
        Assert.Null(node["children"]);
        await c.FailAsync("Tree.requestChildren", Node(f.Button.Id), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Moves_keep_the_subtree()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", -1));
        f.Tree.Move(f.Footer, f.Header);
        f.Tree.Move(f.Header, null);
        var moves = c.Events("Tree.childMoved").Select(m => m["params"]!).ToArray();
        Assert.Equal(f.Footer.Id, moves[0]["nodeId"]!.GetInt64());
        Assert.Equal(f.Header.Id, moves[0]["beforeId"]!.GetInt64());
        Assert.Null(moves[1]["beforeId"]);
        // 子树仍被跟踪
        f.Tree.SetText(f.Title, "T2");
        f.Agent.FlushFrame();
        Assert.Single(c.Events("Tree.textChanged"));
    }

    [Fact]
    public async Task Node_changes_are_coalesced_per_frame()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", -1));
        for (int i = 0; i < 50; i++)
        {
            f.Tree.SetText(f.Ok, "n" + i);
            f.Tree.SetPseudo(f.Button, i % 2 == 0 ? "hover" : "active");
            f.Tree.SetAttr(f.Button, "data-i", i.ToString());
        }
        Assert.Empty(c.Events("Tree.textChanged"));
        f.Agent.FlushFrame();
        Assert.Equal("n49", c.Events("Tree.textChanged").Single()["params"]!["text"]!.GetString());
        Assert.Equal("active", c.Events("Tree.pseudoStateChanged").Single()["params"]!["states"]![0].GetString());
        Assert.Equal("49", c.Events("Tree.attributesChanged").Single()["params"]!["attributes"]!["data-i"]!.GetString());
        f.Agent.FlushFrame();
        Assert.Single(c.Events("Tree.textChanged"));
    }

    [Fact]
    public async Task Document_updates_reset_the_mirror()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", -1));
        var newRoot = f.Tree.Create("app2");
        f.Tree.ReplaceRoot(newRoot);
        Assert.Equal("w1", c.Events("Tree.documentUpdated").Single()["target"]!.GetString());
        f.Tree.Insert(newRoot, f.Tree.Create("div"));
        Assert.Empty(c.Events("Tree.childrenChanged"));
        Assert.Equal(newRoot.Id, (await c.OkAsync("Tree.getDocument"))["root"]!.Id());
        f.Tree.Insert(newRoot, f.Tree.Create("div"));
        Assert.Single(c.Events("Tree.childrenChanged"));

        // 替换接缝本身也是文档更新
        f.Target.Tree = new FakeTree();
        Assert.Equal(2, c.Events("Tree.documentUpdated").Count);
        Assert.Throws<InvalidOperationException>(() => new DevToolsTarget("x", "x", default) { Tree = f.Target.Tree });
    }

    [Fact]
    public async Task Disable_stops_events_and_requires_a_new_document()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", -1));
        await c.OkAsync("Tree.disable");
        f.Tree.Insert(f.Tree.Root, f.Tree.Create("x"));
        f.Tree.SetText(f.Ok, "q");
        f.Agent.FlushFrame();
        Assert.Empty(c.Events("Tree.childrenChanged"));
        Assert.Empty(c.Events("Tree.textChanged"));
        await c.OkAsync("Tree.enable");
        f.Tree.Insert(f.Tree.Root, f.Tree.Create("y"));
        Assert.Empty(c.Events("Tree.childrenChanged"));
        await c.FailAsync("Tree.requestChildren", Node(f.Main.Id), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Sessions_have_independent_mirrors()
    {
        var f = new InspectionFixture();
        var a = f.Connect(out var sa);
        var b = f.Connect();
        await a.OkAsync("Tree.enable");
        await b.OkAsync("Tree.enable");
        await a.OkAsync("Tree.getDocument", P("depth", 1));
        await b.OkAsync("Tree.getDocument", P("depth", 0));
        f.Tree.Insert(f.Tree.Root, f.Tree.Create("nav"));
        f.Agent.FlushFrame();
        Assert.Single(a.Events("Tree.childrenChanged"));
        Assert.Empty(a.Events("Tree.childCountUpdated"));
        Assert.Empty(b.Events("Tree.childrenChanged"));
        Assert.Equal(4, b.Events("Tree.childCountUpdated").Single()["params"]!["childCount"]!.GetInt64());

        // A 断开不影响 B
        sa.Transport.Dispose();
        await b.OkAsync("Tree.requestChildren", Node(f.Tree.Root.Id));
        f.Tree.Insert(f.Tree.Root, f.Tree.Create("nav2"));
        Assert.Single(b.Events("Tree.childrenChanged"));
        Assert.Single(a.Events("Tree.childrenChanged"));
    }

    [Fact]
    public async Task Multiple_targets_are_addressed_by_target_id()
    {
        var f = new InspectionFixture();
        var tree2 = new FakeTree();
        var t2 = new DevToolsTarget("w2", "Second", new Size(10, 10)) { Tree = tree2 };
        f.Agent.AddTarget(t2);
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument");
        Assert.Equal(tree2.Root.Id, (await c.OkAsync("Tree.getDocument", null, "w2"))["root"]!.Id());
        tree2.Insert(tree2.Root, tree2.Create("p"));
        Assert.Equal("w2", c.Events("Tree.childrenChanged").Single()["target"]!.GetString());
        Assert.True(f.Agent.RemoveTarget("w2"));
        Assert.Null(t2.Agent);
        tree2.Insert(tree2.Root, tree2.Create("p"));
        Assert.Single(c.Events("Tree.childrenChanged"));
        await c.FailAsync("Tree.getDocument", null, DevToolsErrorCodes.TargetNotFound, "w2");
        Assert.Throws<InvalidOperationException>(() => new DevToolsAgent().AddTarget(f.Target));
    }

    // ================================================================ 规模与恶意输入

    [Fact]
    public async Task Responses_are_bounded_by_node_budget_and_depth()
    {
        var f = new InspectionFixture(new DevToolsAgentOptions { MaxTreeNodesPerResponse = 100, MaxTreeDepthPerResponse = 3 });
        // 一条 1000 层的链 + 一个 500 个子节点的宽节点
        var chainTop = f.Tree.Add(f.Footer, "chain");
        var cur = chainTop;
        for (int i = 0; i < 1000; i++) cur = f.Tree.Add(cur, "c");
        var wide = f.Tree.Add(f.Span, "wide");
        for (int i = 0; i < 500; i++) f.Tree.Add(wide, "w");
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        var root = (await c.OkAsync("Tree.getDocument", P("depth", -1)))["root"]!;
        // 3 层之后不再展开
        var level3 = root["children"]![2]["children"]![0]["children"]![0];
        Assert.Null(level3["children"]);
        Assert.Equal(1, level3["childCount"]!.GetInt64());
        // 宽节点超出预算：只带 childCount
        var span = (await c.OkAsync("Tree.requestChildren", new JsonObject { ["nodeId"] = f.Main.Id, ["depth"] = -1 }))["children"]![1];
        Assert.Null(span["children"]![0]["children"]);
        // 被请求节点自己的直接子节点总是完整返回
        await c.OkAsync("Tree.pushNodePath", Node(wide.Id));
        Assert.Equal(500, (await c.OkAsync("Tree.requestChildren", Node(wide.Id)))["children"]!.AsArray().Count);
    }

    [Fact]
    public async Task Deep_and_wide_trees_are_handled_iteratively()
    {
        var f = new InspectionFixture(new DevToolsAgentOptions { MaxTreeNodesPerResponse = 1_000_000 });
        var cur = f.Footer;
        for (int i = 0; i < 100_000; i++) cur = f.Tree.Add(cur, "d");
        var wide = f.Tree.Add(f.Span, "wide");
        for (int i = 0; i < 100_000; i++) f.Tree.Add(wide, "w");
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await c.OkAsync("Tree.getDocument", P("depth", -1));                     // 深度受限，JSON 嵌套可被默认读取器解析
        var path = await c.OkAsync("Tree.pushNodePath", Node(cur.Id));
        Assert.Equal(100_000 + 2, path["path"]!.AsArray().Count);
        await c.OkAsync("Tree.getOuterText", Node(f.Tree.Root.Id));
        await c.OkAsync("Tree.pushNodePath", Node(wide.Id));
        Assert.Equal(100_000, (await c.OkAsync("Tree.requestChildren", Node(wide.Id)))["children"]!.AsArray().Count);
        // 移除一个已发送的大子树是线性的
        f.Tree.Remove(f.Footer);
        f.Tree.Remove(f.Main);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task Pending_changes_stay_bounded_without_frames()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", P("depth", -1));
        // 宿主从不调用 FlushFrame：反复插入、修改、移除
        for (int i = 0; i < 20_000; i++)
        {
            var n = f.Tree.Insert(f.Footer, f.Tree.Create("tmp"));
            f.Tree.SetAttr(n, "k", "v");
            f.Tree.Remove(n);
        }
        f.Agent.FlushFrame();
        Assert.Empty(c.Events("Tree.attributesChanged"));
        Assert.Equal(40_000, c.Events("Tree.childrenChanged").Count);
    }
}
