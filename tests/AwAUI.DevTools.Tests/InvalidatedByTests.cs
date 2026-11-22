using System.Diagnostics;
using System.Threading;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Json;
using AwAUI.UI;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// <c>Layout.getLayoutInfo</c> 的 <c>invalidatedBy</c>：UI 侧近似的「最近一次标脏来源」。编辑在目标 UI 线程的调度队列中
/// 先于随后的 <c>getLayoutInfo</c> 处理，因此能在同一帧的布局清除之前读到来源；完成一帧布局后来源被清除。
/// </summary>
public class InvalidatedByTests
{
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    static void Send(Harness h, string method, JsonObject? p, Action<DevToolsResponse> done) =>
        h.P.Client.Call(method, p, null, done);

    static DevToolsResponse Await(Harness h, string method, JsonObject? p)
    {
        DevToolsResponse? response = null;
        Send(h, method, p, r => response = r);
        var sw = Stopwatch.StartNew();
        while (response is null)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"No response to {method}.");
            h.Frames();
            Thread.Sleep(1);
        }
        return response;
    }

    static JsonObject Ok(Harness h, string method, JsonObject? p = null)
    {
        var r = Await(h, method, p);
        Assert.False(r.IsError, method + " failed: " + r);
        return r.ResultObject;
    }

    static void OkEdit(DevToolsResponse r, string what)
    {
        Assert.False(r.IsError, what + " failed: " + r);
    }

    /// <summary>
    /// 把编辑与两次 <c>getLayoutInfo</c> 一起排进目标 UI 线程的队列，再推进一帧：三者在同一帧的样式 / 布局之前依次处理，
    /// 所以信息反映的是编辑后、清除前的状态。
    /// </summary>
    static (DevToolsResponse Edit, JsonObject Source, JsonObject Ancestor) EditAndInspect(
        Harness h, string method, JsonObject edit, long sourceId, long ancestorId)
    {
        DevToolsResponse? editResponse = null;
        DevToolsResponse? sourceResponse = null;
        DevToolsResponse? ancestorResponse = null;
        Send(h, method, edit, r => editResponse = r);
        Send(h, "Layout.getLayoutInfo", Node(sourceId), r => sourceResponse = r);
        Send(h, "Layout.getLayoutInfo", Node(ancestorId), r => ancestorResponse = r);
        h.Frames(1);
        Assert.NotNull(editResponse);
        Assert.NotNull(sourceResponse);
        Assert.NotNull(ancestorResponse);
        OkEdit(editResponse!, method);
        Assert.False(sourceResponse!.IsError, "getLayoutInfo (source) failed: " + sourceResponse);
        Assert.False(ancestorResponse!.IsError, "getLayoutInfo (ancestor) failed: " + ancestorResponse);
        return (editResponse!, sourceResponse.ResultObject, ancestorResponse.ResultObject);
    }

    static void AssertInvalidatedBy(JsonObject info, Element source, string? property)
    {
        var by = info["invalidatedBy"];
        Assert.NotNull(by);
        Assert.Equal(source.UniqueId, by!["nodeId"]!.GetInt64());
        if (property is null) Assert.Null(by["property"]);
        else Assert.Equal(property, by["property"]!.GetString());
    }

    [Fact]
    public void The_latest_of_two_edits_in_the_same_frame_is_reported_at_every_ancestor()
    {
        using var h = new Harness();
        var child = h.AppById("increment");
        var ancestor = child.Parent?.Parent;
        Assert.NotNull(ancestor); // 计数器按钮 -> .actions -> .counter，至少两层，覆盖「祖先已有脏标记」的情况
        Ok(h, "Layout.enable");

        DevToolsResponse? style = null;
        DevToolsResponse? classes = null;
        DevToolsResponse? ancestorInfo = null;
        DevToolsResponse? rootInfo = null;
        Send(h, "Tree.setAttribute",
            new JsonObject { ["nodeId"] = child.UniqueId, ["name"] = "style", ["value"] = "width: 112px" }, r => style = r);
        Send(h, "Tree.setClasses",
            new JsonObject { ["nodeId"] = child.UniqueId, ["classes"] = new JsonArray { "latest-invalidation" } }, r => classes = r);
        Send(h, "Layout.getLayoutInfo", Node(ancestor!.UniqueId), r => ancestorInfo = r);
        Send(h, "Layout.getLayoutInfo", Node(h.App.Root.RootElement.UniqueId), r => rootInfo = r);
        h.Frames(1);

        Assert.NotNull(style);
        Assert.NotNull(classes);
        Assert.NotNull(ancestorInfo);
        Assert.NotNull(rootInfo);
        OkEdit(style!, "Tree.setAttribute");
        OkEdit(classes!, "Tree.setClasses");
        Assert.False(ancestorInfo!.IsError, "getLayoutInfo (ancestor) failed: " + ancestorInfo);
        Assert.False(rootInfo!.IsError, "getLayoutInfo (root) failed: " + rootInfo);
        // 第二次编辑（class）覆盖第一次（style）成为「最近一次」来源，且传播到所有祖先
        AssertInvalidatedBy(ancestorInfo.ResultObject, child, "class");
        AssertInvalidatedBy(rootInfo.ResultObject, child, "class");
    }

    [Fact]
    public void Invalidated_by_points_at_the_source_element_and_property_then_clears_after_a_layout()
    {
        using var h = new Harness();
        var root = h.App.Root.RootElement;
        var child = h.AppById("increment");
        Ok(h, "Layout.enable");

        // style：属性名为 "style"
        var style = EditAndInspect(h, "Tree.setAttribute",
            new JsonObject { ["nodeId"] = child.UniqueId, ["name"] = "style", ["value"] = "width: 111px" },
            child.UniqueId, root.UniqueId);
        AssertInvalidatedBy(style.Source, child, "style");
        AssertInvalidatedBy(style.Ancestor, child, "style");

        // 完成一帧布局后清除（之后没有新的失效来源）
        Assert.Null(Ok(h, "Layout.getLayoutInfo", Node(child.UniqueId))["invalidatedBy"]);
        Assert.Null(Ok(h, "Layout.getLayoutInfo", Node(root.UniqueId))["invalidatedBy"]);

        // class：属性名为 "class"
        var classes = EditAndInspect(h, "Tree.setClasses",
            new JsonObject { ["nodeId"] = child.UniqueId, ["classes"] = new JsonArray { "invalidated-test-class" } },
            child.UniqueId, root.UniqueId);
        AssertInvalidatedBy(classes.Source, child, "class");
        AssertInvalidatedBy(classes.Ancestor, child, "class");
        Assert.Null(Ok(h, "Layout.getLayoutInfo", Node(child.UniqueId))["invalidatedBy"]);

        // 自定义属性：属性名即来源属性名
        var attribute = EditAndInspect(h, "Tree.setAttribute",
            new JsonObject { ["nodeId"] = child.UniqueId, ["name"] = "data-invalidated", ["value"] = "1" },
            child.UniqueId, root.UniqueId);
        AssertInvalidatedBy(attribute.Source, child, "data-invalidated");
        AssertInvalidatedBy(attribute.Ancestor, child, "data-invalidated");
        Assert.Null(Ok(h, "Layout.getLayoutInfo", Node(root.UniqueId))["invalidatedBy"]);
    }
}
