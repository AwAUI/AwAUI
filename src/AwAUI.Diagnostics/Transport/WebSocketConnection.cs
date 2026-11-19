using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Transport;

/// <summary>
/// 一条服务端 WebSocket 连接（RFC 6455）：握手、帧解析（掩码、分片、控制帧）、关闭握手、有序发送队列。
/// 全部为异步 I/O，不占用调用线程。
/// </summary>
/// <remarks>
/// 发送队列有字节上限（<see cref="WebSocketServerOptions.MaxSendQueueBytes"/>）：对端不读时不会无限占用宿主内存。
/// 队列中尚未发出的 Pong 会被合并（RFC 6455 §5.5.3 允许只回应最近一次 Ping），保活 Ping 同一时刻至多排队一个。
/// </remarks>
internal sealed class WebSocketConnection
{
    internal const byte OpContinuation = 0x0, OpText = 0x1, OpBinary = 0x2, OpClose = 0x8, OpPing = 0x9, OpPong = 0xA;

    readonly TcpClient _client;
    readonly Stream _stream;
    readonly WebSocketServerOptions _options;
    readonly byte[] _rbuf = new byte[16 * 1024];
    int _rpos, _rlen;

    readonly object _sendGate = new();
    readonly Queue<SendItem> _queue = new();
    long _queuedBytes;          // 队列中与正在写的帧的字节数（含帧头估计）
    SendItem? _queuedPong;      // 尚未出队的 Pong（用于合并）
    SendItem? _queuedPing;      // 尚未出队的保活 Ping
    bool _pumping;
    bool _closeSent;
    bool _aborted;
    long _lastActivity = Stopwatch.GetTimestamp();
    readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WebSocketConnection(TcpClient client, WebSocketServerOptions options)
    {
        _client = client;
        _stream = client.GetStream();
        _options = options;
    }

    /// <summary>接收循环结束（连接已关闭）时完成。</summary>
    public Task Finished => _finished.Task;

    /// <summary>收到完整的文本消息（已校验 UTF-8）。</summary>
    public Action<byte[]>? TextReceived;
    /// <summary>收到完整的二进制消息。</summary>
    public Action<byte[]>? BinaryReceived;

    /// <summary>发送队列中（含正在写的一帧）的字节数估计。</summary>
    internal long QueuedBytes { get { lock (_sendGate) return _queuedBytes; } }

    /// <summary>对端发来的关闭码（未收到为 null）。</summary>
    public int? RemoteCloseCode { get; private set; }

    // ---- 握手 ----

    /// <summary>读取并校验握手请求。<paramref name="accept"/> 决定是否接受（例如已有连接时返回 503）。</summary>
    public async Task<bool> HandshakeAsync(Func<WebSocketHandshake.Result, WebSocketHandshake.Result> accept)
    {
        using var timeout = new CancellationTokenSource(_options.HandshakeTimeout);
        using var reg = timeout.Token.Register(() => Abort());
        int headerEnd;
        while (true)
        {
            headerEnd = IndexOfHeaderEnd();
            if (headerEnd >= 0) break;
            if (_rlen >= _options.MaxHandshakeSize || _rlen >= _rbuf.Length)
            {
                await WriteRawAsync(WebSocketHandshake.BuildErrorResponse(431, "Request header too large")).ConfigureAwait(false);
                return false;
            }
            int n = await _stream.ReadAsync(_rbuf, _rlen, Math.Min(_rbuf.Length, _options.MaxHandshakeSize) - _rlen).ConfigureAwait(false);
            if (n <= 0) return false;
            _rlen += n;
        }
        string head = Encoding.ASCII.GetString(_rbuf, 0, headerEnd);
        _rpos = headerEnd + 4;   // 之后的字节（若有）属于第一帧
        var result = accept(WebSocketHandshake.Validate(WebSocketHandshake.Parse(head), _options));
        if (!result.Accepted)
        {
            await WriteRawAsync(WebSocketHandshake.BuildErrorResponse(result.Status, result.Message)).ConfigureAwait(false);
            return false;
        }
        await WriteRawAsync(WebSocketHandshake.BuildAcceptResponse(result.Key!)).ConfigureAwait(false);
        return !timeout.IsCancellationRequested;
    }

