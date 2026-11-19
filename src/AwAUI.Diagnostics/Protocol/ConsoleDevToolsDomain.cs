using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// Console 域（design/20）：日志推送 <c>Console.entry</c>、命令注册表 <c>Console.getCommands</c> / <c>Console.invoke</c>。
/// </summary>
/// <remarks>
/// <para>
/// 日志来源有两个：直接调用 <see cref="Log"/>（总是进入缓存），以及 <see cref="DiagnosticHub"/> 上的 <see cref="LogEvent"/>
/// （只在本域启用期间收集，因为该探针类别随启用而开关）。最近的若干条保存在环形缓存中；
/// 会话执行 <c>Console.enable</c> 后先收到响应，再收到缓存中已有条目的补发，此后实时推送。
/// </para>
/// <para>
/// 并发：任意线程都可以 <see cref="Log"/>。每条日志有递增序号；所有 <c>Console.entry</c>（实时与补发）经同一个有序发件队列发出，
/// 同一时刻只有一个线程在发送且不持锁。会话启用时先进入「等待补发」状态（不收实时条目），补发时记下快照的最后序号，
/// 之后只收序号更大的实时条目 —— 因此启用期间别的线程写的日志既不会重复，也不会跑到响应或补发前面。
/// </para>
/// <para>Entry 形状：<c>{ level, message, timestamp, source?: { file, line, column }, componentId?, exception? }</c>，timestamp 为 Unix 毫秒。</para>
/// </remarks>
public sealed class ConsoleDevToolsDomain : DevToolsDomain
{
    readonly object _gate = new();
    readonly RingEntry[] _ring;
    int _ringStart, _ringCount;
    long _seq;
    // 会话 → 补发快照覆盖到的最后序号；long.MaxValue 表示补发尚未进行（暂不推送实时条目）
    readonly Dictionary<DevToolsSession, long> _watermarks = new();
    readonly Queue<Outgoing> _outbox = new();
    bool _draining;
    readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.Ordinal);
    IDisposable? _hubSubscription;

    readonly struct RingEntry
    {
        public RingEntry(long seq, JsonObject entry)
        {
            Seq = seq; Entry = entry;
        }
        public readonly long Seq;
        public readonly JsonObject Entry;
    }

    readonly struct Outgoing
    {
        public Outgoing(DevToolsSession? only, long seq, JsonObject entry)
        {
            Only = only; Seq = seq; Entry = entry;
        }
        /// <summary>null 表示实时广播；否则是只发给该会话的补发。</summary>
        public readonly DevToolsSession? Only;
        public readonly long Seq;
        public readonly JsonObject Entry;
    }

    internal ConsoleDevToolsDomain(int bufferSize) : base("Console", DiagnosticCategory.Log)
    {
        _ring = new RingEntry[Math.Max(0, bufferSize)];
    }

    protected override void RegisterMethods(DevToolsMethodTable methods)
    {
        methods.Add("getCommands", _ => GetCommands());
        methods.Add("invoke", Invoke);
        methods.Add("clear", _ => { Clear(); return null; });
    }

    protected override void OnEnabled() =>
        _hubSubscription = DiagnosticHub.Subscribe<LogEvent>((in LogEvent e) =>
            Log(e.Level, e.Message, e.Exception, e.SourceFile, e.SourceLine));

    protected override void OnDisabled()
    {
        _hubSubscription?.Dispose();
        _hubSubscription = null;
    }

    internal override void SessionEnabling(DevToolsSession session)
    {
        lock (_gate) _watermarks[session] = long.MaxValue;
    }

    internal override void SessionDisabledFor(DevToolsSession session)
    {
        lock (_gate) _watermarks.Remove(session);
    }

    protected override void OnSessionEnabled(DevToolsSession session)
    {
        lock (_gate)
        {
            if (!_watermarks.ContainsKey(session)) return;    // 补发前已被停用
            for (int i = 0; i < _ringCount; i++)
            {
                var e = _ring[(_ringStart + i) % _ring.Length];
                _outbox.Enqueue(new Outgoing(session, e.Seq, e.Entry));
            }
            _watermarks[session] = _seq;
        }
        Drain();
    }

    // ---- 日志 ----

    /// <summary>记录一条日志。任意线程可调用。</summary>
    public void Log(LogLevel level, string message, Exception? exception = null, string? sourceFile = null, int sourceLine = 0, long componentId = 0)
    {
        var entry = new JsonObject
        {
            ["level"] = LevelName(level),
            ["message"] = message ?? string.Empty,
            ["timestamp"] = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
        };
        if (sourceFile != null)
            entry["source"] = new JsonObject { ["file"] = sourceFile, ["line"] = sourceLine, ["column"] = 0 };
        if (componentId != 0) entry["componentId"] = componentId;
        if (exception != null) entry["exception"] = exception.ToString();

        bool live;
        lock (_gate)
        {
            // 在同一把锁下判断：有任何会话处于「启用中 / 已启用」状态（SessionEnabling 先于计数登记水位）就进入发件队列，
            // 由 ShouldStream 按水位决定发给谁。若在锁外读 IsEnabled，补发快照之后才拿到序号的条目可能既不补发也不推送。
            live = _watermarks.Count > 0;
            long seq = ++_seq;
            if (_ring.Length > 0)
            {
                var r = new RingEntry(seq, entry);
                if (_ringCount < _ring.Length) _ring[(_ringStart + _ringCount++) % _ring.Length] = r;
                else
                {
                    _ring[_ringStart] = r;
                    _ringStart = (_ringStart + 1) % _ring.Length;
                }
            }
            if (live) _outbox.Enqueue(new Outgoing(null, seq, entry));
        }
        if (live) Drain();
    }

    /// <summary>
    /// 按入队顺序发出发件队列。同一时刻只有一个线程在发送（其他线程只入队），发送时不持锁。
    /// 一个线程一次最多代发 256 条，其余转到线程池继续，记录日志的线程不会被别的线程持续写入的日志拖住。
    /// </summary>
    void Drain()
    {
        lock (_gate)
        {
            if (_draining) return;
            _draining = true;
        }
        DrainOwned();
    }

    void DrainOwned()
    {
        for (int sent = 0; ; sent++)
        {
            Outgoing item;
            lock (_gate)
            {
                if (_outbox.Count == 0)
                {
                    _draining = false;
                    return;
                }
                if (sent == 256)
                {
                    _ = System.Threading.Tasks.Task.Run(DrainOwned);
                    return;
                }
                item = _outbox.Dequeue();
            }
            try
            {
                if (item.Only != null) EmitTo(item.Only, "entry", item.Entry);
                else
                {
                    long seq = item.Seq;
                    Agent?.SendEvent(this, null, "entry", item.Entry, null, s => ShouldStream(s, seq));
                }
            }
            catch (Exception) { /* 诊断不得把异常抛进记录日志的代码 */ }
        }
    }

    bool ShouldStream(DevToolsSession session, long seq)
    {
        lock (_gate)
            return !_watermarks.TryGetValue(session, out long mark) || seq > mark;
    }

    /// <summary>清空缓存的日志。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring, 0, _ring.Length);
            _ringStart = _ringCount = 0;
        }
    }

    static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warning => "warning",
        _ => "error",
    };

    // ---- 命令 ----

    /// <summary>登记命令；同名命令被替换。</summary>
    public void RegisterCommand(ConsoleCommand command)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));
        lock (_gate) _commands[command.Name] = command;
    }

    /// <summary>便捷登记。</summary>
    public void RegisterCommand(string name, string description, Func<ConsoleArguments, string?> handler, params ConsoleParameter[] parameters) =>
        RegisterCommand(new ConsoleCommand(name, description, parameters, handler));

    public bool UnregisterCommand(string name)
    {
        lock (_gate) return _commands.Remove(name);
    }

    JsonValue GetCommands()
    {
        var list = new JsonArray();
        lock (_gate)
            foreach (var c in _commands.Values) list.Add(c.ToJson());
        return new JsonObject { ["commands"] = list };
    }

    JsonValue? Invoke(DevToolsCallContext ctx)
    {
        string name = ctx.GetString("name");
        ConsoleCommand? command;
        lock (_gate) _commands.TryGetValue(name, out command);
        if (command is null)
            throw DevToolsProtocolException.InvalidParams($"Unknown command '{name}'.", new JsonObject { ["name"] = name });

        var args = ctx.Params["args"] switch
        {
            null or JsonNull => new JsonObject(),
            JsonObject a => a,
            _ => throw DevToolsProtocolException.InvalidParams("Parameter 'args' must be an object.", new JsonObject { ["parameter"] = "args" }),
        };

        var values = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var kv in args)
        {
            ConsoleParameter? decl = null;
            foreach (var p in command.Parameters)
                if (p.Name == kv.Key) { decl = p; break; }
            if (decl is null)
                throw DevToolsProtocolException.InvalidParams($"Command '{name}' has no parameter '{kv.Key}'.", new JsonObject { ["argument"] = kv.Key });
            if (kv.Value.IsNull) continue;
            if (!Matches(decl.Type, kv.Value))
                throw DevToolsProtocolException.InvalidParams($"Argument '{kv.Key}' must be of type {ConsoleParameter.TypeName(decl.Type)}.",
                    new JsonObject { ["argument"] = kv.Key, ["expected"] = ConsoleParameter.TypeName(decl.Type) });
            values[kv.Key] = kv.Value;
        }
        foreach (var p in command.Parameters)
            if (!p.Optional && !values.ContainsKey(p.Name))
                throw DevToolsProtocolException.InvalidParams($"Missing argument '{p.Name}' for command '{name}'.", new JsonObject { ["argument"] = p.Name });

        string? result = command.Handler(new ConsoleArguments(command, values));
        var o = new JsonObject();
        if (result != null) o["result"] = result;
        return o;
    }

    static bool Matches(ConsoleParameterType type, JsonValue v) => type switch
    {
        ConsoleParameterType.String => v.Kind == JsonValueKind.String,
        ConsoleParameterType.Integer => v is JsonNumber n && n.TryGetInt64(out _),
        ConsoleParameterType.Number => v.Kind == JsonValueKind.Number,
        ConsoleParameterType.Boolean => v.Kind == JsonValueKind.Boolean,
        _ => true,
    };
}
