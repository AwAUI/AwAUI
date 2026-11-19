using System.Net.WebSockets;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>
/// 脚本式 WebSocket JSON-RPC 客户端：后台循环收集所有消息，按 id 等待响应、按方法名筛选事件。
/// 也可用作手工调试的小工具（<c>await using var c = await WsRpcClient.ConnectAsync(uri); await c.CallAsync("Tree.getDocument")</c>）。
/// </summary>
sealed class WsRpcClient : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly List<JsonObject> _messages = new();
    readonly CancellationTokenSource _cts = new();
    Task _loop = Task.CompletedTask;
    long _id;

    public static async Task<WsRpcClient> ConnectAsync(Uri uri)
    {
        var c = new WsRpcClient();
        await c._ws.ConnectAsync(uri, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        c._loop = c.ReceiveLoopAsync();
        return c;
    }

    async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await _ws.ReceiveAsync(buffer, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var o = JsonValue.Parse(ms.ToArray()).AsObject();
                    lock (_messages) _messages.Add(o);
                }
                ms.SetLength(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public List<JsonObject> Messages { get { lock (_messages) return _messages.ToList(); } }

    public List<JsonObject> Events(string method) =>
        Messages.Where(m => m["method"] is JsonString s && s.Value == method).ToList();

    public async Task<long> SendAsync(string method, JsonObject? p = null)
    {
        long id = Interlocked.Increment(ref _id);
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (p != null) req["params"] = p;
        await _ws.SendAsync(req.ToUtf8Bytes(), WebSocketMessageType.Text, true, CancellationToken.None);
        return id;
    }

    public async Task<JsonObject> CallAsync(string method, JsonObject? p = null)
    {
        long id = await SendAsync(method, p);
        return await WaitForAsync(m => m["id"] is JsonNumber n && n.GetInt64() == id && !m.ContainsKey("method"));
    }

    public async Task<JsonValue> OkAsync(string method, JsonObject? p = null) => Rpc.Result(await CallAsync(method, p));

    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> predicate, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_messages)
                foreach (var m in _messages)
                    if (predicate(m)) return m;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No matching message. Got: " + string.Join("\n", Messages));
            await Task.Delay(5);
        }
    }

    public Task<JsonObject> WaitForEventAsync(string method) => WaitForAsync(m => m["method"] is JsonString s && s.Value == method);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (WebSocketException) { }
        _cts.Cancel();
        await _loop;
        _ws.Dispose();
    }
}

public class InspectionThreadingTests
{
    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    static int IndexOf(List<JsonObject> messages, Func<JsonObject, bool> predicate) => messages.FindIndex(m => predicate(m));
    static bool IsResponse(JsonObject m, long id) => m["id"] is JsonNumber n && n.GetInt64() == id && !m.ContainsKey("method");
    static bool IsEvent(JsonObject m, string method) => m["method"] is JsonString s && s.Value == method;

