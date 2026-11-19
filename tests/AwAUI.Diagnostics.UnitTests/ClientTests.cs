using System.Net;
using System.Text;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>前端的协议客户端（<see cref="DevToolsClient"/>）：请求与响应的匹配、事件分发、调度器投递、断开与恶意输入。</summary>
public class DevToolsClientTests
{
    static (DevToolsAgent Agent, DevToolsClient Client, TestDomain Domain) Create(DevToolsClientOptions? options = null)
    {
        var agent = new DevToolsAgent();
        var domain = new TestDomain();
        agent.RegisterDomain(domain);
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        return (agent, new DevToolsClient(c, options), domain);
    }

    [Fact]
    public async Task Calls_are_matched_to_responses_by_id()
    {
        var (_, client, _) = Create();
        var echo = await client.CallAsync("Test.echo", new JsonObject { ["x"] = 1 });
        Assert.Equal(1, echo["params"]!["x"]!.GetInt64());
        var slow = client.CallAsync("Test.slow", new JsonObject { ["v"] = 7 });
        var fast = client.CallAsync("Test.echo", new JsonObject { ["y"] = 2 });
        Assert.Equal(2, (await fast)["params"]!["y"]!.GetInt64());
        Assert.Equal(7, (await slow)["v"]!.GetInt64());
        Assert.Equal(0, client.PendingCount);
    }

    [Fact]
    public async Task Error_responses_become_protocol_exceptions()
    {
        var (_, client, _) = Create();
        var ex = await Assert.ThrowsAsync<DevToolsProtocolException>(() => client.CallAsync("Test.fail"));
        Assert.Equal(DevToolsErrorCodes.NodeNotFound, ex.Code);
        Assert.Equal(345, ex.ErrorData!["nodeId"]!.GetInt64());
        var missing = await Assert.ThrowsAsync<DevToolsProtocolException>(() => client.CallAsync("Nope.nothing"));
        Assert.Equal(DevToolsErrorCodes.MethodNotFound, missing.Code);
    }

    [Fact]
    public async Task Events_are_delivered_with_target()
    {
        var (_, client, domain) = Create();
        var events = new List<DevToolsEvent>();
        client.EventReceived += events.Add;
        await client.CallAsync("Test.enable");
        domain.Fire("ping", new JsonObject { ["v"] = 3 }, "w9");
        var e = Assert.Single(events);
        Assert.Equal("Test.ping", e.Method);
        Assert.Equal(3, e.Params["v"]!.GetInt64());
        Assert.Equal("w9", e.Target);
    }

    [Fact]
    public async Task Hello_negotiates_the_protocol_version()
    {
        var (_, client, _) = Create();
        var tcs = new TaskCompletionSource<DevToolsResponse>();
        client.Hello("test", null, r => tcs.SetResult(r));
        var r = await tcs.Task;
        Assert.False(r.IsError, r.ToString());
        Assert.Equal("1.0", r.ResultObject.GetStringOrDefault("protocolVersion"));
    }

    [Fact]
    public void With_a_dispatcher_callbacks_are_always_posted_in_arrival_order()
    {
        var d = new ManualDispatcher();
        var (_, client, domain) = Create(new DevToolsClientOptions { Dispatcher = d });
        var order = new List<string>();
        client.EventReceived += e => order.Add(e.Method);
        client.Call("Test.enable", null, null, r => order.Add("enable:" + r.IsError));
        // 进程内传输同步投递：响应已到达，但回调只是排进了调度器
        Assert.Empty(order);
        domain.Fire("a", new JsonObject());
        domain.Fire("b", new JsonObject());
        client.Call("Test.echo", null, null, r => order.Add("echo"));
        Assert.Empty(order);
        d.RunAll();
        Assert.Equal(new[] { "enable:False", "Test.a", "Test.b", "echo" }, order);
    }

