using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;
using AwAUI.Threading;

namespace AwAUI.Diagnostics.Client;

/// <summary><see cref="DevToolsClient"/> 的选项。</summary>
public sealed class DevToolsClientOptions
{
    /// <summary>
    /// 前端的 UI 线程调度器。设置后，响应回调、<see cref="DevToolsClient.EventReceived"/> 与 <see cref="DevToolsClient.Disconnected"/>
    /// 一律投递到该线程（即使当前就在该线程上也投递，从不在发送方的栈上重入），按消息到达顺序执行；
    /// 为 null 时在传输的接收线程上直接调用。
    /// </summary>
    public IDispatcher? Dispatcher { get; set; }

    /// <summary>入站消息的最大 JSON 嵌套深度（防止恶意对端耗尽栈）。默认 128（Agent 的树响应默认不超过 64 层）。</summary>
    public int MaxMessageDepth { get; set; } = 128;

    /// <summary>同时未完成的请求数上限；超出时新请求立即以错误完成（防止对端不响应时无限累积）。默认 4096。</summary>
    public int MaxPendingRequests { get; set; } = 4096;
}

/// <summary>一个请求的结果：成功时 <see cref="Result"/> 不为 null；失败时为 JSON-RPC 错误或连接断开。</summary>
public sealed class DevToolsResponse
{
    DevToolsResponse() { }

    /// <summary>成功的结果（<c>result</c> 字段）；失败时为 null。</summary>
    public JsonValue? Result { get; private set; }

    /// <summary>是否失败（错误响应、断开连接或请求被拒绝）。</summary>
    public bool IsError => Result is null;

    /// <summary>错误码（见 <see cref="DevToolsErrorCodes"/>）；断开连接时为 <see cref="DevToolsClient.DisconnectedErrorCode"/>。</summary>
    public int ErrorCode { get; private set; }

    /// <summary>错误消息。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>错误的附加数据。</summary>
    public JsonValue? ErrorData { get; private set; }

    /// <summary>失败是否因为连接断开（或客户端已释放）。</summary>
    public bool IsDisconnected => IsError && ErrorCode == DevToolsClient.DisconnectedErrorCode;

    /// <summary>结果对象；失败或结果不是对象时为空对象。</summary>
    public JsonObject ResultObject => Result as JsonObject ?? new JsonObject();

    internal static DevToolsResponse Success(JsonValue result) => new() { Result = result };

    internal static DevToolsResponse Failure(int code, string message, JsonValue? data = null) =>
        new() { ErrorCode = code, ErrorMessage = message, ErrorData = data };

    /// <summary>失败时转换为 <see cref="DevToolsProtocolException"/>。</summary>
    public DevToolsProtocolException ToException() =>
        new(ErrorCode, ErrorMessage ?? "Request failed.", ErrorData);

    public override string ToString() =>
        IsError ? "error " + ErrorCode + ": " + ErrorMessage : Result!.ToJsonString();
}

/// <summary>协议事件（服务端推送的、没有 id 的消息）。</summary>
public sealed class DevToolsEvent
{
    public DevToolsEvent(string method, JsonObject parameters, string? target)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Params = parameters ?? new JsonObject();
        Target = target;
    }

    /// <summary>完整的事件名（<c>Domain.event</c>）。</summary>
    public string Method { get; }

    /// <summary>事件参数（缺失或不是对象时为空对象）。</summary>
    public JsonObject Params { get; }

    /// <summary>事件所属的目标（窗口）；没有时为 null。</summary>
    public string? Target { get; }

    public override string ToString() => Method + " " + Params.ToJsonString();
}

/// <summary>
/// DevTools 协议的前端（design/06 ④、design/20）：在一条 <see cref="DevToolsTransport"/> 上发送 JSON-RPC 请求、按 id 匹配响应、分发事件。
/// 内嵌 / 独立窗口 / 远程前端与测试脚本都通过它与 Agent 通信，传输可以是 <see cref="InProcTransport"/>（同进程）
/// 或 <see cref="WebSocketClientTransport"/>（远程）。
/// </summary>
/// <remarks>
/// <para>线程：发送方法可在任意线程调用。设置了 <see cref="DevToolsClientOptions.Dispatcher"/> 时，所有回调都投递到该调度器、
/// 按消息到达顺序执行，因此前端在 UI 线程上处理响应与事件时不需要加锁，也不会在自己的调用栈上被重入。</para>
/// <para>对端是不可信的：解析失败、形状不符或 id 不匹配的消息被丢弃（计入 <see cref="DroppedMessages"/>），不会抛进宿主；
/// 嵌套深度受 <see cref="DevToolsClientOptions.MaxMessageDepth"/> 限制，消息大小由传输限制。</para>
/// </remarks>
public sealed class DevToolsClient : IDisposable
{
    /// <summary>连接断开或客户端已释放时，未完成请求得到的错误码（客户端自定义，不在协议错误码表中）。</summary>
    public const int DisconnectedErrorCode = -32099;