    [Fact]
    public async Task Handlers_and_notifications_run_on_the_target_ui_thread()
    {
        using var ui = new ThreadDispatcher();
        var f = new InspectionFixture(targetDispatcher: ui);
        f.Tree.UIThread = ui;
        f.Overlay.UIThread = ui;
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Css.enable");
        await c.OkAsync("Overlay.enable");
        await c.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = 1 });
        await c.OkAsync("Tree.requestChildren", new JsonObject { ["nodeId"] = f.Main.Id, ["depth"] = -1 });
        await c.OkAsync("Tree.pushNodePath", Node(f.Title.Id));
        await c.OkAsync("Tree.describeNode", Node(f.Span.Id));
        await c.OkAsync("Tree.querySelectorAll", new JsonObject { ["nodeId"] = f.Tree.Root.Id, ["selector"] = "*" });
        await c.OkAsync("Tree.getNodeAt", new JsonObject { ["x"] = 1, ["y"] = 1 });
        await c.OkAsync("Tree.getOuterText", new JsonObject { ["nodeId"] = f.Tree.Root.Id, ["includeStyles"] = "all" });
        await c.OkAsync("Tree.setAttribute", new JsonObject { ["nodeId"] = f.Button.Id, ["name"] = "x", ["value"] = "1" });
        await c.OkAsync("Layout.getBoxModel", Node(f.Button.Id));
        await c.OkAsync("Css.getMatchedRules", Node(f.Button.Id));
        await c.OkAsync("Css.forcePseudoState", new JsonObject { ["nodeId"] = f.Button.Id, ["states"] = new JsonArray { "hover" } });
        await c.OkAsync("Overlay.highlightNode", Node(f.Button.Id));
        await c.OkAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "pick" });
        await c.FailAsync("Tree.describeNode", Node(1), DevToolsErrorCodes.NodeNotFound);

        // 结构变更在 UI 线程上发生并通知；只发通知的变化可以从任意线程报告，被投递到 UI 线程处理
        await ui.InvokeAsync(() => f.Tree.Insert(f.Tree.Root, f.Tree.Create("nav")));
        f.Tree.NotifyTextChanged(f.Title.Id);
        f.Tree.NotifyAttributeChanged(f.Button.Id, "class");
        f.Overlay.NotifyNodePicked(f.Span.Id);
        f.Styles.NotifyStyleSheetChanged("s1");
        await ui.InvokeAsync(f.Agent.FlushFrame);
        await c.WaitForAsync(m => IsEvent(m, "Tree.childrenChanged"));
        await c.WaitForAsync(m => IsEvent(m, "Tree.textChanged"));
        await c.WaitForAsync(m => IsEvent(m, "Tree.attributesChanged"));
        await c.WaitForAsync(m => IsEvent(m, "Overlay.nodePicked"));
        await c.WaitForAsync(m => IsEvent(m, "Css.styleSheetChanged"));

        // 断开时的撤销也投递到 UI 线程
        await c.OkAsync("Overlay.disable");
        await c.OkAsync("Css.disable");
        await ui.InvokeAsync(() => { });
        Assert.Contains("mode None", f.Overlay.Calls);
        Assert.Empty(f.Styles.Forced);

        Assert.Equal(0, f.Tree.ThreadViolations);
        Assert.Equal(0, f.Overlay.ThreadViolations);
        Assert.Empty(ui.Errors);
    }

    [Fact]
    public async Task Responses_precede_events_produced_after_them_on_the_ui_thread()
    {
        var ui = new ManualDispatcher();
        var f = new InspectionFixture(targetDispatcher: ui);
        var c = f.Connect();
        await c.OkAsync("Tree.enable");            // enable / disable 不需要 UI 线程，直接处理

        await c.SendRawAsync($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Tree.getDocument\"}}");
        Assert.Equal(1, ui.Pending);
        FakeNode? nav = null;
        ui.Post(() => nav = f.Tree.Insert(f.Tree.Root, f.Tree.Create("nav")));
        Assert.Single(c.Messages);                  // 只有 enable 的响应
        ui.RunAll();
        var messages = c.Messages;
        int response = IndexOf(messages, m => IsResponse(m, 1));
        int evt = IndexOf(messages, m => IsEvent(m, "Tree.childrenChanged"));
        Assert.True(response >= 0 && evt > response, string.Join("\n", messages));
        Assert.Equal(3, Rpc.Result(messages[response])["root"]!["childCount"]!.GetInt64());
        Assert.Equal(nav!.Id, messages[evt]["params"]!["inserted"]![0]["node"]!.Id());

        // 反过来：变化先于请求发生，则只体现在响应里，没有事件（新会话，尚未 getDocument）
        var c2 = f.Connect();
        await c2.OkAsync("Tree.enable");
        c2.ClearMessages();
        ui.Post(() => f.Tree.Insert(f.Tree.Root, f.Tree.Create("aside")));
        await c2.SendRawAsync($"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Tree.getDocument\"}}");
        ui.RunAll();
        Assert.Equal(5, Rpc.Result(c2.Messages.Single())["root"]!["childCount"]!.GetInt64());
    }

    [Fact]
    public async Task Agent_dispatcher_is_used_when_the_target_has_none()
    {
        using var ui = new ThreadDispatcher();
        var f = new InspectionFixture(new DevToolsAgentOptions { Dispatcher = ui });
        f.Tree.UIThread = ui;
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = -1 });
        f.Tree.NotifyTextChanged(f.Ok.Id);           // 从测试线程报告
        await ui.InvokeAsync(f.Agent.FlushFrame);
        await c.WaitForAsync(m => IsEvent(m, "Tree.textChanged"));
        Assert.Equal(0, f.Tree.ThreadViolations);
    }

    [Fact]
    public async Task Requests_to_a_target_are_processed_in_order()
    {
        using var ui = new ThreadDispatcher();
        var f = new InspectionFixture(targetDispatcher: ui);
        var c = f.Connect();
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = 0 });
        // 不等待响应地连发：展开 → 查询 → 再展开，处理顺序与发送顺序一致
        for (int i = 0; i < 50; i++)
            await c.SendRawAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{100 + i},\"method\":\"Tree.requestChildren\",\"params\":{{\"nodeId\":{f.Tree.Root.Id}}}}}");
        await c.WaitForAsync(m => IsResponse(m, 149));
        var ids = c.Messages.Where(m => m["id"] is JsonNumber n && n.GetInt64() is >= 100 and < 150).Select(m => m["id"]!.GetInt64()).ToArray();
        Assert.Equal(Enumerable.Range(100, 50).Select(i => (long)i), ids);
    }
}

