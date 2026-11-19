using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// DevTools 的 UI 侧接缝（元素树、样式检查与编辑、叠加层与拾取）经真实的协议 Agent 与进程内传输驱动：
/// 无窗口平台 + 软件后端，断言协议响应、事件与渲染出的像素。
/// </summary>
public partial class DevToolsTests
{
    const string Css = """
        :root { background-color: #ffffff; }
        .panel { display: flex; gap: 4px; padding: 6px; margin: 3px; border: 2px solid #000000; }
        .item { width: 20px; height: 20px; background-color: #0000ff; }
        .item.hot { background-color: #ff0000; }
        text { font-size: 10px; color: #333333; }
        """;

    partial class Panel : Component
    {
        public readonly State<List<int>> Items = new(new List<int> { 1, 2, 3 });
        public readonly State<string> Title = new("hello");

        protected override Node Build()
        {
            var children = new NodeList { new Text { Id = "title", Children = [Title.Value] } };
            foreach (var i in Items.Value) children.Add(new Div { Key = i, Id = "i" + i, Class = "item" });
            return new Div { Class = "panel", Id = "panel", Children = children };
        }
    }

    sealed class Fixture
    {
        public readonly HeadlessPlatform Platform = new() { DefaultScale = 1 };
        public readonly UIHost Host;
        public readonly HeadlessWindow Window;
        public readonly UIDevTools DevTools;
        public readonly DevToolsClient Client;
        public readonly Panel App = new();

        public Fixture(Node? root = null, string css = Css)
        {
            Host = AwApp.CreateHost(Platform, new WindowOptions { Size = new Size(200, 100) }, new SoftwareRenderBackend(), new TestTextEngine(),
                root ?? App, AwAUI.Css.StyleSheet.Parse(css, "test.css"));
            Window = (HeadlessWindow)Host.Window;
            DevTools = Host.EnableDevTools();
            Platform.RunFrame();
            Client = new DevToolsClient(DevTools);
        }

        public Element El(string id) => Host.Root.RootElement.ById(id);

        public long Id(string id) => El(id).UniqueId;

        public Color Pixel(float x, float y) => Window.Framebuffer!.GetPixel((int)x, (int)y);

        public void Frame() => Platform.RunFrame();
    }

    static JsonObject P(string key, JsonValue value) => new() { [key] = value };

    [Fact]
    public void GetDocument_reports_the_element_tree_with_attributes_and_components()
    {
        var f = new Fixture();
        f.Client.Call("Tree.enable");
        var root = f.Client.Call("Tree.getDocument", P("depth", -1))["root"]!;
        Assert.Equal(f.Host.Root.RootElement.UniqueId, root["id"]!.GetInt64());
        Assert.Equal("root", root["tag"]!.GetString());
        var panel = root["children"]![0]!;
        Assert.Equal("div", panel["tag"]!.GetString());
        Assert.Equal("panel", panel["elementId"]!.GetString());
        Assert.Equal("panel", panel["classes"]![0]!.GetString());
        Assert.Equal("Panel", panel["componentName"]!.GetString());
        var kids = panel["children"]!.AsArray();
        Assert.Equal(4, kids.Count);
        Assert.Equal("title", kids[0]["elementId"]!.GetString());
        var text = kids[0]["children"]![0]!;
        Assert.Equal("text", text["kind"]!.GetString());
        Assert.Equal("hello", text["text"]!.GetString());

        // 盒模型：边框盒 = margin 3 处开始，padding 6、border 2
        var box = f.Client.Call("Layout.getBoxModel", DevToolsClient.Node(f.Id("panel")));
        var border = box["border"]!.AsArray();
        Assert.Equal(3, border[0].GetDouble());
        Assert.Equal(3, border[1].GetDouble());
        var content = box["content"]!.AsArray();
        Assert.Equal(3 + 2 + 6, content[0].GetDouble());
        var margin = box["margin"]!.AsArray();
        Assert.Equal(0, margin[0].GetDouble());

        // 选择器查询与命中测试
        var all = f.Client.Call("Tree.querySelectorAll", new JsonObject { ["nodeId"] = root["id"]!.GetInt64(), ["selector"] = ".panel > .item" })["nodeIds"]!.AsArray();
        Assert.Equal(new[] { f.Id("i1"), f.Id("i2"), f.Id("i3") }, all.Select(v => v.GetInt64()).ToArray());
        Assert.Equal(-32005, f.Client.Fail("Tree.querySelectorAll", new JsonObject { ["nodeId"] = root["id"]!.GetInt64(), ["selector"] = ".a >" }));
        var i2 = f.El("i2").BoundingRect.Center;
        Assert.Equal(f.Id("i2"), f.Client.Call("Tree.getNodeAt", new JsonObject { ["x"] = i2.X, ["y"] = i2.Y })["nodeId"]!.GetInt64());
    }

