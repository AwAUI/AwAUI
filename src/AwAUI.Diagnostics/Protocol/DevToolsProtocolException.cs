using System;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>方法处理器抛出它来返回一个 JSON-RPC 错误响应（<c>error: { code, message, data }</c>）。</summary>
public sealed class DevToolsProtocolException : Exception
{
    public DevToolsProtocolException(int code, string message, JsonValue? data = null) : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    /// <summary>错误码，见 <see cref="DevToolsErrorCodes"/>。</summary>
    public int Code { get; }

    /// <summary>可选的附加数据（错误对象的 <c>data</c> 字段）。</summary>
    public JsonValue? ErrorData { get; }

    public static DevToolsProtocolException InvalidParams(string message, JsonValue? data = null) =>
        new(DevToolsErrorCodes.InvalidParams, message, data);
}
