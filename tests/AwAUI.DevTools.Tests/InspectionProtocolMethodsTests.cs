using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;
using AwAUI.UI;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// design/20 新增的检查方法经真实的无窗口应用与进程内协议驱动：
/// <c>Tree.search</c> / <c>scrollIntoView</c> / <c>focus</c>、<c>Css.getVariables</c>、<c>Layout.getLayoutInfo</c>、<c>Overlay.setShowOptions</c>。
/// </summary>
public partial class InspectionProtocolMethodsTests
{
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    static DevToolsResponse Call(Harness h, DevToolsClient client, string method, JsonObject? p = null)
    {
        DevToolsResponse? response = null;
        client.Call(method, p, null, r => response = r);
        var sw = Stopwatch.StartNew();
        while (response is null)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"No response to {method}.");
            h.Frames();
            Thread.Sleep(1);
        }
        return response;
    }

    static JsonObject Ok(Harness h, DevToolsClient client, string method, JsonObject? p = null)
    {
        var r = Call(h, client, method, p);
        Assert.False(r.IsError, method + " failed: " + r);
        return r.ResultObject;
    }

    static void Fail(Harness h, DevToolsClient client, string method, JsonObject? p, int code)
    {
        var r = Call(h, client, method, p);
        Assert.True(r.IsError, method + " unexpectedly succeeded: " + r);
        Assert.Equal(code, r.ErrorCode);
    }

    // ------------------------------------------------------------ Tree.search

    [Fact]
    public void Tree_search_matches_tag_id_class_text_and_selector_without_enable()
    {
        using var h = new Harness();
        var inc = h.AppById("increment");

        var results = Ok(h, h.P.Client, "Tree.search", new JsonObject { ["query"] = "button" })["results"]!.AsArray();
        Assert.Contains(results, x => x!["nodeId"]!.GetInt64() == inc.UniqueId && x!["match"]!.GetString() == "tag");

        results = Ok(h, h.P.Client, "Tree.search", new JsonObject { ["query"] = "increment" })["results"]!.AsArray();
        var byId = Assert.Single(results);
        Assert.Equal(inc.UniqueId, byId!["nodeId"]!.GetInt64());
        Assert.Equal("id", byId["match"]!.GetString());

        results = Ok(h, h.P.Client, "Tree.search", new JsonObject { ["query"] = "primary" })["results"]!.AsArray();
        Assert.Contains(results, x => x!["nodeId"]!.GetInt64() == inc.UniqueId && x!["match"]!.GetString() == "class");

        // 直接文本内容（Counter 的按钮文字是 Increment）
        results = Ok(h, h.P.Client, "Tree.search", new JsonObject { ["query"] = "Increment" })["results"]!.AsArray();
        Assert.Contains(results, x => x!["nodeId"]!.GetInt64() == inc.UniqueId && x!["match"]!.GetString() == "text");

        // CSS 选择器
        results = Ok(h, h.P.Client, "Tree.search", new JsonObject { ["query"] = "button.primary" })["results"]!.AsArray();
        Assert.Contains(results, x => x!["nodeId"]!.GetInt64() == inc.UniqueId && x!["match"]!.GetString() == "selector");

        // 无法解析的选择器不是错误
        results = Ok(h, h.P.Client, "Tree.search", new JsonObject { ["query"] = "a >> b" })["results"]!.AsArray();
        Assert.Empty(results);
    }

    [Fact]
    public void Tree_search_caps_results_and_reports_truncation()
    {
        using var h = new Harness();
        h.App.DevTools!.Agent.Options.MaxTreeNodesPerResponse = 2;
        var r = Ok(h, h.P.Client, "Tree.search", new JsonObject { ["query"] = "*" });
        Assert.Equal(2, r["results"]!.AsArray().Count);
        Assert.True(r["truncated"]!.GetBoolean());
    }

    // ------------------------------------------------------------ scrollIntoView / focus

    [Fact]
    public void Tree_scrollIntoView_scrolls_the_nearest_container()
    {
        using var h = new Harness(root: new ScrollBox());
        h.Frames(4);
        var viewport = h.AppById("scroll-viewport");
        var target = h.AppById("scroll-target");
        Assert.True(viewport.ScrollController.MaxOffset.Y > 0, "the test root is not scrollable");

        Ok(h, h.P.Client, "Tree.scrollIntoView", Node(target.UniqueId));

        var sw = Stopwatch.StartNew();
        while (viewport.ScrollController.OffsetY <= 0)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("scrollIntoView did not move the container.");
            h.Frames();
        }
        Assert.True(viewport.ScrollController.OffsetY > 0);
    }

    [Fact]
    public void Tree_focus_moves_focus_without_requiring_enable()
    {
        using var h = new Harness();
        var inc = h.AppById("increment");
        Assert.False(inc.IsFocused);

        Ok(h, h.P.Client, "Tree.focus", Node(inc.UniqueId));
        Assert.True(inc.IsFocused);
    }

    [Fact]
    public void Tree_scrollIntoView_and_focus_report_missing_nodes()
    {
        using var h = new Harness();
        Fail(h, h.P.Client, "Tree.scrollIntoView", Node(999_999), DevToolsErrorCodes.NodeNotFound);
        Fail(h, h.P.Client, "Tree.focus", Node(999_999), DevToolsErrorCodes.NodeNotFound);
    }

    sealed partial class ScrollBox : Component
    {
        protected override Node Build() => new Div
        {
            Id = "scroll-viewport",
            Style = "height: 80px; overflow-y: scroll",
            Children =
            [
                new Div { Id = "scroll-spacer", Style = "height: 500px" },
                new Div { Id = "scroll-target", Style = "height: 20px" },
            ],
        };
    }

    // ------------------------------------------------------------ Css.getVariables

    [Fact]
    public void Css_getVariables_reports_inherited_custom_properties_without_enable()
    {
        using var h = new Harness();
        var inc = h.AppById("increment");
        var rootId = h.App.Root.RootElement.UniqueId;

        var vars = Ok(h, h.P.Client, "Css.getVariables", Node(inc.UniqueId))["variables"]!.AsArray();
        Assert.NotEmpty(vars);
        var accent = vars.Single(v => v!["name"]!.GetString() == "--accent");
        Assert.Contains("4f46e5", accent["value"]!.GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rootId, accent["definedOn"]!.GetInt64());          // :root 定义了它，按钮继承
        Assert.False(string.IsNullOrEmpty(accent["ruleId"]!.GetString()));

        // 名称按序数排序
        var names = vars.Select(v => v!["name"]!.GetString()).ToArray();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);

        Fail(h, h.P.Client, "Css.getVariables", Node(999_999), DevToolsErrorCodes.NodeNotFound);

        // 文本节点没有变量
        var textNode = h.AppById("count").Children[0];
        Assert.True(textNode.IsTextNode);
        Assert.Empty(Ok(h, h.P.Client, "Css.getVariables", Node(textNode.UniqueId))["variables"]!.AsArray());
    }

    // ------------------------------------------------------------ Layout.getLayoutInfo

    [Fact]
    public void Layout_getLayoutInfo_reports_display_and_constraints()
    {
        using var h = new Harness();
        var inc = h.AppById("increment");

        var info = Ok(h, h.P.Client, "Layout.getLayoutInfo", Node(inc.UniqueId));
        Assert.False(string.IsNullOrEmpty(info["display"]!.GetString()));
        var constraints = info["constraints"]!.AsObject();
        Assert.True(constraints.ContainsKey("minWidth"));
        Assert.True(constraints.ContainsKey("maxWidth"));
        Assert.True(constraints.ContainsKey("minHeight"));
        Assert.True(constraints.ContainsKey("maxHeight"));
        Assert.False(info["isLayoutBoundary"]!.GetBoolean());
        Assert.True(info["lastLayoutFrame"]!.GetInt64() > 0);

        Fail(h, h.P.Client, "Layout.getLayoutInfo", Node(999_999), DevToolsErrorCodes.NodeNotFound);

        // 文本节点没有布局盒
        var textNode = h.AppById("count").Children[0];
        Assert.True(textNode.IsTextNode);
        Fail(h, h.P.Client, "Layout.getLayoutInfo", Node(textNode.UniqueId), DevToolsErrorCodes.InvalidParams);
    }

    // ------------------------------------------------------------ Overlay.setShowOptions

    [Fact]
    public void Overlay_setShowOptions_requires_enable_and_stores_the_options()
    {
        using var h = new Harness();
        var inc = h.AppById("increment");

        Fail(h, h.P.Client, "Overlay.setShowOptions", new JsonObject { ["rulers"] = true }, DevToolsErrorCodes.DomainNotEnabled);
        Ok(h, h.P.Client, "Overlay.enable");
        Ok(h, h.P.Client, "Overlay.setShowOptions", new JsonObject
        {
            ["rulers"] = true,
            ["hitTargets"] = true,
            ["layoutGrids"] = new JsonArray { inc.UniqueId },
        });
        var options = h.App.DevTools!.ShowOptions;
        Assert.True(options.Rulers);
        Assert.True(options.HitTargets);
        Assert.False(options.Touches);
        Assert.Equal(new[] { inc.UniqueId }, options.LayoutGrids);

        // 后一次设置替换之前的设置；省略的字段回到默认值
        Ok(h, h.P.Client, "Overlay.setShowOptions", new JsonObject { ["touches"] = true });
        Assert.False(h.App.DevTools!.ShowOptions.Rulers);
        Assert.True(h.App.DevTools!.ShowOptions.Touches);
        Assert.Empty(h.App.DevTools!.ShowOptions.LayoutGrids);
    }

    [Fact]
    public void Overlay_setShowOptions_is_cleared_when_the_owning_session_disables_or_disconnects()
    {
        using var h = new Harness();
        Ok(h, h.P.Client, "Overlay.enable");
        Ok(h, h.P.Client, "Overlay.setShowOptions", new JsonObject { ["rulers"] = true });
        Assert.True(h.App.DevTools!.ShowOptions.Rulers);

        // 停用撤销
        Ok(h, h.P.Client, "Overlay.disable");
        Assert.False(h.App.DevTools!.ShowOptions.Rulers);

        // 断开撤销：使用自己的会话
        var (agentSide, clientSide) = InProcTransport.CreatePair();
        var session = h.App.DevTools!.Connect(agentSide);
        using var client = new DevToolsClient(clientSide, new DevToolsClientOptions { Dispatcher = h.Platform.Dispatcher });
        Ok(h, client, "Overlay.enable");
        Ok(h, client, "Overlay.setShowOptions", new JsonObject { ["hitTargets"] = true, ["rulers"] = true });
        Assert.True(h.App.DevTools!.ShowOptions.HitTargets);
        session.Transport.Dispose();
        h.Frames(2);
        Assert.False(h.App.DevTools!.ShowOptions.HitTargets);
        Assert.False(h.App.DevTools!.ShowOptions.Rulers);
    }
}