    [Fact]
    public void Tree_events_keep_a_front_end_mirror_in_document_order_under_keyed_reordering()
    {
        var f = new Fixture();
        f.Client.Call("Tree.enable");
        var mirror = new TreeMirror();
        mirror.Load(f.Client.Call("Tree.getDocument", P("depth", -1))["root"]!);
        var rng = new Random(1234);
        int next = 4, moves = 0, inserts = 0;
        for (int round = 0; round < 60; round++)
        {
            var list = new List<int>(f.App.Items.Value);
            // 随机删除、插入、打乱（协调器报告最少移动），检验插入 / 移动事件的 beforeId 在每一步都能定位
            for (int k = rng.Next(3); k > 0 && list.Count > 0; k--) list.RemoveAt(rng.Next(list.Count));
            for (int k = rng.Next(4); k > 0; k--) list.Insert(rng.Next(list.Count + 1), next++);
            for (int k = rng.Next(4); k > 0 && list.Count > 1; k--)
            {
                int a = rng.Next(list.Count), b = rng.Next(list.Count);
                (list[a], list[b]) = (list[b], list[a]);
            }
            f.Client.Clear();
            f.App.Items.Value = list;
            f.Frame();
            foreach (var m in f.Client.Messages)
            {
                if (m["method"] is not JsonString method) continue;
                if (method.Value == "Tree.childMoved") moves++;
                if (method.Value == "Tree.childrenChanged" && m["params"]!["inserted"]!.AsArray().Count > 0) inserts++;
                mirror.Apply(m);
            }

            var panel = f.El("panel");
            var expected = panel.Children.Select(c => c.UniqueId).ToList();
            Assert.Equal(expected, mirror.Children[panel.UniqueId]);
        }
        Assert.True(moves > 20 && inserts > 20, $"moves {moves}, inserts {inserts}");
    }

