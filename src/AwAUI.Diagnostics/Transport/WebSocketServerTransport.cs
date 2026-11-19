using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AwAUI.Diagnostics.Transport;

/// <summary>
/// WebSocket 服务端传输（design/06 ③），基于 <see cref="TcpListener"/> 自实现 RFC 6455，全目标统一（含 .NET Framework）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>默认只绑定回环地址；同一时刻只服务一个连接，其余握手请求得到 503。连接断开后可接受新连接，
/// 每次新连接触发 <see cref="DevToolsTransport.Connected"/>，Agent 据此重置会话（需要重新配对）。</item>
/// <item>支持：握手（Sec-WebSocket-Accept = Base64(SHA-1)）、客户端掩码校验、分片重组、Ping/Pong、关闭握手、文本与二进制消息、
/// 最大消息长度（1009）、文本 UTF-8 校验（1007）、协议错误（1002）、Origin 校验。不协商扩展（permessage-deflate）与子协议。</item>
/// <item>服务端发送的每条消息是单个未分片帧，按调用顺序发出。</item>
/// <item>所有 I/O 都是异步的，在线程池上运行；<see cref="DevToolsTransport.Received"/> 在 I/O 线程上触发。</item>
/// </list>
/// </remarks>
public sealed class WebSocketServerTransport : DevToolsTransport
{
    readonly WebSocketServerOptions _options;
    readonly object _gate = new();
    TcpListener? _listener;
    WebSocketConnection? _current;
    bool _stopped;

    public WebSocketServerTransport(WebSocketServerOptions? options = null)
    {
        _options = options ?? new WebSocketServerOptions();
        if (_options.MaxMessageSize <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxMessageSize must be positive.");
        if (_options.MaxSendQueueBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxSendQueueBytes must be positive.");
    }

    public WebSocketServerOptions Options => _options;

    /// <summary>实际监听的终结点（<see cref="Start"/> 之后可用）。</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    public override bool IsConnected { get { lock (_gate) return _current != null; } }
    public override bool SupportsBlobs => true;

    /// <summary>开始监听。立即返回；接受连接在后台异步进行。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_stopped) throw new ObjectDisposedException(nameof(WebSocketServerTransport));
            if (_listener != null) throw new InvalidOperationException("The server is already started.");
            var listener = new TcpListener(_options.Address, _options.Port);
            listener.Start();
            _listener = listener;
            LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        }
        _ = AcceptLoopAsync();
    }

    async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpListener? listener;
            lock (_gate) listener = _stopped ? null : _listener;
            if (listener is null) return;
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }
            catch (SocketException)
            {
                lock (_gate) if (_stopped) return;
                continue;
            }
            _ = HandleClientAsync(client);
        }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        WebSocketConnection conn;
        try
        {
            client.NoDelay = true;
            conn = new WebSocketConnection(client, _options);
        }
        catch (Exception)
        {
            client.Close();
            return;
        }

        bool accepted = false;
        try
        {
            accepted = await conn.HandshakeAsync(result =>
            {
                if (!result.Accepted) return result;
                lock (_gate)
                {
                    if (_stopped) return new WebSocketHandshake.Result(503, "Server is stopping");
                    if (_current != null) return new WebSocketHandshake.Result(503, "Another DevTools client is already connected");
                    _current = conn;
                }
                return result;
            }).ConfigureAwait(false);
        }
        catch (Exception) { }

        if (!accepted)
        {
            lock (_gate) if (_current == conn) _current = null;
            conn.Abort();
            return;
        }

        conn.TextReceived = data => OnReceived(data);
        conn.BinaryReceived = data => OnBlobReceived(data);
        try { OnConnected(); }
        catch (Exception) { }
        await conn.RunAsync().ConfigureAwait(false);
        lock (_gate) if (_current == conn) _current = null;
        try { OnDisconnected(); }
        catch (Exception) { }
    }

    WebSocketConnection? Current { get { lock (_gate) return _current; } }

    /// <summary>当前连接发送队列中的字节数（测试与诊断用）。</summary>
    internal long QueuedSendBytes => Current?.QueuedBytes ?? 0;

    public override Task SendAsync(ReadOnlyMemory<byte> message) =>
        Current?.EnqueueAsync(WebSocketConnection.OpText, message) ?? Task.CompletedTask;

    public override Task SendBlobAsync(ReadOnlyMemory<byte> data) =>
        Current?.EnqueueAsync(WebSocketConnection.OpBinary, data) ?? Task.CompletedTask;

    /// <summary>文本消息与其后的二进制帧原子地进入同一个发送队列，中间不会插入其他帧。</summary>
    public override Task SendWithBlobsAsync(ReadOnlyMemory<byte> message, IReadOnlyList<ReadOnlyMemory<byte>> blobs)
    {
        if (blobs is null) throw new ArgumentNullException(nameof(blobs));
        return Current?.EnqueueAsync(WebSocketConnection.OpText, message, blobs) ?? Task.CompletedTask;
    }

    /// <summary>绑定在非回环地址（含 <see cref="IPAddress.Any"/>）上时为 true，Agent 据此要求配对令牌。</summary>
    public override bool AcceptsRemoteConnections => !IPAddress.IsLoopback(_options.Address);

    /// <summary>以关闭握手断开当前连接（不停止监听）。</summary>
    public Task DisconnectAsync(int closeCode = 1000, string reason = "") =>
        Current?.CloseAsync(closeCode, reason) ?? Task.CompletedTask;

    /// <summary>以 1008（Policy Violation）关闭当前连接，例如协议主版本不兼容。</summary>
    public override Task DisconnectAsync(string reason) => DisconnectAsync(1008, reason);

    /// <summary>停止监听并以 1001（Going Away）关闭当前连接。</summary>
    public async Task StopAsync()
    {
        TcpListener? listener;
        WebSocketConnection? current;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            listener = _listener;
            current = _current;
        }
        try { listener?.Stop(); }
        catch (Exception) { }
        if (current != null)
        {
            await current.CloseAsync(1001, "Server stopping").ConfigureAwait(false);
            await current.Finished.ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        TcpListener? listener;
        WebSocketConnection? current;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            listener = _listener;
            current = _current;
        }
        try { listener?.Stop(); }
        catch (Exception) { }
        current?.Abort();
    }
}