    /// <summary>未完成请求过多时新请求得到的错误码（客户端自定义）。</summary>
    public const int TooManyRequestsErrorCode = -32098;

    readonly DevToolsTransport _transport;
    readonly DevToolsClientOptions _options;
    readonly object _gate = new();
    readonly Dictionary<long, Action<DevToolsResponse>> _pending = new();
    readonly Action<ReadOnlyMemory<byte>> _onReceived;
    readonly Action _onDisconnected;
    readonly Action _onConnected;
    long _nextId;
    long _dropped;
    bool _disposed;
    bool _down;

    public DevToolsClient(DevToolsTransport transport, DevToolsClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new DevToolsClientOptions();
        _onReceived = OnReceived;
        _onDisconnected = OnTransportDisconnected;
        _onConnected = OnTransportConnected;
        transport.Received += _onReceived;
        transport.Disconnected += _onDisconnected;
        transport.Connected += _onConnected;
    }

    /// <summary>底层传输。</summary>
    public DevToolsTransport Transport => _transport;

    /// <summary>传输当前是否连接。</summary>
    public bool IsConnected => !_disposed && _transport.IsConnected;

    /// <summary>未完成的请求数。</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>被丢弃的入站消息数（无法解析、形状不符或没有对应请求的响应）。</summary>
    public long DroppedMessages => Interlocked.Read(ref _dropped);

    /// <summary>收到事件（在调度器上，见 <see cref="DevToolsClientOptions.Dispatcher"/>）。</summary>
    public event Action<DevToolsEvent>? EventReceived;

    /// <summary>传输断开（在调度器上）；之前未完成的请求都已以 <see cref="DisconnectedErrorCode"/> 失败。</summary>
    public event Action? Disconnected;

