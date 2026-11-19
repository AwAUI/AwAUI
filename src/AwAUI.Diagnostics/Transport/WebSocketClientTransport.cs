using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AwAUI.Diagnostics.Transport;

/// <summary><see cref="WebSocketClientTransport"/> 的选项。</summary>
public sealed class WebSocketClientTransportOptions
{
    /// <summary>单条入站消息（重组分片后）的最大字节数；超过时以 1009 关闭连接。默认 64 MiB（远程对端不可信）。</summary>
    public int MaxMessageSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>连接（TCP + 握手）的超时。默认 10 秒。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>客户端保活 Ping 的间隔；<see cref="TimeSpan.Zero"/> 表示不发送（服务端的 Ping 总会被自动应答）。浏览器上被忽略。</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.Zero;
}

/// <summary>
/// WebSocket 客户端传输（design/06 ③）：前端主动连接应用的 <c>ws://host:port/devtools</c>（<see cref="WebSocketServerTransport"/>），
/// 使同一个 DevTools 前端（<c>AwAUI.DevTools</c> 面板、脚本）既能检查本进程，也能检查远程应用。基于 <see cref="ClientWebSocket"/>，
/// 文本帧承载 JSON，二进制帧承载 blob。
/// </summary>
/// <remarks>
/// 接收在一个后台循环里顺序进行（接收事件不重入）；发送在一把异步锁下按调用顺序进行，<see cref="SendWithBlobsAsync"/> 的一组帧连续发出。
/// 入站消息大小受 <see cref="WebSocketClientTransportOptions.MaxMessageSize"/> 限制。断开（对端关闭、网络错误、超限）后触发一次
/// <see cref="DevToolsTransport.Disconnected"/>，传输不会自动重连。
/// </remarks>
public sealed class WebSocketClientTransport : DevToolsTransport
{
    readonly ClientWebSocket _socket = new();
    readonly WebSocketClientTransportOptions _options;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    int _state; // 0 未连接，1 已连接，2 已断开
    Task? _receiveLoop;

    WebSocketClientTransport(Uri uri, WebSocketClientTransportOptions options)
    {
        Uri = uri;
        _options = options;
    }

    /// <summary>连接的地址。</summary>
    public Uri Uri { get; }

    /// <summary>对端发来的关闭码（未收到为 null）。</summary>
    public int? RemoteCloseCode { get; private set; }

    /// <summary>连接到 <paramref name="uri"/>（<c>ws://</c> 或 <c>wss://</c>）并开始接收。连接失败时抛出异常。</summary>
    public static async Task<WebSocketClientTransport> ConnectAsync(Uri uri, WebSocketClientTransportOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));
        if (uri.Scheme != "ws" && uri.Scheme != "wss") throw new ArgumentException("The URI scheme must be ws or wss.", nameof(uri));
        var t = new WebSocketClientTransport(uri, options ?? new WebSocketClientTransportOptions());
        try
        {
            await t.ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            t.Dispose();
            throw;
        }
        return t;
    }

    async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_options.KeepAliveInterval > TimeSpan.Zero && !IsBrowser)
        {
            try { _socket.Options.KeepAliveInterval = _options.KeepAliveInterval; }
            catch (PlatformNotSupportedException) { /* 浏览器不支持客户端保活 */ }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        timeout.CancelAfter(_options.ConnectTimeout);
        await _socket.ConnectAsync(Uri, timeout.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref _state, 1);
        OnConnected();
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    static bool IsBrowser => RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER"));

    public override bool IsConnected => Volatile.Read(ref _state) == 1 && _socket.State == WebSocketState.Open;

    public override bool SupportsBlobs => true;

    public override Task SendAsync(ReadOnlyMemory<byte> message) =>
        SendFramesAsync(new[] { (message, WebSocketMessageType.Text) });

    public override Task SendBlobAsync(ReadOnlyMemory<byte> data) =>
        SendFramesAsync(new[] { (data, WebSocketMessageType.Binary) });

    public override Task SendWithBlobsAsync(ReadOnlyMemory<byte> message, IReadOnlyList<ReadOnlyMemory<byte>> blobs)
    {
        if (blobs is null) throw new ArgumentNullException(nameof(blobs));
        var frames = new (ReadOnlyMemory<byte>, WebSocketMessageType)[blobs.Count + 1];
        frames[0] = (message, WebSocketMessageType.Text);
        for (int i = 0; i < blobs.Count; i++) frames[i + 1] = (blobs[i], WebSocketMessageType.Binary);
        return SendFramesAsync(frames);
    }

    async Task SendFramesAsync((ReadOnlyMemory<byte> Data, WebSocketMessageType Type)[] frames)
    {
        // 约定：没有对端时消息被丢弃
        if (!IsConnected) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (data, type) in frames)
            {
                if (!IsConnected) return;
                var segment = MemoryMarshal.TryGetArray(data, out var s) ? s : new ArraySegment<byte>(data.ToArray());
                await _socket.SendAsync(segment, type, endOfMessage: true, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (Volatile.Read(ref _state) != 1 || _socket.State != WebSocketState.Open)
        {
            // 发送途中连接已断开：与「没有对端」同样处理（接收循环负责报告断开）
        }
        finally
        {
            _sendLock.Release();
        }
    }

    async Task ReceiveLoopAsync()
    {
        var chunk = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var r = await _socket.ReceiveAsync(new ArraySegment<byte>(chunk), _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    RemoteCloseCode = r.CloseStatus is { } status ? (int)status : null;
                    try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { /* 对端已经关闭 */ }
                    break;
                }
                if (message.Length + r.Count > _options.MaxMessageSize)
                {
                    try { await _socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { /* 关闭失败也视为断开 */ }
                    break;
                }
                message.Write(chunk, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var data = message.ToArray();
                message.SetLength(0);
                if (message.Capacity > 1024 * 1024) message = new MemoryStream();
                if (r.MessageType == WebSocketMessageType.Text) OnReceived(data);
                else OnBlobReceived(data);
            }
        }
        catch (Exception)
        {
            // 网络错误、取消：作为断开处理
        }
        MarkDisconnected();
    }

    void MarkDisconnected()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2) return;
        try { OnDisconnected(); }
        catch (Exception) { /* 订阅者的异常不影响关闭 */ }
    }

    /// <summary>以正常关闭码关闭连接（已排队的发送先完成）。</summary>
    public override async Task DisconnectAsync(string reason)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason ?? "", timeout.Token).ConfigureAwait(false); }
                catch (Exception) { /* 关闭失败也视为断开 */ }
            }
        }
        finally
        {
            _sendLock.Release();
        }
        if (_receiveLoop is { } loop)
        {
            var done = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (done != loop) _cts.Cancel();
        }
        MarkDisconnected();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        if (Volatile.Read(ref _state) == 1)
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        try { _socket.Abort(); }
        catch (Exception) { }
        _socket.Dispose();
        MarkDisconnected();
    }
}
