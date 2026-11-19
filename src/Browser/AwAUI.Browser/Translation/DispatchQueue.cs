using System;
using System.Collections.Generic;

namespace AwAUI.Browser;

/// <summary>
/// 主线程调度器的队列部分（不依赖 JS，可在普通 .NET 上测试）。投递的操作排队，由一次「drain」宏任务批量执行；
/// 需要安排 drain 时调用构造时给定的回调（浏览器上是经 MessageChannel 投递一个宏任务）。
/// <para>
/// 每次 drain 只执行开始时已在队列中的操作：执行中再投递的留给下一个宏任务，避免饿死渲染与输入。
/// 某个操作抛出异常时异常照常向外传播，但剩余的操作会安排到下一次 drain，不会滞留到下一次 Post 才执行。
/// </para>
/// </summary>
internal sealed class DispatchQueue
{
    readonly Queue<Action> _queue = new();
    readonly Action _requestDrain;
    bool _scheduled;

    /// <param name="requestDrain">安排一次 <see cref="Drain"/>；在锁外调用，可能来自任意线程。</param>
    public DispatchQueue(Action requestDrain) => _requestDrain = requestDrain ?? throw new ArgumentNullException(nameof(requestDrain));

    /// <summary>排队中的操作数。</summary>
    public int Count
    {
        get { lock (_queue) return _queue.Count; }
    }

    /// <summary>已安排 drain、尚未开始执行。</summary>
    public bool DrainScheduled
    {
        get { lock (_queue) return _scheduled; }
    }

    public void Enqueue(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        bool schedule;
        lock (_queue)
        {
            _queue.Enqueue(action);
            schedule = !_scheduled;
            _scheduled = true;
        }
        if (schedule) _requestDrain();
    }

    public void Drain()
    {
        int count;
        lock (_queue)
        {
            count = _queue.Count;
            _scheduled = false;
        }
        try
        {
            for (int i = 0; i < count; i++)
            {
                Action a;
                lock (_queue)
                {
                    if (_queue.Count == 0) break;
                    a = _queue.Dequeue();
                }
                a();
            }
        }
        finally
        {
            bool more;
            lock (_queue)
            {
                more = _queue.Count > 0 && !_scheduled;
                if (more) _scheduled = true;
            }
            if (more) _requestDrain();
        }
    }
}
