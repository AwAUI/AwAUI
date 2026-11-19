using System.Text;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>记录 Agent 发出的消息；可在某条消息发出之前插入动作，模拟其他线程恰在此刻产生事件。</summary>
sealed class HookTransport : DevToolsTransport
{
    readonly List<JsonObject> _sent = new();

    /// <summary>每条消息发出（记录）之前调用。</summary>
    public Action<JsonObject>? BeforeSend;

    public override bool IsConnected => true;

    public List<JsonObject> Sent { get { lock (_sent) return _sent.ToList(); } }

    public override Task SendAsync(ReadOnlyMemory<byte> message)
    {
        var o = JsonValue.Parse(message.Span).AsObject();
        BeforeSend?.Invoke(o);
        lock (_sent) _sent.Add(o);
        return Task.CompletedTask;
    }

    public void Inject(string json) => OnReceived(Encoding.UTF8.GetBytes(json));
}

/// <summary>评审补充：enable / disable 与 UI 线程上的处理器、事件之间的竞争。</summary>
public class InspectionRaceTests
{
    static bool IsResponse(JsonObject m, long id) => m["id"] is JsonNumber n && n.GetInt64() == id && !m.ContainsKey("method");

    static string Raw(long id, string method, string paramsJson = "{}") =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";

    [Theory]
    [InlineData("Tree", "Tree.documentUpdated")]
    [InlineData("Css", "Css.styleSheetChanged")]
    [InlineData("Overlay", "Overlay.nodePicked")]
    public void Events_racing_with_enable_never_precede_its_response(string domain, string eventName)
    {
        var f = new InspectionFixture();
        Action produce = domain switch
        {
            "Tree" => () => f.Tree.NotifyDocumentUpdated(),
            "Css" => () => f.Styles.NotifyStyleSheetChanged("s1"),
            _ => () => f.Overlay.NotifyNodePicked(f.Button.Id),
        };
        var t = new HookTransport();
        f.Agent.Attach(t);
        // 另一线程（UI 线程）恰在 enable 已被处理、响应尚未发出时产生事件
        t.BeforeSend = m =>
        {
            if (!IsResponse(m, 1)) return;
            t.BeforeSend = null;
            produce();
        };
        t.Inject(Raw(1, domain + ".enable"));
        var sent = t.Sent;
        Assert.True(IsResponse(sent[0], 1), string.Join("\n", sent));
        Assert.Single(sent);

        // 响应之后产生的事件照常推送
        produce();
        Assert.Equal(eventName, t.Sent.Last()["method"]!.GetString());

        // 停用后不再推送；重新启用的响应同样先于事件
        t.Inject(Raw(2, domain + ".disable"));
        produce();
        Assert.True(IsResponse(t.Sent.Last(), 2));
        t.BeforeSend = m =>
        {
            if (!IsResponse(m, 3)) return;
            t.BeforeSend = null;
            produce();
        };
        int count = t.Sent.Count;
        t.Inject(Raw(3, domain + ".enable"));
        Assert.Equal(count + 1, t.Sent.Count);
        Assert.True(IsResponse(t.Sent.Last(), 3), string.Join("\n", t.Sent));
    }

    [Fact]
    public async Task Overlay_requests_queued_on_the_ui_thread_do_not_outlive_disable()
    {
        var ui = new ManualDispatcher();
        var f = new InspectionFixture(targetDispatcher: ui);
        var c = f.Connect();
        await c.OkAsync("Overlay.enable");
        await c.SendRawAsync(Raw(1, "Overlay.highlightNode", $"{{\"nodeId\":{f.Button.Id}}}"));
        await c.SendRawAsync(Raw(2, "Overlay.setInspectMode", "{\"mode\":\"pick\"}"));
        Assert.Equal(2, ui.Pending);
        await c.OkAsync("Overlay.disable");           // 不经过 UI 线程，先于排队的请求完成；此时还没有需要撤销的东西
        ui.RunAll();
        Rpc.Error(c.Messages.Single(m => IsResponse(m, 1)), DevToolsErrorCodes.DomainNotEnabled);
        Rpc.Error(c.Messages.Single(m => IsResponse(m, 2)), DevToolsErrorCodes.DomainNotEnabled);
        Assert.Empty(f.Overlay.Calls);                // 高亮与拾取模式都没有被留下

        // 断开同理
        var c2 = f.Connect(out var s2);
        await c2.OkAsync("Overlay.enable");
        await c2.SendRawAsync(Raw(3, "Overlay.setInspectMode", "{\"mode\":\"pick\"}"));
        s2.Transport.Dispose();
        ui.RunAll();
        Assert.Empty(f.Overlay.Calls);
    }

