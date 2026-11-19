using System;
using System.Collections.Generic;
using AwAUI.Threading;

namespace AwAUI.UI;

/// <summary>
/// 响应式依赖图中的节点（<see cref="State{T}"/>、<see cref="Computed{T}"/>、副作用、组件的依赖观察者）。不可在外部派生。
/// <para>
/// 算法：推-拉结合。写入 State 时版本号递增，并沿订阅边把直接观察者标为 Dirty、更远的标为 Check，
/// 副作用与组件只被「排期」而不立即执行；执行前对 Check 状态逐个拉取上游 Computed 并比较版本号，
/// 只有确实变化才重算。因此不会出现中间态（glitch-free），Computed 结果不变时下游也不会重算。
/// Computed 无订阅者时不挂在上游的订阅链上（避免泄漏），读取时用全局版本号快速判断是否可能过期。
/// </para>
/// </summary>
public abstract class ReactiveCell
{
    private protected ReactiveCell() { }

    // ---- 作为数据源
    internal Edge? _subs;
    internal int _version;

    // ---- 作为观察者
    internal List<Edge>? _deps;
    internal CellStatus _status;

    // 依赖收尾时的临时标记（同步、不可重入）
    internal long _mark;
    internal Edge? _markEdge;

    /// <summary>观察者当前是否挂在上游订阅链上。</summary>
    internal virtual bool IsLinked => true;

    /// <summary>作为观察者被标脏：副作用与组件在此排期。</summary>
    internal virtual void Schedule() { }

    /// <summary>作为数据源：确保值是最新的（Computed 重写）。</summary>
    internal virtual void Refresh() { }

    internal bool HasSubscribers => _subs is not null;
}

internal enum CellStatus : byte
{
    Clean = 0,
    Check = 1,
    Dirty = 2,
}

/// <summary>依赖边：source → target。target 的依赖表持有它；target 链接时它同时在 source 的订阅双链表中。</summary>
internal sealed class Edge
{
    public readonly ReactiveCell Source;
    public readonly ReactiveCell Target;
    public int Version;
    public Edge? Prev;
    public Edge? Next;
    public bool Linked;

    public Edge(ReactiveCell source, ReactiveCell target, int version)
    {
        Source = source;
        Target = target;
        Version = version;
    }
}

/// <summary>正在收集依赖的一次执行。</summary>
internal sealed class TrackingFrame
{
    public ReactiveCell? Observer;
    public readonly List<ReactiveCell> Sources = new();
    public readonly List<int> Versions = new();
}

/// <summary>每个 UI 线程一份的响应式运行时（依赖收集栈、批处理深度、待执行副作用）。</summary>
internal sealed class ReactiveRuntime
{
    [ThreadStatic] static ReactiveRuntime? t_current;

    public static ReactiveRuntime Current => t_current ??= new ReactiveRuntime();

    /// <summary>每次写入递增：无订阅者的 Computed 用它快速判断是否可能过期。</summary>
    public long GlobalVersion;

    public IDispatcher? Dispatcher;

    readonly List<TrackingFrame> _framePool = new();
    int _depth;
    public TrackingFrame? Top;

    public int BatchDepth;
    public int ComputingDepth;
    readonly List<ReactiveCell> _pendingEffects = new();
    bool _flushing;
    long _markCounter;
    readonly List<ReactiveCell> _propagateStack = new();
    readonly List<Edge> _edgeScratch = new();

    public const int MaxEffectIterations = 1000;

    public TrackingFrame PushFrame(ReactiveCell? observer)
    {
        if (_depth == _framePool.Count) _framePool.Add(new TrackingFrame());
        var f = _framePool[_depth++];
        f.Observer = observer;
        f.Sources.Clear();
        f.Versions.Clear();
        Top = f;
        return f;
    }

    public void PopFrame()
    {
        var f = _framePool[--_depth];
        f.Observer = null;
        f.Sources.Clear();
        f.Versions.Clear();
        Top = _depth > 0 ? _framePool[_depth - 1] : null;
    }

    /// <summary>当前执行中的观察者（无追踪时为 null）。</summary>
    public ReactiveCell? CurrentObserver => Top?.Observer;

    public void Track(ReactiveCell source)
    {
        var f = Top;
        if (f is null || f.Observer is null) return;
        f.Sources.Add(source);
        f.Versions.Add(source._version);
    }

