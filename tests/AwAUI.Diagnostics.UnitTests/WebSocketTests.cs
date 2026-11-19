using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

public class WebSocketHandshakeTests
{
    [Fact]
    public void Close_reason_is_truncated_on_a_utf8_boundary()
    {
        string reason = "a" + new string('你', 100);    // 1 + 300 字节
        var p = WebSocketConnection.ClosePayload(1008, reason);
        Assert.True(p.Length <= 125);
        Assert.Equal(2 + 1 + 3 * 40, p.Length);
        string decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(p, 2, p.Length - 2);
        Assert.Equal(reason.Substring(0, 41), decoded);
        Assert.Equal(1008, (p[0] << 8) | p[1]);
        Assert.Equal(2 + 5, WebSocketConnection.ClosePayload(1000, "short").Length);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1011, true)]
    [InlineData(1012, true)]
    [InlineData(1013, true)]
    [InlineData(1014, true)]
    [InlineData(1004, false)]
    [InlineData(1005, false)]
    [InlineData(1006, false)]
    [InlineData(1015, false)]
    [InlineData(2999, false)]
    [InlineData(3000, true)]
    [InlineData(4999, true)]
    [InlineData(5000, false)]
    public void Close_codes(int code, bool valid) => Assert.Equal(valid, WebSocketConnection.IsValidCloseCode(code));

    [Fact]
    public void Accept_key_matches_rfc6455_example()
    {
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebSocketHandshake.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    static string Request(params string[] headers) =>
        "GET /devtools?x=1 HTTP/1.1\r\n" + string.Join("\r\n", headers);

    static readonly string[] Good =
    {
        "Host: 127.0.0.1:9222", "Upgrade: websocket", "Connection: keep-alive, Upgrade",
        "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", "Sec-WebSocket-Version: 13",
    };

    [Fact]
    public void Validates_requests()
    {
        var opts = new WebSocketServerOptions { Path = "/devtools" };
        Assert.True(WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Good)), opts).Accepted);
        Assert.Equal(404, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Good)), new WebSocketServerOptions { Path = "/other" }).Status);

        string[] Without(string prefix) => Good.Where(h => !h.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(400, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Without("Upgrade"))), opts).Status);
        Assert.Equal(400, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Without("Connection"))), opts).Status);
        Assert.Equal(400, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Without("Sec-WebSocket-Key"))), opts).Status);
        Assert.Equal(400, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Without("Host"))), opts).Status);
        Assert.Equal(426, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Without("Sec-WebSocket-Version").Append("Sec-WebSocket-Version: 8").ToArray())), opts).Status);
        Assert.Equal(400, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Without("Sec-WebSocket-Key").Append("Sec-WebSocket-Key: c2hvcnQ=").ToArray())), opts).Status);
        Assert.Equal(403, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Good.Append("Origin: https://evil.example").ToArray())), opts).Status);
        Assert.True(WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Good.Append("Origin: http://localhost:3000").ToArray())), opts).Accepted);
        Assert.Equal(403, WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Good.Append("Origin: null").ToArray())), opts).Status);
        Assert.Equal(405, WebSocketHandshake.Validate(WebSocketHandshake.Parse("POST / HTTP/1.1\r\n" + string.Join("\r\n", Good)), opts).Status);
        Assert.Equal(400, WebSocketHandshake.Validate(WebSocketHandshake.Parse("GET / HTTP/1.0\r\n" + string.Join("\r\n", Good)), new WebSocketServerOptions()).Status);
        Assert.Equal(400, WebSocketHandshake.Validate(WebSocketHandshake.Parse("garbage"), opts).Status);
        // 头名大小写不敏感，令牌大小写不敏感
        Assert.True(WebSocketHandshake.Validate(WebSocketHandshake.Parse(Request(Good.Select(h => h.ToUpperInvariant().Replace("DGHLIHNHBXBSZSBUB25JZQ==", "dGhlIHNhbXBsZSBub25jZQ==")).ToArray())), opts).Accepted);
    }
}

