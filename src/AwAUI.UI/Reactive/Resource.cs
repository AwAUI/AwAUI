using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Threading;

namespace AwAUI.UI;

/// <summary>异步资源的三种状态。</summary>
public enum ResourceStatus : byte
{
    /// <summary>还没有可用的结果（首次加载进行中，或加载被取消后尚未重新开始）。</summary>
    Loading = 0,

    /// <summary>加载成功，<see cref="ResourceSnapshot{T}.Value"/> 是结果。</summary>
    Success = 1,

    /// <summary>加载失败，<see cref="ResourceSnapshot{T}.Error"/> 是异常。</summary>
    Failure = 2,
}

/// <summary>
/// <see cref="Resource{T}"/> 的不可变快照。在 <c>Build()</c> 中读取快照（或其上的 <see cref="Status"/>、<see cref="Value"/>、<see cref="Error"/>）
/// 会登记依赖；加载结果变化时组件重建，用 switch 按 <see cref="Status"/> 渲染加载中 / 成功 / 失败三个分支：
/// <code>
/// var snapshot = _profile.Snapshot;
/// return snapshot.Status switch
/// {
///     ResourceStatus.Loading => new Text("加载中…"),
///     ResourceStatus.Failure => new Text("失败：" + snapshot.Error!.Message),
///     _ => new Text(snapshot.Value.Name),
/// };
/// </code>
/// <para>
/// <see cref="IsReloading"/> 表示在一次新的加载进行期间仍显示上一次成功或失败的结果（旧数据继续可见，可显示一个角落里的刷新指示）。
/// 首次加载期间该值为 false 且 <see cref="Status"/> 为 <see cref="ResourceStatus.Loading"/>。
/// </para>
/// </summary>
public readonly struct ResourceSnapshot<T> : IEquatable<ResourceSnapshot<T>>
{
    /// <summary>构造快照。失败状态必须给出异常。</summary>
    public ResourceSnapshot(ResourceStatus status, T value = default!, Exception? error = null, bool isReloading = false)
    {
        if (status == ResourceStatus.Failure && error is null)
            throw new ArgumentNullException(nameof(error), "失败状态必须提供异常。");
        Status = status;
        Value = value;
        Error = error;
        IsReloading = isReloading;
    }

    /// <summary>当前状态。</summary>
    public ResourceStatus Status { get; }

    /// <summary>
    /// 成功时的结果；其他状态为 <c>default</c>（重新加载失败时不会保留旧值）。用 <see cref="HasValue"/> 判断是否可用。
    /// </summary>
    public T Value { get; }

    /// <summary>失败时的异常；其他状态为 null。</summary>
    public Exception? Error { get; }

    /// <summary>新的加载是否正在进行、并仍显示上一次的结果（成功值或错误）。</summary>
    public bool IsReloading { get; }

    /// <summary>是否还没有任何结果（首次加载中）。</summary>
    public bool IsLoading => Status == ResourceStatus.Loading;

    /// <summary>是否已成功加载。</summary>
    public bool IsSuccess => Status == ResourceStatus.Success;

    /// <summary>是否加载失败。</summary>
    public bool IsFailure => Status == ResourceStatus.Failure;

    /// <summary>是否有可用的结果（成功状态，重新加载期间依然为 true）。</summary>
    public bool HasValue => Status == ResourceStatus.Success;

    /// <summary>是否有加载正在进行（首次加载或重新加载）。新建但尚未调用 <see cref="Resource{T}.Load"/> 的资源状态也是 Loading。</summary>
    public bool IsBusy => IsLoading || IsReloading;

    internal ResourceSnapshot<T> WithReloading(bool isReloading) => new(Status, Value, Error, isReloading);

    public bool Equals(ResourceSnapshot<T> other) =>
        Status == other.Status
        && IsReloading == other.IsReloading
        && EqualityComparer<T>.Default.Equals(Value, other.Value)
        && ReferenceEquals(Error, other.Error);

    public override bool Equals(object? obj) => obj is ResourceSnapshot<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = (int)Status;
        hash = hash * 31 + (IsReloading ? 1 : 0);
        hash = hash * 31 + (Error is null ? 0 : Error.GetHashCode());
        hash = hash * 31 + (Value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(Value));
        return hash;
    }

    public static bool operator ==(ResourceSnapshot<T> left, ResourceSnapshot<T> right) => left.Equals(right);

    public static bool operator !=(ResourceSnapshot<T> left, ResourceSnapshot<T> right) => !left.Equals(right);

    public override string ToString() =>
        Status == ResourceStatus.Failure ? "Failure(" + Error?.Message + ")" : Status + "(" + (Value is null ? "" : Value.ToString()) + ")";
}

