using System.Text;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;
using AwAUI.Threading;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>测试用 Domain：覆盖同步/异步方法、错误、需启用、版本门控、blob、事件与按帧合并。</summary>
sealed class TestDomain : DevToolsDomain
{
    public int FrameCalls;
    public int Enabled, Disabled;

    public TestDomain(params DiagnosticCategory[] categories) : base("Test", categories) { }

    protected override void RegisterMethods(DevToolsMethodTable m)
    {
        m.Add("echo", ctx => new JsonObject { ["params"] = ctx.Params, ["target"] = ctx.Target?.Id, ["client"] = ctx.Session.ClientName });
        m.Add("needsEnable", _ => new JsonObject { ["ok"] = true }, requiresEnable: true);
        m.Add("newMethod", _ => new JsonObject { ["new"] = true }, sinceMinor: 1);
        m.Add("throws", _ => throw new InvalidOperationException("boom"));
        m.Add("fail", _ => throw new DevToolsProtocolException(DevToolsErrorCodes.NodeNotFound, "Node not found", new JsonObject { ["nodeId"] = 345 }));
        m.Add("typed", ctx => new JsonObject
        {
            ["s"] = ctx.GetString("s"),
            ["i"] = ctx.GetInt64("i"),
            ["b"] = ctx.GetBoolean("b", false),
            ["o"] = ctx.GetOptionalString("o"),
        });
        m.AddAsync("slow", async ctx =>
        {
            await Task.Delay(20).ConfigureAwait(false);
            return new JsonObject { ["v"] = ctx.GetInt64("v") };
        });
        m.Add("blob", ctx =>
        {
            int size = (int)(ctx.GetOptionalInt64("size") ?? 3);
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)(i + 1);
            return new JsonObject { ["blobId"] = ctx.AttachBlob(data, "application/x-test") };
        });
        m.Add("fire", ctx => { Emit(ctx.GetString("name"), new JsonObject { ["v"] = ctx.GetInt64("v") }); return null; });
    }

    public void Fire(string name, JsonValue p, string? target = null) => Emit(name, p, target);
    public void FireCoalesced(string name, string key, JsonValue p) => EmitCoalesced(name, key, p);
    protected override void OnFrame() => FrameCalls++;
    protected override void OnEnabled() => Enabled++;
    protected override void OnDisabled() => Disabled++;
}

/// <summary>进程内 JSON-RPC 客户端。InProc 传输同步投递，同步方法的响应在发送返回前已到达。</summary>
sealed class RpcClient
{
    readonly InProcTransport _transport;
    readonly object _gate = new();
    readonly List<JsonObject> _messages = new();
    readonly List<string> _order = new();
    readonly List<byte[]> _blobs = new();
    long _id;

    public RpcClient(InProcTransport transport)
    {
        _transport = transport;
        transport.Received += m =>
        {
            var o = JsonValue.Parse(m.Span).AsObject();
            lock (_gate) { _messages.Add(o); _order.Add("text"); }
        };
        transport.BlobReceived += b =>
        {
            lock (_gate) { _blobs.Add(b.ToArray()); _order.Add("blob"); }
        };
    }

    public static (DevToolsAgent Agent, RpcClient Client, DevToolsSession Session) Create(DevToolsAgentOptions? options = null)
    {
        var agent = new DevToolsAgent(options);
        var (a, c) = InProcTransport.CreatePair();
        var session = agent.Attach(a);
        return (agent, new RpcClient(c), session);
    }

    public List<JsonObject> Messages { get { lock (_gate) return _messages.ToList(); } }
    public List<string> Order { get { lock (_gate) return _order.ToList(); } }
    public List<byte[]> Blobs { get { lock (_gate) return _blobs.ToList(); } }
    public void ClearMessages() { lock (_gate) { _messages.Clear(); _order.Clear(); } }

    public Task SendRawAsync(string json) => _transport.SendAsync(Encoding.UTF8.GetBytes(json));

    public async Task<JsonObject> CallAsync(string method, JsonObject? p = null, string? target = null)
    {
        long id = Interlocked.Increment(ref _id) + 1000;
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (p != null) req["params"] = p;
        if (target != null) req["target"] = target;
        await _transport.SendAsync(req.ToUtf8Bytes());
        return await WaitForAsync(m => m["id"] is JsonNumber n && n.GetInt64() == id && !m.ContainsKey("method"));
    }

    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_gate)
                foreach (var m in _messages)
                    if (predicate(m)) return m;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No matching message. Got: " + string.Join("\n", Messages));
            await Task.Delay(5);
        }
    }

    public List<JsonObject> Events(string method) => Messages.Where(m => m["method"] is JsonString s && s.Value == method).ToList();

    public Task<JsonObject> HelloAsync(string version = "1.0", string? token = null)
    {
        var p = new JsonObject { ["client"] = "test", ["protocolVersion"] = version };
        if (token != null) p["token"] = token;
        return CallAsync("App.hello", p);
    }
}

static class Rpc
{
    public static JsonValue Result(JsonObject response)
    {
        Assert.True(response["error"] is null, "Unexpected error: " + response);
        Assert.Equal("2.0", response["jsonrpc"]!.GetString());
        return response["result"]!;
    }

    public static JsonObject Error(JsonObject response, int code)
    {
        Assert.Null(response["result"]);
        var e = response["error"]!.AsObject();
        Assert.Equal(code, e["code"]!.GetInt64());
        Assert.False(string.IsNullOrEmpty(e["message"]!.GetString()));
        return e;
    }
}

/// <summary>手动泵送的调度器，模拟 UI 线程。</summary>
sealed class ManualDispatcher : IDispatcher
{
    readonly Queue<Action> _queue = new();
    public bool CheckAccess() => false;
    public void Post(Action action) { lock (_queue) _queue.Enqueue(action); }
    public int Pending { get { lock (_queue) return _queue.Count; } }
    public void RunAll()
    {
        while (true)
        {
            Action a;
            lock (_queue) { if (_queue.Count == 0) return; a = _queue.Dequeue(); }
            a();
        }
    }
}
