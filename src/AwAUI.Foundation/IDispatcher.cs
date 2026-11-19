using System;

namespace AwAUI.Threading;

/// <summary>线程亲和的消息调度器。元素树只能在其所属调度器的线程上访问。</summary>
public interface IDispatcher
{
    /// <summary>当前线程是否为调度器线程。</summary>
    bool CheckAccess();

    /// <summary>把操作投递到调度器线程，按投递顺序执行。可从任意线程调用。</summary>
    void Post(Action action);
}

public static class DispatcherExtensions
{
    public static void VerifyAccess(this IDispatcher dispatcher)
    {
        if (!dispatcher.CheckAccess())
            throw new InvalidOperationException("The calling thread cannot access this object because it is owned by a different thread.");
    }
}
