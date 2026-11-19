using System;
using System.Net;

namespace AwAUI.Diagnostics.Transport;

/// <summary><see cref="WebSocketServerTransport"/> 的选项。</summary>
public sealed class WebSocketServerOptions
{
    /// <summary>
    /// 监听地址，默认只绑定回环（design/06「安全」）。改为非回环地址时传输报告
    /// <see cref="DevToolsTransport.AcceptsRemoteConnections"/>，Agent 要求设置配对令牌，否则拒绝挂接 / 拒绝所有请求。
    /// </summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>监听端口；0 表示由系统分配（启动后看 <see cref="WebSocketServerTransport.LocalEndPoint"/>）。</summary>
    public int Port { get; set; }

    /// <summary>只接受该请求路径（如 "/devtools"）；null 表示接受任意路径。</summary>
    public string? Path { get; set; }

    /// <summary>单条消息（重组分片后）的最大字节数，超过时以 1009 关闭连接。默认 16 MiB。</summary>
    public int MaxMessageSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>握手请求头的最大字节数。</summary>
    public int MaxHandshakeSize { get; set; } = 16 * 1024;

    /// <summary>握手必须在此时间内完成。</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>服务端发起关闭后等待对端关闭帧的时间。</summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>定期发送 Ping 的间隔；<see cref="TimeSpan.Zero"/> 表示不发送。</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// 启用 <see cref="KeepAliveInterval"/> 时，若某个保活 Ping 发出后超过本值仍没有从对端收到任何字节（包括 Pong），判定对端失联并断开 TCP。
    /// 计时从 Ping 发出时开始，且每个保活间隔最多记入 2 倍间隔：线程池饥饿或进程挂起（调试器断点、系统睡眠）期间
    /// Ping 发不出去、Pong 读不进来，这段时间不算作对端无响应。
    /// <see cref="TimeSpan.Zero"/> 表示不检查。默认 30 秒。
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 发送队列（已排队但尚未写入套接字的帧，含正在写的那一帧）的字节上限，默认 32 MiB。
    /// 对端停止读取（调试器暂停、慢速 adb / Wi-Fi 链路、卡死的前端）时，事件不会无限堆积在被调试应用的内存里：
    /// 超过上限时丢弃队列中的帧，以 1008 关闭连接，<see cref="CloseTimeout"/> 后强制断开；前端重连后可重新获取状态。
    /// 队列为空时单条消息不受此限（大截图总能发出）。
    /// </summary>
    public long MaxSendQueueBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    /// 校验 Origin 头（参数为 null 表示请求没有 Origin，即非浏览器客户端）。默认 <see cref="IsLocalOrigin"/>：
    /// 允许无 Origin 与 localhost / 127.0.0.1 / [::1] 来源，拒绝其他网页发起的跨站 WebSocket 连接。
    /// </summary>
    public Func<string?, bool>? OriginValidator { get; set; }

    /// <summary>默认的 Origin 校验。</summary>
    public static bool IsLocalOrigin(string? origin)
    {
        if (origin is null) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;
        string host = uri.Host;
        return host == "localhost" || host == "127.0.0.1" || host == "[::1]" || host == "::1";
    }
}
