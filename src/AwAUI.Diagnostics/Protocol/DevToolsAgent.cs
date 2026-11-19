using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Diagnostics.Json;
using AwAUI.Diagnostics.Transport;
using AwAUI.Threading;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>Agent 选项。</summary>
public sealed class DevToolsAgentOptions
{
    /// <summary>
    /// 配对令牌。设置后，会话必须先用携带该令牌的 <c>App.hello</c> 完成配对，否则除 App.hello 外一律返回 -32006。
    /// 远程传输（WebSocket）应总是设置它。为 null 时不要求配对（进程内）。
    /// </summary>
    public string? PairingToken { get; set; }

    /// <summary>本端协议版本，默认 <see cref="DevToolsAgent.CurrentProtocolVersion"/>。</summary>
    public ProtocolVersion ProtocolVersion { get; set; } = DevToolsAgent.CurrentProtocolVersion;

    /// <summary>
    /// UI 线程调度器。设置后，收到的消息投递到该线程处理（方法处理器可安全访问元素树）；
    /// 为 null 时在传输的接收线程上直接处理。
    /// </summary>
    public IDispatcher? Dispatcher { get; set; }

    /// <summary>App.getInfo / App.hello 返回的应用信息。</summary>
    public DevToolsAppInfo AppInfo { get; set; } = new();

    /// <summary>Console 域缓存的最近日志条数（启用时补发）。</summary>
    public int ConsoleBufferSize { get; set; } = 500;

    /// <summary>入站消息的最大 JSON 嵌套深度。</summary>
    public int MaxMessageDepth { get; set; } = 64;

    /// <summary>
    /// Tree 域一次响应最多展开的节点数（<c>getDocument</c> / <c>requestChildren</c> 的 depth 较大时）。超出预算的节点只带
    /// <c>childCount</c> 不带 <c>children</c>，前端按需再请求。被请求节点自己的直接子节点总是完整返回。
    /// </summary>
    public int MaxTreeNodesPerResponse { get; set; } = 10_000;

    /// <summary>
    /// Tree 域一次响应中节点嵌套的最大层数。每层节点占两层 JSON 嵌套，默认值让响应在 64 层的读取深度限制内
    /// （<see cref="Json.JsonReaderOptions.DefaultMaxDepth"/>）；更深的节点只带 <c>childCount</c>。
    /// </summary>
    public int MaxTreeDepthPerResponse { get; set; } = 24;

    /// <summary>节点文本（文本节点内容）的最大长度（UTF-16 单元）；超出时截断并标记 <c>textTruncated</c>。</summary>
    public int MaxNodeTextLength { get; set; } = 10_000;
}

/// <summary>
/// DevTools 协议 Agent（design/06 ②、design/20）：在一条或多条传输上实现 JSON-RPC 2.0，把请求分派到各 Domain。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>消息：请求 <c>{jsonrpc, id, method, params?, target?}</c>；无 id 的合法请求是通知，执行但不响应（方法出错也不响应）。
/// 不合法的请求对象（<c>jsonrpc</c> 不是 "2.0"、<c>method</c> 不是字符串、<c>params</c> 不是对象或数组、<c>target</c> 不是字符串）
/// 按 JSON-RPC 2.0 总是得到 -32600 响应；没有可用 id 时 id 为 null。</item>
/// <item><c>params</c> 必须是对象（按名传参）；数组形式的按位置传参返回 -32602。</item>
/// <item><b>批量请求不支持</b>：顶层是数组时返回一个 id 为 null 的 -32600 错误。理由：DevTools 方法常有帧相关副作用，
/// 逐条有序处理更容易推理；二进制旁路依赖响应与二进制消息的顺序，批量会让它复杂化。</item>
/// <item><c>target</c> 省略或为 null 时指默认目标（第一个登记的目标）；指定了不存在的目标返回 -32002。</item>
/// <item>配对：见 <see cref="DevToolsAgentOptions.PairingToken"/>。版本：见 <see cref="ProtocolVersion"/>；
/// App.hello 主版本不兼容时返回错误后断开该连接（<see cref="DevToolsTransport.DisconnectAsync(string)"/>）。</item>
/// <item>发送时不持有任何锁：带 blob 的响应通过 <see cref="DevToolsTransport.SendWithBlobsAsync"/> 作为整体发送。</item>
/// <item>访问元素树的方法（Tree / Css / Layout / Overlay 域）被投递到目标的 UI 线程执行，处理器、响应与 AfterResponse 在那里连续完成，
/// 因此响应总是先于 UI 线程上随后产生的事件；同一目标上的这类请求按到达顺序处理。其余方法的响应可能先于它们发出（JSON-RPC 允许乱序，按 id 对应）。</item>
/// <item>会话在 <c>{Domain}.enable</c> 的响应发出之后才开始接收该 Domain 的事件；在此之前（其他线程上）产生的事件不发给它，
/// 因此事件总是在启用它们的响应之后到达。</item>
/// </list>
/// </remarks>
public sealed class DevToolsAgent : IDisposable
{
    /// <summary>本实现的协议版本。</summary>
    public static readonly ProtocolVersion CurrentProtocolVersion = new(1, 0);

