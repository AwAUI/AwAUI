using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AwAUI.Diagnostics.Transport;

/// <summary>
/// 进程内成对传输：一端发送的消息投递到另一端的 <see cref="DevToolsTransport.Received"/>（复制一份，接收方拥有它）。
/// 用于内嵌 / 独立窗口形态的前端与测试。
/// </summary>
/// <remarks>
/// <para>当前仍然传递 JSON 字节；design/20 所说「进程内跳过 JSON、直接调用强类型接口」需要协议源生成器，届时在此之上增加。</para>
/// <para>
/// 投递：每一端有一个收件队列。发送把消息放进对端的队列；若当时没有线程正在投递该队列，就在发送方的线程上立即投递，
/// 因此无竞争时发送返回时对端已经处理完。若接收回调里又发送了消息（重入，例如内嵌前端在 UI 线程上收到响应后立刻发下一个请求），
/// 或另一个线程正在投递，新消息只入队，由正在投递的线程在当前回调返回后按顺序投递 —— 回调从不嵌套、从不并发，
/// <see cref="SendWithBlobsAsync"/> 的一组消息总是连续到达。投递者一次最多代投 256 条，其余转到线程池继续，
/// 所以别的线程持续发送时，发送方也不会被无限期拖在投递循环里。投递时不持有任何锁，发送方从不阻塞等待。
/// </para>
/// <para>
/// 因此接收回调里不要同步等待对方对本次发送的答复（它要等当前回调返回后才会被投递）。
/// 接收回调抛出的异常被吞掉，不影响后续投递。
/// </para>
/// </remarks>
public sealed class InProcTransport : DevToolsTransport
{
    readonly object _gate = new();
    InProcTransport? _peer;
    volatile bool _disposed;

    readonly Queue<Delivery> _inbox = new();
    bool _delivering;

    readonly struct Delivery
    {
        public Delivery(byte[] data, bool isBlob)
        {
            Data = data; IsBlob = isBlob;
        }
        public readonly byte[] Data;
        public readonly bool IsBlob;
    }

    InProcTransport() { }

    /// <summary>创建一对相连的传输。</summary>
    public static (InProcTransport Agent, InProcTransport Client) CreatePair()
    {
        var a = new InProcTransport();
        var b = new InProcTransport();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public override bool IsConnected { get { lock (_gate) return _peer != null; } }
    public override bool SupportsBlobs => true;

    public override Task SendAsync(ReadOnlyMemory<byte> message) =>
        Post(new[] { new Delivery(message.ToArray(), false) });

    public override Task SendBlobAsync(ReadOnlyMemory<byte> data) =>
        Post(new[] { new Delivery(data.ToArray(), true) });

    public override Task SendWithBlobsAsync(ReadOnlyMemory<byte> message, IReadOnlyList<ReadOnlyMemory<byte>> blobs)
    {
        if (blobs is null) throw new ArgumentNullException(nameof(blobs));
        var items = new Delivery[blobs.Count + 1];
        items[0] = new Delivery(message.ToArray(), false);
        for (int i = 0; i < blobs.Count; i++) items[i + 1] = new Delivery(blobs[i].ToArray(), true);
        return Post(items);
    }

    Task Post(Delivery[] items)
    {
        InProcTransport? peer;
        lock (_gate)
        {
            if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(InProcTransport)));
            peer = _peer;
        }
        peer?.Enqueue(items);
        return Task.CompletedTask;
    }

    /// <summary>一个线程一次最多代为投递的条数；之后把剩余的投递交给线程池，发送方不会被别的线程持续发来的消息拖住。</summary>
    const int MaxBatch = 256;

    void Enqueue(Delivery[] items)
    {
        lock (_inbox)
        {
            foreach (var item in items) _inbox.Enqueue(item);
            if (_delivering) return;    // 正在投递的线程（可能就是本线程的上层栈帧）会接着投递
            _delivering = true;
        }
        Drain();
    }

    /// <summary>在持有投递权（_delivering）的情况下投递收件队列。</summary>
    void Drain()
    {
        for (int delivered = 0; ; delivered++)
        {
            Delivery item;
            lock (_inbox)
            {
                if (_inbox.Count == 0 || _disposed)
                {
                    _inbox.Clear();
                    _delivering = false;
                    return;
                }
                if (delivered == MaxBatch)
                {
                    // 别的线程发得比投递快：保留投递权，转到线程池继续（顺序不变，仍然只有一个投递者）
                    _ = Task.Run(Drain);
                    return;
                }
                item = _inbox.Dequeue();
            }
            try
            {
                if (item.IsBlob) OnBlobReceived(item.Data);
                else OnReceived(item.Data);
            }
            catch (Exception) { /* 接收方的异常不影响发送方与后续投递 */ }
        }
    }

    protected override void Dispose(bool disposing)
    {
        InProcTransport? peer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            peer = _peer;
            _peer = null;
        }
        if (peer != null)
        {
            lock (peer._gate) peer._peer = null;
            peer.OnDisconnected();
        }
        OnDisconnected();
    }
}