    [Fact]
    public void Attribute_and_text_changes_are_batched_into_the_next_frame()
    {
        var f = new Fixture();
        f.Client.Call("Tree.enable");
        f.Client.Call("Tree.getDocument", P("depth", -1));
        f.Client.Clear();
        f.Client.Call("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Id("i1"), ["name"] = "data-x", ["value"] = "1" });
        f.Client.Call("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Id("i1"), ["name"] = "class", ["value"] = "item hot" });
        f.App.Title.Value = "bye";
        Assert.Empty(f.Client.Events("Tree.attributesChanged"));
        f.Frame();
        var attr = Assert.Single(f.Client.Events("Tree.attributesChanged"));
        Assert.Equal(f.Id("i1"), attr["nodeId"]!.GetInt64());
        Assert.Equal("1", f.El("i1").GetAttribute("data-x"));
        Assert.True(f.El("i1").HasClass("hot"));
        Assert.Single(f.Client.Events("Tree.textChanged"));

        // class 修改经样式重算进入下一帧的像素
        var r = f.El("i1").BoundingRect;
        ColorAssert.Near(Color.Parse("#ff0000"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        Assert.Equal(-32004, f.Client.Fail("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Id("i1"), ["name"] = "tabindex", ["value"] = "1" }));
    }

    [Fact]
    public void Matched_rules_and_computed_style_come_from_the_css_engine()
    {
        var f = new Fixture();
        var r = f.Client.Call("Css.getMatchedRules", DevToolsClient.Node(f.Id("i1")));
        var matched = r["matched"]!.AsArray();
        var item = matched.Single(m => m["rule"]!["selectorText"]!.GetString() == ".item");
        Assert.Equal("author", item["rule"]!["origin"]!.GetString());
        Assert.Equal("test.css", item["rule"]!["source"]!["file"]!.GetString());
        Assert.Equal(new long[] { 0, 1, 0 }, item["specificity"]!.AsArray().Select(v => v.GetInt64()).ToArray());
        var decls = item["rule"]!["declarations"]!.AsArray();
        Assert.Equal(new[] { "width", "height", "background-color" }, decls.Select(d => d["name"]!.GetString()).ToArray());
        Assert.All(decls, d => Assert.Equal("active", d["status"]!.GetString()));
        // 内建默认样式表（UserAgent）中的 div 规则也会列出（如果有）；继承：text 的 color 来自自己的规则
        var text = f.Client.Call("Css.getMatchedRules", DevToolsClient.Node(f.Id("title")));
        Assert.Contains(text["matched"]!.AsArray(), m => m["rule"]!["selectorText"]!.GetString() == "text");

        var computed = f.Client.Call("Css.getComputedStyle", DevToolsClient.Node(f.Id("i1")))["properties"]!.AsArray();
        var bg = computed.Single(p => p["name"]!.GetString() == "background-color");
        Assert.Contains("0, 0, 255", bg["value"]!.GetString());
        Assert.Equal(item["rule"]!["id"]!.GetString(), bg["from"]!["ruleId"]!.GetString());
        Assert.Equal("author", bg["from"]!["origin"]!.GetString());
        // 继承来的 color：来自根上的规则还是初始值，取决于样式表；text 自己的 color 来自 "text" 规则
        var textColor = f.Client.Call("Css.getComputedStyle", DevToolsClient.Node(f.Id("title")))["properties"]!.AsArray()
            .Single(p => p["name"]!.GetString() == "color");
        Assert.Contains("51, 51, 51", textColor["value"]!.GetString());
    }

    [Fact]
    public void Editing_a_rule_restyles_and_changes_the_next_rendered_frame()
    {
        var f = new Fixture();
        var r = f.El("i2").BoundingRect;
        ColorAssert.Near(Color.Parse("#0000ff"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        var rule = f.Client.Call("Css.getMatchedRules", DevToolsClient.Node(f.Id("i2")))["matched"]!.AsArray()
            .Single(m => m["rule"]!["selectorText"]!.GetString() == ".item")["rule"]!;
        string ruleId = rule["id"]!.GetString();
        string sheetId = rule["sheetId"]!.GetString();

        f.Client.Call("Css.enable");
        var decls = f.Client.Call("Css.setStyleText", new JsonObject
        {
            ["target"] = new JsonObject { ["ruleId"] = ruleId },
            ["text"] = " width: 30px; height: 20px; background-color: #00ff00; /* color: red */ bogus: 1; ",
        })["declarations"]!.AsArray();
        Assert.Equal(new[] { "width", "height", "background-color", "color", "bogus" }, decls.Select(d => d["name"]!.GetString()).ToArray());
        Assert.True(decls[3]["disabled"]!.GetBoolean());
        Assert.Equal("unsupported", decls[4]["status"]!.GetString());
        Assert.Single(f.Client.Events("Css.styleSheetChanged"));

        f.Frame();
        r = f.El("i2").BoundingRect;
        Assert.Equal(30, r.Width);
        ColorAssert.Near(Color.Parse("#00ff00"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        Assert.Contains("#00ff00", f.Client.Call("Css.getStyleSheetText", P("sheetId", sheetId))["text"]!.GetString());

        // 规则文本中的花括号会改变规则结构：拒绝
        Assert.Equal(-32005, f.Client.Fail("Css.setStyleText", new JsonObject
        {
            ["target"] = new JsonObject { ["ruleId"] = ruleId },
            ["text"] = "color: red } .x { color: blue",
        }));

        // clearOverrides 还原原始样式表
        f.Client.Call("Css.clearOverrides");
        f.Frame();
        r = f.El("i2").BoundingRect;
        Assert.Equal(20, r.Width);
        ColorAssert.Near(Color.Parse("#0000ff"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
    }

    [Fact]
    public void Inline_edits_survive_rebuilds_and_added_rules_win_the_cascade()
    {
        var f = new Fixture();
        f.Client.Call("Css.setStyleText", new JsonObject
        {
            ["target"] = DevToolsClient.Node(f.Id("i1")),
            ["text"] = "background-color: #00ff00",
        });
        // 组件重建不会冲掉 DevTools 的内联修改
        f.App.Title.Value = "rebuilt";
        f.Frame();
        var r = f.El("i1").BoundingRect;
        ColorAssert.Near(Color.Parse("#00ff00"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        var inline = f.Client.Call("Css.getInlineStyle", DevToolsClient.Node(f.Id("i1")))["declarations"]!.AsArray();
        Assert.Equal("background-color", Assert.Single(inline)["name"]!.GetString());

        // DevTools 来源的新规则（级联中最高）
        var rule = f.Client.Call("Css.addRule", new JsonObject { ["selector"] = "#i3", ["text"] = "background-color: #ff00ff" })["rule"]!;
        Assert.Equal("devtools", rule["origin"]!.GetString());
        f.Frame();
        r = f.El("i3").BoundingRect;
        ColorAssert.Near(Color.Parse("#ff00ff"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        Assert.Equal(-32005, f.Client.Fail("Css.addRule", new JsonObject { ["selector"] = "#i3 >", ["text"] = "" }));

        // 强制伪状态
        f.Client.Call("Css.enable");
        f.Client.Call("Css.forcePseudoState", new JsonObject { ["nodeId"] = f.Id("i2"), ["states"] = new JsonArray { "hover" } });
        Assert.Equal(PseudoStates.Hover, f.El("i2").States & PseudoStates.Hover);

        f.Client.Call("Css.clearOverrides");
        f.Frame();
        r = f.El("i1").BoundingRect;
        ColorAssert.Near(Color.Parse("#0000ff"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        r = f.El("i3").BoundingRect;
        ColorAssert.Near(Color.Parse("#0000ff"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        Assert.Equal(PseudoStates.None, f.El("i2").States & PseudoStates.Hover);
    }

    [Fact]
    public void Highlight_draws_box_model_colors_over_the_frame_and_follows_layout()
    {
        var f = new Fixture();
        var panel = f.El("panel").BoundingRect;
        // 外边距区（左上角 0..3）原为白色背景
        ColorAssert.Near(Color.White, f.Pixel(1, 1), 0.02f);
        f.Client.Call("Overlay.enable");
        f.Client.Call("Overlay.highlightNode", new JsonObject { ["nodeId"] = f.Id("panel"), ["config"] = new JsonObject { ["showInfo"] = false } });
        f.Frame();
        var cfg = new Diagnostics.Inspection.HighlightConfig();
        ColorAssert.Near(Blend(cfg.MarginColor, Color.White), f.Pixel(1, panel.Center.Y), 0.03f);
        ColorAssert.Near(Blend(cfg.BorderColor, Color.Parse("#000000")), f.Pixel(panel.X + 1, panel.Center.Y), 0.03f);
        ColorAssert.Near(Blend(cfg.PaddingColor, Color.White), f.Pixel(panel.X + 4, panel.Y + 4), 0.03f);
        // 应用自己的 DisplayList 不含叠加层（不参与命中测试）
        Assert.NotSame(f.Host.Pipeline.Frame, f.Host.PresentedFrame);
        Assert.Equal(f.Id("panel"), f.Host.Pipeline.HitTest(new Point(panel.X + 4, panel.Y + 4))!.UniqueId);

        f.Client.Call("Overlay.hideHighlight");
        f.Frame();
        ColorAssert.Near(Color.White, f.Pixel(1, panel.Center.Y), 0.02f);
        Assert.Same(f.Host.Pipeline.Frame, f.Host.PresentedFrame);
    }

    static Color Blend(Color src, Color dst) =>
        new(src.R * src.A + dst.R * (1 - src.A), src.G * src.A + dst.G * (1 - src.A), src.B * src.A + dst.B * (1 - src.A), 1);

    [Fact]
    public void F12_pick_mode_intercepts_input_and_reports_the_picked_node()
    {
        var f = new Fixture();
        f.Client.Call("Overlay.enable");
        var picked = new List<Element>();
        f.DevTools.ElementPicked += picked.Add;
        f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.F12, KeyModifiers.None, false, 0, TimeSpan.Zero));
        f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Up, PhysicalKey.F12, KeyModifiers.None, false, 0, TimeSpan.Zero));
        Assert.Equal(Diagnostics.Inspection.InspectMode.Pick, f.DevTools.InspectMode);

        var p = f.El("i2").BoundingRect.Center;
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        Assert.Same(f.El("i2"), f.DevTools.HighlightedElement);
        Assert.Equal(PseudoStates.None, f.El("i2").States & PseudoStates.Hover); // 输入没有路由进应用
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        f.Frame();
        Assert.Same(f.El("i2"), Assert.Single(picked));
        var ev = Assert.Single(f.Client.Events("Overlay.nodePicked"));
        Assert.Equal(f.Id("i2"), ev["nodeId"]!.GetInt64());
        // 本地 F12 拾取：选中后退出拾取并保持高亮
        Assert.Equal(Diagnostics.Inspection.InspectMode.None, f.DevTools.InspectMode);
        Assert.Same(f.El("i2"), f.DevTools.HighlightedElement);
        Assert.Contains(".item {", f.DevTools.DescribeElement(f.El("i2")));

        // 再次 F12 进入、Esc 取消
        f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.F12, KeyModifiers.None, false, 0, TimeSpan.Zero));
        f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.Escape, KeyModifiers.None, false, 0, TimeSpan.Zero));
        Assert.Equal(Diagnostics.Inspection.InspectMode.None, f.DevTools.InspectMode);
        Assert.Single(f.Client.Events("Overlay.inspectModeCanceled"));
    }

    [Fact]
    public void Component_style_hooks_are_registered_once_per_type_on_first_mount()
    {
        var f = new Fixture(new Div { Children = [new Styled { Id = "a" }, new Styled { Id = "b" }] });
        var sheets = f.Host.Root.ComponentStyleSheets;
        Assert.Equal(2, sheets.Count);
        Assert.True(sheets[0].IsGlobal);
        Assert.Equal(typeof(Styled), sheets[1].ComponentType);
        var r = f.El("a").BoundingRect;
        ColorAssert.Near(Color.Parse("#00ff00"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
        // DevTools 看得到组件样式表
        f.Client.Call("Css.enable");
        var list = f.Client.Call("Css.getStyleSheets")["sheets"]!.AsArray();
        Assert.Contains(list, s => s["sourceUrl"] is JsonString u && u.Value == typeof(Styled).FullName);
    }

    partial class Styled : Component
    {
        public string? Id { get; set; }

        protected internal override string? ScopedStyleSheet => ".styled[aw-test] { width: 10px; height: 10px; background-color: #00ff00; }";

        protected internal override string? GlobalStyleSheet => ".unused { color: red; }";

        protected override Node Build()
        {
            var d = new Div { Id = Id, Class = "styled" };
            d.__Scope = "aw-test";
            return d;
        }
    }
}