    readonly Dictionary<string, MethodEntry> _methods = new(StringComparer.Ordinal);
    readonly List<DevToolsDomain> _domains = new();
    readonly List<DevToolsTarget> _targets = new();
    readonly object _gate = new();
    readonly List<PendingEvent> _pending = new();
    readonly Dictionary<string, int> _pendingIndex = new(StringComparer.Ordinal);
    DevToolsSession[] _sessions = Array.Empty<DevToolsSession>();
    long _blobCounter;
    bool _disposed;

    public DevToolsAgent(DevToolsAgentOptions? options = null)
    {
        Options = options ?? new DevToolsAgentOptions();
        App = new AppDevToolsDomain();
        Console = new ConsoleDevToolsDomain(Options.ConsoleBufferSize);
        Tree = new TreeDevToolsDomain();
        Css = new CssDevToolsDomain();
        Layout = new LayoutDevToolsDomain();
        Overlay = new OverlayDevToolsDomain();
        RegisterDomain(App);
        RegisterDomain(Console);
        RegisterDomain(Tree);
        RegisterDomain(Css);
        RegisterDomain(Layout);
        RegisterDomain(Overlay);
    }

    public DevToolsAgentOptions Options { get; }
    /// <summary>本端协议版本。</summary>
    public ProtocolVersion ProtocolVersion => Options.ProtocolVersion;
    /// <summary>内建的 App 域。</summary>
    public AppDevToolsDomain App { get; }
    /// <summary>内建的 Console 域（日志与调试命令）。</summary>
    public ConsoleDevToolsDomain Console { get; }
    /// <summary>内建的 Tree 域（元素树，数据源为各目标的 <see cref="DevToolsTarget.Tree"/>）。</summary>
    public TreeDevToolsDomain Tree { get; }
    /// <summary>内建的 Css 域（数据源为各目标的 <see cref="DevToolsTarget.Styles"/>）。</summary>
    public CssDevToolsDomain Css { get; }
    /// <summary>内建的 Layout 域（盒模型，数据源为 <see cref="DevToolsTarget.Tree"/>）。</summary>
    public LayoutDevToolsDomain Layout { get; }
    /// <summary>内建的 Overlay 域（高亮与拾取，执行者为 <see cref="DevToolsTarget.Overlay"/>）。</summary>
    public OverlayDevToolsDomain Overlay { get; }

    /// <summary>已登记的 Domain（按登记顺序）。</summary>
    public IReadOnlyList<DevToolsDomain> Domains
    {
        get { lock (_gate) return _domains.ToArray(); }
    }

    /// <summary>当前会话。</summary>
    public IReadOnlyList<DevToolsSession> Sessions => Volatile.Read(ref _sessions);

