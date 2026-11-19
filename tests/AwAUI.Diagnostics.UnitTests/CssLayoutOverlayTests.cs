using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;

namespace AwAUI.Diagnostics.UnitTests;

public class CssDomainTests
{
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    [Fact]
    public async Task GetMatchedRules_shape()
    {
        var f = new InspectionFixture();
        f.Styles.Inline[f.Tree.Root.Id] = new() { new StyleDeclarationInfo("margin", "0") { Important = true } };
        var c = f.Connect();
        var r = await c.OkAsync("Css.getMatchedRules", Node(f.Tree.Root.Id));
        var inline = r["inline"]![0];
        Assert.Equal("margin", inline["name"]!.GetString());
        Assert.Equal("0", inline["value"]!.GetString());
        Assert.True(inline["important"]!.GetBoolean());
        Assert.Equal("active", inline["status"]!.GetString());
        Assert.Null(inline["disabled"]);
        Assert.Empty(r["matched"]!.AsArray());       // 根没有 class a
        Assert.Empty(r["inherited"]!.AsArray());
        var pe = r["pseudoElements"]![0];
        Assert.Equal("before", pe["name"]!.GetString());
        Assert.Equal(new long[] { 1, 0, 1 }, pe["matched"]![0]["specificity"]!.Longs());

        r = await c.OkAsync("Css.getMatchedRules", Node(f.Button.Id));
        var m = r["matched"]![0];
        Assert.Equal(".a", m["matchedSelector"]!.GetString());
        Assert.Equal(new long[] { 0, 1, 0 }, m["specificity"]!.Longs());
        var rule = m["rule"]!;
        Assert.Equal("r1", rule["id"]!.GetString());
        Assert.Equal(".a, .b", rule["selectorText"]!.GetString());
        Assert.Equal("author", rule["origin"]!.GetString());
        Assert.Equal("base", rule["layer"]!.GetString());
        Assert.Equal("(min-width: 100px)", rule["media"]!.GetString());
        Assert.Equal("s1", rule["sheetId"]!.GetString());
        Assert.Equal("app.css", rule["source"]!["file"]!.GetString());
        var d0 = rule["declarations"]![0];
        Assert.Equal("overridden", d0["status"]!.GetString());
        Assert.Equal(5, d0["range"]!["start"]!.GetInt64());
        Assert.Equal(16, d0["range"]!["end"]!.GetInt64());
        var d1 = rule["declarations"]![1];
        Assert.Equal("inactive", d1["status"]!.GetString());
        Assert.Equal("Parent is not a flex container", d1["reason"]!.GetString());
        Assert.Equal(f.Main.Id, r["inherited"]![0]["fromNodeId"]!.GetInt64());

        await c.FailAsync("Css.getMatchedRules", Node(3), DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Css.getMatchedRules", null, DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Inline_and_computed_style()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Css.setStyleText", new JsonObject { ["target"] = Node(f.Span.Id), ["text"] = "color: blue; /* gap: 4px */; width: 10px !important" });
        var decls = (await c.OkAsync("Css.getInlineStyle", Node(f.Span.Id)))["declarations"]!.AsArray();
        Assert.Equal(new[] { "color", "gap", "width" }, decls.Select(d => d["name"]!.GetString()));
        Assert.True(decls[1]["disabled"]!.GetBoolean());
        Assert.True(decls[2]["important"]!.GetBoolean());
        Assert.Equal("10px", decls[2]["value"]!.GetString());
        Assert.Empty((await c.OkAsync("Css.getInlineStyle", Node(f.Main.Id)))["declarations"]!.AsArray());
        await c.FailAsync("Css.getInlineStyle", Node(4), DevToolsErrorCodes.NodeNotFound);

        var props = (await c.OkAsync("Css.getComputedStyle", Node(f.Button.Id)))["properties"]!.AsArray();
        Assert.Equal("#ff0000", props[0]["value"]!.GetString());
        Assert.Equal("r1", props[0]["from"]!["ruleId"]!.GetString());
        Assert.Equal("author", props[0]["from"]!["origin"]!.GetString());
        Assert.Equal("default", props[1]["from"]!["origin"]!.GetString());
        Assert.Equal(f.Main.Id, props[2]["from"]!["nodeId"]!.GetInt64());
        Assert.Null(props[3]["from"]);
        await c.FailAsync("Css.getComputedStyle", Node(4), DevToolsErrorCodes.NodeNotFound);
    }

