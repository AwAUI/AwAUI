using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Transport;
using AwAUI.UI;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// design/06「运行时 Tailwind」：DevTools 在帧末把元素树上新出现与不再出现的类名分别交给
/// <see cref="UIDevToolsOptions.ClassCandidates"/> / <see cref="UIDevToolsOptions.ClassCandidatesRemoved"/>，
/// 包括初始树、换根与经协议修改的 <c>class</c>；没有变化的帧不回调，消失后再出现会重新报告。
/// </summary>
public class ClassCandidateTests
{
    static List<string> Flat(List<IReadOnlyList<string>> batches)
    {
        var all = new List<string>();
        foreach (var batch in batches) all.AddRange(batch);
        return all;
    }

    [Fact]
    public void Initial_tree_classes_are_reported_once_and_quiet_frames_do_not_callback()
    {
        using var h = new Harness(attach: false);
        var batches = new List<IReadOnlyList<string>>();
        h.App.EnableDevTools(new UIDevToolsOptions { ClassCandidates = batches.Add });

        h.Frames(2);
        var reported = Flat(batches);
        Assert.Contains("app", reported);
        Assert.Contains("counter", reported);
        Assert.Contains("count", reported);
        // 每个类名只报告一次（跨批次去重）
        Assert.Equal(reported.Count, reported.Distinct(StringComparer.Ordinal).Count());

        // 没有新类名的帧不再回调
        int count = batches.Count;
        h.Frames(3);
        Assert.Equal(count, batches.Count);
    }

    [Fact]
    public void Classes_that_leave_the_tree_are_reported_to_the_removal_sink()
    {
        using var h = new Harness(attach: false);
        var added = new List<IReadOnlyList<string>>();
        var removed = new List<IReadOnlyList<string>>();
        h.App.EnableDevTools(new UIDevToolsOptions
        {
            ClassCandidates = added.Add,
            ClassCandidatesRemoved = removed.Add,
        });
        h.Frames(1);
        Assert.Contains("count", Flat(added));
        Assert.Empty(removed);

        // 换根：旧树的类名报告移除，新树的类名报告新增
        h.App.Mount(new Div { Class = "fresh-class" });
        h.Frames(1);
        Assert.Contains("fresh-class", Flat(added));
        Assert.Contains("count", Flat(removed));
        Assert.Contains("counter", Flat(removed));

        // 再换回来：count 重新报告新增，fresh-class 报告移除
        h.App.Mount(new Div { Class = "count" });
        h.Frames(1);
        Assert.Contains("fresh-class", Flat(removed));
        Assert.Equal(2, Flat(added).Count(name => name == "count"));
    }

    [Fact]
    public async Task Classes_set_over_the_protocol_are_reported_at_the_end_of_the_frame()
    {
        using var h = new Harness(attach: false);
        var batches = new List<IReadOnlyList<string>>();
        var devTools = h.App.EnableDevTools(new UIDevToolsOptions { ClassCandidates = batches.Add });
        h.Frames(1);
        Assert.Contains("count", Flat(batches));

        var (agentSide, clientSide) = InProcTransport.CreatePair();
        devTools.Connect(agentSide);
        using var client = new DevToolsClient(clientSide);
        var count = h.AppById("count");
        var parameters = new JsonObject
        {
            ["nodeId"] = count.UniqueId,
            ["classes"] = new JsonArray { "count", "runtime-added", "my-widget" },
        };
        await client.CallAsync("Tree.setClasses", parameters);

        // 类名在协议调用时进入待发送集合，帧末一次性交给回调；已经报告过的 count 不再出现
        Assert.Equal(0, Flat(batches).Count(name => name == "runtime-added"));
        h.Frames(1);
        Assert.Contains("runtime-added", Flat(batches));
        Assert.Contains("my-widget", Flat(batches));
        Assert.Equal(1, Flat(batches).Count(name => name == "count"));

        // 与元素上的实际类一致
        Assert.True(count.HasClass("runtime-added"));
        Assert.True(count.HasClass("my-widget"));
    }

    [Fact]
    public void Classes_added_by_the_host_are_reported_after_the_next_frame()
    {
        using var h = new Harness(attach: false);
        var batches = new List<IReadOnlyList<string>>();
        h.App.EnableDevTools(new UIDevToolsOptions { ClassCandidates = batches.Add });
        h.Frames(1);

        // 宿主换根（新的子树替换旧树）：新子树里的类名在下一帧末报告
        h.App.Mount(new Div { Class = "fresh-class", Children = { new Div { Class = "fresh-child" } } });
        h.Frames(1);
        Assert.Contains("fresh-class", Flat(batches));
        Assert.Contains("fresh-child", Flat(batches));
    }
}