    /// <summary>登记一个 Domain。每个名字只能登记一次。</summary>
    public void RegisterDomain(DevToolsDomain domain)
    {
        if (domain is null) throw new ArgumentNullException(nameof(domain));
        lock (_gate)
        {
            if (domain.Agent != null) throw new InvalidOperationException("The domain is already registered with an agent.");
            foreach (var d in _domains)
                if (d.Name == domain.Name) throw new InvalidOperationException($"Domain '{domain.Name}' is already registered.");
            var staged = new Dictionary<string, MethodEntry>(StringComparer.Ordinal);
            domain.RegisterMethodsCore(new DevToolsMethodTable(domain, staged));
            foreach (var kv in staged)
                if (_methods.ContainsKey(kv.Key)) throw new InvalidOperationException($"Method '{kv.Key}' is already registered.");
            foreach (var kv in staged) _methods.Add(kv.Key, kv.Value);
            _methods[domain.Name + ".enable"] = new MethodEntry(domain, ctx => EnableDomain(ctx, domain), null, false, 0, false);
            _methods[domain.Name + ".disable"] = new MethodEntry(domain, ctx => DisableDomain(ctx, domain), null, false, 0, false);
            domain.Agent = this;
            _domains.Add(domain);
        }
    }

    // ---- 目标 ----

    /// <summary>登记一个目标（窗口）。第一个登记的目标是默认目标。一个目标只能登记到一个 Agent。</summary>
    public void AddTarget(DevToolsTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        lock (_gate)
        {
            if (target.Agent != null) throw new InvalidOperationException("The target is already registered with an agent.");
            foreach (var t in _targets)
                if (t.Id == target.Id) throw new InvalidOperationException($"Target '{target.Id}' already exists.");
            _targets.Add(target);
            target.Agent = this;
        }
    }