/// <summary>
/// 异步资源：一个可读的响应式快照加上「开始 / 重新加载 / 取消」控制，对应 design/05「状态与数据」。
/// 用 <see cref="Load(Func{CancellationToken, Task{T}})"/> 开始加载，在 <c>Build()</c> 中读取 <see cref="Snapshot"/>（或
/// <see cref="Status"/>、<see cref="Value"/>、<see cref="Error"/>、<see cref="IsLoading"/>）即可在结果变化时重建。
/// <list type="bullet">
/// <item><b>旧的加载会被丢弃</b>：后一次 <see cref="Load"/> 让前一次的结果（即使加载器不理会取消令牌）不再生效。
/// 取消不会把快照置为 <see cref="ResourceStatus.Failure"/>。</item>
/// <item><b>重新加载保留旧结果</b>：已有成功或失败结果时，新的加载进行期间快照仍是上一次的成功值或错误，且
/// <see cref="ResourceSnapshot{T}.IsReloading"/> 为 true；加载完成后被新结果替换。</item>
/// <item><b>线程</b>：<see cref="Load"/>、<see cref="Reload"/>、<see cref="Cancel"/>、<see cref="Dispose"/> 应在 UI 线程调用
/// （与 <see cref="State{T}"/> 相同）；加载器的完成可以发生在任意线程，有调度器（构造时注入，否则
/// <see cref="Reactive.CurrentThreadDispatcher"/> / <see cref="Reactive.DefaultDispatcher"/>）时结果封送回 UI 线程，
/// 没有调度器（无头测试）时在完成的线程上直接写入。</item>
/// <item><b>不要在 <c>Build()</c> 中调用 <see cref="Load"/></b>：那会在 Build 中写状态（AW1005）。在 OnMount、事件处理器或副作用中开始加载。</item>
/// <item><b>释放</b>：<see cref="Dispose"/> 取消进行中的加载并让之后的结果失效；快照保持不变，之后仍可读取。</item>
/// </list>
/// </summary>
public sealed class Resource<T> : IDisposable
{
    readonly State<ResourceSnapshot<T>> _state;
    readonly IDispatcher? _dispatcher;
    readonly object _gate = new object();
    Func<CancellationToken, Task<T>>? _loader;
    CancellationTokenSource? _cts;
    int _generation;
    bool _disposed;

    /// <summary>使用当前线程的调度器（<see cref="Reactive.CurrentThreadDispatcher"/>，否则 <see cref="Reactive.DefaultDispatcher"/>）封送完成。</summary>
    public Resource() : this(null) { }

    /// <summary>
    /// 用给定的调度器封送加载完成；为 null 时按 <see cref="Reactive.CurrentThreadDispatcher"/>、<see cref="Reactive.DefaultDispatcher"/> 的顺序取。
    /// 都没有时在完成的线程上直接写入（无头场景）。
    /// </summary>
    public Resource(IDispatcher? dispatcher)
    {
        _dispatcher = dispatcher ?? Reactive.CurrentThreadDispatcher ?? Reactive.DefaultDispatcher;
        _state = new State<ResourceSnapshot<T>>(new ResourceSnapshot<T>(ResourceStatus.Loading));
    }

    /// <summary>快照的响应式来源。读取 <see cref="State{T}.Value"/> 或在组件里读取本资源会登记依赖。</summary>
    public State<ResourceSnapshot<T>> State => _state;

    /// <summary>当前快照（读取会登记依赖）。</summary>
    public ResourceSnapshot<T> Snapshot => _state.Value;

    /// <summary>当前状态（读取会登记依赖）。</summary>
    public ResourceStatus Status => _state.Value.Status;

    /// <summary>成功时的结果，否则为 <c>default</c>（读取会登记依赖）。</summary>
    public T Value => _state.Value.Value;

    /// <summary>失败时的异常，否则为 null（读取会登记依赖）。</summary>
    public Exception? Error => _state.Value.Error;

    /// <summary>是否还没有任何结果、首次加载进行中（读取会登记依赖）。</summary>
    public bool IsLoading => _state.Value.IsLoading;

    /// <summary>是否已成功加载（读取会登记依赖）。</summary>
    public bool IsSuccess => _state.Value.IsSuccess;

    /// <summary>是否加载失败（读取会登记依赖）。</summary>
    public bool IsFailure => _state.Value.IsFailure;

