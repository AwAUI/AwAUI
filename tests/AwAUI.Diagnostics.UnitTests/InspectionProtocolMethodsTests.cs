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
/// design/20 新增方法（Tree.search / scrollIntoView / focus、Css.getVariables、Layout.getLayoutInfo、Overlay.setShowOptions）
/// 在内存接缝上的协议行为：匹配、上限、默认不支持与显示选项的所有权清理。
/// </summary>
public class InspectionProtocolMethodsTests
{
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    // ------------------------------------------------------------ Tree.search

    [Fact]
    public async Task Search_matches_tag_id_class_text_and_selector()
    {
        var f = new InspectionFixture();
        var c = f.Connect();

        // 不需要 Tree.enable
        var r = await c.OkAsync("Tree.search", new JsonObject { ["query"] = "button" });
        var results = r["results"]!.AsArray();
        Assert.Single(results);
        Assert.Equal(f.Button.Id, results[0]!["nodeId"]!.GetInt64());
        Assert.Equal("tag", results[0]!["match"]!.GetString());

        results = (await c.OkAsync("Tree.search", new JsonObject { ["query"] = "header" }))["results"]!.AsArray();
        Assert.Equal(f.Header.Id, Assert.Single(results)["nodeId"]!.GetInt64());

        results = (await c.OkAsync("Tree.search", new JsonObject { ["query"] = "root" }))["results"]!.AsArray();
        Assert.Equal(f.Tree.Root.Id, Assert.Single(results)["nodeId"]!.GetInt64());
        Assert.Equal("id", results[0]!["match"]!.GetString());

        results = (await c.OkAsync("Tree.search", new JsonObject { ["query"] = "primary" }))["results"]!.AsArray();
        Assert.Equal("class", Assert.Single(results)["match"]!.GetString());
        Assert.Equal(f.Button.Id, results[0]!["nodeId"]!.GetInt64());

        // 文本节点的内容归到父元素；文档顺序、去重
        results = (await c.OkAsync("Tree.search", new JsonObject { ["query"] = "Title" }))["results"]!.AsArray();
        Assert.Equal("text", Assert.Single(results)["match"]!.GetString());
        Assert.Equal(f.Header.Id, results[0]!["nodeId"]!.GetInt64());

        // CSS 选择器（.a 同时命中 header 与 button，按文档顺序）
        results = (await c.OkAsync("Tree.search", new JsonObject { ["query"] = ".a" }))["results"]!.AsArray();
        Assert.Equal(new[] { f.Header.Id, f.Button.Id }, results.Select(x => x!["nodeId"]!.GetInt64()));
        Assert.All(results, x => Assert.Equal("selector", x!["match"]!.GetString()));
    }