    int IndexOfHeaderEnd()
    {
        for (int i = 3; i < _rlen; i++)
            if (_rbuf[i] == '\n' && _rbuf[i - 1] == '\r' && _rbuf[i - 2] == '\n' && _rbuf[i - 3] == '\r') return i - 3;
        return -1;
    }

    async Task WriteRawAsync(byte[] data)
    {
        try { await _stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- 接收 ----

    /// <summary>运行接收循环直到连接关闭。从不抛异常。</summary>
    public async Task RunAsync()
    {
        try
        {
            if (_options.KeepAliveInterval > TimeSpan.Zero) _ = KeepAliveAsync();
            await ReceiveLoopAsync().ConfigureAwait(false);
        }
        catch (Exception) { /* 连接中断 */ }
        finally
        {
            Abort();
            _finished.TrySetResult(true);
        }
    }

    async Task ReceiveLoopAsync()
    {
        byte[]? message = null;
        int messageLength = 0;
        byte messageOpcode = 0;
        var mask = new byte[4];

        while (true)
        {
            if (!await FillAsync(2).ConfigureAwait(false)) return;
            byte b0 = _rbuf[_rpos], b1 = _rbuf[_rpos + 1];
            _rpos += 2;
            bool fin = (b0 & 0x80) != 0;
            byte opcode = (byte)(b0 & 0x0F);
            if ((b0 & 0x70) != 0) { await FailAsync(1002, "Reserved bits must be 0").ConfigureAwait(false); return; }
            if ((b1 & 0x80) == 0) { await FailAsync(1002, "Client frames must be masked").ConfigureAwait(false); return; }

            ulong len = (ulong)(b1 & 0x7F);
            if (len == 126)
            {
                if (!await FillAsync(2).ConfigureAwait(false)) return;
                len = (ulong)((_rbuf[_rpos] << 8) | _rbuf[_rpos + 1]);
                _rpos += 2;
            }
            else if (len == 127)
            {
                if (!await FillAsync(8).ConfigureAwait(false)) return;
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | _rbuf[_rpos + i];
                _rpos += 8;
                if ((len >> 63) != 0) { await FailAsync(1002, "Invalid payload length").ConfigureAwait(false); return; }
            }
            if (!await FillAsync(4).ConfigureAwait(false)) return;
            Buffer.BlockCopy(_rbuf, _rpos, mask, 0, 4);
            _rpos += 4;

            if (opcode >= 0x8)
            {
                if (!fin) { await FailAsync(1002, "Control frames must not be fragmented").ConfigureAwait(false); return; }
                if (len > 125) { await FailAsync(1002, "Control frame payload too long").ConfigureAwait(false); return; }
                if (opcode != OpClose && opcode != OpPing && opcode != OpPong) { await FailAsync(1002, "Unknown opcode").ConfigureAwait(false); return; }
                var payload = new byte[(int)len];
                if (!await ReadPayloadAsync(payload, 0, payload.Length, mask).ConfigureAwait(false)) return;
                if (opcode == OpPing) _ = EnqueuePongAsync(payload);
                else if (opcode == OpClose) { await HandleCloseAsync(payload).ConfigureAwait(false); return; }
                continue;
            }

            if (opcode != OpContinuation && opcode != OpText && opcode != OpBinary) { await FailAsync(1002, "Unknown opcode").ConfigureAwait(false); return; }
            if (opcode == OpContinuation && messageOpcode == 0) { await FailAsync(1002, "Unexpected continuation frame").ConfigureAwait(false); return; }
            if (opcode != OpContinuation && messageOpcode != 0) { await FailAsync(1002, "Expected a continuation frame").ConfigureAwait(false); return; }
            if ((ulong)messageLength + len > (ulong)_options.MaxMessageSize) { await FailAsync(1009, "Message too big").ConfigureAwait(false); return; }

            int total = messageLength + (int)len;
            if (opcode != OpContinuation)
            {
                messageOpcode = opcode;
                message = new byte[fin ? total : Math.Max(total, 256)];
            }
            else if (message!.Length < total)
            {
                Array.Resize(ref message, (int)Math.Min(_options.MaxMessageSize, Math.Max(total, (long)message.Length * 2)));
            }
            if (!await ReadPayloadAsync(message, messageLength, (int)len, mask).ConfigureAwait(false)) return;
            messageLength = total;

            if (!fin) continue;
            byte[] complete = message!.Length == messageLength ? message : AsExact(message, messageLength);
            byte op = messageOpcode;
            message = null; messageLength = 0; messageOpcode = 0;
            if (op == OpText)
            {
                if (Utf8Util.IndexOfInvalid(complete) >= 0) { await FailAsync(1007, "Invalid UTF-8 in text message").ConfigureAwait(false); return; }
                Deliver(TextReceived, complete);
            }
            else Deliver(BinaryReceived, complete);
        }
    }

    static byte[] AsExact(byte[] buffer, int length)
    {
        var r = new byte[length];
        Buffer.BlockCopy(buffer, 0, r, 0, length);
        return r;
    }

    static void Deliver(Action<byte[]>? handler, byte[] data)
    {
        try { handler?.Invoke(data); }
        catch (Exception) { /* 接收方的异常不得中断连接 */ }
    }

    /// <summary>确保缓冲区里至少有 <paramref name="count"/> 个未读字节。对端关闭返回 false。</summary>
    async Task<bool> FillAsync(int count)
    {
        if (_rlen - _rpos >= count) return true;
        if (_rpos > 0)
        {
            Buffer.BlockCopy(_rbuf, _rpos, _rbuf, 0, _rlen - _rpos);
            _rlen -= _rpos;
            _rpos = 0;
        }
        while (_rlen < count)
        {
            int n = await _stream.ReadAsync(_rbuf, _rlen, _rbuf.Length - _rlen).ConfigureAwait(false);
            if (n <= 0) return false;
            _rlen += n;
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
        }
        return true;
    }

    /// <summary>读取 <paramref name="count"/> 字节负载到 dst 并去掩码（掩码索引从帧负载起点算起）。</summary>
    async Task<bool> ReadPayloadAsync(byte[] dst, int offset, int count, byte[] mask)
    {
        int done = 0;
        int buffered = Math.Min(count, _rlen - _rpos);
        if (buffered > 0)
        {
            Buffer.BlockCopy(_rbuf, _rpos, dst, offset, buffered);
            _rpos += buffered;
            done = buffered;
        }
        while (done < count)
        {
            int n = await _stream.ReadAsync(dst, offset + done, count - done).ConfigureAwait(false);
            if (n <= 0) return false;
            done += n;
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
        }
        for (int i = 0; i < count; i++) dst[offset + i] ^= mask[i & 3];
        return true;
    }

    async Task HandleCloseAsync(byte[] payload)
    {
        if (payload.Length == 1) { await FailAsync(1002, "Invalid close payload").ConfigureAwait(false); return; }
        int code = 0;
        if (payload.Length >= 2)
        {
            code = (payload[0] << 8) | payload[1];
            if (!IsValidCloseCode(code)) { await FailAsync(1002, "Invalid close code").ConfigureAwait(false); return; }
            if (Utf8Util.IndexOfInvalid(new ReadOnlySpan<byte>(payload, 2, payload.Length - 2)) >= 0)
            {
                await FailAsync(1007, "Invalid UTF-8 in close reason").ConfigureAwait(false);
                return;
            }
        }
        RemoteCloseCode = code == 0 ? 1005 : code;
        // 回应关闭帧（若尚未发送），然后关闭 TCP
        await EnqueueAsync(OpClose, code == 0 ? Array.Empty<byte>() : new[] { payload[0], payload[1] }).ConfigureAwait(false);
    }

    /// <summary>可出现在关闭帧里的状态码（RFC 6455 §7.4 与 IANA 注册表：1012–1014 已注册；1004–1006、1015 不得出现在线上）。</summary>
    internal static bool IsValidCloseCode(int code) =>
        (code >= 1000 && code <= 1003) || (code >= 1007 && code <= 1014) || (code >= 3000 && code <= 4999);

    /// <summary>以错误码发起关闭；关闭帧发出后继续读取并丢弃对端数据，直到对端关闭或超时，以免未读数据导致 RST 冲掉关闭帧。</summary>
    async Task FailAsync(int code, string reason)
    {
        try
        {
            if (await EnqueueAsync(OpClose, ClosePayload(code, reason)).ConfigureAwait(false))
                await LingerAsync().ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    async Task LingerAsync()
    {
        var deadline = Task.Delay(_options.CloseTimeout);
        var buffer = new byte[4096];
        while (true)
        {
            var read = _stream.ReadAsync(buffer, 0, buffer.Length);
            if (await Task.WhenAny(read, deadline).ConfigureAwait(false) != read)
            {
                _ = read.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
            if (await read.ConfigureAwait(false) <= 0) return;
        }
    }

    /// <summary>构造关闭帧负载：状态码 + 原因（截断到 123 字节，且不切断多字节 UTF-8 序列）。</summary>
    internal static byte[] ClosePayload(int code, string reason)
    {
        var r = Encoding.UTF8.GetBytes(reason ?? string.Empty);
        int length = r.Length;
        if (length > 123)   // 控制帧负载 ≤ 125
        {
            length = 123;
            while (length > 0 && (r[length] & 0xC0) == 0x80) length--;    // r[length] 是续字节：回退到字符边界
        }
        var p = new byte[2 + length];
        p[0] = (byte)(code >> 8);
        p[1] = (byte)code;
        Buffer.BlockCopy(r, 0, p, 2, length);
        return p;
    }

    /// <summary>
    /// 保活：每隔 <see cref="WebSocketServerOptions.KeepAliveInterval"/> 发一个 Ping；
    /// 某个 Ping 发出后累计超过 <see cref="WebSocketServerOptions.KeepAliveTimeout"/> 仍没有收到对端任何字节，才判定失联。
    /// </summary>
    /// <remarks>
    /// 不能简单地比较「现在」与「最后一次收到字节」：本循环、发送泵、接收循环都跑在线程池上，线程池饥饿或进程被挂起
    /// （调试器断点、系统睡眠）时它们一起停摆 —— Ping 没有发出去，或者对端的 Pong 已到达却还没被读出，
    /// 醒来后墙钟时间早已超时，一个一直有求必应的对端就会被误断开。因此：
    /// <list type="bullet">
    /// <item>计时从 Ping 入队时起算，之前没有 Ping 在等回应的空闲时间不算；</item>
    /// <item>每轮最多记入 2 × 间隔（至少 50 ms）：本循环晚醒的那段时间进程本身没在运行，不能都算作对端沉默；</item>
    /// <item>判定前再看套接字里是否已有未读字节：有就说明对端在回应，只是接收循环还没来得及读。</item>
    /// </list>
    /// 对端真的失联时进程照常运行，每轮都按实际间隔记入，约「间隔 + 超时」后断开。
    /// </remarks>
    async Task KeepAliveAsync()
    {
        try
        {
            long timeoutTicks = _options.KeepAliveTimeout > TimeSpan.Zero
                ? (long)(_options.KeepAliveTimeout.TotalSeconds * Stopwatch.Frequency)
                : long.MaxValue;
            var interval = _options.KeepAliveInterval;
            long maxCreditTicks = (long)(Math.Max(2 * interval.TotalSeconds, 0.05) * Stopwatch.Frequency);
            long pingSince = 0;     // 尚未得到回应的最早一个 Ping 的入队时间；0 表示没有
            long silentTicks = 0;   // 自 pingSince 起记入的对端沉默时间
            long last = Stopwatch.GetTimestamp();
            while (!_finished.Task.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(interval), _finished.Task).ConfigureAwait(false);
                if (_finished.Task.IsCompleted) return;
                long now = Stopwatch.GetTimestamp();
                long slept = now - last;
                last = now;
                if (pingSince != 0 && Volatile.Read(ref _lastActivity) - pingSince < 0)
                {
                    // 上一个 Ping 之后还没收到任何字节
                    silentTicks += Math.Min(slept, maxCreditTicks);
                    if (silentTicks > timeoutTicks && !HasUnreadInput())
                    {
                        Abort();    // 对端失联：关闭帧也送不到，直接断开
                        return;
                    }
                }
                else
                {
                    // 之前的 Ping 已得到回应（或尚未发过）：从这个 Ping 重新计时
                    pingSince = now;
                    silentTicks = 0;
                }
                _ = EnqueuePingAsync();
            }
        }
        catch (Exception) { }
    }

    /// <summary>对端发来、已到达本机但接收循环尚未读出的字节。</summary>
    bool HasUnreadInput()
    {
        try { return _client.Available > 0; }
        catch (Exception) { return false; }    // 套接字已关闭
    }

    // ---- 发送 ----

    sealed class SendItem
    {
        public SendItem(byte opcode, ReadOnlyMemory<byte> payload)
        {
            Opcode = opcode; Payload = payload;
        }
        public readonly byte Opcode;
        public ReadOnlyMemory<byte> Payload;    // Pong 合并时可替换（在 _sendGate 下）
        public long Cost => Payload.Length + 14;
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// 入队一帧，按入队顺序发送。关闭帧发出后其余帧被丢弃；连接已断开时立即完成（消息丢弃）。
    /// 返回的任务完成值表示帧是否写出。
    /// </summary>
    public Task<bool> EnqueueAsync(byte opcode, ReadOnlyMemory<byte> payload) =>
        EnqueueAsync(opcode, payload, null);

    /// <summary>
    /// 原子地入队一条数据消息及其后的二进制消息（中间不会插入其他帧）。返回最后一帧的任务。
    /// 数据帧超出发送队列上限时触发溢出关闭（见 <see cref="WebSocketServerOptions.MaxSendQueueBytes"/>）。
    /// </summary>
    public Task<bool> EnqueueAsync(byte opcode, ReadOnlyMemory<byte> payload, IReadOnlyList<ReadOnlyMemory<byte>>? binaryFollowers)
    {
        var first = new SendItem(opcode, payload);
        SendItem last = first;
        long cost = first.Cost;
        SendItem[]? followers = null;
        if (binaryFollowers != null && binaryFollowers.Count > 0)
        {
            followers = new SendItem[binaryFollowers.Count];
            for (int i = 0; i < followers.Length; i++)
            {
                followers[i] = new SendItem(OpBinary, binaryFollowers[i]);
                cost += followers[i].Cost;
            }
            last = followers[followers.Length - 1];
        }

        bool start = false;
        SendItem[]? dropped = null;
        lock (_sendGate)
        {
            if (_closeSent || _aborted) return s_false;
            bool isData = opcode is OpText or OpBinary or OpContinuation;
            if (isData && _queuedBytes > 0 && _queuedBytes + cost > _options.MaxSendQueueBytes)
            {
                dropped = OverflowLocked(ref start);
            }
            else
            {
                if (opcode == OpClose) _closeSent = true;
                EnqueueLocked(first, ref start);
                if (followers != null)
                    foreach (var f in followers) EnqueueLocked(f, ref start);
            }
        }
        if (dropped != null)
        {
            foreach (var d in dropped) d.Done.TrySetResult(false);
            // 对端不读：关闭帧多半也送不出去，超时后强制断开
            _ = Task.Delay(_options.CloseTimeout).ContinueWith(_ => Abort(), TaskScheduler.Default);
        }
        if (start) _ = Task.Run(PumpAsync);
        return dropped != null ? s_false : last.Done.Task;
    }

    static readonly Task<bool> s_false = Task.FromResult(false);

    void EnqueueLocked(SendItem item, ref bool start)
    {
        _queue.Enqueue(item);
        _queuedBytes += item.Cost;
        if (!_pumping) { _pumping = true; start = true; }
    }

    /// <summary>发送队列溢出：丢弃排队的帧，改发 1008 关闭帧。返回被丢弃的帧。</summary>
    SendItem[] OverflowLocked(ref bool start)
    {
        var dropped = _queue.ToArray();
        _queue.Clear();
        foreach (var d in dropped) _queuedBytes -= d.Cost;
        _queuedPong = _queuedPing = null;
        _closeSent = true;
        EnqueueLocked(new SendItem(OpClose, ClosePayload(1008, "Send queue overflow: the DevTools client is not reading")), ref start);
        return dropped;
    }

    /// <summary>回应 Ping。尚未发出的 Pong 被合并为最新一个。</summary>
    Task<bool> EnqueuePongAsync(byte[] payload)
    {
        lock (_sendGate)
        {
            if (_queuedPong != null && !_closeSent && !_aborted)
            {
                _queuedBytes += payload.Length - _queuedPong.Payload.Length;
                _queuedPong.Payload = payload;
                return _queuedPong.Done.Task;
            }
        }
        var item = new SendItem(OpPong, payload);
        bool start = false;
        lock (_sendGate)
        {
            if (_closeSent || _aborted) return s_false;
            EnqueueLocked(item, ref start);
            _queuedPong = item;
        }
        if (start) _ = Task.Run(PumpAsync);
        return item.Done.Task;
    }

    /// <summary>保活 Ping：已有一个在排队时不再追加。</summary>
    Task<bool> EnqueuePingAsync()
    {
        var item = new SendItem(OpPing, Array.Empty<byte>());
        bool start = false;
        lock (_sendGate)
        {
            if (_closeSent || _aborted) return s_false;
            if (_queuedPing != null) return _queuedPing.Done.Task;
            EnqueueLocked(item, ref start);
            _queuedPing = item;
        }
        if (start) _ = Task.Run(PumpAsync);
        return item.Done.Task;
    }

    async Task PumpAsync()
    {
        while (true)
        {
            SendItem item;
            ReadOnlyMemory<byte> payload;
            lock (_sendGate)
            {
                if (_queue.Count == 0 || _aborted) { _pumping = false; return; }
                item = _queue.Dequeue();
                if (item == _queuedPong) _queuedPong = null;
                if (item == _queuedPing) _queuedPing = null;
                payload = item.Payload;
            }
            try
            {
                await WriteFrameAsync(item.Opcode, payload).ConfigureAwait(false);
                item.Done.TrySetResult(true);
                if (item.Opcode == OpClose) OnCloseSent();
            }
            catch (Exception)
            {
                item.Done.TrySetResult(false);   // 连接已断：消息丢弃，不让调用方的任务失败
                Abort();
            }
            finally
            {
                lock (_sendGate) _queuedBytes -= item.Cost;
            }
        }
    }

    void OnCloseSent()
    {
        // 已收到对端的关闭帧（我们是在回应）→ 立即关闭 TCP；否则等对端回应，超时后强制关闭
        if (RemoteCloseCode != null) { Abort(); return; }
        _ = Task.Delay(_options.CloseTimeout).ContinueWith(_ => Abort(), TaskScheduler.Default);
    }

    async Task WriteFrameAsync(byte opcode, ReadOnlyMemory<byte> payload)
    {
        int len = payload.Length;
        int headerLen = len < 126 ? 2 : len <= 0xFFFF ? 4 : 10;
        bool single = len <= 64 * 1024;
        byte[] frame = new byte[single ? headerLen + len : headerLen];
        frame[0] = (byte)(0x80 | opcode);
        if (len < 126) frame[1] = (byte)len;
        else if (len <= 0xFFFF)
        {
            frame[1] = 126;
            frame[2] = (byte)(len >> 8);
            frame[3] = (byte)len;
        }
        else
        {
            frame[1] = 127;
            ulong l = (ulong)len;
            for (int i = 0; i < 8; i++) frame[2 + i] = (byte)(l >> (56 - 8 * i));
        }
        if (single)
        {
            payload.Span.CopyTo(new Span<byte>(frame, headerLen, len));
            await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
            return;
        }
        await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> seg))
            await _stream.WriteAsync(seg.Array!, seg.Offset, seg.Count).ConfigureAwait(false);
        else
        {
            var copy = payload.ToArray();
            await _stream.WriteAsync(copy, 0, copy.Length).ConfigureAwait(false);
        }
    }

    /// <summary>服务端发起关闭：发送关闭帧，等待对端回应（或超时）后断开。</summary>
    public async Task CloseAsync(int code, string reason)
    {
        await EnqueueAsync(OpClose, ClosePayload(code, reason)).ConfigureAwait(false);
        await Task.WhenAny(_finished.Task, Task.Delay(_options.CloseTimeout)).ConfigureAwait(false);
        Abort();
    }

    /// <summary>立即断开 TCP。</summary>
    public void Abort()
    {
        SendItem[] dropped;
        lock (_sendGate)
        {
            if (_aborted) return;
            _aborted = true;
            dropped = _queue.ToArray();
            _queue.Clear();
            foreach (var d in dropped) _queuedBytes -= d.Cost;
            _queuedPong = _queuedPing = null;
        }
        foreach (var d in dropped) d.Done.TrySetResult(false);
        try { _client.Close(); }
        catch (Exception) { }
    }
}