/// <summary>端到端：真实 WebSocket 上的 Tree / Css / Layout / Overlay。</summary>
public sealed class InspectionWebSocketTests : IAsyncLifetime
{
    WebSocketServerTransport _server = null!;
    ThreadDispatcher _ui = null!;
    InspectionFixture _f = null!;

    public Task InitializeAsync()
    {
        _ui = new ThreadDispatcher();
        _f = new InspectionFixture(new DevToolsAgentOptions { PairingToken = "tok" }, _ui);
        _f.Tree.UIThread = _ui;
        _f.Overlay.UIThread = _ui;
        _server = new WebSocketServerTransport(new WebSocketServerOptions { CloseTimeout = TimeSpan.FromMilliseconds(300) });
        _f.Agent.Attach(_server);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
        _f.Agent.Dispose();
        _ui.Dispose();
    }

    Uri Uri => new($"ws://127.0.0.1:{_server.LocalEndPoint!.Port}/devtools");

    async Task<WsRpcClient> ConnectAsync()
    {
        var c = await WsRpcClient.ConnectAsync(Uri);
        await c.OkAsync("App.hello", new JsonObject { ["client"] = "ws-test", ["protocolVersion"] = "1.0", ["token"] = "tok" });
        return c;
    }

    static JsonObject Node(long id) => new() { ["nodeId"] = id };

