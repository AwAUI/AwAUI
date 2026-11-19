using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using AwAUI.Threading;

namespace AwAUI.UI;

/// <summary>
/// 可变状态。在 <c>Build()</c>、<see cref="Computed{T}"/>、副作用中读取 <see cref="Value"/> 会自动登记依赖；
/// 写入时值相等（<see cref="EqualityComparer{T}.Default"/> 或构造时给定的比较器）则忽略，否则只让真正读过它的观察者重算。
/// <para>
/// 线程：State 记住创建时所在线程的调度器（<see cref="Reactive.CurrentThreadDispatcher"/>，否则 <see cref="Reactive.DefaultDispatcher"/>；
/// 两者在创建时都为空则在写入时取当时的 <see cref="Reactive.DefaultDispatcher"/>）；
/// 非 UI 线程的写入被封送（Post）到该调度器，读取只应在 UI 线程进行。
/// </para>
/// <para>集合类状态应整体替换（<c>Update(l =&gt; l.Append(x).ToList())</c>），原地修改不会触发更新（分析器 AW1003）。</para>
/// </summary>
public sealed class State<T> : ReactiveCell
{
    T _value;
    Func<T>? _lazy;
    readonly IEqualityComparer<T> _comparer;
    readonly IDispatcher? _dispatcher;

    public State(T initial) : this(initial, null) { }

    public State(T initial, IEqualityComparer<T>? comparer)
    {
        _value = initial;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _dispatcher = ReactiveRuntime.Current.Dispatcher ?? Reactive.DefaultDispatcher;
    }

    /// <summary>惰性初值：第一次读取或写入时才调用 <paramref name="lazyInitial"/>（重的初始化不会在 Props 载体上执行）。</summary>
    public State(Func<T> lazyInitial) : this(lazyInitial, null) { }

    public State(Func<T> lazyInitial, IEqualityComparer<T>? comparer)
    {
        _lazy = lazyInitial ?? throw new ArgumentNullException(nameof(lazyInitial));
        _value = default!;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _dispatcher = ReactiveRuntime.Current.Dispatcher ?? Reactive.DefaultDispatcher;
    }

    /// <summary>当前值。读：登记依赖；写：值相等则忽略。</summary>
    public T Value
    {
        get
        {
            EnsureInitialized();
            ReactiveRuntime.Current.Track(this);
            return _value;
        }
        set
        {
            // 创建时还没有调度器（例如宿主设置 DefaultDispatcher 之前创建的全局 Store）时，写入时再取进程级默认值
            var d = _dispatcher ?? Reactive.DefaultDispatcher;
            if (d is not null && !d.CheckAccess())
            {
                var v = value;
                d.Post(() => SetCore(v));
                return;
            }
            SetCore(value);
        }
    }

    /// <summary>读取但不登记依赖。</summary>
    public T Peek()
    {
        EnsureInitialized();
        return _value;
    }

    /// <summary>基于当前值计算新值并写入（非 UI 线程调用时整个更新被封送到 UI 线程执行）。</summary>
    public void Update(Func<T, T> updater)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));
        var d = _dispatcher ?? Reactive.DefaultDispatcher;
        if (d is not null && !d.CheckAccess())
        {
            d.Post(() => SetCore(updater(Peek())));
            return;
        }
        SetCore(updater(Peek()));
    }

    void EnsureInitialized()
    {
        var lazy = _lazy;
        if (lazy is null) return;
        _lazy = null;
        _value = lazy();
    }

    void SetCore(T value)
    {
        _lazy = null;
        if (_comparer.Equals(_value, value)) return;
        var rt = ReactiveRuntime.Current;
        if (rt.ComputingDepth > 0)
            throw new InvalidOperationException("不能在 Computed 的计算函数中写入 State：Computed 必须是无副作用的纯计算。");
        if (rt.CurrentObserver is ComponentObserver co) co.Component.ReportStateWriteDuringBuild();
        _value = value;
        _version++;
        rt.Propagate(this);
        if (rt.BatchDepth == 0) rt.FlushEffects();
    }

    public override string ToString() => _lazy is null ? _value?.ToString() ?? "" : "(未初始化)";
}