    /// <summary>
    /// 发送请求，结果交给 <paramref name="callback"/>（在调度器上，恰好调用一次；可以为 null 表示不关心结果）。
    /// </summary>
    public void Call(string method, JsonObject? parameters, string? target, Action<DevToolsResponse>? callback)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        long id = 0;
        DevToolsResponse? rejected = null;
        lock (_gate)
        {
            if (_disposed || _down || !_transport.IsConnected)
            {
                // 断开之后传输会静默丢弃发送：立即失败，而不是让请求永远挂起
                rejected = DevToolsResponse.Failure(DisconnectedErrorCode, _disposed ? "The client is disposed." : "Not connected.");
            }
            else if (_pending.Count >= Math.Max(1, _options.MaxPendingRequests))
            {
                rejected = DevToolsResponse.Failure(TooManyRequestsErrorCode, "Too many pending requests.");
            }
            else
            {
                id = ++_nextId;
                _pending.Add(id, callback ?? (_ => { }));
            }
        }
        if (rejected is not null)
        {
            // 在锁外回调（没有调度器时回调同步运行，不能在持有客户端的锁时运行用户代码）
            Deliver(() => callback?.Invoke(rejected));
            return;
        }
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) req["params"] = parameters;
        if (target is not null) req["target"] = target;
        Task task;
        try
        {
            task = _transport.SendAsync(req.ToUtf8Bytes());
        }
        catch (Exception ex)
        {
            FailPending(id, ex.Message);
            return;
        }
        if (task.IsCompleted)
        {
            if (task.IsFaulted || task.IsCanceled) FailPending(id, task.Exception?.GetBaseException().Message ?? "Send canceled.");
            return;
        }
        task.ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled) FailPending(id, t.Exception?.GetBaseException().Message ?? "Send canceled.");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>发送请求并等待结果；错误响应抛 <see cref="DevToolsProtocolException"/>。</summary>
    /// <remarks>设置了调度器时，任务在调度器执行投递之后才完成（例如无窗口测试需要泵送调度器）。</remarks>
    public Task<JsonValue> CallAsync(string method, JsonObject? parameters = null, string? target = null)
    {
        var tcs = new TaskCompletionSource<JsonValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        Call(method, parameters, target, r =>
        {
            if (r.IsError) tcs.TrySetException(r.ToException());
            else tcs.TrySetResult(r.Result!);
        });
        return tcs.Task;
    }

    /// <summary>
    /// <c>App.hello</c>：协商版本并（远程连接时）出示配对令牌，结果交给 <paramref name="callback"/>（形状见 design/20「会话」）。
    /// </summary>
    public void Hello(string clientName, string? token, Action<DevToolsResponse>? callback)
    {
        var p = new JsonObject { ["client"] = clientName ?? "AwAUI.DevTools", ["protocolVersion"] = DevToolsAgent.CurrentProtocolVersion.ToString() };
        if (token is not null) p["token"] = token;
        Call("App.hello", p, null, callback);
    }

    // ------------------------------------------------------------ 接收

    void OnReceived(ReadOnlyMemory<byte> message)
    {
        JsonObject msg;
        try
        {
            var root = JsonValue.Parse(message.Span, new JsonReaderOptions { MaxDepth = Math.Max(8, _options.MaxMessageDepth) });
            if (root is not JsonObject o)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            msg = o;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        if (msg["method"] is JsonString method && !msg.ContainsKey("id"))
        {
            var ev = new DevToolsEvent(method.Value, msg["params"] as JsonObject ?? new JsonObject(), msg["target"] is JsonString t ? t.Value : null);
            Deliver(() => EventReceived?.Invoke(ev));
            return;
        }

        if (msg["id"] is not JsonNumber idValue || !idValue.TryGetInt64(out long id))
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        Action<DevToolsResponse>? callback;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out callback))
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _pending.Remove(id);
        }
        DevToolsResponse response;
        if (msg["error"] is JsonObject err)
        {
            int code = err["code"] is JsonNumber n && n.TryGetInt64(out long c) && c >= int.MinValue && c <= int.MaxValue ? (int)c : DevToolsErrorCodes.InternalError;
            response = DevToolsResponse.Failure(code, err.GetStringOrDefault("message") ?? "Error", err["data"]);
        }
        else if (msg["result"] is { } result)
        {
            response = DevToolsResponse.Success(result);
        }
        else
        {
            response = DevToolsResponse.Failure(DevToolsErrorCodes.InternalError, "Malformed response (neither 'result' nor 'error').");
        }
        Deliver(() => callback(response));
    }

    void OnTransportConnected()
    {
        lock (_gate) _down = false;
    }

    void OnTransportDisconnected()
    {
        lock (_gate) _down = true;
        FailAll("Disconnected.");
        Deliver(() => Disconnected?.Invoke());
    }

    void FailPending(long id, string message)
    {
        Action<DevToolsResponse>? callback;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out callback)) return;
            _pending.Remove(id);
        }
        var r = DevToolsResponse.Failure(DisconnectedErrorCode, message);
        Deliver(() => callback(r));
    }

    void FailAll(string message)
    {
        List<Action<DevToolsResponse>> callbacks;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            callbacks = new List<Action<DevToolsResponse>>(_pending.Values);
            _pending.Clear();
        }
        var r = DevToolsResponse.Failure(DisconnectedErrorCode, message);
        foreach (var cb in callbacks) Deliver(() => cb(r));
    }

    /// <summary>在调度器上（没有调度器时直接）执行回调；回调抛出的异常被吞掉，不影响后续消息。</summary>
    void Deliver(Action action)
    {
        var d = _options.Dispatcher;
        if (d is null) SafeInvoke(action);
        else d.Post(() => SafeInvoke(action));
    }

    static void SafeInvoke(Action action)
    {
        try { action(); }
        catch (Exception) { /* 前端回调的异常不得中断传输的接收 */ }
    }

    /// <summary>解除与传输的关联并让未完成的请求失败；不释放传输本身。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _transport.Received -= _onReceived;
        _transport.Disconnected -= _onDisconnected;
        _transport.Connected -= _onConnected;
        FailAll("The client is disposed.");
    }
}