    /// <summary>
    /// 一次执行结束：把收集到的依赖与旧依赖表对比，复用未变的边、链接新增的、断开不再使用的。O(依赖数)。
    /// 返回执行期间是否有依赖被改写（读后写），此时观察者应重新排期。
    /// </summary>
    public bool FinishTracking(ReactiveCell observer, TrackingFrame frame)
    {
        var old = observer._deps;
        var sources = frame.Sources;
        var versions = frame.Versions;

        // 快速路径：依赖序列与上次完全相同（最常见），原地更新版本，不分配
        if (old is not null && old.Count == sources.Count)
        {
            bool same = true;
            for (int i = 0; i < old.Count; i++)
            {
                if (!ReferenceEquals(old[i].Source, sources[i])) { same = false; break; }
            }
            if (same)
            {
                bool staleFast = false;
                for (int i = 0; i < old.Count; i++)
                {
                    var e = old[i];
                    e.Version = versions[i];
                    if (e.Source._version != e.Version) staleFast = true;
                }
                return staleFast;
            }
        }

        long oldMark = ++_markCounter;
        if (old is not null)
        {
            foreach (var e in old)
            {
                e.Source._mark = oldMark;
                e.Source._markEdge = e;
            }
        }
        long newMark = ++_markCounter;
        bool linked = observer.IsLinked;
        // 收集到暂存表，最后拷回旧表：同一 State 被读取多次（常见）时不走快速路径，也不必每次分配新表
        var next = _edgeScratch;
        next.Clear();
        bool stale = false;
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            if (s._mark == newMark) continue; // 重复读取：保留第一次读到的版本
            Edge e;
            if (s._mark == oldMark)
            {
                e = s._markEdge!;
                e.Version = versions[i];
            }
            else
            {
                e = new Edge(s, observer, versions[i]);
                if (linked) Link(e);
            }
            s._mark = newMark;
            s._markEdge = null;
            next.Add(e);
            if (s._version != e.Version) stale = true;
        }
        if (old is not null)
        {
            foreach (var e in old)
            {
                if (e.Source._mark != newMark)
                {
                    e.Source._markEdge = null;
                    if (e.Linked) Unlink(e);
                }
            }
        }
        if (old is null)
        {
            observer._deps = new List<Edge>(next);
        }
        else
        {
            old.Clear();
            old.AddRange(next);
        }
        next.Clear();
        return stale;
    }

    public static void Link(Edge e)
    {
        if (e.Linked) return;
        var s = e.Source;
        bool first = s._subs is null;
        e.Prev = null;
        e.Next = s._subs;
        if (s._subs is not null) s._subs.Prev = e;
        s._subs = e;
        e.Linked = true;
        if (first && s is IComputedCell c) c.OnBecameObserved();
    }

    public static void Unlink(Edge e)
    {
        if (!e.Linked) return;
        var s = e.Source;
        if (e.Prev is not null) e.Prev.Next = e.Next;
        else s._subs = e.Next;
        if (e.Next is not null) e.Next.Prev = e.Prev;
        e.Prev = e.Next = null;
        e.Linked = false;
        if (s._subs is null && s is IComputedCell c) c.OnBecameUnobserved();
    }

    /// <summary>断开观察者的全部依赖（副作用销毁、组件卸载）。</summary>
    public static void UnlinkAll(ReactiveCell observer)
    {
        var deps = observer._deps;
        if (deps is null) return;
        foreach (var e in deps)
            if (e.Linked) Unlink(e);
        observer._deps = null;
    }

    /// <summary>数据源变化：直接观察者标 Dirty，其后的标 Check；副作用与组件排期。迭代实现，不受链长限制。</summary>
    public void Propagate(ReactiveCell source)
    {
        GlobalVersion++;
        var stack = _propagateStack;
        int baseCount = stack.Count;
        for (var e = source._subs; e is not null; e = e.Next) Mark(e.Target, CellStatus.Dirty, stack);
        while (stack.Count > baseCount)
        {
            var c = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            for (var e = c._subs; e is not null; e = e.Next) Mark(e.Target, CellStatus.Check, stack);
        }
    }

    static void Mark(ReactiveCell target, CellStatus level, List<ReactiveCell> stack)
    {
        if (target._status >= level) return;
        bool wasClean = target._status == CellStatus.Clean;
        target._status = level;
        if (!wasClean) return;
        if (target is IComputedCell) stack.Add(target);
        else target.Schedule();
    }

    /// <summary>观察者处于 Check 时：逐个拉取上游并比较版本，判断是否真的需要重算。</summary>
    public static bool DependenciesChanged(ReactiveCell observer)
    {
        var deps = observer._deps;
        if (deps is null) return false;
        for (int i = 0; i < deps.Count; i++)
        {
            var e = deps[i];
            e.Source.Refresh();
            if (e.Source._version != e.Version) return true;
        }
        return false;
    }

    /// <summary>观察者是否需要执行（并在不需要时把状态清为 Clean）。</summary>
    public static bool ShouldRun(ReactiveCell observer)
    {
        switch (observer._status)
        {
            case CellStatus.Dirty:
                return true;
            case CellStatus.Check:
                if (DependenciesChanged(observer)) return true;
                observer._status = CellStatus.Clean;
                return false;
            default:
                return false;
        }
    }

    /// <summary>排期副作用；由写入方在传播结束后调用 <see cref="FlushEffects"/>（传播途中执行会看到中间态）。</summary>
    public void EnqueueEffect(ReactiveCell effect) => _pendingEffects.Add(effect);

    public void FlushEffects()
    {
        if (_flushing) return;
        _flushing = true;
        try
        {
            int iterations = 0;
            while (_pendingEffects.Count > 0)
            {
                if (++iterations > MaxEffectIterations)
                {
                    _pendingEffects.Clear();
                    throw new InvalidOperationException(
                        "AW1005: 副作用之间的 State 写入形成了循环（超过 " + MaxEffectIterations + " 轮仍未稳定）。检查副作用是否写入了自己读取的 State。");
                }
                var batch = _pendingEffects.ToArray();
                _pendingEffects.Clear();
                for (int i = 0; i < batch.Length; i++)
                {
                    try
                    {
                        ((EffectCell)batch[i]).RunIfNeeded();
                    }
                    catch
                    {
                        // 未执行的副作用保留到下一次刷新，否则它们的状态停在非 Clean 而永远不会再排期
                        for (int j = i + 1; j < batch.Length; j++) _pendingEffects.Add(batch[j]);
                        throw;
                    }
                }
            }
        }
        finally
        {
            _flushing = false;
        }
    }
}

/// <summary>Computed 的非泛型视图。</summary>
internal interface IComputedCell
{
    void OnBecameObserved();
    void OnBecameUnobserved();
}