/// <summary>
/// 派生值：惰性求值并缓存，依赖自动追踪；只有依赖确实变化时才重算，结果相等则下游不重算。
/// 计算函数必须是纯的（其中写 State 会抛出异常）；抛出的异常被缓存并在每次读取时重新抛出，直到依赖变化。
/// </summary>
public sealed class Computed<T> : ReactiveCell, IComputedCell
{
    readonly Func<T> _compute;
    readonly IEqualityComparer<T> _comparer;
    T _value = default!;
    Exception? _error;
    bool _initialized;
    bool _computing;
    long _globalSeen = -1;

    public Computed(Func<T> compute) : this(compute, null) { }

    public Computed(Func<T> compute, IEqualityComparer<T>? comparer)
    {
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public T Value
    {
        get
        {
            Refresh();
            ReactiveRuntime.Current.Track(this);
            if (_error is not null) ExceptionDispatchInfo.Capture(_error).Throw();
            return _value;
        }
    }

    /// <summary>读取但不登记依赖。</summary>
    public T Peek()
    {
        Refresh();
        if (_error is not null) ExceptionDispatchInfo.Capture(_error).Throw();
        return _value;
    }

    internal override bool IsLinked => _subs is not null;

    internal override void Refresh()
    {
        if (_computing) throw new InvalidOperationException("Computed 之间存在循环依赖。");
        var rt = ReactiveRuntime.Current;
        if (_initialized)
        {
            bool observed = _subs is not null;
            if (observed && _status == CellStatus.Clean) return;
            if (!observed && _globalSeen == rt.GlobalVersion) return;
            if (!(observed && _status == CellStatus.Dirty))
            {
                _computing = true;
                bool changed;
                try { changed = ReactiveRuntime.DependenciesChanged(this); }
                finally { _computing = false; }
                if (!changed)
                {
                    _status = CellStatus.Clean;
                    _globalSeen = rt.GlobalVersion;
                    return;
                }
            }
        }
        Recompute(rt);
    }

    void Recompute(ReactiveRuntime rt)
    {
        _computing = true;
        rt.ComputingDepth++;
        var frame = rt.PushFrame(this);
        T value = default!;
        Exception? error = null;
        bool stale;
        try
        {
            try { value = _compute(); }
            catch (Exception ex) { error = ex; }
            finally
            {
                rt.ComputingDepth--;
                _computing = false;
            }
            stale = rt.FinishTracking(this, frame);
        }
        finally
        {
            rt.PopFrame();
        }

        bool changed;
        if (error is not null)
        {
            changed = !ReferenceEquals(error, _error);
            _error = error;
        }
        else
        {
            changed = !_initialized || _error is not null || !_comparer.Equals(_value, value);
            if (changed) _value = value;
            _error = null;
        }
        _initialized = true;
        if (changed) _version++;
        _status = stale ? CellStatus.Dirty : CellStatus.Clean;
        _globalSeen = rt.GlobalVersion;
    }

    void IComputedCell.OnBecameObserved()
    {
        var deps = _deps;
        if (deps is not null)
            foreach (var e in deps) ReactiveRuntime.Link(e);
        // 未被观察期间收不到通知：若期间有过写入，先标为 Check，下次读取时校验
        if (_initialized && _globalSeen != ReactiveRuntime.Current.GlobalVersion && _status == CellStatus.Clean)
            _status = CellStatus.Check;
    }

    void IComputedCell.OnBecameUnobserved()
    {
        var deps = _deps;
        if (deps is null) return;
        foreach (var e in deps)
            if (e.Linked) ReactiveRuntime.Unlink(e);
    }

    public override string ToString() => _initialized ? _value?.ToString() ?? "" : "(未计算)";
}

/// <summary>组件外的副作用（<see cref="Reactive.Effect(Action)"/>）。创建时立即执行一次，之后依赖变化时在写入（或批处理）结束后重新执行。</summary>
internal sealed class EffectCell : ReactiveCell, IDisposable
{
    readonly Action? _action;
    readonly Func<Action?>? _withCleanup;
    Action? _cleanup;
    bool _disposed;