    [Fact]
    public async Task Overlay_state_set_while_the_session_goes_away_is_undone()
    {
        var f = new InspectionFixture();
        var c = f.Connect(out var session);
        await c.OkAsync("Overlay.enable");
        // 处理器执行期间（另一线程上）会话被分离：停用时的撤销已经执行过（那时还没有设置者），处理器必须自行撤销
        f.Overlay.DuringHighlight = () => f.Agent.Detach(session);
        Rpc.Error(await c.CallAsync("Overlay.highlightNode", new JsonObject { ["nodeId"] = f.Button.Id }), DevToolsErrorCodes.DomainNotEnabled);
        Assert.Equal(new[] { "highlight " + f.Button.Id, "hide" }, f.Overlay.Calls);
    }

    [Fact]
    public async Task ForcePseudoState_queued_or_racing_with_disable_is_undone()
    {
        var ui = new ManualDispatcher();
        var f = new InspectionFixture(targetDispatcher: ui);
        var c = f.Connect();
        await c.OkAsync("Css.enable");
        await c.SendRawAsync(Raw(1, "Css.forcePseudoState", $"{{\"nodeId\":{f.Button.Id},\"states\":[\"hover\"]}}"));
        await c.OkAsync("Css.disable");
        ui.RunAll();
        Rpc.Error(c.Messages.Single(m => IsResponse(m, 1)), DevToolsErrorCodes.DomainNotEnabled);
        Assert.Empty(f.Styles.Calls);
        Assert.Empty(f.Styles.Forced);

        var f2 = new InspectionFixture();
        var c2 = f2.Connect(out var session);
        await c2.OkAsync("Css.enable");
        f2.Styles.DuringForce = () => { f2.Styles.DuringForce = null; f2.Agent.Detach(session); };
        Rpc.Error(await c2.CallAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = f2.Button.Id, ["states"] = new JsonArray { "hover" } }),
            DevToolsErrorCodes.DomainNotEnabled);
        Assert.Empty(f2.Styles.Forced);
        Assert.Equal($"force {f2.Button.Id} []", f2.Styles.Calls.Last());
    }

    [Fact]
    public async Task GetDocument_queued_while_the_target_is_removed_leaves_no_mirror_behind()
    {
        var ui = new ManualDispatcher();
        var f = new InspectionFixture(targetDispatcher: ui);
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.SendRawAsync(Raw(1, "Tree.getDocument"));
        Assert.True(f.Agent.RemoveTarget("w1"));
        ui.RunAll();
        Rpc.Result(c.Messages.Single(m => IsResponse(m, 1)));     // 快照照常返回
        // 目标重新登记后，旧请求不应留下镜像（否则会推送前端从未建立的节点的事件）
        f.Agent.AddTarget(f.Target);
        f.Tree.Insert(f.Tree.Root, f.Tree.Create("nav"));
        ui.RunAll();
        Assert.Empty(c.Events("Tree.childrenChanged"));
    }

    [Fact]
    public async Task Cross_parent_move_reported_insert_first_keeps_the_mirror_consistent()
    {
        var f = new InspectionFixture();
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = -1 });
        c.ClearMessages();

        // UI 层先报告了在新父节点下的插入，再报告从旧父节点移除
        var b = f.Button;
        f.Main.Children.Remove(b);
        f.Footer.Children.Add(b);
        b.Parent = f.Footer;
        f.Tree.NotifyChildInserted(f.Footer.Id, b.Id);
        f.Tree.NotifyChildRemoved(f.Main.Id, b.Id);

        var changes = c.Events("Tree.childrenChanged").Select(e => e["params"]!).ToArray();
        Assert.Equal(2, changes.Length);
        // 前端先从旧位置移除，再在新位置插入：节点不会同时出现在两处
        Assert.Equal(f.Main.Id, changes[0]["parentId"]!.GetInt64());
        Assert.Equal(new[] { b.Id }, changes[0]["removed"]!.Longs());
        Assert.Equal(f.Footer.Id, changes[1]["parentId"]!.GetInt64());
        Assert.Equal(b.Id, changes[1]["inserted"]![0]["node"]!.Id());

        // 迟到的移除通知不会把新位置上的节点从镜像中丢掉
        f.Tree.SetAttr(b, "data-x", "1");
        f.Agent.FlushFrame();
        Assert.Single(c.Events("Tree.attributesChanged"));
        Assert.Single((await c.OkAsync("Tree.requestChildren", new JsonObject { ["nodeId"] = b.Id }))["children"]!.AsArray());
    }
}
