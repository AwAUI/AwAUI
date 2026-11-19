using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

public class ProtocolVersionTests
{
    [Theory]
    [InlineData("1.0", true, 1, 0)]
    [InlineData("12.34", true, 12, 34)]
    [InlineData("1", false, 0, 0)]
    [InlineData("1.", false, 0, 0)]
    [InlineData(".1", false, 0, 0)]
    [InlineData("1.0.0", false, 0, 0)]
    [InlineData("-1.0", false, 0, 0)]
    [InlineData("1.+2", false, 0, 0)]
    [InlineData("a.b", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void Parse(string text, bool ok, int major, int minor)
    {
        Assert.Equal(ok, ProtocolVersion.TryParse(text, out var v));
        if (ok) { Assert.Equal(major, v.Major); Assert.Equal(minor, v.Minor); Assert.Equal(text, v.ToString()); }
    }

    [Fact]
    public void Negotiate()
    {
        var server = new ProtocolVersion(1, 3);
        Assert.True(server.TryNegotiate(new ProtocolVersion(1, 1), out var a) && a == new ProtocolVersion(1, 1));
        Assert.True(server.TryNegotiate(new ProtocolVersion(1, 9), out var b) && b == new ProtocolVersion(1, 3));
        Assert.False(server.TryNegotiate(new ProtocolVersion(2, 0), out _));
    }
}

public class AgentTests
{
    [Fact]
    public async Task Hello_negotiates_minor_version_and_reports_app()
    {
        var (agent, client, session) = RpcClient.Create(new DevToolsAgentOptions { ProtocolVersion = new ProtocolVersion(1, 3) });
        agent.Options.AppInfo.Name = "Demo";
        agent.Options.AppInfo.RenderBackend = "software";
        agent.AddTarget(new DevToolsTarget("w1", "Main", new Size(800, 600), 2f));

        var r = Rpc.Result(await client.HelloAsync("1.1")).AsObject();
        Assert.Equal("1.1", r["protocolVersion"]!.GetString());
        Assert.Equal("Demo", r["app"]!["name"]!.GetString());
        Assert.Equal("software", r["app"]!["renderBackend"]!.GetString());
        Assert.False(string.IsNullOrEmpty(r["app"]!["runtime"]!.GetString()));
        Assert.Equal(JsonValueKind.Boolean, r["app"]!["features"]!["aot"]!.Kind);
        var t = r["targets"]![0];
        Assert.Equal("w1", t["id"]!.GetString());
        Assert.Equal(800, t["size"]!["width"]!.GetDouble());
        Assert.Equal(2, t["scale"]!.GetDouble());
        var domains = r["domains"]!.AsArray().Select(d => d.GetString()).ToArray();
        Assert.Contains("App", domains);
        Assert.Contains("Console", domains);
        Assert.True(session.HasHandshake);
        Assert.Equal("test", session.ClientName);
        Assert.Equal(new ProtocolVersion(1, 1), session.ProtocolVersion);

        Assert.Equal("1.3", Rpc.Result(await client.HelloAsync("1.7"))["protocolVersion"]!.GetString());
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    [InlineData("one")]
    public async Task Hello_rejects_incompatible_or_malformed_version(string version)
    {
        var (_, client, session) = RpcClient.Create();
        var e = Rpc.Error(await client.HelloAsync(version), DevToolsErrorCodes.InvalidParams);
        Assert.Equal("1.0", e["data"]!["protocolVersion"]!.GetString());
        Assert.False(session.HasHandshake);
    }

    [Fact]
    public async Task Hello_requires_parameters()
    {
        var (_, client, _) = RpcClient.Create();
        var e = Rpc.Error(await client.CallAsync("App.hello", new JsonObject { ["client"] = "x" }), DevToolsErrorCodes.InvalidParams);
        Assert.Equal("protocolVersion", e["data"]!["parameter"]!.GetString());
        Rpc.Error(await client.CallAsync("App.hello", new JsonObject { ["client"] = 1, ["protocolVersion"] = "1.0" }), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Pairing_token_gates_everything_but_hello()
    {
        var (agent, client, session) = RpcClient.Create(new DevToolsAgentOptions { PairingToken = "s3cret" });
        Assert.False(session.IsAuthorized);
        Rpc.Error(await client.CallAsync("App.getInfo"), DevToolsErrorCodes.Unauthorized);
        Rpc.Error(await client.CallAsync("Console.enable"), DevToolsErrorCodes.Unauthorized);
        Rpc.Error(await client.CallAsync("No.such"), DevToolsErrorCodes.Unauthorized);   // 未配对时不泄露方法是否存在
        Rpc.Error(await client.HelloAsync(), DevToolsErrorCodes.Unauthorized);
        Rpc.Error(await client.HelloAsync(token: "s3cre"), DevToolsErrorCodes.Unauthorized);
        Rpc.Error(await client.HelloAsync(token: "s3cret!"), DevToolsErrorCodes.Unauthorized);
        Assert.False(session.IsAuthorized);

        // 未配对的会话收不到事件，即便 Domain 被别人启用
        agent.Console.Log(LogLevel.Info, "hidden");

        Rpc.Result(await client.HelloAsync(token: "s3cret"));
        Assert.True(session.IsAuthorized);
        Rpc.Result(await client.CallAsync("App.getInfo"));
        Assert.Empty(client.Events("Console.entry"));
    }

    [Fact]
    public async Task Pairing_is_reset_when_the_connection_drops()
    {
        var agent = new DevToolsAgent(new DevToolsAgentOptions { PairingToken = "t" });
        var (a, c) = InProcTransport.CreatePair();
        var session = agent.Attach(a);
        var client = new RpcClient(c);
        Rpc.Result(await client.HelloAsync(token: "t"));
        Rpc.Result(await client.CallAsync("Console.enable"));
        Assert.True(agent.Console.IsEnabled);
        c.Dispose();
        Assert.False(session.IsAuthorized);
        Assert.False(session.HasHandshake);
        Assert.False(agent.Console.IsEnabled);
    }

    [Fact]
    public async Task Version_gated_methods_are_not_found_on_older_minor()
    {
        var (agent, client, _) = RpcClient.Create(new DevToolsAgentOptions { ProtocolVersion = new ProtocolVersion(1, 1) });
        agent.RegisterDomain(new TestDomain());
        Rpc.Result(await client.CallAsync("Test.newMethod"));                       // 握手前按本端版本
        Rpc.Result(await client.HelloAsync("1.0"));
        var e = Rpc.Error(await client.CallAsync("Test.newMethod"), DevToolsErrorCodes.MethodNotFound);
        Assert.Equal("Test.newMethod", e["data"]!["method"]!.GetString());
        Rpc.Result(await client.HelloAsync("1.1"));
        Assert.True(Rpc.Result(await client.CallAsync("Test.newMethod"))["new"]!.GetBoolean());
    }

    [Fact]
    public async Task Unknown_methods_return_method_not_found()
    {
        var (_, client, _) = RpcClient.Create();
        Rpc.Error(await client.CallAsync("Nope.nothing"), DevToolsErrorCodes.MethodNotFound);
        Rpc.Error(await client.CallAsync("App"), DevToolsErrorCodes.MethodNotFound);
        Rpc.Error(await client.CallAsync("app.getInfo"), DevToolsErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task Parse_errors_return_null_id_and_position()
    {
        var (_, client, _) = RpcClient.Create();
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\n\"method\":}");
        var m = client.Messages.Single();
        Assert.True(m["id"]!.IsNull);
        var e = Rpc.Error(m, DevToolsErrorCodes.ParseError);
        Assert.Equal(34, e["data"]!["offset"]!.GetInt64());
        Assert.Equal(2, e["data"]!["line"]!.GetInt64());
        Assert.Equal(10, e["data"]!["column"]!.GetInt64());
    }

    [Theory]
    [InlineData("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.getInfo\"}]")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"x\"")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"App.getInfo\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":[1],\"method\":\"App.getInfo\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"App.getInfo\"}")]
    public async Task Invalid_requests_without_usable_id(string json)
    {
        var (_, client, _) = RpcClient.Create();
        await client.SendRawAsync(json);
        var m = client.Messages.Single();
        Assert.True(m["id"]!.IsNull);
        Rpc.Error(m, DevToolsErrorCodes.InvalidRequest);
    }

    [Theory]
    [InlineData("{\"id\":7,\"method\":\"App.getInfo\"}", DevToolsErrorCodes.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":7,\"method\":\"App.getInfo\"}", DevToolsErrorCodes.InvalidRequest)]
    [InlineData("{\"jsonrpc\":2.0,\"id\":7,\"method\":\"App.getInfo\"}", DevToolsErrorCodes.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7}", DevToolsErrorCodes.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":5}", DevToolsErrorCodes.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"App.getInfo\",\"params\":\"x\"}", DevToolsErrorCodes.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"App.getInfo\",\"params\":[1]}", DevToolsErrorCodes.InvalidParams)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"App.getInfo\",\"target\":1}", DevToolsErrorCodes.InvalidRequest)]
    public async Task Invalid_requests_echo_id(string json, int code)
    {
        var (_, client, _) = RpcClient.Create();
        await client.SendRawAsync(json);
        var m = client.Messages.Single();
        Assert.Equal(7, m["id"]!.GetInt64());
        Rpc.Error(m, code);
    }

    [Fact]
    public async Task Ids_are_echoed_verbatim()
    {
        var (_, client, _) = RpcClient.Create();
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"App.getInfo\"}");
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1.50,\"method\":\"App.getInfo\"}");
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"App.getInfo\"}");
        var ms = client.Messages;
        Assert.Equal(3, ms.Count);
        Assert.Equal("abc", ms[0]["id"]!.GetString());
        Assert.Equal("1.50", ((JsonNumber)ms[1]["id"]!).RawText);
        Assert.True(ms[2]["id"]!.IsNull);
        Assert.NotNull(ms[2]["result"]);
    }

    [Fact]
    public async Task Notifications_execute_but_never_respond()
    {
        var (agent, client, session) = RpcClient.Create();
        var test = new TestDomain();
        agent.RegisterDomain(test);
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"Test.enable\"}");
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"Nope.nothing\"}");
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"Test.throws\"}");
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"Test.typed\",\"params\":{}}");
        Assert.Empty(client.Messages);
        Assert.True(session.IsDomainEnabled("Test"));
    }

    [Fact]
    public async Task Target_field_selects_target()
    {
        var (agent, client, _) = RpcClient.Create();
        agent.RegisterDomain(new TestDomain());
        Assert.True(Rpc.Result(await client.CallAsync("Test.echo"))["target"]!.IsNull);   // 尚无目标
        agent.AddTarget(new DevToolsTarget("w1", "One", new Size(1, 1)));
        agent.AddTarget(new DevToolsTarget("w2", "Two", new Size(1, 1)));
        Assert.Equal("w1", Rpc.Result(await client.CallAsync("Test.echo"))["target"]!.GetString());
        Assert.Equal("w2", Rpc.Result(await client.CallAsync("Test.echo", null, "w2"))["target"]!.GetString());
        var e = Rpc.Error(await client.CallAsync("Test.echo", null, "w9"), DevToolsErrorCodes.TargetNotFound);
        Assert.Equal("w9", e["data"]!["target"]!.GetString());
        Assert.True(agent.RemoveTarget("w2"));
        Rpc.Error(await client.CallAsync("Test.echo", null, "w2"), DevToolsErrorCodes.TargetNotFound);
        Assert.Throws<InvalidOperationException>(() => agent.AddTarget(new DevToolsTarget("w1", "dup", default)));
    }

    [Fact]
    public async Task Domain_enable_gates_methods_events_and_probes()
    {
        var cat = DiagnosticCategory.Custom + 20;
        var (agent, client, _) = RpcClient.Create();
        var test = new TestDomain(cat);
        agent.RegisterDomain(test);

        var e = Rpc.Error(await client.CallAsync("Test.needsEnable"), DevToolsErrorCodes.DomainNotEnabled);
        Assert.Equal("Test", e["data"]!["domain"]!.GetString());
        test.Fire("before", new JsonObject());
        Assert.False(DiagnosticHub.IsEnabled(cat));

        Rpc.Result(await client.CallAsync("Test.enable"));
        Rpc.Result(await client.CallAsync("Test.enable"));   // 幂等
        Assert.Equal(1, test.Enabled);
        Assert.True(test.IsEnabled);
        Assert.True(DiagnosticHub.IsEnabled(cat));
        Assert.True(Rpc.Result(await client.CallAsync("Test.needsEnable"))["ok"]!.GetBoolean());
        test.Fire("changed", new JsonObject { ["x"] = 1 }, "w1");
        var ev = client.Events("Test.changed").Single();
        Assert.Equal(1, ev["params"]!["x"]!.GetInt64());
        Assert.Equal("w1", ev["target"]!.GetString());
        Assert.Null(ev["id"]);
        Assert.Empty(client.Events("Test.before"));

        Rpc.Result(await client.CallAsync("Test.disable"));
        Assert.Equal(1, test.Disabled);
        Assert.False(DiagnosticHub.IsEnabled(cat));
        Rpc.Error(await client.CallAsync("Test.needsEnable"), DevToolsErrorCodes.DomainNotEnabled);
        test.Fire("after", new JsonObject());
        Assert.Empty(client.Events("Test.after"));
    }

    [Fact]
    public async Task Events_go_only_to_sessions_that_enabled_the_domain()
    {
        var agent = new DevToolsAgent();
        var test = new TestDomain();
        agent.RegisterDomain(test);
        var (a1, c1) = InProcTransport.CreatePair();
        var (a2, c2) = InProcTransport.CreatePair();
        agent.Attach(a1);
        agent.Attach(a2);
        var one = new RpcClient(c1);
        var two = new RpcClient(c2);
        Rpc.Result(await one.CallAsync("Test.enable"));
        test.Fire("e", new JsonObject());
        Assert.Single(one.Events("Test.e"));
        Assert.Empty(two.Events("Test.e"));
        Rpc.Result(await two.CallAsync("Test.enable"));
        Rpc.Result(await one.CallAsync("Test.disable"));
        Assert.True(test.IsEnabled);    // 仍有一个会话启用
        test.Fire("e", new JsonObject());
        Assert.Single(one.Events("Test.e"));
        Assert.Single(two.Events("Test.e"));
    }

    [Fact]
    public async Task Coalesced_events_are_flushed_once_per_frame_last_value_wins()
    {
        var (agent, client, _) = RpcClient.Create();
        var test = new TestDomain();
        agent.RegisterDomain(test);
        test.FireCoalesced("attr", "n1", new JsonObject { ["v"] = 0 });   // 未启用：不入队
        Rpc.Result(await client.CallAsync("Test.enable"));
        client.ClearMessages();

        test.FireCoalesced("attr", "n1", new JsonObject { ["v"] = 1 });
        test.FireCoalesced("attr", "n2", new JsonObject { ["v"] = 2 });
        test.FireCoalesced("attr", "n1", new JsonObject { ["v"] = 3 });
        test.FireCoalesced("frame", "n1", new JsonObject { ["v"] = 4 });   // 不同事件名，键独立
        Assert.Empty(client.Messages);

        agent.FlushFrame();
        Assert.Equal(1, test.FrameCalls);
        var msgs = client.Messages;
        Assert.Equal(3, msgs.Count);
        Assert.Equal(new long[] { 3, 2, 4 }, msgs.Select(m => m["params"]!["v"]!.GetInt64()).ToArray());
        Assert.Equal("Test.attr", msgs[0]["method"]!.GetString());
        Assert.Equal("Test.frame", msgs[2]["method"]!.GetString());

        agent.FlushFrame();
        Assert.Equal(3, client.Messages.Count);
    }

    [Fact]
    public async Task Handler_errors_map_to_error_codes()
    {
        var (agent, client, _) = RpcClient.Create();
        agent.RegisterDomain(new TestDomain());
        var internalError = Rpc.Error(await client.CallAsync("Test.throws"), DevToolsErrorCodes.InternalError);
        Assert.Contains("boom", internalError["message"]!.GetString());
        var notFound = Rpc.Error(await client.CallAsync("Test.fail"), DevToolsErrorCodes.NodeNotFound);
        Assert.Equal("Node not found", notFound["message"]!.GetString());
        Assert.Equal(345, notFound["data"]!["nodeId"]!.GetInt64());
    }

    [Fact]
    public async Task Typed_parameter_helpers_validate()
    {
        var (agent, client, _) = RpcClient.Create();
        agent.RegisterDomain(new TestDomain());
        var ok = Rpc.Result(await client.CallAsync("Test.typed", new JsonObject { ["s"] = "x", ["i"] = 2.0, ["b"] = true, ["o"] = null }));
        Assert.Equal("x", ok["s"]!.GetString());
        Assert.Equal(2, ok["i"]!.GetInt64());
        Assert.True(ok["o"]!.IsNull);
        Rpc.Error(await client.CallAsync("Test.typed", new JsonObject { ["s"] = 1, ["i"] = 2 }), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await client.CallAsync("Test.typed", new JsonObject { ["s"] = "x", ["i"] = 2.5 }), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await client.CallAsync("Test.typed", new JsonObject { ["s"] = "x" }), DevToolsErrorCodes.InvalidParams);
        Rpc.Error(await client.CallAsync("Test.typed", new JsonObject { ["s"] = "x", ["i"] = 1, ["b"] = "yes" }), DevToolsErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Async_handlers_respond_later()
    {
        var (agent, client, _) = RpcClient.Create();
        agent.RegisterDomain(new TestDomain());
        var r = Rpc.Result(await client.CallAsync("Test.slow", new JsonObject { ["v"] = 42 }));
        Assert.Equal(42, r["v"]!.GetInt64());
    }

    [Fact]
    public async Task Blobs_follow_their_response_in_order()
    {
        var (agent, client, _) = RpcClient.Create();
        agent.RegisterDomain(new TestDomain());
        var resp = await client.CallAsync("Test.blob", new JsonObject { ["size"] = 5 });
        string blobId = Rpc.Result(resp)["blobId"]!.GetString();
        var announced = resp["blobs"]!.AsArray().Single();
        Assert.Equal(blobId, announced["blobId"]!.GetString());
        Assert.Equal(5, announced["length"]!.GetInt64());
        Assert.Equal("application/x-test", announced["mimeType"]!.GetString());
        Assert.Equal(new[] { "text", "blob" }, client.Order);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, client.Blobs.Single());

        var second = Rpc.Result(await client.CallAsync("Test.blob"))["blobId"]!.GetString();
        Assert.NotEqual(blobId, second);
    }

    [Fact]
    public async Task Dispatcher_moves_processing_to_ui_thread()
    {
        var dispatcher = new ManualDispatcher();
        var agent = new DevToolsAgent(new DevToolsAgentOptions { Dispatcher = dispatcher });
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        var client = new RpcClient(c);
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.getInfo\"}");
        Assert.Empty(client.Messages);
        Assert.Equal(1, dispatcher.Pending);
        dispatcher.RunAll();
        Rpc.Result(client.Messages.Single());
    }

    [Fact]
    public void Domain_registration_rules()
    {
        var agent = new DevToolsAgent();
        agent.RegisterDomain(new TestDomain());
        Assert.Throws<InvalidOperationException>(() => agent.RegisterDomain(new TestDomain()));
        Assert.Throws<InvalidOperationException>(() => new DevToolsAgent().RegisterDomain(agent.Console));
        Assert.Equal(new[] { "App", "Console", "Tree", "Css", "Layout", "Overlay", "Test" }, agent.Domains.Select(d => d.Name).ToArray());
    }

    [Fact]
    public async Task App_getInfo_returns_app_info()
    {
        var (agent, client, _) = RpcClient.Create();
        agent.Options.AppInfo.Capabilities.Add("gpu");
        var info = Rpc.Result(await client.CallAsync("App.getInfo"));
        Assert.Equal("AwAUI App", info["name"]!.GetString());
        Assert.Equal("gpu", info["capabilities"]![0].GetString());
        if (OperatingSystem.IsMacOS()) Assert.Equal("macos", info["platform"]!.GetString());
        Assert.False(string.IsNullOrEmpty(info["frameworkVersion"]!.GetString()));
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":1,\"params\":\"bar\"}")]      // JSON-RPC 2.0 规范自带的例子
    [InlineData("{\"jsonrpc\":\"1.0\",\"method\":\"App.getInfo\"}")]
    [InlineData("{\"method\":\"App.getInfo\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"App.getInfo\",\"params\":\"x\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"App.getInfo\",\"target\":1}")]
    public async Task Invalid_request_objects_without_id_still_get_an_error(string json)
    {
        var (_, client, _) = RpcClient.Create();
        await client.SendRawAsync(json);
        var m = client.Messages.Single();
        Assert.True(m["id"]!.IsNull);
        Rpc.Error(m, DevToolsErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Positional_params_in_a_notification_are_a_silent_method_error()
    {
        var (_, client, _) = RpcClient.Create();
        await client.SendRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"App.getInfo\",\"params\":[1]}");
        Assert.Empty(client.Messages);
    }

    sealed class RemoteFakeTransport : DevToolsTransport
    {
        public readonly List<string> Sent = new();
        public override bool IsConnected => true;
        public override bool AcceptsRemoteConnections => true;
        public override Task SendAsync(ReadOnlyMemory<byte> message)
        {
            lock (Sent) Sent.Add(System.Text.Encoding.UTF8.GetString(message.Span));
            return Task.CompletedTask;
        }
        public void Deliver(string json) => OnReceived(System.Text.Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void Remote_transports_require_a_pairing_token()
    {
        var remote = new RemoteFakeTransport();
        Assert.Throws<InvalidOperationException>(() => new DevToolsAgent().Attach(remote));

        // 挂接后再清空令牌：每个请求（包括 App.hello）都被拒绝
        var options = new DevToolsAgentOptions { PairingToken = "t" };
        var agent = new DevToolsAgent(options);
        agent.Attach(remote);
        options.PairingToken = null;
        remote.Deliver("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.hello\",\"params\":{\"client\":\"x\",\"protocolVersion\":\"1.0\"}}");
        var reply = JsonValue.Parse(remote.Sent.Single()).AsObject();
        Rpc.Error(reply, DevToolsErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Reentrant_in_proc_requests_do_not_split_a_response_from_its_blobs()
    {
        var agent = new DevToolsAgent();
        agent.RegisterDomain(new TestDomain());
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        var log = new List<string>();
        c.Received += m =>
        {
            var o = JsonValue.Parse(m.Span).AsObject();
            long id = o["id"]!.GetInt64();
            log.Add("text" + id + ":" + o["blobs"]![0]["length"]!.GetInt64());
            // 内嵌前端在 UI 线程上收到响应后立刻发出下一个请求（重入）
            if (id == 1)
                c.SendAsync(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Test.blob\",\"params\":{\"size\":2}}"));
        };
        c.BlobReceived += b => log.Add("blob:" + b.Length);
        await c.SendAsync(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Test.blob\",\"params\":{\"size\":1}}"));
        Assert.Equal(new[] { "text1:1", "blob:1", "text2:2", "blob:2" }, log);
    }

    [Fact]
    public async Task Cross_thread_in_proc_clients_with_their_own_lock_do_not_deadlock()
    {
        var agent = new DevToolsAgent();
        agent.RegisterDomain(new TestDomain());
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        var clientLock = new object();
        int replies = 0, blobs = 0;
        c.Received += _ => { lock (clientLock) replies++; };
        c.BlobReceived += _ => { lock (clientLock) blobs++; };
        var request = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Test.blob\"}");
        const int N = 2000;
        var t1 = Task.Run(() => { for (int i = 0; i < N; i++) c.SendAsync(request); });
        var t2 = Task.Run(() => { for (int i = 0; i < N; i++) lock (clientLock) c.SendAsync(request); });
        var all = Task.WhenAll(t1, t2);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20))));
        await all;
        // 有竞争时投递可能转到线程池继续，等它完成
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            lock (clientLock)
                if (replies == 2 * N && blobs == 2 * N) break;
            Assert.True(DateTime.UtcNow < deadline, $"replies={replies} blobs={blobs}");
            await Task.Delay(10);
        }
    }
}