    [Fact]
    public async Task SetStyleText_targets_and_errors()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Css.enable");
        var decls = (await c.OkAsync("Css.setStyleText", new JsonObject { ["target"] = new JsonObject { ["ruleId"] = "r1" }, ["text"] = "color: green" }))["declarations"]!;
        Assert.Equal("green", decls[0]["value"]!.GetString());
        Assert.Equal("s1", c.Events("Css.styleSheetChanged").Single()["params"]!["sheetId"]!.GetString());

        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = new JsonObject { ["ruleId"] = "nope" }, ["text"] = "a: b" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = Node(999), ["text"] = "a: b" }, DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = Node(f.Main.Id), ["text"] = "a { b }" }, DevToolsErrorCodes.ValueParseError);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = new JsonObject { ["nodeId"] = 1, ["ruleId"] = "r1" }, ["text"] = "" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = new JsonObject(), ["text"] = "" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = new JsonObject { ["nodeId"] = "1" }, ["text"] = "" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = 5, ["text"] = "" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["text"] = "" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.setStyleText", new JsonObject { ["target"] = Node(f.Main.Id) }, DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Style_sheets_and_rules()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Css.enable");
        var sheets = (await c.OkAsync("Css.getStyleSheets"))["sheets"]!.AsArray();
        var s1 = sheets.Single(s => s["sheetId"]!.GetString() == "s1");
        Assert.Equal("author", s1["origin"]!.GetString());
        Assert.Equal("app.css", s1["sourceUrl"]!.GetString());
        Assert.Equal("App", s1["title"]!.GetString());
        Assert.False(s1["disabled"]!.GetBoolean());
        Assert.True(s1["editable"]!.GetBoolean());
        Assert.False(sheets.Single(s => s["sheetId"]!.GetString() == "ua")["editable"]!.GetBoolean());

        Assert.Equal(".a { color: red; }", (await c.OkAsync("Css.getStyleSheetText", new JsonObject { ["sheetId"] = "s1" }))["text"]!.GetString());
        await c.FailAsync("Css.getStyleSheetText", new JsonObject { ["sheetId"] = "zz" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.getStyleSheetText", null, DevToolsErrorCodes.InvalidParams);

        var diags = (await c.OkAsync("Css.setStyleSheetText", new JsonObject { ["sheetId"] = "s1", ["text"] = ".a { bogus: 1 }" }))["diagnostics"]!;
        var d = diags[0];
        Assert.Equal("warning", d["severity"]!.GetString());
        Assert.Equal("AW2002", d["code"]!.GetString());
        Assert.Equal(1, d["line"]!.GetInt64());
        Assert.Equal(6, d["column"]!.GetInt64());
        Assert.Single(c.Events("Css.styleSheetChanged"));
        Assert.Empty((await c.OkAsync("Css.setStyleSheetText", new JsonObject { ["sheetId"] = "s1", ["text"] = "" }))["diagnostics"]!.AsArray());
        await c.FailAsync("Css.setStyleSheetText", new JsonObject { ["sheetId"] = "zz", ["text"] = "" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Css.setStyleSheetText", new JsonObject { ["sheetId"] = "ua", ["text"] = "" }, DevToolsErrorCodes.NotSupported);

        var rule = (await c.OkAsync("Css.addRule", new JsonObject { ["selector"] = ".new", ["text"] = "color: red" }))["rule"]!;
        Assert.Equal("devtools", rule["origin"]!.GetString());
        Assert.Equal(".new", rule["selectorText"]!.GetString());
        Assert.Equal("devtools", rule["sheetId"]!.GetString());
        Assert.Equal("red", rule["declarations"]![0]["value"]!.GetString());
        var added = c.Events("Css.styleSheetAdded").Single();
        Assert.Equal("devtools", added["params"]!["sheet"]!["sheetId"]!.GetString());
        Assert.Equal("w1", added["target"]!.GetString());
        await c.OkAsync("Css.addRule", new JsonObject { ["selector"] = ".newer" });
        await c.FailAsync("Css.addRule", new JsonObject { ["selector"] = "!" }, DevToolsErrorCodes.ValueParseError);
        await c.FailAsync("Css.addRule", null, DevToolsErrorCodes.InvalidParams);

        f.Styles.RemoveSheet("devtools");
        Assert.Equal("devtools", c.Events("Css.styleSheetRemoved").Single()["params"]!["sheetId"]!.GetString());
        await c.OkAsync("Css.disable");
        f.Styles.RemoveSheet("s1");
        Assert.Single(c.Events("Css.styleSheetRemoved"));
    }

    [Fact]
    public async Task Optional_style_methods_are_not_supported_by_default()
    {
        var f = new InspectionFixture();
        f.Styles.SupportsOptional = false;
        var c = f.Connect();
        await c.FailAsync("Css.addRule", new JsonObject { ["selector"] = ".x" }, DevToolsErrorCodes.NotSupported);
        await c.FailAsync("Css.clearOverrides", null, DevToolsErrorCodes.NotSupported);
    }

    [Fact]
    public async Task ForcePseudoState_is_undone_when_the_session_goes_away()
    {
        var f = new InspectionFixture();
        var a = f.Connect(out var sa);
        var b = f.Connect();
        var p = new JsonObject { ["nodeId"] = f.Button.Id, ["states"] = new JsonArray { "hover", "focus-visible", "hover" } };
        await a.FailAsync("Css.forcePseudoState", p, DevToolsErrorCodes.DomainNotEnabled);
        await a.OkAsync("Css.enable");
        await b.OkAsync("Css.enable");
        await a.OkAsync("Css.forcePseudoState", p);
        Assert.Equal(new[] { "hover", "focus-visible" }, f.Styles.Forced[f.Button.Id]);
        await b.OkAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = f.Span.Id, ["states"] = new JsonArray { "active" } });

        var e = await a.FailAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = f.Button.Id, ["states"] = new JsonArray { "visited" } }, DevToolsErrorCodes.InvalidParams);
        Assert.Contains("hover", e["data"]!["allowed"]!.AsArray().Select(x => x.GetString()));
        await a.FailAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = 2, ["states"] = new JsonArray() }, DevToolsErrorCodes.NodeNotFound);
        await a.FailAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = f.Button.Id }, DevToolsErrorCodes.InvalidParams);

        // A 停用：撤销 A 强制的状态，B 的保留
        await a.OkAsync("Css.disable");
        Assert.False(f.Styles.Forced.ContainsKey(f.Button.Id));
        Assert.True(f.Styles.Forced.ContainsKey(f.Span.Id));
        // 空数组取消；断开时没有可撤销的就不调用
        await b.OkAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = f.Span.Id, ["states"] = new JsonArray() });
        int calls = f.Styles.Calls.Count;
        sa.Transport.Dispose();
        Assert.Equal(calls, f.Styles.Calls.Count);
    }

    [Fact]
    public async Task ClearOverrides()
    {
        var f = new InspectionFixture();
        var c = f.Connect(out var s);
        await c.OkAsync("Css.enable");
        await c.OkAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = f.Button.Id, ["states"] = new JsonArray { "hover" } });
        await c.OkAsync("Css.clearOverrides", Node(f.Button.Id));
        Assert.Contains("clear " + f.Button.Id, f.Styles.Calls);
        Assert.Empty(f.Styles.Forced);
        await c.OkAsync("Css.clearOverrides");
        Assert.Contains("clear all", f.Styles.Calls);
        await c.FailAsync("Css.clearOverrides", Node(77), DevToolsErrorCodes.NodeNotFound);
        // 已被 clearOverrides 撤销的强制状态不会在断开时再撤销一次
        int calls = f.Styles.Calls.Count;
        s.Transport.Dispose();
        Assert.Equal(calls, f.Styles.Calls.Count);
    }

    [Fact]
    public async Task ComputedStyleInvalidated_is_coalesced_and_filtered_by_the_tree_mirror()
    {
        var f = new InspectionFixture();
        var a = f.Connect();
        var b = f.Connect();
        await a.OkAsync("Css.enable");
        await a.OkAsync("Tree.enable");
        await a.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = 1 });
        await b.OkAsync("Css.enable");
        await b.OkAsync("Tree.enable");
        await b.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = -1 });

        f.Styles.NotifyComputedStyleInvalidated(f.Main.Id);
        f.Styles.NotifyComputedStyleInvalidated(f.Button.Id);
        f.Styles.NotifyComputedStyleInvalidated(f.Main.Id);
        f.Styles.NotifyComputedStyleInvalidated(123456);
        Assert.Empty(a.Events("Css.computedStyleInvalidated"));
        f.Agent.FlushFrame();
        Assert.Equal(new[] { f.Main.Id }, a.Events("Css.computedStyleInvalidated").Single()["params"]!["nodeIds"]!.Longs());
        Assert.Equal(new[] { f.Main.Id, f.Button.Id }, b.Events("Css.computedStyleInvalidated").Single()["params"]!["nodeIds"]!.Longs());
        f.Agent.FlushFrame();
        Assert.Single(b.Events("Css.computedStyleInvalidated"));
    }

    [Fact]
    public async Task ComputedStyleInvalidated_pending_set_stays_bounded_without_frames()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Css.enable");
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = -1 });
        for (int i = 0; i < 20_000; i++)
        {
            var n = f.Tree.Insert(f.Footer, f.Tree.Create("tmp"));
            f.Styles.NotifyComputedStyleInvalidated(n.Id);
            f.Tree.Remove(n);
        }
        f.Styles.NotifyComputedStyleInvalidated(f.Footer.Id);
        f.Agent.FlushFrame();
        Assert.Equal(new[] { f.Footer.Id }, c.Events("Css.computedStyleInvalidated").Single()["params"]!["nodeIds"]!.Longs());
    }
}