/// <summary>端到端：真实 TCP 上的 WebSocket 服务器 + Agent。</summary>
public sealed class WebSocketServerTests : IAsyncLifetime
{
    WebSocketServerTransport _server = null!;
    DevToolsAgent _agent = null!;
    DevToolsSession _session = null!;
    TestDomain _test = null!;
    readonly List<byte[]> _serverBlobs = new();
    int _connects, _disconnects;

    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    // 关闭超时缩短：原始客户端不回关闭帧时服务端会等满 CloseTimeout
    public Task InitializeAsync() => StartAsync(new WebSocketServerOptions { CloseTimeout = TimeSpan.FromMilliseconds(300) });

    Task StartAsync(WebSocketServerOptions options)
    {
        _server = new WebSocketServerTransport(options);
        _agent = new DevToolsAgent(new DevToolsAgentOptions { PairingToken = "pair-123" });
        _test = new TestDomain();
        _agent.RegisterDomain(_test);
        _session = _agent.Attach(_server);
        _server.BlobReceived += b => { lock (_serverBlobs) _serverBlobs.Add(b.ToArray()); };
        _server.Connected += () => Interlocked.Increment(ref _connects);
        _server.Disconnected += () => Interlocked.Increment(ref _disconnects);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
        _agent.Dispose();
    }

    Uri Uri => new($"ws://127.0.0.1:{_server.LocalEndPoint!.Port}/devtools");

    static async Task SendAsync(ClientWebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Timeout());

