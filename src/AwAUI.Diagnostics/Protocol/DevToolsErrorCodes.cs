namespace AwAUI.Diagnostics.Protocol;

/// <summary>JSON-RPC 错误码（design/20「错误码」表）。</summary>
public static class DevToolsErrorCodes
{
    /// <summary>JSON 解析失败（-32700）。</summary>
    public const int ParseError = -32700;
    /// <summary>消息不是有效的 JSON-RPC 请求（-32600），包括批量请求（本协议不支持批量）。</summary>
    public const int InvalidRequest = -32600;
    /// <summary>方法不存在，或在协商出的次版本中尚不存在（-32601）。</summary>
    public const int MethodNotFound = -32601;
    /// <summary>参数无效（-32602），包括协议主版本不兼容。</summary>
    public const int InvalidParams = -32602;
    /// <summary>处理方法时发生内部错误（-32603）。</summary>
    public const int InternalError = -32603;
    /// <summary>节点不存在或已被移除。</summary>
    public const int NodeNotFound = -32001;
    /// <summary>目标（窗口）不存在。</summary>
    public const int TargetNotFound = -32002;
    /// <summary>Domain 未启用。</summary>
    public const int DomainNotEnabled = -32003;
    /// <summary>当前运行时或平台不支持该操作。</summary>
    public const int NotSupported = -32004;
    /// <summary>值无法解析（CSS 文本、属性值）。</summary>
    public const int ValueParseError = -32005;
    /// <summary>未授权（远程连接未完成配对）。</summary>
    public const int Unauthorized = -32006;
}