public class LayoutDomainTests
{
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    [Fact]
    public async Task GetBoxModel_returns_quads()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        var box = await c.OkAsync("Layout.getBoxModel", Node(f.Button.Id));
        Assert.Equal(new double[] { 10, 30, 90, 30, 90, 54, 10, 54 }, box["border"]!.AsArray().Select(x => x.GetDouble()));
        Assert.Equal(new double[] { 5, 25, 95, 25, 95, 59, 5, 59 }, box["margin"]!.AsArray().Select(x => x.GetDouble()));
        Assert.Equal(new double[] { 11, 31, 89, 31, 89, 53, 11, 53 }, box["padding"]!.AsArray().Select(x => x.GetDouble()));
        Assert.Equal(new double[] { 13, 33, 87, 33, 87, 51, 13, 51 }, box["content"]!.AsArray().Select(x => x.GetDouble()));
        Assert.Equal(80, box["width"]!.GetDouble());
        Assert.Equal(24, box["height"]!.GetDouble());
        Assert.Null(box["scrollOffset"]);

        box = await c.OkAsync("Layout.getBoxModel", Node(f.Main.Id));
        Assert.Equal(15, box["scrollOffset"]!["y"]!.GetDouble());
        Assert.Equal(2000, box["scrollSize"]!["height"]!.GetDouble());