    static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, Timeout());
            if (r.MessageType == WebSocketMessageType.Close) return (r.MessageType, Array.Empty<byte>());
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
        }
    }

    static async Task<JsonObject> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var (type, data) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Text, type);
        return JsonValue.Parse(data).AsObject();
    }

    async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(Uri, Timeout());
        return ws;
    }

    [Fact]
    public void Binds_to_loopback_by_default()
    {
        Assert.Equal(IPAddress.Loopback, _server.LocalEndPoint!.Address);
        Assert.NotEqual(0, _server.LocalEndPoint.Port);
    }

    [Fact]
    public async Task ClientWebSocket_end_to_end_hello_and_console_event()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Console.enable\"}");
        Rpc.Error(await ReceiveJsonAsync(ws), DevToolsErrorCodes.Unauthorized);

        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"App.hello\",\"params\":{\"client\":\"xunit\",\"protocolVersion\":\"1.0\",\"token\":\"wrong\"}}");
        Rpc.Error(await ReceiveJsonAsync(ws), DevToolsErrorCodes.Unauthorized);

        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"App.hello\",\"params\":{\"client\":\"xunit\",\"protocolVersion\":\"1.0\",\"token\":\"pair-123\"}}");
        var hello = await ReceiveJsonAsync(ws);
        Assert.Equal(3, hello["id"]!.GetInt64());
        Assert.Equal("1.0", Rpc.Result(hello)["protocolVersion"]!.GetString());

        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"Console.enable\"}");
        var enabled = await ReceiveJsonAsync(ws);
        Assert.Equal(4, enabled["id"]!.GetInt64());
        Assert.Empty(Rpc.Result(enabled).AsObject());

        _agent.Console.Log(LogLevel.Info, "hello from the app — 你好");
        var entry = await ReceiveJsonAsync(ws);
        Assert.Equal("Console.entry", entry["method"]!.GetString());
        Assert.Equal("hello from the app — 你好", entry["params"]!["message"]!.GetString());
        Assert.Equal("info", entry["params"]!["level"]!.GetString());

        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.True(_server.IsConnected);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Timeout());
        Assert.Equal(WebSocketCloseStatus.NormalClosure, ws.CloseStatus);
        await WaitUntilAsync(() => Volatile.Read(ref _disconnects) == 1 && !_server.IsConnected);
        Assert.False(_session.IsAuthorized);
    }

    [Fact]
    public async Task Blobs_are_sent_as_binary_frames_after_the_response()
    {
        using var ws = await ConnectAsync();
        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.hello\",\"params\":{\"client\":\"x\",\"protocolVersion\":\"1.0\",\"token\":\"pair-123\"}}");
        Rpc.Result(await ReceiveJsonAsync(ws));
        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Test.blob\",\"params\":{\"size\":70000}}");
        var resp = await ReceiveJsonAsync(ws);
        Assert.Equal(70000, resp["blobs"]![0]["length"]!.GetInt64());
        var (type, data) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(70000, data.Length);
        Assert.Equal(1, data[0]);
        Assert.Equal(unchecked((byte)70000), data[69999]);

        // 客户端发来的二进制消息走 BlobReceived
        await ws.SendAsync(new byte[] { 9, 8, 7 }, WebSocketMessageType.Binary, true, Timeout());
        await WaitUntilAsync(() => { lock (_serverBlobs) return _serverBlobs.Count == 1; });
        lock (_serverBlobs) Assert.Equal(new byte[] { 9, 8, 7 }, _serverBlobs[0]);
    }

    [Fact]
    public async Task Large_and_fragmented_client_messages_are_reassembled()
    {
        using var ws = await ConnectAsync();
        string big = new string('x', 300_000);
        string hello = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.hello\",\"params\":{\"client\":\"" + big + "\",\"protocolVersion\":\"1.0\",\"token\":\"pair-123\"}}";
        var bytes = Encoding.UTF8.GetBytes(hello);
        // ClientWebSocket 按我们给的块分片发送
        for (int off = 0; off < bytes.Length; off += 50_000)
        {
            int n = Math.Min(50_000, bytes.Length - off);
            await ws.SendAsync(new ArraySegment<byte>(bytes, off, n), WebSocketMessageType.Text, off + n == bytes.Length, Timeout());
        }
        Rpc.Result(await ReceiveJsonAsync(ws));
        Assert.Equal(big, _session.ClientName);
        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Test.echo\",\"params\":{\"big\":\"" + big + "\"}}");
        var echo = Rpc.Result(await ReceiveJsonAsync(ws));
        Assert.Equal(big, echo["params"]!["big"]!.GetString());
    }

    [Fact]
    public async Task Second_client_is_rejected_while_one_is_connected_and_reconnect_resets_session()
    {
        using var first = await ConnectAsync();
        await SendAsync(first, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.hello\",\"params\":{\"client\":\"a\",\"protocolVersion\":\"1.0\",\"token\":\"pair-123\"}}");
        Rpc.Result(await ReceiveJsonAsync(first));

        using var raw = new RawClient();
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        string status = await raw.HandshakeAsync();
        Assert.StartsWith("HTTP/1.1 503", status);

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Timeout());
        await WaitUntilAsync(() => !_server.IsConnected);

        using var second = await ConnectAsync();
        await SendAsync(second, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.getInfo\"}");
        Rpc.Error(await ReceiveJsonAsync(second), DevToolsErrorCodes.Unauthorized);   // 新连接需要重新配对
    }

    [Fact]
    public async Task Raw_fragmentation_with_interleaved_ping()
    {
        using var raw = new RawClient();
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        var msg = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"App.hello\",\"params\":{\"client\":\"raw\",\"protocolVersion\":\"1.0\",\"token\":\"pair-123\"}}");
        await raw.SendFrameAsync(false, 0x1, msg.AsSpan(0, 10).ToArray());
        await raw.SendFrameAsync(true, 0x9, "ping!"u8.ToArray());           // 控制帧可夹在分片之间
        await raw.SendFrameAsync(false, 0x0, msg.AsSpan(10, 30).ToArray());
        await raw.SendFrameAsync(true, 0x0, msg.AsSpan(40).ToArray());

        var pong = await raw.ReadFrameAsync();
        Assert.Equal(0xA, pong.Opcode);
        Assert.Equal("ping!", Encoding.UTF8.GetString(pong.Payload));
        var resp = await raw.ReadFrameAsync();
        Assert.Equal(0x1, resp.Opcode);
        Assert.True(resp.Fin);
        var hello = JsonValue.Parse(resp.Payload).AsObject();
        Rpc.Result(hello);
        Assert.Equal(5, hello["id"]!.GetInt64());

        // 64 位长度编码的帧
        await raw.SendFrameAsync(true, 0x1, Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"App.getInfo\"}"), forceLength64: true);
        Assert.Equal(6, JsonValue.Parse((await raw.ReadFrameAsync()).Payload)["id"]!.GetInt64());

        // 关闭握手：服务端回显关闭码后断开 TCP
        await raw.SendFrameAsync(true, 0x8, new byte[] { 0x03, 0xE8, (byte)'o', (byte)'k' });
        var close = await raw.ReadFrameAsync();
        Assert.Equal(0x8, close.Opcode);
        Assert.Equal(1000, close.CloseCode);
        Assert.True(await raw.IsClosedAsync());
    }

    [Theory]
    [InlineData("unmasked", 1002)]
    [InlineData("rsv", 1002)]
    [InlineData("opcode", 1002)]
    [InlineData("continuation", 1002)]
    [InlineData("nested", 1002)]
    [InlineData("fragmentedPing", 1002)]
    [InlineData("longPing", 1002)]
    [InlineData("utf8", 1007)]
    [InlineData("closeCode", 1002)]
    [InlineData("closeOneByte", 1002)]
    [InlineData("closeUtf8", 1007)]
    public async Task Protocol_violations_close_the_connection_with_status(string kind, int code)
    {
        using var raw = new RawClient();
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        switch (kind)
        {
            case "unmasked": await raw.SendFrameAsync(true, 0x1, "{}"u8.ToArray(), masked: false); break;
            case "rsv": await raw.SendFrameAsync(true, 0x1, "{}"u8.ToArray(), rsv: 0x40); break;
            case "opcode": await raw.SendFrameAsync(true, 0x3, "{}"u8.ToArray()); break;
            case "continuation": await raw.SendFrameAsync(true, 0x0, "{}"u8.ToArray()); break;
            case "nested":
                await raw.SendFrameAsync(false, 0x1, "{"u8.ToArray());
                await raw.SendFrameAsync(true, 0x1, "}"u8.ToArray());
                break;
            case "fragmentedPing": await raw.SendFrameAsync(false, 0x9, "x"u8.ToArray()); break;
            case "longPing": await raw.SendFrameAsync(true, 0x9, new byte[126]); break;
            case "utf8": await raw.SendFrameAsync(true, 0x1, new byte[] { 0x22, 0xC0, 0xAF, 0x22 }); break;
            case "closeCode": await raw.SendFrameAsync(true, 0x8, new byte[] { 0x03, 0xED }); break;    // 1005 不得出现在线上
            case "closeOneByte": await raw.SendFrameAsync(true, 0x8, new byte[] { 0x03 }); break;
            case "closeUtf8": await raw.SendFrameAsync(true, 0x8, new byte[] { 0x03, 0xE8, 0xFF }); break;
        }
        var close = await raw.ReadFrameAsync();
        Assert.Equal(0x8, close.Opcode);
        Assert.Equal(code, close.CloseCode);
        Assert.True(await raw.IsClosedAsync());
    }

    [Fact]
    public async Task Oversized_messages_close_with_1009()
    {
        await DisposeAsync();
        await StartAsync(new WebSocketServerOptions { MaxMessageSize = 1024 });
        using var raw = new RawClient();
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        await raw.SendFrameAsync(false, 0x1, new byte[600]);
        await raw.SendFrameAsync(true, 0x0, new byte[600]);
        var close = await raw.ReadFrameAsync();
        Assert.Equal(1009, close.CloseCode);
    }

    [Fact]
    public async Task Bad_handshakes_get_http_errors()
    {
        using (var raw = new RawClient())
        {
            await raw.ConnectAsync(_server.LocalEndPoint!.Port);
            var status = await raw.HandshakeAsync("Sec-WebSocket-Version: 8");
            Assert.StartsWith("HTTP/1.1 426", status);
            Assert.Contains("Sec-WebSocket-Version: 13", status);
        }
        using (var raw = new RawClient())
        {
            await raw.ConnectAsync(_server.LocalEndPoint!.Port);
            Assert.StartsWith("HTTP/1.1 403", await raw.HandshakeAsync(origin: "https://evil.example"));
        }
        Assert.Equal(0, Volatile.Read(ref _connects));
        // 失败的握手不会占住唯一的连接槽位
        using var ws = await ConnectAsync();
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Stop_closes_client_with_going_away()
    {
        using var ws = await ConnectAsync();
        var receive = ReceiveAsync(ws);
        await _server.StopAsync();
        var (type, _) = await receive;
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, ws.CloseStatus);
    }

    [Fact]
    public async Task Server_answers_client_pings_from_ClientWebSocket_keepalive()
    {
        await DisposeAsync();
        await StartAsync(new WebSocketServerOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(50) });
        using var ws = await ConnectAsync();
        // 服务端每 50ms 发 Ping；ClientWebSocket 自动回 Pong，连接保持可用
        await Task.Delay(300);
        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.getInfo\"}");
        Rpc.Error(await ReceiveJsonAsync(ws), DevToolsErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Major_version_mismatch_returns_error_then_closes_the_connection()
    {
        using var ws = await ConnectAsync();
        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.hello\",\"params\":{\"client\":\"x\",\"protocolVersion\":\"2.0\",\"token\":\"pair-123\"}}");
        var e = Rpc.Error(await ReceiveJsonAsync(ws), DevToolsErrorCodes.InvalidParams);
        Assert.Equal("1.0", e["data"]!["protocolVersion"]!.GetString());
        var (type, _) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ws.CloseStatus);
        await WaitUntilAsync(() => !_server.IsConnected);
        Assert.False(_session.IsAuthorized);
    }

    [Fact]
    public void Non_loopback_binding_requires_a_pairing_token()
    {
        using var any = new WebSocketServerTransport(new WebSocketServerOptions { Address = IPAddress.Any });
        Assert.True(any.AcceptsRemoteConnections);
        Assert.False(_server.AcceptsRemoteConnections);
        Assert.Throws<InvalidOperationException>(() => new DevToolsAgent().Attach(any));
        new DevToolsAgent(new DevToolsAgentOptions { PairingToken = "t" }).Attach(any);
    }

    [Fact]
    public async Task Client_that_stops_reading_is_disconnected_instead_of_growing_the_send_queue()
    {
        await DisposeAsync();
        await StartAsync(new WebSocketServerOptions { MaxSendQueueBytes = 256 * 1024, CloseTimeout = TimeSpan.FromMilliseconds(200) });
        using var raw = new RawClient(receiveBufferSize: 4096);
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        await raw.SendFrameAsync(true, 0x1, Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.hello\",\"params\":{\"client\":\"raw\",\"protocolVersion\":\"1.0\",\"token\":\"pair-123\"}}"));
        Assert.Equal(1, JsonValue.Parse((await raw.ReadFrameAsync()).Payload)["id"]!.GetInt64());
        await raw.SendFrameAsync(true, 0x1, Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Console.enable\"}"));
        Assert.Equal(2, JsonValue.Parse((await raw.ReadFrameAsync()).Payload)["id"]!.GetInt64());

        // 客户端从此不再读取。以前每条 Console.entry 都留在发送队列里（20 万条 ≈ 676 MiB）
        string text = new('x', 2000);
        for (int i = 0; i < 50_000 && _server.IsConnected; i++)
        {
            _agent.Console.Log(LogLevel.Info, text);
            Assert.True(_server.QueuedSendBytes <= 256 * 1024 + 4096, $"queued {_server.QueuedSendBytes} bytes");
        }
        await WaitUntilAsync(() => !_server.IsConnected);
        Assert.Equal(1, Volatile.Read(ref _disconnects));
        Assert.False(_session.IsAuthorized);
        Assert.Equal(0, _server.QueuedSendBytes);
    }

    [Fact]
    public async Task Pongs_for_a_ping_flood_are_coalesced_while_the_client_does_not_read()
    {
        using var raw = new RawClient(receiveBufferSize: 4096);
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        const int N = 60_000;    // 远超内核套接字缓冲区，迫使服务端的写入停住
        var payload = new byte[125];
        long maxQueued = 0;
        for (int i = 0; i < N; i++)
        {
            payload[0] = (byte)i;
            payload[1] = (byte)(i >> 8);
            await raw.SendFrameAsync(true, 0x9, payload);
            maxQueued = Math.Max(maxQueued, _server.QueuedSendBytes);
        }
        await raw.SendFrameAsync(true, 0x8, new byte[] { 0x03, 0xE8 });
        int pongs = 0;
        byte[]? last = null;
        while (true)
        {
            var f = await raw.ReadFrameAsync();
            if (f.Opcode == 0x8) break;
            Assert.Equal(0xA, f.Opcode);
            pongs++;
            last = f.Payload;
        }
        Assert.True(pongs < N, $"{pongs} pongs for {N} pings: not coalesced");
        Assert.True(maxQueued <= 2 * (125 + 14), $"queued {maxQueued} bytes");
        Assert.Equal(unchecked((byte)(N - 1)), last![0]);    // 合并后回应的是最近一次 Ping
        Assert.Equal(unchecked((byte)((N - 1) >> 8)), last[1]);
    }

    [Fact]
    public async Task Keepalive_drops_a_peer_that_never_answers()
    {
        await DisposeAsync();
        await StartAsync(new WebSocketServerOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(50), KeepAliveTimeout = TimeSpan.FromMilliseconds(200) });
        using var raw = new RawClient();
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        await WaitUntilAsync(() => Volatile.Read(ref _disconnects) == 1);    // 原始客户端从不回 Pong
        Assert.False(_server.IsConnected);
    }

    [Fact]
    public async Task Keepalive_keeps_a_responsive_peer()
    {
        await DisposeAsync();
        // 回 Pong 的 ClientWebSocket 与服务端在同一进程、共用线程池：整个解决方案并行测试时机器满载，Ping → Pong → 读出
        // 要经过好几次线程池调度，每次都可能被推迟上百毫秒。超时取 1 秒留足余量，等待 2.5 秒（超过两轮「间隔 + 超时」），
        // Pong 若不被当作对端活动，连接在约 1.1 秒时就会被断开。线程池饥饿本身由 KeepAliveStarvationTests 专门覆盖
        await StartAsync(new WebSocketServerOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(50), KeepAliveTimeout = TimeSpan.FromSeconds(1) });
        using var ws = await ConnectAsync();
        var receive = ReceiveJsonAsync(ws);    // 有挂起的接收时 ClientWebSocket 才会处理 Ping 并回 Pong
        await Task.Delay(2500);
        Assert.Equal(0, Volatile.Read(ref _disconnects));
        await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"App.getInfo\"}");
        Rpc.Error(await receive, DevToolsErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Close_frame_survives_unread_client_data_after_a_protocol_error()
    {
        using var raw = new RawClient();
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        await raw.SendFrameAsync(true, 0x1, "{}"u8.ToArray(), masked: false);
        await raw.SendBytesAsync(new byte[256 * 1024]);    // 服务端不再解析这些字节；立即断开会以 RST 冲掉关闭帧
        var close = await raw.ReadFrameAsync();
        Assert.Equal(0x8, close.Opcode);
        Assert.Equal(1002, close.CloseCode);
        Assert.True(await raw.IsClosedAsync());
    }

    [Fact]
    public async Task Registered_close_codes_are_accepted_and_echoed()
    {
        using var raw = new RawClient();
        await raw.ConnectAsync(_server.LocalEndPoint!.Port);
        Assert.StartsWith("HTTP/1.1 101", await raw.HandshakeAsync());
        await raw.SendFrameAsync(true, 0x8, new byte[] { 0x03, 0xF4 });    // 1012 Service Restart
        var close = await raw.ReadFrameAsync();
        Assert.Equal(1012, close.CloseCode);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}

/// <summary>手写帧的原始 WebSocket 客户端，用于构造违规与边界帧。</summary>
sealed class RawClient : IDisposable
{
    readonly TcpClient _tcp = new();
    NetworkStream _stream = null!;

    public RawClient(int receiveBufferSize = 0)
    {
        if (receiveBufferSize > 0) _tcp.ReceiveBufferSize = receiveBufferSize;
    }

    public Task SendBytesAsync(byte[] data) => _stream.WriteAsync(data, 0, data.Length);
    readonly Random _rng = new(42);
    readonly List<byte> _pending = new();

    public async Task ConnectAsync(int port)
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, port);
        _stream = _tcp.GetStream();
    }

    public async Task<string> HandshakeAsync(string version = "Sec-WebSocket-Version: 13", string? origin = null)
    {
        string req = "GET /devtools HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                     "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" + version + "\r\n" +
                     (origin != null ? "Origin: " + origin + "\r\n" : "") + "\r\n";
        var bytes = Encoding.ASCII.GetBytes(req);
        await _stream.WriteAsync(bytes);
        var head = new List<byte>();
        var one = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            int n = await _stream.ReadAsync(one, cts.Token);
            if (n == 0) break;
            head.Add(one[0]);
            int c = head.Count;
            if (c >= 4 && head[c - 1] == '\n' && head[c - 2] == '\r' && head[c - 3] == '\n' && head[c - 4] == '\r') break;
        }
        string text = Encoding.ASCII.GetString(head.ToArray());
        if (text.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", text);
        return text;
    }

    public async Task SendFrameAsync(bool fin, int opcode, byte[] payload, bool masked = true, int rsv = 0, bool forceLength64 = false)
    {
        var f = new List<byte> { (byte)((fin ? 0x80 : 0) | rsv | opcode) };
        byte m = (byte)(masked ? 0x80 : 0);
        if (payload.Length < 126 && !forceLength64) f.Add((byte)(m | payload.Length));
        else if (payload.Length <= 0xFFFF && !forceLength64) { f.Add((byte)(m | 126)); f.Add((byte)(payload.Length >> 8)); f.Add((byte)payload.Length); }
        else { f.Add((byte)(m | 127)); for (int i = 7; i >= 0; i--) f.Add((byte)((long)payload.Length >> (8 * i))); }
        var key = new byte[4];
        _rng.NextBytes(key);
        if (masked) f.AddRange(key);
        for (int i = 0; i < payload.Length; i++) f.Add(masked ? (byte)(payload[i] ^ key[i & 3]) : payload[i]);
        await _stream.WriteAsync(f.ToArray());
    }

    public sealed record Frame(bool Fin, int Opcode, byte[] Payload)
    {
        public int CloseCode => Payload.Length >= 2 ? (Payload[0] << 8) | Payload[1] : 0;
    }

    async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int done = 0;
        while (done < buf.Length)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(done), ct);
            if (n == 0) throw new EndOfStreamException();
            done += n;
        }
    }

    public async Task<Frame> ReadFrameAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var h = new byte[2];
        await ReadExactAsync(h, cts.Token);
        Assert.Equal(0, h[1] & 0x80);    // 服务端帧不加掩码
        long len = h[1] & 0x7F;
        if (len == 126) { var e = new byte[2]; await ReadExactAsync(e, cts.Token); len = (e[0] << 8) | e[1]; }
        else if (len == 127) { var e = new byte[8]; await ReadExactAsync(e, cts.Token); len = 0; foreach (var b in e) len = (len << 8) | b; }
        var payload = new byte[len];
        await ReadExactAsync(payload, cts.Token);
        return new Frame((h[0] & 0x80) != 0, h[0] & 0x0F, payload);
    }

    /// <summary>对端是否已关闭 TCP（读到 EOF）。</summary>
    public async Task<bool> IsClosedAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buf = new byte[1];
        try { return await _stream.ReadAsync(buf, cts.Token) == 0; }
        catch (IOException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose() => _tcp.Dispose();
}