    /// <summary>移除目标；各会话为它保存的节点状态随之丢弃（不再推送它的事件）。</summary>
    public bool RemoveTarget(string id)
    {
        DevToolsTarget? removed = null;
        lock (_gate)
        {
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i].Id == id)
                {
                    removed = _targets[i];
                    _targets.RemoveAt(i);
                    removed.Agent = null;
                    break;
                }
        }
        if (removed is null) return false;
        Tree.ForgetTarget(removed);
        Css.ForgetTarget(removed);
        Overlay.ForgetTarget(removed);
        return true;
    }

    /// <summary>目标的检查接缝被替换。</summary>
    internal void OnTargetSeamsChanged(DevToolsTarget target, bool tree)
    {
        if (tree)
        {
            if (Tree.IsEnabled) Tree.OnDocumentUpdated(target);
            return;
        }
        Css.ForgetTarget(target);
        Overlay.ForgetTarget(target);
    }

    public IReadOnlyList<DevToolsTarget> Targets
    {
        get { lock (_gate) return _targets.ToArray(); }
    }

    public DevToolsTarget? DefaultTarget
    {
        get { lock (_gate) return _targets.Count > 0 ? _targets[0] : null; }
    }

    public bool TryGetTarget(string id, out DevToolsTarget? target)
    {
        lock (_gate)
            foreach (var t in _targets)
                if (t.Id == id) { target = t; return true; }
        target = null;
        return false;
    }

    // ---- 会话 ----

    /// <summary>
    /// 在一条传输上建立会话。诊断被特性开关禁用时抛 <see cref="InvalidOperationException"/>；
    /// 传输可能接受跨设备连接（<see cref="DevToolsTransport.AcceptsRemoteConnections"/>）而没有设置配对令牌时也抛出。
    /// </summary>
    public DevToolsSession Attach(DevToolsTransport transport)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        if (!DiagnosticsFeature.IsEnabled)
            throw new InvalidOperationException($"Diagnostics are disabled by the '{DiagnosticsFeature.SwitchName}' feature switch.");
        if (transport.AcceptsRemoteConnections && Options.PairingToken is null)
            throw new InvalidOperationException("A transport that accepts remote connections requires DevToolsAgentOptions.PairingToken to be set.");
        var session = new DevToolsSession(this, transport) { IsAuthorized = Options.PairingToken is null };
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DevToolsAgent));
            var n = new DevToolsSession[_sessions.Length + 1];
            Array.Copy(_sessions, n, _sessions.Length);
            n[_sessions.Length] = session;
            Volatile.Write(ref _sessions, n);
        }
        session.OnReceivedHandler = m => OnReceived(session, m);
        session.OnConnectionChanged = () => Run(() => ResetSession(session));
        transport.Received += session.OnReceivedHandler;
        transport.Connected += session.OnConnectionChanged;
        transport.Disconnected += session.OnConnectionChanged;
        return session;
    }

    /// <summary>移除会话（不释放传输）。</summary>
    public void Detach(DevToolsSession session)
    {
        lock (_gate)
        {
            int i = Array.IndexOf(_sessions, session);
            if (i < 0) return;
            var n = new DevToolsSession[_sessions.Length - 1];
            Array.Copy(_sessions, 0, n, 0, i);
            Array.Copy(_sessions, i + 1, n, i, _sessions.Length - i - 1);
            Volatile.Write(ref _sessions, n);
        }
        Unhook(session);
        ResetSession(session);
    }

    static void Unhook(DevToolsSession session)
    {
        if (session.OnReceivedHandler != null) session.Transport.Received -= session.OnReceivedHandler;
        if (session.OnConnectionChanged != null)
        {
            session.Transport.Connected -= session.OnConnectionChanged;
            session.Transport.Disconnected -= session.OnConnectionChanged;
        }
    }

    void ResetSession(DevToolsSession session)
    {
        session.HasHandshake = false;
        session.ClientName = null;
        session.IsAuthorized = Options.PairingToken is null;
        session.ProtocolVersion = ProtocolVersion;
        foreach (var d in session.TakeAllEnabled()) SafeCall(() => d.SessionDisabled(session));
    }

    void Run(Action action)
    {
        var dispatcher = Options.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) SafeCall(action);
        else dispatcher.Post(() => SafeCall(action));
    }

    static void SafeCall(Action action)
    {
        try { action(); }
        catch (Exception) { /* 诊断代码不得把异常抛进宿主 */ }
    }

    void OnReceived(DevToolsSession session, ReadOnlyMemory<byte> message)
    {
        var dispatcher = Options.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Observe(ProcessAsync(session, message));
        else dispatcher.Post(() => Observe(ProcessAsync(session, message)));
    }

    /// <summary>发送并吞掉传输层的同步异常与失败任务：诊断代码不得把异常抛进宿主（例如应用线程上的 Console.Log）。</summary>
    static void Send(DevToolsTransport transport, ReadOnlyMemory<byte> data, bool blob)
    {
        Task task;
        try { task = blob ? transport.SendBlobAsync(data) : transport.SendAsync(data); }
        catch (Exception) { return; }
        Observe(task);
    }

    static void Observe(Task task)
    {
        if (task.IsCompleted) { _ = task.Exception; return; }
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ---- 分派 ----

    /// <summary>处理一条入站消息（传输接收事件之外，也供测试与自定义传输直接调用）。</summary>
    public async Task ProcessAsync(DevToolsSession session, ReadOnlyMemory<byte> message)
    {
        JsonValue root;
        try
        {
            root = JsonValue.Parse(message.Span, new JsonReaderOptions { MaxDepth = Options.MaxMessageDepth });
        }
        catch (JsonException ex)
        {
            SendError(session, JsonValue.Null, DevToolsErrorCodes.ParseError, "Parse error: " + ex.Reason,
                new JsonObject { ["offset"] = ex.BytePosition, ["line"] = ex.Line, ["column"] = ex.Column });
            return;
        }

        if (root is JsonArray)
        {
            SendError(session, JsonValue.Null, DevToolsErrorCodes.InvalidRequest, "Batch requests are not supported.");
            return;
        }
        if (root is not JsonObject req)
        {
            SendError(session, JsonValue.Null, DevToolsErrorCodes.InvalidRequest, "Invalid request: expected an object.");
            return;
        }

        bool isNotification = !req.TryGetValue("id", out var id);
        if (!isNotification && id.Kind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null))
        {
            SendError(session, JsonValue.Null, DevToolsErrorCodes.InvalidRequest, "Invalid request: 'id' must be a string, number or null.");
            return;
        }
        // 不合法的请求对象不是通知：即使没有 id 也要响应（id 为 null），JSON-RPC 2.0 §5
        JsonValue replyId = isNotification ? JsonValue.Null : id;
        if (req["jsonrpc"] is not JsonString { Value: "2.0" })
        {
            SendError(session, replyId, DevToolsErrorCodes.InvalidRequest, "Invalid request: 'jsonrpc' must be \"2.0\".");
            return;
        }
        if (req["method"] is not JsonString { Value: var method })
        {
            SendError(session, replyId, DevToolsErrorCodes.InvalidRequest, "Invalid request: 'method' must be a string.");
            return;
        }

        JsonObject parameters;
        switch (req["params"])
        {
            case null:
            case JsonNull:
                parameters = new JsonObject();
                break;
            case JsonObject o:
                parameters = o;
                break;
            case JsonArray:
                // 数组是合法的结构化值（请求本身合法），只是本协议不支持按位置传参：属于参数错误，通知不响应
                if (!isNotification) SendError(session, id, DevToolsErrorCodes.InvalidParams, "Positional parameters are not supported; 'params' must be an object.");
                return;
            default:
                SendError(session, replyId, DevToolsErrorCodes.InvalidRequest, "Invalid request: 'params' must be an object.");
                return;
        }

        string? targetId;
        switch (req["target"])
        {
            case null:
            case JsonNull:
                targetId = null;
                break;
            case JsonString s:
                targetId = s.Value;
                break;
            default:
                SendError(session, replyId, DevToolsErrorCodes.InvalidRequest, "Invalid request: 'target' must be a string.");
                return;
        }

        DevToolsCallContext? ctx = null;
        JsonValue? result;
        try
        {
            if (Options.PairingToken is null && session.Transport.AcceptsRemoteConnections)
                throw new DevToolsProtocolException(DevToolsErrorCodes.Unauthorized, "Unauthorized: remote connections require a pairing token to be configured on the agent.");
            if (!session.IsAuthorized && method != "App.hello")
                throw new DevToolsProtocolException(DevToolsErrorCodes.Unauthorized, "Unauthorized: the session is not paired. Call App.hello with the pairing token.");

            MethodEntry? entry;
            lock (_gate) _methods.TryGetValue(method, out entry);
            if (entry is null || entry.SinceMinor > session.ProtocolVersion.Minor)
                throw new DevToolsProtocolException(DevToolsErrorCodes.MethodNotFound, $"Method '{method}' not found.", new JsonObject { ["method"] = method });

            DevToolsTarget? target;
            if (targetId is null) target = DefaultTarget;
            else if (!TryGetTarget(targetId, out target))
                throw new DevToolsProtocolException(DevToolsErrorCodes.TargetNotFound, $"Target '{targetId}' not found.", new JsonObject { ["target"] = targetId });

            if (entry.RequiresEnable && !session.IsDomainEnabled(entry.Domain))
                throw new DevToolsProtocolException(DevToolsErrorCodes.DomainNotEnabled, $"Domain '{entry.Domain.Name}' is not enabled.", new JsonObject { ["domain"] = entry.Domain.Name });

            ctx = new DevToolsCallContext(this, session, method, parameters, target, isNotification);
            if (entry.Sync != null)
            {
                // 需要访问元素树的方法投递到目标的 UI 线程：处理器、响应与 AfterResponse 在那里作为一个整体执行，
                // 因此响应总是先于该线程上之后产生的事件发出（「事件在启用它们的响应之后」）。
                var ui = entry.RunOnTargetThread ? target?.EffectiveDispatcher : null;
                if (ui != null && !ui.CheckAccess())
                {
                    var done = new TaskCompletionSource<bool>();
                    var c = ctx;
                    var sync = entry.Sync;
                    ui.Post(() =>
                    {
                        try { Complete(session, id, isNotification, c, sync); }
                        finally { done.TrySetResult(true); }
                    });
                    await done.Task.ConfigureAwait(false);
                    return;
                }
                Complete(session, id, isNotification, ctx, entry.Sync);
                return;
            }
            result = await entry.Async!(ctx).ConfigureAwait(false);
        }
        catch (DevToolsProtocolException ex)
        {
            if (!isNotification) SendError(session, id, ex.Code, ex.Message, ex.ErrorData);
            if (ctx?.DisconnectReason is { } reason) Disconnect(session, reason);
            return;
        }
        catch (Exception ex)
        {
            if (!isNotification)
                SendError(session, id, DevToolsErrorCodes.InternalError, "Internal error: " + ex.Message,
                    new JsonObject { ["exception"] = ex.GetType().FullName });
            return;
        }

        if (!isNotification) SendResult(session, id, result, ctx.Blobs);
        if (ctx.AfterResponse is { } after) SafeCall(after);
    }

    /// <summary>执行同步处理器并发出响应（或错误）。</summary>
    void Complete(DevToolsSession session, JsonValue id, bool isNotification, DevToolsCallContext ctx, DevToolsMethodHandler handler)
    {
        JsonValue? result;
        try
        {
            result = handler(ctx);
        }
        catch (DevToolsProtocolException ex)
        {
            if (!isNotification) SendError(session, id, ex.Code, ex.Message, ex.ErrorData);
            if (ctx.DisconnectReason is { } reason) Disconnect(session, reason);
            return;
        }
        catch (Exception ex)
        {
            if (!isNotification)
                SendError(session, id, DevToolsErrorCodes.InternalError, "Internal error: " + ex.Message,
                    new JsonObject { ["exception"] = ex.GetType().FullName });
            return;
        }
        if (!isNotification) SendResult(session, id, result, ctx.Blobs);
        if (ctx.AfterResponse is { } after) SafeCall(after);
    }

    static void Disconnect(DevToolsSession session, string reason)
    {
        Task task;
        try { task = session.Transport.DisconnectAsync(reason); }
        catch (Exception) { return; }
        Observe(task);
    }

    static JsonValue? EnableDomain(DevToolsCallContext ctx, DevToolsDomain domain)
    {
        var session = ctx.Session;
        if (session.IsDomainEnabled(domain)) return null;
        // 先让 Domain 为该会话准备状态（例如 Console 暂停对它的实时推送直到补发完成），再把会话标为已启用
        domain.SessionEnabling(session);
        if (session.SetEnabled(domain, true))
        {
            domain.SessionEnabled();
            // 响应发出之后才开始向该会话推送事件：其他线程（例如目标的 UI 线程）在此期间产生的事件不会先于响应到达
            ctx.AfterResponse = () =>
            {
                session.SetLive(domain);
                domain.SessionEnabledCore(session);
            };
        }
        return null;
    }

    static JsonValue? DisableDomain(DevToolsCallContext ctx, DevToolsDomain domain)
    {
        if (ctx.Session.SetEnabled(domain, false)) domain.SessionDisabled(ctx.Session);
        return null;
    }

    internal string NextBlobId() => "b" + Interlocked.Increment(ref _blobCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- 发送 ----

    static void WriteHeader(JsonWriter w)
    {
        w.WriteStartObject();
        w.WritePropertyName("jsonrpc"u8);
        w.WriteStringUtf8("2.0"u8);
    }

    void SendResult(DevToolsSession session, JsonValue id, JsonValue? result, List<DevToolsBlob>? blobs)
    {
        var buf = new JsonBufferWriter();
        var w = new JsonWriter(buf);
        WriteHeader(w);
        w.WritePropertyName("id"u8);
        id.WriteTo(w);
        w.WritePropertyName("result"u8);
        if (result is null) { w.WriteStartObject(); w.WriteEndObject(); }
        else result.WriteTo(w);
        if (blobs != null)
        {
            w.WritePropertyName("blobs"u8);
            w.WriteStartArray();
            foreach (var b in blobs)
            {
                w.WriteStartObject();
                w.WriteString("blobId", b.Id);
                w.WriteNumber("length", b.Data.Length);
                w.WriteString("mimeType", b.MimeType);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();

        if (blobs is null)
        {
            Send(session.Transport, buf.WrittenMemory, blob: false);
            return;
        }
        // 响应与它公告的 blob 作为整体发送，中间不会插入其他消息（包括接收回调里重入发出的响应）
        var data = new ReadOnlyMemory<byte>[blobs.Count];
        for (int i = 0; i < data.Length; i++) data[i] = blobs[i].Data;
        Task task;
        try { task = session.Transport.SendWithBlobsAsync(buf.WrittenMemory, data); }
        catch (Exception) { return; }
        Observe(task);
    }

    void SendError(DevToolsSession session, JsonValue id, int code, string message, JsonValue? data = null)
    {
        var buf = new JsonBufferWriter();
        var w = new JsonWriter(buf);
        WriteHeader(w);
        w.WritePropertyName("id"u8);
        id.WriteTo(w);
        w.WritePropertyName("error"u8);
        w.WriteStartObject();
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        if (data != null) { w.WritePropertyName("data"u8); data.WriteTo(w); }
        w.WriteEndObject();
        w.WriteEndObject();
        Send(session.Transport, buf.WrittenMemory, blob: false);
    }

    /// <summary>向启用了 <paramref name="domain"/> 的已授权会话发送事件；<paramref name="only"/> 限定单个会话，<paramref name="filter"/> 进一步筛选。</summary>
    internal void SendEvent(DevToolsDomain domain, DevToolsSession? only, string eventName, JsonValue? parameters, string? target,
        Func<DevToolsSession, bool>? filter = null)
    {
        var sessions = Volatile.Read(ref _sessions);
        ReadOnlyMemory<byte> bytes = default;
        bool serialized = false;
        foreach (var s in sessions)
        {
            if (only != null && s != only) continue;
            if (!s.IsAuthorized || !s.IsDomainLive(domain)) continue;
            if (filter != null && !filter(s)) continue;
            if (!serialized)
            {
                bytes = SerializeEvent(domain.Name, eventName, parameters, target);
                serialized = true;
            }
            Send(s.Transport, bytes, blob: false);
        }
    }

    static ReadOnlyMemory<byte> SerializeEvent(string domain, string eventName, JsonValue? parameters, string? target)
    {
        var buf = new JsonBufferWriter();
        var w = new JsonWriter(buf);
        WriteHeader(w);
        w.WriteString("method", domain + "." + eventName);
        w.WritePropertyName("params"u8);
        if (parameters is null) { w.WriteStartObject(); w.WriteEndObject(); }
        else parameters.WriteTo(w);
        if (target != null) w.WriteString("target", target);
        w.WriteEndObject();
        return buf.WrittenMemory;
    }

    // ---- 按帧合并 ----

    readonly struct PendingEvent
    {
        public PendingEvent(DevToolsDomain domain, string name, JsonValue? parameters, string? target)
        {
            Domain = domain; Name = name; Params = parameters; Target = target;
        }
        public readonly DevToolsDomain Domain;
        public readonly string Name;
        public readonly JsonValue? Params;
        public readonly string? Target;
    }

    internal void QueueCoalesced(DevToolsDomain domain, string eventName, string key, JsonValue? parameters, string? target)
    {
        string fullKey = domain.Name + "\n" + eventName + "\n" + key;
        lock (_pending)
        {
            var e = new PendingEvent(domain, eventName, parameters, target);
            if (_pendingIndex.TryGetValue(fullKey, out int i)) _pending[i] = e;
            else
            {
                _pendingIndex.Add(fullKey, _pending.Count);
                _pending.Add(e);
            }
        }
    }

    /// <summary>
    /// 帧边界：调用各已启用 Domain 的 <see cref="DevToolsDomain.OnFrame"/>，然后发出本帧合并的事件（每帧至多一批）。
    /// 由帧调度器在每帧提交后调用（在 UI 线程上）。
    /// </summary>
    public void FlushFrame()
    {
        DevToolsDomain[] domains;
        lock (_gate) domains = _domains.ToArray();
        foreach (var d in domains)
            if (d.IsEnabled) SafeCall(d.FrameCore);

        PendingEvent[] batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
            _pendingIndex.Clear();
        }
        foreach (var e in batch) SendEvent(e.Domain, null, e.Name, e.Params, e.Target);
    }

    public void Dispose()
    {
        DevToolsSession[] sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions;
            _sessions = Array.Empty<DevToolsSession>();
        }
        foreach (var s in sessions)
        {
            Unhook(s);
            ResetSession(s);
        }
    }
}
