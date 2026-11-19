using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwAUI.Diagnostics.Transport;

namespace AwAUI.Diagnostics.UnitTests;

/// <summary>
/// 故意让线程池饥饿的测试：与其他测试并行会拖慢它们，因此放在不并行的集合里单独运行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolStarvationCollection
{
    public const string Name = "ThreadPoolStarvation";
}

[Collection(ThreadPoolStarvationCollection.Name)]
public sealed class KeepAliveStarvationTests
{
    /// <summary>
    /// 线程池饥饿期间保活定时器晚醒、根本没有发 Ping：醒来后不得因为「很久没收到字节」就断开一个一直有求必应的对端。
    /// 对端在独立线程上用同步 I/O 回 Pong，不受线程池饥饿影响。
    /// </summary>
    [Fact]
    public async Task Keepalive_does_not_drop_a_responsive_peer_after_thread_pool_starvation()
    {
        var server = new WebSocketServerTransport(new WebSocketServerOptions
        {
            KeepAliveInterval = TimeSpan.FromMilliseconds(50),
            KeepAliveTimeout = TimeSpan.FromMilliseconds(200),
        });
        int disconnects = 0;
        server.Disconnected += () => Interlocked.Increment(ref disconnects);
        server.Start();
        using var peer = new SyncPongPeer(server.LocalEndPoint!.Port);
        try
        {
            peer.Start();
            WaitUntil(() => Volatile.Read(ref peer.Pongs) >= 3, "the server never sent keepalive pings");

            // 占满线程池 1 秒（远超 间隔 + 超时）。多排一些阻塞项：饥饿期间线程池注入的新线程也会被它们占住
            var starve = TimeSpan.FromSeconds(1);
            var clock = Stopwatch.StartNew();
            int blockers = Math.Max(ThreadPool.ThreadCount, Environment.ProcessorCount) * 2 + 16;
            using var release = new ManualResetEventSlim(false);
            for (int i = 0; i < blockers; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    var left = starve - clock.Elapsed;
                    if (left > TimeSpan.Zero) release.Wait(left);
                }, null);
            long probeRanAt = -1;
            ThreadPool.UnsafeQueueUserWorkItem(_ => Interlocked.Exchange(ref probeRanAt, clock.ElapsedMilliseconds), null);
            Thread.Sleep(starve);
            release.Set();

            WaitUntil(() => Interlocked.Read(ref probeRanAt) >= 0, "the probe work item never ran");
            Assert.True(Interlocked.Read(ref probeRanAt) >= starve.TotalMilliseconds * 0.7,
                $"the thread pool was not starved (probe ran after {Interlocked.Read(ref probeRanAt)} ms): the test did not exercise the scenario");

            // 饥饿结束后保活继续工作，连接仍在
            int pongsAfter = Volatile.Read(ref peer.Pongs);
            WaitUntil(() => Volatile.Read(ref peer.Pongs) >= pongsAfter + 3 || peer.Closed, "keepalive stopped after the starvation");
            Assert.False(peer.Closed, "the server dropped a peer that answered every ping");
            Assert.Equal(0, Volatile.Read(ref disconnects));
            Assert.True(server.IsConnected);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(message);
            Thread.Sleep(10);
        }
    }

    /// <summary>在专用线程上以同步 I/O 运行的 WebSocket 客户端：收到 Ping 立即回 Pong，其余帧忽略。</summary>
    sealed class SyncPongPeer : IDisposable
    {
        readonly TcpClient _tcp = new();
        readonly int _port;
        Thread? _thread;
        public int Pongs;
        volatile bool _closed;

        public SyncPongPeer(int port) => _port = port;

        public bool Closed => _closed;

        public void Start()
        {
            _tcp.NoDelay = true;
            _tcp.ReceiveTimeout = 30_000;
            _tcp.Connect(IPAddress.Loopback, _port);
            var stream = _tcp.GetStream();
            var req = Encoding.ASCII.GetBytes(
                "GET /devtools HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
            stream.Write(req, 0, req.Length);
            var head = new StringBuilder();
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int b = stream.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                head.Append((char)b);
            }
            Assert.StartsWith("HTTP/1.1 101", head.ToString());
            _thread = new Thread(() => Run(stream)) { IsBackground = true, Name = "SyncPongPeer" };
            _thread.Start();
        }

        void Run(NetworkStream stream)
        {
            try
            {
                var h = new byte[2];
                while (true)
                {
                    ReadExact(stream, h);
                    int opcode = h[0] & 0x0F, len = h[1] & 0x7F;
                    if (len >= 126) throw new InvalidDataException("unexpected long frame");    // 只会收到 Ping
                    var payload = new byte[len];
                    ReadExact(stream, payload);
                    if (opcode == 0x8) break;
                    if (opcode != 0x9) continue;
                    var pong = new byte[6 + len];    // 掩码键全 0：负载原样
                    pong[0] = 0x8A;
                    pong[1] = (byte)(0x80 | len);
                    Buffer.BlockCopy(payload, 0, pong, 6, len);
                    stream.Write(pong, 0, pong.Length);
                    Interlocked.Increment(ref Pongs);
                }
            }
            catch (Exception) { }
            _closed = true;
        }

        static void ReadExact(Stream s, byte[] buf)
        {
            int done = 0;
            while (done < buf.Length)
            {
                int n = s.Read(buf, done, buf.Length - done);
                if (n <= 0) throw new EndOfStreamException();
                done += n;
            }
        }

        public void Dispose()
        {
            _tcp.Dispose();
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
    }
}