    public EffectCell(Action? action, Func<Action?>? withCleanup)
    {
        _action = action;
        _withCleanup = withCleanup;
    }

    internal override bool IsLinked => !_disposed;

    internal override void Schedule() => ReactiveRuntime.Current.EnqueueEffect(this);

    public void RunIfNeeded()
    {
        if (_disposed) return;
        if (!ReactiveRuntime.ShouldRun(this)) return;
        Run();
    }

    public void Run()
    {
        var rt = ReactiveRuntime.Current;
        _status = CellStatus.Clean;
        RunCleanup(rt);
        var frame = rt.PushFrame(this);
        bool stale;
        try
        {
            if (_withCleanup is not null) _cleanup = _withCleanup();
            else _action!();
        }
        finally
        {
            stale = rt.FinishTracking(this, frame);
            rt.PopFrame();
        }
        if (stale && _status == CellStatus.Clean)
        {
            _status = CellStatus.Dirty;
            Schedule();
        }
    }

    void RunCleanup(ReactiveRuntime rt)
    {
        var c = _cleanup;
        if (c is null) return;
        _cleanup = null;
        rt.PushFrame(null);
        try { c(); }
        finally { rt.PopFrame(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReactiveRuntime.UnlinkAll(this);
        RunCleanup(ReactiveRuntime.Current);
    }
}

/// <summary>响应式工具。</summary>
public static class Reactive
{
    static volatile IDispatcher? s_defaultDispatcher;

    /// <summary>
    /// 进程级的默认 UI 调度器：在没有登记线程调度器的线程上创建的 State（例如静态的全局 Store）使用它封送写入。
    /// 由应用宿主设置。
    /// </summary>
    public static IDispatcher? DefaultDispatcher
    {
        get => s_defaultDispatcher;
        set => s_defaultDispatcher = value;
    }

    /// <summary>当前线程登记的 UI 调度器（<see cref="UIRoot"/> 构造时会登记）。此后在本线程创建的 State 用它封送跨线程写入。</summary>
    public static IDispatcher? CurrentThreadDispatcher
    {
        get => ReactiveRuntime.Current.Dispatcher;
        set => ReactiveRuntime.Current.Dispatcher = value;
    }

    /// <summary>合并多次写入：期间排期的副作用在最外层批处理结束后统一执行一次（组件本来就在帧的 Build 阶段才重建）。</summary>
    public static void Batch(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        var rt = ReactiveRuntime.Current;
        rt.BatchDepth++;
        try
        {
            action();
        }
        finally
        {
            rt.BatchDepth--;
        }
        if (rt.BatchDepth == 0) rt.FlushEffects();
    }

    /// <summary>组件外的副作用：立即执行一次并追踪依赖，依赖变化后重新执行。释放返回值以停止。</summary>
    public static IDisposable Effect(Action effect)
    {
        if (effect is null) throw new ArgumentNullException(nameof(effect));
        var e = new EffectCell(effect, null);
        RunNew(e);
        return e;
    }

    /// <summary>带清理的副作用：返回的清理函数在下次重新执行前与释放时调用。</summary>
    public static IDisposable Effect(Func<Action?> effect)
    {
        if (effect is null) throw new ArgumentNullException(nameof(effect));
        var e = new EffectCell(null, effect);
        RunNew(e);
        return e;
    }

    static void RunNew(EffectCell e)
    {
        e.Run();
        // 首次执行中写入了自己读取的 State 时，它已被重新排期
        var rt = ReactiveRuntime.Current;
        if (rt.BatchDepth == 0) rt.FlushEffects();
    }

    /// <summary>在不登记依赖的情况下读取。</summary>
    public static T Untrack<T>(Func<T> read)
    {
        if (read is null) throw new ArgumentNullException(nameof(read));
        var rt = ReactiveRuntime.Current;
        rt.PushFrame(null);
        try { return read(); }
        finally { rt.PopFrame(); }
    }
}