    [Fact]
    public async Task Elements_panel_flow_over_websocket()
    {
        await using var c = await ConnectAsync();
        Rpc.Error(await c.CallAsync("Tree.getDocument"), DevToolsErrorCodes.DomainNotEnabled);
        await c.OkAsync("Tree.enable");
        await c.OkAsync("Css.enable");
        await c.OkAsync("Overlay.enable");
        var root = (await c.OkAsync("Tree.getDocument"))["root"]!;
        Assert.Equal(_f.Tree.Root.Id, root.Id());
        Assert.Equal(3, root["children"]!.AsArray().Count);

        // 拾取 → 显示路径 → 样式与盒模型
        await c.OkAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "pick" });
        _f.Overlay.NotifyNodePicked(_f.Ok.Id);
        var picked = await c.WaitForEventAsync("Overlay.nodePicked");
        long pickedId = picked["params"]!["nodeId"]!.GetInt64();
        var path = await c.OkAsync("Tree.pushNodePath", Node(pickedId));
        Assert.Equal(_f.Ok.Id, path["path"]!.Longs().Last());
        await c.OkAsync("Overlay.setInspectMode", new JsonObject { ["mode"] = "none" });
        await c.OkAsync("Overlay.highlightNode", Node(_f.Button.Id));
        var rules = await c.OkAsync("Css.getMatchedRules", Node(_f.Button.Id));
        Assert.Equal("r1", rules["matched"]![0]["rule"]!["id"]!.GetString());
        var box = await c.OkAsync("Layout.getBoxModel", Node(_f.Button.Id));
        Assert.Equal(80, box["width"]!.GetDouble());

        // 实时改样式
        await c.OkAsync("Css.setStyleText", new JsonObject { ["target"] = new JsonObject { ["ruleId"] = "r1" }, ["text"] = "color: blue" });
        await c.WaitForEventAsync("Css.styleSheetChanged");

        // UI 线程上的变化
        long navId = 0;
        await _ui.InvokeAsync(() => navId = _f.Tree.Insert(_f.Tree.Root, _f.Tree.Create("nav")).Id);
        var changed = await c.WaitForEventAsync("Tree.childrenChanged");
        Assert.Equal(navId, changed["params"]!["inserted"]![0]["node"]!.Id());
        await _ui.InvokeAsync(() => _f.Tree.SetAttr(_f.Button, "aria-label", "OK"));
        await _ui.InvokeAsync(_f.Agent.FlushFrame);
        var attrs = await c.WaitForEventAsync("Tree.attributesChanged");
        Assert.Equal("OK", attrs["params"]!["attributes"]!["aria-label"]!.GetString());

        // 错误
        Rpc.Error(await c.CallAsync("Tree.describeNode", Node(5)), DevToolsErrorCodes.NodeNotFound);
        Rpc.Error(await c.CallAsync("Tree.querySelector", new JsonObject { ["nodeId"] = _f.Tree.Root.Id, ["selector"] = "a >" }), DevToolsErrorCodes.ValueParseError);
        Rpc.Error(await c.CallAsync("Css.getComputedStyle", new JsonObject { ["nodeId"] = "x" }), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await c.CallAsync("Overlay.highlightNode", Node(5)), DevToolsErrorCodes.NodeNotFound);

        Assert.Equal(0, _f.Tree.ThreadViolations);
        Assert.Equal(0, _f.Overlay.ThreadViolations);
    }

    [Fact]
    public async Task Getdocument_response_precedes_mutation_events_over_websocket()
    {
        await using var c = await ConnectAsync();
        await c.OkAsync("Tree.enable");
        // 在 UI 线程忙碌时发出请求，并在请求之后（同一 UI 线程上）发生变化
        var gate = new ManualResetEventSlim();
        var blocked = new ManualResetEventSlim();
        _ui.Post(() => { blocked.Set(); gate.Wait(); });
        Assert.True(blocked.Wait(TimeSpan.FromSeconds(10)));     // UI 线程已取走并阻塞在这一项上，队列此后只含请求
        long id = await c.SendAsync("Tree.getDocument", new JsonObject { ["depth"] = 1 });
        // 等请求被投递到 UI 线程队列之后再排入变化（服务端接收是异步的）
        await WaitUntilAsync(() => _ui.QueueCount >= 1);
        _ui.Post(() => _f.Tree.Insert(_f.Tree.Root, _f.Tree.Create("late")));
        gate.Set();
        await c.WaitForEventAsync("Tree.childrenChanged");
        var messages = c.Messages;
        int response = messages.FindIndex(m => m["id"] is JsonNumber n && n.GetInt64() == id);
        int evt = messages.FindIndex(m => m["method"] is JsonString s && s.Value == "Tree.childrenChanged");
        Assert.True(response >= 0 && response < evt, string.Join("\n", messages));
    }

    [Fact]
    public async Task Websocket_and_in_proc_sessions_share_the_tree()
    {
        await using var ws = await ConnectAsync();
        var local = _f.Connect();
        Rpc.Result(await local.HelloAsync(token: "tok"));
        await ws.OkAsync("Tree.enable");
        await ws.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = 1 });
        await local.OkAsync("Tree.enable");
        await local.OkAsync("Tree.getDocument", new JsonObject { ["depth"] = 1 });
        await _ui.InvokeAsync(() => _f.Tree.Remove(_f.Footer));
        await ws.WaitForEventAsync("Tree.childrenChanged");
        await local.WaitForAsync(m => m["method"] is JsonString s && s.Value == "Tree.childrenChanged");

        // WebSocket 断开：它的 Tree 状态被丢弃，进程内会话不受影响
        await ws.DisposeAsync();
        await WaitUntilAsync(() => !_server.IsConnected);
        await _ui.InvokeAsync(() => _f.Tree.Insert(_f.Tree.Root, _f.Tree.Create("again")));
        await local.WaitForAsync(m => m["method"] is JsonString s && s.Value == "Tree.childrenChanged"
                                      && m["params"]!["inserted"]!.AsArray().Count == 1);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10);
        }
    }
}