    /// <summary>是否有新的加载正在进行并显示上一次的结果（读取会登记依赖）。</summary>
    public bool IsReloading => _state.Value.IsReloading;

    /// <summary>是否有加载正在进行（首次加载或重新加载；读取会登记依赖）。</summary>
    public bool IsBusy => _state.Value.IsBusy;

    /// <summary>是否有可用的结果（读取会登记依赖）。</summary>
    public bool HasValue => _state.Value.HasValue;

    /// <summary>最近一次传给 <see cref="Load(Func{CancellationToken, Task{T}})"/> 的加载器；还没有时返回 null。</summary>
    public Func<CancellationToken, Task<T>>? Loader
    {
        get { lock (_gate) return _loader; }
    }

    /// <summary>是否已释放。</summary>
    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>
    /// 开始（或重新开始）加载。上一次加载被取消，其结果即使晚到也会被丢弃。已有结果时保留旧结果并置
    /// <see cref="IsReloading"/>，首次加载时保持 <see cref="ResourceStatus.Loading"/>。
    /// <para>
    /// 返回的任务在本次加载落定后完成：加载失败被写入快照而不会让任务出错；被取消或被新的加载取代时任务正常完成。
    /// 加载器不理会取消令牌时任务可能一直不完成（此时不再需要它）。
    /// </para>
    /// </summary>
    public Task Load(Func<CancellationToken, Task<T>> loader)
    {
        if (loader is null) throw new ArgumentNullException(nameof(loader));

        CancellationTokenSource? previous;
        CancellationTokenSource cts;
        int generation;
        ResourceSnapshot<T> next;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            previous = _cts;
            _loader = loader;
            generation = ++_generation;
            cts = new CancellationTokenSource();
            _cts = cts;
            var current = _state.Peek();
            next = current.IsLoading ? current : current.WithReloading(true);
        }

        previous?.Cancel();
        _state.Value = next;
        return RunAsync(generation, cts, loader);
    }

    /// <summary>用最近一次的加载器重新加载；还没有加载过时什么也不做。语义与 <see cref="Load(Func{CancellationToken, Task{T}})"/> 相同。</summary>
    public Task Reload()
    {
        Func<CancellationToken, Task<T>>? loader;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            loader = _loader;
        }
        return loader is null ? Task.CompletedTask : Load(loader);
    }

    /// <summary><see cref="Reload"/> 的别名。</summary>
    public Task Refresh() => Reload();

    /// <summary>
    /// 取消进行中的加载（若有）：结果不会再被写入，快照保持原状（取消不会被当作失败），并清除
    /// <see cref="IsReloading"/>。之后可以 <see cref="Reload"/> 或 <see cref="Load"/>。
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            cts = _cts;
            _cts = null;
        }
        cts?.Cancel();
        _state.Update(snapshot => snapshot.IsReloading ? snapshot.WithReloading(false) : snapshot);
    }

    /// <summary>取消进行中的加载并停止接收后续结果。幂等；快照保持不变。</summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            cts = _cts;
            _cts = null;
        }
        cts?.Cancel();
    }

    async Task RunAsync(int generation, CancellationTokenSource cts, Func<CancellationToken, Task<T>> loader)
    {
        try
        {
            T value;
            try
            {
                var task = loader(cts.Token)
                    ?? throw new InvalidOperationException("Resource 的加载器返回了 null。");
                value = await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消（无论是否由本次的令牌触发）不是失败：不写快照。
                return;
            }
            catch (Exception ex)
            {
                Apply(generation, default!, ex);
                return;
            }
            Apply(generation, value, null);
        }
        finally
        {
            lock (_gate)
            {
                // 只有仍是最新一次加载时才释放自己的令牌源：被取代或取消的加载可能仍有代码在使用令牌，
                // 释放它会让加载器注册取消回调时抛 ObjectDisposedException，交给 GC 更安全。
                if (generation == _generation)
                {
                    _cts = null;
                    cts.Dispose();
                }
            }
        }
    }

    void Apply(int generation, T value, Exception? error)
    {
        // 有调度器时把完成封送回 UI 线程；没有调度器（无头）时就在完成的线程上写入（State 自身不会再封送）。
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Post(() => Apply(generation, value, error));
            return;
        }
        lock (_gate)
        {
            if (_disposed || generation != _generation) return; // 已被更新的加载取代（或已释放）
            _cts = null;
        }
        _state.Value = error is null
            ? new ResourceSnapshot<T>(ResourceStatus.Success, value)
            : new ResourceSnapshot<T>(ResourceStatus.Failure, default!, error);
    }
}