    [Fact]
    public async Task Disconnect_fails_pending_requests_and_raises_Disconnected()
    {
        var agent = new DevToolsAgent();
        var hang = new HangingDomain();
        agent.RegisterDomain(hang);
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        var client = new DevToolsClient(c);
        bool disconnected = false;
        client.Disconnected += () => disconnected = true;
        var pending = client.CallAsync("Hang.forever");
        Assert.Equal(1, client.PendingCount);
        a.Dispose();
        var ex = await Assert.ThrowsAsync<DevToolsProtocolException>(() => pending);
        Assert.Equal(DevToolsClient.DisconnectedErrorCode, ex.Code);
        Assert.True(disconnected);
        Assert.Equal(0, client.PendingCount);
        hang.Release();
    }

    [Fact]
    public async Task Disposed_client_fails_new_and_pending_calls()
    {
        var agent = new DevToolsAgent();
        var hang = new HangingDomain();
        agent.RegisterDomain(hang);
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        var client = new DevToolsClient(c);
        var pending = client.CallAsync("Hang.forever");
        client.Dispose();
        Assert.Equal(DevToolsClient.DisconnectedErrorCode, (await Assert.ThrowsAsync<DevToolsProtocolException>(() => pending)).Code);
        Assert.Equal(DevToolsClient.DisconnectedErrorCode, (await Assert.ThrowsAsync<DevToolsProtocolException>(() => client.CallAsync("Test.echo"))).Code);
        hang.Release();
    }

    [Fact]
    public async Task Pending_requests_are_bounded()
    {
        var agent = new DevToolsAgent();
        var hang = new HangingDomain();
        agent.RegisterDomain(hang);
        var (a, c) = InProcTransport.CreatePair();
        agent.Attach(a);
        var client = new DevToolsClient(c, new DevToolsClientOptions { MaxPendingRequests = 3 });
        var calls = Enumerable.Range(0, 3).Select(_ => client.CallAsync("Hang.forever")).ToList();
        var ex = await Assert.ThrowsAsync<DevToolsProtocolException>(() => client.CallAsync("Hang.forever"));
        Assert.Equal(DevToolsClient.TooManyRequestsErrorCode, ex.Code);
        hang.Release();
        await Task.WhenAll(calls);
    }

    [Fact]
    public void Malformed_and_unsolicited_messages_are_dropped()
    {
        var (fake, peer) = InProcTransport.CreatePair();
        var client = new DevToolsClient(peer, new DevToolsClientOptions { MaxMessageDepth = 16 });
        var events = new List<DevToolsEvent>();
        client.EventReceived += events.Add;
        void Send(string s) => fake.SendAsync(Encoding.UTF8.GetBytes(s)).Wait();
        Send("not json");
        Send("[1,2,3]");
        Send("{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{}}");          // 没有对应的请求
        Send("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"result\":{}}");
        Send(new string('[', 10_000) + new string(']', 10_000));     // 超过深度上限
        Send("{\"jsonrpc\":\"2.0\",\"method\":\"A.b\",\"params\":5}"); // 参数不是对象：事件照常，参数为空对象
        Assert.Equal(5, client.DroppedMessages);
        var e = Assert.Single(events);
        Assert.Equal("A.b", e.Method);
        Assert.Equal(0, e.Params.Count);
    }

