using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AwAUI.Diagnostics.Transport;

/// <summary>
/// DevTools 传输（design/06 ③、design/20）。一条传输连接一个对端；Agent 在其上建立一个会话。
/// </summary>
/// <remarks>
/// <para>
/// 与 design/06 的草图相比，发送返回 <see cref="Task"/> 而不是 <c>ValueTask</c>：netstandard2.0 基线没有 ValueTask，
/// 而核心库不引入 System.Threading.Tasks.Extensions。
/// </para>
/// <para>
/// 约定：<see cref="Received"/> 与 <see cref="BlobReceived"/> 交给订阅者的内存归订阅者所有（传输不会复用它）；
/// 事件可能在任意线程（I/O 线程）上触发，但同一传输上的接收事件不会重入（一个处理器返回前不会开始投递下一条）。
/// 发送方法线程安全，按调用顺序发出；传给发送方法的内存在返回的任务完成前不得修改。
/// Agent 调用发送方法时不持有任何锁，因此进程内传输可以在发送方的栈上直接运行对端的接收回调。
/// </para>
/// </remarks>
public abstract class DevToolsTransport : IDisposable
{
    /// <summary>收到一条文本（UTF-8 JSON）消息。</summary>
    public event Action<ReadOnlyMemory<byte>>? Received;

    /// <summary>收到一条二进制消息。</summary>
    public event Action<ReadOnlyMemory<byte>>? BlobReceived;

    /// <summary>对端连上（一次新的连接开始，Agent 据此重置会话）。</summary>
    public event Action? Connected;

    /// <summary>对端断开。</summary>
    public event Action? Disconnected;

    /// <summary>当前是否有对端连接。</summary>
    public abstract bool IsConnected { get; }

    /// <summary>是否支持二进制旁路（<see cref="SendBlobAsync"/>）。</summary>
    public virtual bool SupportsBlobs => false;

    /// <summary>发送一条 UTF-8 JSON 文本消息。没有对端时消息被丢弃。</summary>
    public abstract Task SendAsync(ReadOnlyMemory<byte> message);

    /// <summary>发送一条二进制消息。Agent 发送带 blob 的响应时使用 <see cref="SendWithBlobsAsync"/>，见 <see cref="Protocol.DevToolsBlob"/>。</summary>
    public virtual Task SendBlobAsync(ReadOnlyMemory<byte> data) =>
        Task.FromException(new NotSupportedException("This transport does not support binary messages."));

    /// <summary>
    /// 把一条文本消息与紧随其后的若干二进制消息作为<b>不可分割的整体</b>发送：本传输上的其他消息（包括其他线程、
    /// 或接收回调里重入发出的消息）不会插到它们中间。Agent 用它发送带 blob 的响应（见 <see cref="Protocol.DevToolsBlob"/>）。
    /// </summary>
    /// <remarks>
    /// 默认实现只是依次调用 <see cref="SendAsync"/> 与 <see cref="SendBlobAsync"/>，不保证原子性；
    /// <see cref="SupportsBlobs"/> 为 true 的传输必须重写它（内建传输都已重写）。返回的任务在最后一条消息发出（或被丢弃）后完成。
    /// </remarks>
    public virtual Task SendWithBlobsAsync(ReadOnlyMemory<byte> message, IReadOnlyList<ReadOnlyMemory<byte>> blobs)
    {
        if (blobs is null) throw new ArgumentNullException(nameof(blobs));
        Task last = SendAsync(message);
        for (int i = 0; i < blobs.Count; i++) last = SendBlobAsync(blobs[i]);
        return last;
    }

    /// <summary>
    /// 是否可能接受来自其他设备的连接（例如绑定在非回环地址上的服务端）。为 true 时 Agent 要求设置配对令牌
    /// （design/06「安全」：跨设备连接需要配对令牌）。
    /// </summary>
    public virtual bool AcceptsRemoteConnections => false;

    /// <summary>
    /// 主动断开当前对端（例如 App.hello 主版本不兼容时拒绝连接，design/20）。已排队的消息先发出。
    /// 默认实现什么也不做（进程内传输的对端是同一进程中的前端，会话保持未配对状态即可）。
    /// </summary>
    public virtual Task DisconnectAsync(string reason) => Task.CompletedTask;

    protected void OnReceived(ReadOnlyMemory<byte> message) => Received?.Invoke(message);
    protected void OnBlobReceived(ReadOnlyMemory<byte> data) => BlobReceived?.Invoke(data);
    protected void OnConnected() => Connected?.Invoke();
    protected void OnDisconnected() => Disconnected?.Invoke();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }
}
