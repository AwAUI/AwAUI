using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>同步方法处理器。返回结果对象；返回 null 表示空结果 <c>{}</c>。抛 <see cref="DevToolsProtocolException"/> 返回错误。</summary>
public delegate JsonValue? DevToolsMethodHandler(DevToolsCallContext context);

/// <summary>
/// 异步方法处理器（例如需要等下一帧才有结果的截图）。处理器在调度线程上开始执行，但 await 之后的续体不保证回到调度线程，
/// 需要访问元素树时应自行投递回 UI 线程。
/// </summary>
public delegate Task<JsonValue?> DevToolsAsyncMethodHandler(DevToolsCallContext context);

/// <summary>一次方法调用的上下文：参数、会话、目标，以及读取参数的便捷方法（参数不合法时抛 -32602）。</summary>
public sealed class DevToolsCallContext
{
    List<DevToolsBlob>? _blobs;

    internal DevToolsCallContext(DevToolsAgent agent, DevToolsSession session, string method, JsonObject parameters, DevToolsTarget? target, bool isNotification)
    {
        Agent = agent; Session = session; Method = method; Params = parameters; Target = target; IsNotification = isNotification;
    }

    public DevToolsAgent Agent { get; }
    public DevToolsSession Session { get; }
    /// <summary>完整方法名，如 "Console.invoke"。</summary>
    public string Method { get; }
    /// <summary>参数对象；请求省略 params 时为空对象。</summary>
    public JsonObject Params { get; }
    /// <summary>请求的 <c>target</c>；省略时为默认目标（没有任何目标时为 null）。</summary>
    public DevToolsTarget? Target { get; }
    /// <summary>是否为通知（无 id，不会有响应）。</summary>
    public bool IsNotification { get; }

    internal List<DevToolsBlob>? Blobs => _blobs;

    /// <summary>响应发出之后要执行的动作（例如 enable 后补发事件）。</summary>
    internal Action? AfterResponse { get; set; }

    /// <summary>设置后，Agent 在发出错误响应之后断开这条连接（App.hello 主版本不兼容时「拒绝连接」）。</summary>
    internal string? DisconnectReason { get; set; }

    /// <summary>附加一个二进制旁路数据，返回其 blobId（放进结果对象里）。</summary>
    public string AttachBlob(ReadOnlyMemory<byte> data, string mimeType = "application/octet-stream")
    {
        if (!Session.Transport.SupportsBlobs)
            throw new DevToolsProtocolException(DevToolsErrorCodes.NotSupported, "The transport does not support binary data.");
        var blob = new DevToolsBlob(Agent.NextBlobId(), data, mimeType);
        (_blobs ??= new List<DevToolsBlob>()).Add(blob);
        return blob.Id;
    }

    // ---- 参数读取 ----

    JsonValue? Get(string name) => Params[name] is { IsNull: false } v ? v : null;

    static DevToolsProtocolException Bad(string name, string expected) =>
        DevToolsProtocolException.InvalidParams($"Parameter '{name}' must be {expected}.", new JsonObject { ["parameter"] = name });

    public string GetString(string name) =>
        Get(name) is JsonString s ? s.Value : throw (Get(name) is null ? Missing(name) : Bad(name, "a string"));

    public string? GetOptionalString(string name) => Get(name) switch
    {
        null => null,
        JsonString s => s.Value,
        _ => throw Bad(name, "a string"),
    };

    public long GetInt64(string name) =>
        Get(name) is JsonNumber n && n.TryGetInt64(out long v) ? v : throw (Get(name) is null ? Missing(name) : Bad(name, "an integer"));

    public long? GetOptionalInt64(string name) => Get(name) switch
    {
        null => null,
        JsonNumber n when n.TryGetInt64(out long v) => v,
        _ => throw Bad(name, "an integer"),
    };

    public double GetDouble(string name) =>
        Get(name) is JsonNumber n ? n.GetDouble() : throw (Get(name) is null ? Missing(name) : Bad(name, "a number"));

    public bool GetBoolean(string name, bool defaultValue) => Get(name) switch
    {
        null => defaultValue,
        JsonBoolean b => b.GetBoolean(),
        _ => throw Bad(name, "a boolean"),
    };

    /// <summary>必需的字符串数组参数。</summary>
    public IReadOnlyList<string> GetStringArray(string name)
    {
        if (Get(name) is not JsonArray a) throw (Get(name) is null ? Missing(name) : Bad(name, "an array of strings"));
        var list = new string[a.Count];
        for (int i = 0; i < list.Length; i++)
            list[i] = a[i] is JsonString s ? s.Value : throw Bad(name, "an array of strings");
        return list;
    }

    /// <summary>可选的对象参数；省略或为 null 时返回 null。</summary>
    public JsonObject? GetOptionalObject(string name) => Get(name) switch
    {
        null => null,
        JsonObject o => o,
        _ => throw Bad(name, "an object"),
    };

    /// <summary>必需的有限数值参数（拒绝溢出为 ±∞ 的字面量）。</summary>
    public double GetFiniteDouble(string name)
    {
        double v = GetDouble(name);
        return !double.IsNaN(v) && !double.IsInfinity(v) ? v : throw Bad(name, "a finite number");
    }

    static DevToolsProtocolException Missing(string name) =>
        DevToolsProtocolException.InvalidParams($"Missing required parameter '{name}'.", new JsonObject { ["parameter"] = name });
}
