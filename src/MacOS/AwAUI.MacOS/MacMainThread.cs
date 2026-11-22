using System;
using System.Threading.Tasks;

namespace AwAUI.MacOS;

/// <summary>在 AppKit 主线程上执行同步操作并返回已完成（或异步投递）的任务。</summary>
internal static class MacMainThread
{
    public static Task<T> Invoke<T>(MacPlatform platform, Func<T> action)
    {
        if (platform.Dispatcher.CheckAccess())
        {
            try { return Task.FromResult(action()); }
            catch (Exception e) when (e is not OutOfMemoryException) { return Task.FromException<T>(e); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        platform.Dispatcher.Post(() =>
        {
            try { tcs.TrySetResult(action()); }
            catch (Exception e) when (e is not OutOfMemoryException) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }
}