    [Fact]
    public async Task Search_unparsable_selector_is_not_an_error()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        var r = await c.OkAsync("Tree.search", new JsonObject { ["query"] = "a >> b" });
        Assert.Empty(r["results"]!.AsArray());
        Assert.Null(r["truncated"]);
    }

    [Fact]
    public async Task Search_caps_results_and_reports_truncation()
    {
        var f = new InspectionFixture(new DevToolsAgentOptions { MaxTreeNodesPerResponse = 2 });
        var c = f.Connect();
        var r = await c.OkAsync("Tree.search", new JsonObject { ["query"] = "*" });
        var results = r["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.True(r["truncated"]!.GetBoolean());
        // 前两个按文档顺序：header、main
        Assert.Equal(new[] { f.Header.Id, f.Main.Id }, results.Select(x => x!["nodeId"]!.GetInt64()));
    }

    [Fact]
    public async Task Search_empty_query_returns_no_results()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        Assert.Empty((await c.OkAsync("Tree.search", new JsonObject { ["query"] = "" }))["results"]!.AsArray());
        await c.FailAsync("Tree.search", null, DevToolsErrorCodes.InvalidParams);
    }

    // ------------------------------------------------------------ scrollIntoView / focus

    [Fact]
    public async Task Scroll_and_focus_are_optional_seams()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.FailAsync("Tree.scrollIntoView", Node(f.Button.Id), DevToolsErrorCodes.NotSupported);
        await c.FailAsync("Tree.focus", Node(f.Button.Id), DevToolsErrorCodes.NotSupported);
        await c.FailAsync("Tree.focus", null, DevToolsErrorCodes.InvalidParams);
    }

    // ------------------------------------------------------------ Css.getVariables

    [Fact]
    public async Task GetVariables_reports_winning_definitions_in_name_order()
    {
        var agent = new DevToolsAgent();
        var tree = new FakeTree();
        var styles = new VariableStyles();
        styles.Computed[tree.Root.Id] = new[]
        {
            new ComputedStyleProperty("--z-last", "#fff"),
            new ComputedStyleProperty("color", "red"),
            new ComputedStyleProperty("--a", "#000") { FromNodeId = 7, FromRuleId = "s1/2" },
        };
        var target = new DevToolsTarget("w1", "Main", new Size(100, 100)) { Tree = tree, Styles = styles };
        agent.AddTarget(target);
        var (a, cl) = InProcTransport.CreatePair();
        agent.Attach(a);
        var c = new RpcClient(cl);

        // 不需要 Css.enable
        var r = await c.OkAsync("Css.getVariables", Node(tree.Root.Id));
        var vars = r["variables"]!.AsArray();
        Assert.Equal(new[] { "--a", "--z-last" }, vars.Select(v => v!["name"]!.GetString()).ToArray());
        Assert.Equal("#000", vars[0]!["value"]!.GetString());
        Assert.Equal(7, vars[0]!["definedOn"]!.GetInt64());
        Assert.Equal("s1/2", vars[0]!["ruleId"]!.GetString());
        Assert.Equal(tree.Root.Id, vars[1]!["definedOn"]!.GetInt64());
        Assert.Null(vars[1]!["ruleId"]);

        await c.FailAsync("Css.getVariables", Node(12345), DevToolsErrorCodes.NodeNotFound);
    }

    // ------------------------------------------------------------ Layout.getLayoutInfo

    [Fact]
    public async Task GetLayoutInfo_is_an_optional_seam()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.FailAsync("Layout.getLayoutInfo", Node(f.Button.Id), DevToolsErrorCodes.NotSupported);
    }

    // ------------------------------------------------------------ Overlay.setShowOptions

    sealed class RecordingOverlay : OverlayHost
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

    static (DevToolsAgent Agent, RecordingOverlay Overlay, RpcClient Client, DevToolsSession Session) CreateOverlayFixture()
    {
        var agent = new DevToolsAgent();
        var tree = new FakeTree();
        var overlay = new RecordingOverlay();
        var target = new DevToolsTarget("w1", "Main", new Size(100, 100)) { Tree = tree, Overlay = overlay };
        agent.AddTarget(target);
        var (client, session) = Connect(agent);
        return (agent, overlay, client, session);
    }

    /// <summary>在同一个 Agent 上建立额外的会话（默认目标仍是第一个目标）。</summary>
    static (RpcClient Client, DevToolsSession Session) Connect(DevToolsAgent agent)
    {
        var (a, cl) = InProcTransport.CreatePair();
        var session = agent.Attach(a);
        return (new RpcClient(cl), session);
    }

    [Fact]
    public async Task SetShowOptions_requires_enable_and_rejects_bad_parameters()
    {
        var (_, overlay, c, _) = CreateOverlayFixture();
        await c.FailAsync("Overlay.setShowOptions", new JsonObject { ["rulers"] = true }, DevToolsErrorCodes.DomainNotEnabled);
        await c.OkAsync("Overlay.enable");
        await c.FailAsync("Overlay.setShowOptions", new JsonObject { ["rulers"] = 1 }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Overlay.setShowOptions", new JsonObject { ["layoutGrids"] = new JsonArray { "x" } }, DevToolsErrorCodes.InvalidParams);
        await c.FailAsync("Overlay.setShowOptions", new JsonObject { ["layoutGrids"] = new JsonArray { 0L } }, DevToolsErrorCodes.InvalidParams);
        Assert.Equal(0, overlay.Calls);
    }

    [Fact]
    public async Task SetShowOptions_is_reset_when_the_owning_session_disables_or_disconnects()
    {
        var (agent, overlay, a, _) = CreateOverlayFixture();
        var (b, sb) = Connect(agent);
        await a.OkAsync("Overlay.enable");
        await b.OkAsync("Overlay.enable");

        await a.OkAsync("Overlay.setShowOptions", new JsonObject
        {
            ["rulers"] = true,
            ["hitTargets"] = true,
            ["layoutGrids"] = new JsonArray { 7L, 8L },
        });
        Assert.True(overlay.Last.Rulers);
        Assert.True(overlay.Last.HitTargets);
        Assert.Equal(new long[] { 7, 8 }, overlay.Last.LayoutGrids.ToArray());

        // 后设置者生效：A 停用不会清掉 B 的选项
        await b.OkAsync("Overlay.setShowOptions", new JsonObject { ["touches"] = true });
        await a.OkAsync("Overlay.disable");
        Assert.True(overlay.Last.Touches);
        Assert.False(overlay.Last.Rulers);

        // B 断开：恢复为全部关闭
        sb.Transport.Dispose();
        Assert.True(overlay.Calls >= 3);
        Assert.False(overlay.Last.Rulers);
        Assert.False(overlay.Last.HitTargets);
        Assert.False(overlay.Last.Touches);
        Assert.Empty(overlay.Last.LayoutGrids);
    }

    /// <summary>只实现 GetComputedStyle 的样式接缝。</summary>
    sealed class VariableStyles : StyleInspector
    {
        public readonly Dictionary<long, IReadOnlyList<ComputedStyleProperty>> Computed = new();

        public override MatchedStylesInfo? GetMatchedRules(long nodeId) => null;
        public override IReadOnlyList<ComputedStyleProperty>? GetComputedStyle(long nodeId) =>
            Computed.TryGetValue(nodeId, out var list) ? list : null;
        public override IReadOnlyList<StyleSheetInfo> GetStyleSheets() => Array.Empty<StyleSheetInfo>();
        public override string? GetStyleSheetText(string sheetId) => null;
        public override IReadOnlyList<StyleDeclarationInfo>? SetStyleText(StyleTextTarget target, string text) => null;
        public override IReadOnlyList<StyleDiagnostic>? SetStyleSheetText(string sheetId, string text) => null;
        public override bool ForcePseudoState(long nodeId, IReadOnlyList<string> states) => false;
    }
}