    [Fact]
    public void Responses_without_result_or_error_fail_the_call()
    {
        var (fake, peer) = InProcTransport.CreatePair();
        var client = new DevToolsClient(peer);
        DevToolsResponse? got = null;
        fake.Received += m =>
        {
            long id = JsonValue.Parse(m.Span)["id"]!.GetInt64();
            fake.SendAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + "}"));
        };
        client.Call("X.y", null, null, r => got = r);
        Assert.NotNull(got);
        Assert.True(got!.IsError);
        Assert.Equal(DevToolsErrorCodes.InternalError, got.ErrorCode);
    }

    [Fact]
    public void Rejected_calls_without_a_dispatcher_do_not_run_the_callback_under_the_client_lock()
    {
        // 没有调度器时被拒绝的请求在调用方线程上同步回调；回调里若等待另一个需要访问客户端的线程，不能死锁
        var (a, c) = InProcTransport.CreatePair();
        var client = new DevToolsClient(c);
        a.Dispose();
        bool otherThreadGotIn = false;
        DevToolsResponse? got = null;
        client.Call("X.y", null, null, r =>
        {
            got = r;
            otherThreadGotIn = Task.Run(() => client.PendingCount).Wait(TimeSpan.FromSeconds(10));
        });
        Assert.True(got!.IsDisconnected);
        Assert.True(otherThreadGotIn, "the callback ran while the client held its lock");
    }

    [Fact]
    public void After_a_reconnect_new_calls_work_and_stale_responses_from_the_old_connection_are_dropped()
    {
        var t = new ToggleTransport();
        var client = new DevToolsClient(t);
        int disconnects = 0;
        client.Disconnected += () => disconnects++;
        DevToolsResponse? first = null, second = null;
        client.Call("A.a", null, null, r => first = r);
        long oldId = t.LastId;
        t.Drop();
        Assert.True(first!.IsDisconnected);
        Assert.Equal(1, disconnects);
        // 断开期间的请求立即失败
        DevToolsResponse? during = null;
        client.Call("A.b", null, null, r => during = r);
        Assert.True(during!.IsDisconnected);

        t.Reconnect();
        client.Call("A.c", null, null, r => second = r);
        long newId = t.LastId;
        Assert.NotEqual(oldId, newId);
        // 旧连接上那个请求的迟到响应：没有对应的请求，被丢弃，不会完成新的请求
        t.Deliver("{\"jsonrpc\":\"2.0\",\"id\":" + oldId + ",\"result\":{\"stale\":true}}");
        Assert.Null(second);
        Assert.Equal(1, client.DroppedMessages);
        t.Deliver("{\"jsonrpc\":\"2.0\",\"id\":" + newId + ",\"result\":{\"ok\":true}}");
        Assert.True(second!.ResultObject["ok"] is JsonBoolean);
        Assert.Equal(0, client.PendingCount);
    }

    /// <summary>可以断开再重连的假传输：记录发出的请求 id，测试手动投递响应。</summary>
    sealed class ToggleTransport : DevToolsTransport
    {
        bool _connected = true;
        public long LastId;
        public override bool IsConnected => _connected;
        public override Task SendAsync(ReadOnlyMemory<byte> message)
        {
            if (_connected) LastId = JsonValue.Parse(message.Span)["id"]!.GetInt64();
            return Task.CompletedTask;
        }
        public void Drop()
        {
            _connected = false;
            OnDisconnected();
        }
        public void Reconnect()
        {
            _connected = true;
            OnConnected();
        }
        public void Deliver(string json) => OnReceived(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>不响应的方法（直到测试放行）。</summary>
    sealed class HangingDomain : DevToolsDomain
    {
        readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HangingDomain() : base("Hang") { }
        public void Release() => _gate.TrySetResult(true);
        protected override void RegisterMethods(DevToolsMethodTable m) =>
            m.AddAsync("forever", async _ =>
            {
                await _gate.Task.ConfigureAwait(false);
                return new JsonObject();
            });
    }
}

/// <summary>WebSocket 客户端传输：与服务端传输 + Agent 端到端。</summary>
public class WebSocketClientTransportTests
{
    static TimeSpan Timeout => TimeSpan.FromSeconds(20);

    static (DevToolsAgent Agent, WebSocketServerTransport Server, TestDomain Domain) StartServer(int maxMessageSize = 16 * 1024 * 1024)
    {
        var agent = new DevToolsAgent();
        var domain = new TestDomain();
        agent.RegisterDomain(domain);
        var server = new WebSocketServerTransport(new WebSocketServerOptions { Port = 0, Path = "/devtools", MaxMessageSize = maxMessageSize });
        agent.Attach(server);
        server.Start();
        return (agent, server, domain);
    }

    static Uri UriOf(WebSocketServerTransport server) => new($"ws://127.0.0.1:{server.LocalEndPoint!.Port}/devtools");

    [Fact]
    public async Task Client_talks_to_the_agent_over_a_real_socket()
    {
        var (_, server, domain) = StartServer();
        try
        {
            using var transport = await WebSocketClientTransport.ConnectAsync(UriOf(server));
            Assert.True(transport.IsConnected);
            using var client = new DevToolsClient(transport);
            var events = new List<DevToolsEvent>();
            var gotEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.EventReceived += e =>
            {
                lock (events) events.Add(e);
                gotEvent.TrySetResult(true);
            };
            var hello = await client.CallAsync("App.hello", new JsonObject { ["client"] = "t", ["protocolVersion"] = "1.0" }).WaitAsync(Timeout);
            Assert.Equal("1.0", hello["protocolVersion"]!.GetString());
            await client.CallAsync("Test.enable").WaitAsync(Timeout);
            var big = new string('x', 300_000);    // 分片 / 大消息
            var echo = await client.CallAsync("Test.echo", new JsonObject { ["s"] = big }).WaitAsync(Timeout);
            Assert.Equal(big, echo["params"]!["s"]!.GetString());
            domain.Fire("ping", new JsonObject { ["v"] = 1 });
            await gotEvent.Task.WaitAsync(Timeout);
            lock (events) Assert.Equal("Test.ping", events[0].Method);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Server_close_disconnects_the_client_and_fails_pending_calls()
    {
        var (_, server, _) = StartServer();
        using var transport = await WebSocketClientTransport.ConnectAsync(UriOf(server));
        using var client = new DevToolsClient(transport);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult(true);
        await client.CallAsync("Test.echo").WaitAsync(Timeout);
        await server.StopAsync();
        await disconnected.Task.WaitAsync(Timeout);
        Assert.False(transport.IsConnected);
        // 断开之后的发送被丢弃，请求以「断开」失败而不是挂起
        var ex = await Assert.ThrowsAsync<DevToolsProtocolException>(() => client.CallAsync("Test.echo").WaitAsync(Timeout));
        Assert.Equal(DevToolsClient.DisconnectedErrorCode, ex.Code);
    }

    [Fact]
    public async Task Oversized_incoming_messages_close_the_connection()
    {
        var (_, server, domain) = StartServer();
        try
        {
            using var transport = await WebSocketClientTransport.ConnectAsync(UriOf(server), new WebSocketClientTransportOptions { MaxMessageSize = 64 * 1024 });
            using var client = new DevToolsClient(transport);
            var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += () => disconnected.TrySetResult(true);
            await client.CallAsync("Test.enable").WaitAsync(Timeout);
            domain.Fire("big", new JsonObject { ["s"] = new string('y', 200_000) });
            await disconnected.Task.WaitAsync(Timeout);
            Assert.False(transport.IsConnected);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Connect_rejects_bad_uris_and_unreachable_servers()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => WebSocketClientTransport.ConnectAsync(new Uri("http://127.0.0.1:1/x")));
        // 找一个没有监听的端口
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => WebSocketClientTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools"),
            new WebSocketClientTransportOptions { ConnectTimeout = TimeSpan.FromSeconds(5) }));
    }

    [Fact]
    public async Task Disconnect_closes_gracefully()
    {
        var (agent, server, _) = StartServer();
        try
        {
            var transport = await WebSocketClientTransport.ConnectAsync(UriOf(server));
            using var client = new DevToolsClient(transport);
            await client.CallAsync("Test.echo").WaitAsync(Timeout);
            await transport.DisconnectAsync("bye").WaitAsync(Timeout);
            Assert.False(transport.IsConnected);
            transport.Dispose();
            // 服务端随后可以接受新的连接（旧连接的关闭握手可能还没处理完：重试）
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                try
                {
                    using var again = await WebSocketClientTransport.ConnectAsync(UriOf(server));
                    using var client2 = new DevToolsClient(again);
                    await client2.CallAsync("Test.echo").WaitAsync(TimeSpan.FromSeconds(2));
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }
            }
            Assert.NotNull(agent);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