        await c.FailAsync("Layout.getBoxModel", Node(f.Span.Id), DevToolsErrorCodes.InvalidParams);    // display: none
        await c.FailAsync("Layout.getBoxModel", Node(f.Ok.Id), DevToolsErrorCodes.InvalidParams);      // 文本节点
        await c.FailAsync("Layout.getBoxModel", Node(5), DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Layout.getBoxModel", null, DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Non_finite_coordinates_are_written_as_zero()
    {
        var f = new InspectionFixture();
        f.Button.Box = new Rect(float.NaN, float.PositiveInfinity, 1, 1);
        var c = f.Connect();
        var box = await c.OkAsync("Layout.getBoxModel", Node(f.Button.Id));
        Assert.Equal(0, box["border"]![0].GetDouble());
        Assert.Equal(0, box["border"]![1].GetDouble());
    }

    [Fact]
    public void Quad_from_rect_is_clockwise_from_top_left()
    {
        var q = Quad.FromRect(new Rect(1, 2, 3, 4));
        Assert.Equal(new Point(1, 2), q.P1);
        Assert.Equal(new Point(4, 2), q.P2);
        Assert.Equal(new Point(4, 6), q.P3);
        Assert.Equal(new Point(1, 6), q.P4);
        Assert.Equal(q, Quad.FromRect(new Rect(1, 2, 3, 4)));
    }
}

public class OverlayDomainTests
{
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    [Fact]
    public async Task Methods_require_enable()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.FailAsync("Overlay.highlightNode", Node(f.Main.Id), DevToolsErrorCodes.DomainNotEnabled);
        await c.FailAsync("Overlay.hideHighlight", null, DevToolsErrorCodes.DomainNotEnabled);
        await c.FailAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "pick" }, DevToolsErrorCodes.DomainNotEnabled);
        Assert.Empty(f.Overlay.Calls);
    }

    [Fact]
    public async Task HighlightNode_with_default_and_custom_config()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Overlay.enable");
        await c.OkAsync("Overlay.highlightNode", Node(f.Main.Id));
        Assert.Equal("highlight " + f.Main.Id, f.Overlay.Calls.Last());
        Assert.True(f.Overlay.LastConfig!.ShowInfo);
        Assert.Equal(new HighlightConfig().ContentColor, f.Overlay.LastConfig.ContentColor);

        await c.OkAsync("Overlay.highlightNode", new JsonObject
        {
            ["nodeId"] = f.Button.Id,
            ["config"] = new JsonObject { ["showInfo"] = false, ["showRulers"] = true, ["contentColor"] = "#ff000080", ["marginColor"] = "#0f0" },
        });
        var cfg = f.Overlay.LastConfig!;
        Assert.False(cfg.ShowInfo);
        Assert.True(cfg.ShowRulers);
        Assert.Equal(Color.FromRgba(255, 0, 0, 128), cfg.ContentColor);
        Assert.Equal(Color.FromRgb(0, 255, 0), cfg.MarginColor);

        await c.FailAsync("Overlay.highlightNode", Node(99), DevToolsErrorCodes.NodeNotFound);
        await c.FailAsync("Overlay.highlightNode", new JsonObject { ["nodeId"] = f.Main.Id, ["config"] = new JsonObject { ["contentColor"] = "red" } }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Overlay.highlightNode", new JsonObject { ["nodeId"] = f.Main.Id, ["config"] = new JsonObject { ["showInfo"] = 1 } }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Overlay.highlightNode", new JsonObject { ["nodeId"] = f.Main.Id, ["config"] = "x" }, DevToolsErrorCodes.InvalidParams);
        await c.OkAsync("Overlay.hideHighlight");
        Assert.Equal("hide", f.Overlay.Calls.Last());
    }

    [Fact]
    public async Task Inspect_mode_and_pick_events()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        var other = f.Connect();
        await c.OkAsync("Overlay.enable");
        await c.OkAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "pick" });
        Assert.Equal("mode Pick", f.Overlay.Calls.Last());
        await c.FailAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "Pick" }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Overlay.setInspectMode", null, DevToolsErrorCodes.InvalidParams);

        f.Overlay.NotifyNodePicked(f.Button.Id);
        var picked = c.Events("Overlay.nodePicked").Single();
        Assert.Equal(f.Button.Id, picked["params"]!["nodeId"]!.GetInt64());
        Assert.Equal("w1", picked["target"]!.GetString());
        Assert.Empty(other.Events("Overlay.nodePicked"));       // 未启用 Overlay 的会话收不到

        f.Overlay.NotifyInspectModeCanceled();
        Assert.Single(c.Events("Overlay.inspectModeCanceled"));
        await c.OkAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "measure" });
        await c.OkAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "none" });
        Assert.Equal(new[] { "mode Pick", "mode Measure", "mode None" }, f.Overlay.Calls);

        await c.OkAsync("Overlay.disable");
        f.Overlay.NotifyNodePicked(f.Button.Id);
        Assert.Single(c.Events("Overlay.nodePicked"));
        Assert.Equal(3, f.Overlay.Calls.Count);                 // 没有需要撤销的
    }

    [Fact]
    public async Task Disable_or_disconnect_undoes_what_the_session_set()
    {
        var f = new InspectionFixture();
        var a = f.Connect(out var sa);
        var b = f.Connect();
        await a.OkAsync("Overlay.enable");
        await b.OkAsync("Overlay.enable");
        await a.OkAsync("Overlay.highlightNode", Node(f.Main.Id));
        await a.OkAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "pick" });
        await a.OkAsync("Overlay.disable");
        Assert.Equal(new[] { "highlight " + f.Main.Id, "mode Pick", "hide", "mode None" }, f.Overlay.Calls);

        // B 后设置：A 断开不影响 B 的高亮
        await a.OkAsync("Overlay.enable");
        await a.OkAsync("Overlay.highlightNode", Node(f.Main.Id));
        await b.OkAsync("Overlay.highlightNode", Node(f.Button.Id));
        int n = f.Overlay.Calls.Count;
        sa.Transport.Dispose();
        Assert.Equal(n, f.Overlay.Calls.Count);
    }
}
