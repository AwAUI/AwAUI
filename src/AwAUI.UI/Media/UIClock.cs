using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AwAUI.Threading;

namespace AwAUI.UI;

/// <summary>
/// UI 线程上的时间源与定时器（动画图片按帧时长推进时使用）。回调总在 UI 线程上执行；
/// 定时器只在需要时安排一次，不做忙轮询：没有可见的动画时不产生任何帧请求。
/// <para>
/// 默认实现（<see cref="CreateSystem"/>）用单调时钟与线程池定时器，到期后经 <see cref="IDispatcher"/> 投递回 UI 线程；
/// 测试与无窗口渲染使用 <see cref="ManualUIClock"/> 手动推进时间。
/// </para>
/// </summary>
public abstract class UIClock
{
    /// <summary>单调时间（起点任意）。</summary>
    public abstract TimeSpan Now { get; }

    /// <summary>在 <paramref name="delay"/> 之后于 UI 线程调用 <paramref name="callback"/> 一次。释放返回值可取消。</summary>
    public abstract IDisposable Schedule(TimeSpan delay, Action callback);

    /// <summary>基于单调时钟与线程池定时器的实现；回调经 <paramref name="dispatcher"/> 投递回 UI 线程。</summary>
    public static UIClock CreateSystem(IDispatcher dispatcher) => new SystemUIClock(dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));

    sealed class SystemUIClock : UIClock
    {
        readonly IDispatcher _dispatcher;
        readonly Stopwatch _watch = Stopwatch.StartNew();

        public SystemUIClock(IDispatcher dispatcher) => _dispatcher = dispatcher;

        public override TimeSpan Now => _watch.Elapsed;

        public override IDisposable Schedule(TimeSpan delay, Action callback)
        {
            if (callback is null) throw new ArgumentNullException(nameof(callback));
            return new Pending(this, delay, callback);
        }

        sealed class Pending : IDisposable
        {
            readonly SystemUIClock _clock;
            readonly Action _callback;
            Timer? _timer;
            int _state; // 0 等待，1 已触发，2 已取消

            public Pending(SystemUIClock clock, TimeSpan delay, Action callback)
            {
                _clock = clock;
                _callback = callback;
                long ms = (long)Math.Ceiling(Math.Max(0, Math.Min(delay.TotalMilliseconds, int.MaxValue - 1)));
                _timer = new Timer(Fire, null, ms, Timeout.Infinite);
            }

            void Fire(object? _)
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
                Interlocked.Exchange(ref _timer, null)?.Dispose();
                _clock._dispatcher.Post(_callback);
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
                Interlocked.Exchange(ref _timer, null)?.Dispose();
            }
        }
    }
}

/// <summary>
/// 手动推进的时钟：<see cref="Advance"/> 按到期顺序同步执行到期的回调。用于测试与确定性的无窗口渲染；只能在 UI 线程上使用。
/// </summary>
public sealed class ManualUIClock : UIClock
{
    /// <summary>一次 <see cref="Advance"/> 最多执行的回调数（防御回调里不断安排零延迟的新回调）。</summary>
    public const int MaxCallbacksPerAdvance = 100_000;

    readonly List<Entry> _pending = new();
    TimeSpan _now;
    long _seq;

    public override TimeSpan Now => _now;

    /// <summary>尚未到期（或已到期但还没推进执行）的回调数。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>最早的待执行回调的到期时间；没有时为 null。</summary>
    public TimeSpan? NextDue
    {
        get
        {
            TimeSpan? best = null;
            foreach (var e in _pending)
                if (best is null || e.Due < best) best = e.Due;
            return best;
        }
    }

    public override IDisposable Schedule(TimeSpan delay, Action callback)
    {
        if (callback is null) throw new ArgumentNullException(nameof(callback));
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var e = new Entry(this, _now + delay, ++_seq, callback);
        _pending.Add(e);
        return e;
    }

    /// <summary>时间前进 <paramref name="delta"/>，期间到期的回调按到期时间（同时到期按安排顺序）执行；回调执行时 <see cref="Now"/> 是它的到期时间。</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
        var target = _now + delta;
        for (int n = 0; n < MaxCallbacksPerAdvance; n++)
        {
            Entry? next = null;
            foreach (var e in _pending)
                if (e.Due <= target && (next is null || e.Due < next.Due || (e.Due == next.Due && e.Seq < next.Seq))) next = e;
            if (next is null) break;
            _pending.Remove(next);
            if (next.Due > _now) _now = next.Due;
            next.Callback();
        }
        _now = target;
    }

    sealed class Entry : IDisposable
    {
        readonly ManualUIClock _owner;

        public Entry(ManualUIClock owner, TimeSpan due, long seq, Action callback)
        {
            _owner = owner;
            Due = due;
            Seq = seq;
            Callback = callback;
        }

        public TimeSpan Due { get; }
        public long Seq { get; }
        public Action Callback { get; }

        public void Dispose() => _owner._pending.Remove(this);
    }
}
