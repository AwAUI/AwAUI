using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using AwAUI.Threading;

namespace AwAUI.UI;

/// <summary>
/// 帧管线中 Build 之后的阶段（样式级联、布局、绘制、提交）的接入点。集成阶段实现它并设置到 <see cref="UIRoot.Pipeline"/>。
/// </summary>
public abstract class FramePipeline
{
    /// <summary>在每帧的 Build 阶段之后执行；应处理并清除元素上的脏标记。</summary>
    public abstract void Run(UIRoot root);

    /// <summary>
    /// 根登记了一张组件样式表（某个组件类型首次挂载，见 <see cref="Component.ScopedStyleSheet"/>）。在 Build 阶段同步调用；
    /// 管线应把它加入级联（Author 来源）。挂接到根之前登记的样式表见 <see cref="UIRoot.ComponentStyleSheets"/>。
    /// </summary>
    public virtual void OnComponentStyleSheet(UIRoot root, ComponentStyleSheet sheet) { }
}

/// <summary>组件类型带来的一张样式表（<see cref="Component.ScopedStyleSheet"/> 或 <see cref="Component.GlobalStyleSheet"/>）。</summary>
public sealed class ComponentStyleSheet
{
    internal ComponentStyleSheet(Type componentType, string text, bool isGlobal)
    {
        ComponentType = componentType;
        Text = text;
        IsGlobal = isGlobal;
    }

    /// <summary>首次带来它的组件类型。</summary>
    public Type ComponentType { get; }

    /// <summary>CSS 文本。</summary>
    public string Text { get; }

    /// <summary>是否为全局样式（否则为作用域样式）。</summary>
    public bool IsGlobal { get; }

    /// <summary>样式表的来源名（诊断与 DevTools 显示）：组件类型全名，全局样式加 <c> (global)</c>。</summary>
    public string SourceName => (ComponentType.FullName ?? ComponentType.Name) + (IsGlobal ? " (global)" : "");

    public override string ToString() => SourceName;
}

/// <summary>
/// UI 根：把根组件挂到 <see cref="RootElement"/> 下，维护脏组件集合，并按需执行帧的 Build 阶段
/// （design/05「帧内流程」：Input → Animate → Build → Style → Layout → Paint → Commit）。
/// <para>
/// 平台的帧时钟订阅 <see cref="FrameRequested"/>（或轮询 <see cref="NeedsFrame"/>），在下一个垂直同步时调用 <see cref="RunFrame"/>。
/// 一帧内脏组件按深度自上而下重建，每个组件最多一次；Build 期间又被弄脏的组件推迟到下一帧，
/// 连续 <see cref="MaxReentrantFrames"/> 帧都如此时视为循环重建（AW1005）并停止重建它们。
/// </para>
/// <para>只能在构造它的 UI 线程上使用；若给定调度器，构造时把它登记为本线程的响应式调度器，跨线程的 State 写入经它封送。</para>
/// </summary>
public sealed class UIRoot
{
    /// <summary>连续多少帧出现「Build 中弄脏已重建的组件」后判定为循环重建。</summary>
    public const int MaxReentrantFrames = 50;

    readonly IDispatcher? _dispatcher;
    readonly Fiber _rootFiber;
    readonly Reconciler _reconciler;
    readonly List<Component> _dirty = new();
    readonly List<Component> _dirtySpare = new();
    readonly List<Component> _deferred = new();
    readonly List<Component> _commit = new();
    readonly List<ElementTreeObserver> _observers = new();
    readonly List<UIError> _unhandled = new();

    Node? _pendingRoot;
    bool _rootDirty;
    bool _inFrame;
    bool _inPipeline;
    bool _frameRequested;
    int _reentrantFrames;
    int _dispatchDepth;
    long _queueSeq;

    public UIRoot(IDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
        if (dispatcher is not null && dispatcher.CheckAccess()) Reactive.CurrentThreadDispatcher = dispatcher;
        RootElement = new Element("root");
        RootElement._root = this;
        _rootFiber = new Fiber { Kind = FiberKind.Root, Element = RootElement };
        _reconciler = new Reconciler(this);
        FocusManager = new FocusManager(this);
        Input = new InputManager(this);
    }

    /// <summary>构造时给定的 UI 线程调度器（异步图片解码据此回到 UI 线程）。</summary>
    internal IDispatcher? Dispatcher => _dispatcher;

    UIClock? _clock;
    bool _clockSet;
    ImageCache? _images;

    /// <summary>
    /// UI 线程的时间源与定时器（动画图片按帧时长推进）。默认：有调度器时为 <see cref="UIClock.CreateSystem"/>，否则为 null（动画停在第一帧）。
    /// 测试与确定性的无窗口渲染可设置 <see cref="ManualUIClock"/>。
    /// </summary>
    public UIClock? Clock
    {
        get
        {
            if (!_clockSet)
            {
                _clockSet = true;
                if (_dispatcher is not null) _clock = UIClock.CreateSystem(_dispatcher);
            }
            return _clock;
        }
        set
        {
            _clockSet = true;
            _clock = value;
        }
    }

    /// <summary>本根的图片缓存（解码结果按来源共享、引用计数、异步解码、动画播放）。</summary>
    public ImageCache Images => _images ??= new ImageCache(this);

    internal ImageCache? ImagesOrNull => _images;

    /// <summary>根元素（标签 <c>root</c>，CSS <c>:root</c> 匹配它）。根组件的元素是它的子节点。</summary>
    public Element RootElement { get; }

    public FocusManager FocusManager { get; }

    /// <summary>指针、键盘与文本输入的派发入口。</summary>
    public InputManager Input { get; }

    /// <summary>Build 之后的帧阶段（样式、布局、绘制）；集成前为 null。</summary>
    public FramePipeline? Pipeline { get; set; }

    /// <summary>已执行的帧数（每次 <see cref="RunFrame"/> 加一）。</summary>
    public long FrameNumber { get; private set; }

    /// <summary>当前挂载的根节点描述。</summary>
    public Node? RootNode { get; private set; }

    /// <summary>
    /// 需要一帧：有待重建的组件，或（存在 <see cref="Pipeline"/> 时）元素上有脏标记，或渲染管线的滚动需要后续的帧
    /// （<see cref="RenderPipeline.NeedsAnimationFrame"/>：平滑滚动、惯性、覆盖式滚动条淡出等），这样 <see cref="UIHost"/>
    /// 以外的宿主也经 <see cref="FrameRequested"/> 得到这些帧。
    /// </summary>
    public bool NeedsFrame =>
        _rootDirty || _dirty.Count > 0
        || (Pipeline is not null && (RootElement.DirtyFlags != ElementDirtyFlags.None || RootElement.DescendantDirtyFlags != ElementDirtyFlags.None))
        || (Pipeline is RenderPipeline p && p.NeedsAnimationFrame);

    /// <summary>从「不需要帧」变为「需要帧」时触发一次（帧执行中产生的请求在帧末尾统一触发）。</summary>
    public event Action<UIRoot>? FrameRequested;

    /// <summary>运行时诊断（AW1005 Build 中写 State、AW1008 重复 Key ...）。</summary>
    public event Action<UIDiagnostic>? Diagnostic;

    /// <summary>
    /// 没有错误边界捕获的异常。没有订阅者时，异常在引发它的 <see cref="RunFrame"/> / 派发调用结束时重新抛出。
    /// </summary>
    public event Action<UIError>? UnhandledError;

    // ------------------------------------------------------------ 挂载

    /// <summary>挂载根节点（通常是根组件）并立即执行一帧。再次调用会与上一次的根协调。</summary>
    public void Mount(Node root)
    {
        VerifyAccess();
        _pendingRoot = root ?? throw new ArgumentNullException(nameof(root));
        _rootDirty = true;
        RunFrame();
    }

    /// <summary>卸载全部内容。</summary>
    public void Unmount()
    {
        VerifyAccess();
        _pendingRoot = null;
        _rootDirty = true;
        RunFrame();
    }

    // ------------------------------------------------------------ 帧

    /// <summary>执行一帧：重建脏组件（自上而下，每个最多一次）→ 提交（OnMount、副作用）→ <see cref="Pipeline"/>。</summary>
    public void RunFrame()
    {
        VerifyAccess();
        if (_inFrame) throw new InvalidOperationException("不能在帧执行过程中再次调用 RunFrame。");
        _inFrame = true;
        _frameRequested = false;
        FrameNumber++;
        try
        {
            if (_rootDirty)
            {
                _rootDirty = false;
                RootNode = _pendingRoot;
                _reconciler.ReconcileChildren(_rootFiber, null, _pendingRoot, null);
            }
            ProcessDirty();
            Commit();
            FinishDeferred();
        }
        finally
        {
            _inFrame = false;
        }
        if (Pipeline is { } pipeline)
        {
            // 管线自身打的脏标记（样式差异 → 布局 / 绘制）在本帧内处理，不应再请求下一帧
            _inPipeline = true;
            try
            {
                pipeline.Run(this);
            }
            finally
            {
                _inPipeline = false;
            }
        }
        if (NeedsFrame) RaiseFrameRequested();
        ThrowUnhandled();
    }

    /// <summary>反复执行帧直到没有待重建的组件（测试与同步场景使用）。返回执行的帧数。</summary>
    public int RunUntilIdle(int maxFrames = 100)
    {
        int n = 0;
        while ((_rootDirty || _dirty.Count > 0) && n < maxFrames)
        {
            RunFrame();
            n++;
        }
        return n;
    }

    void ProcessDirty()
    {
        int rounds = 0;
        while (_dirty.Count > 0)
        {
            if (++rounds > 10_000) break; // 防御：同一帧内脏集合不断增长
            _dirtySpare.Clear();
            _dirtySpare.AddRange(_dirty);
            _dirty.Clear();
            // 按深度自上而下；同深度保持入队顺序
            StableSortByDepth(_dirtySpare);
            for (int i = 0; i < _dirtySpare.Count; i++)
            {
                var c = _dirtySpare[i];
                c._queued = false;
                if (!c._mounted || c._unmounting) continue;
                if (c._builtFrame == FrameNumber && !c._extraBuildAllowed)
                {
                    // 已被父组件在本帧重建且之后没再变脏：无需处理
                    if (!c._forceRebuild && c._observer!._status == CellStatus.Clean) continue;
                    c._queued = true;
                    _deferred.Add(c);
                    continue;
                }
                if (!c._forceRebuild && !c._extraBuildAllowed && !ReactiveRuntime.ShouldRun(c._observer!)) continue;
                _reconciler.RebuildStandalone(c);
            }
        }
    }

    static readonly Comparison<Component> s_byDepth = (a, b) =>
        a._depth != b._depth ? a._depth.CompareTo(b._depth) : a._queueSeq.CompareTo(b._queueSeq);

    /// <summary>按深度自上而下，同深度保持入队顺序（O(n log n)）。</summary>
    static void StableSortByDepth(List<Component> list)
    {
        if (list.Count > 1) list.Sort(s_byDepth);
    }

    /// <summary>一帧内「错误边界切换后备界面 → 提交」的最大轮数（防御 OnError 中反复 Reset 之类的死循环）。</summary>
    const int MaxCommitRounds = 1000;

    void Commit()
    {
        // 提交期间的写入在下一帧生效；唯一的例外是被错误边界捕获的异常：需要在本帧内切换到后备界面，
        // 而后备界面（及其中再次出错时的外层边界）又要提交，因此循环直到稳定
        for (int round = 0; round < MaxCommitRounds; round++)
        {
            for (int i = 0; i < _commit.Count; i++)
            {
                var c = _commit[i];
                if (!c._mounted || c._unmounting) continue;
                if (!c._mountCalled)
                {
                    c._mountCalled = true;
                    try
                    {
                        c.InvokeOnMount();
                    }
                    catch (Exception ex)
                    {
                        ReportError(ex, c, ErrorPhase.Mount);
                    }
                }
                c.RunPendingEffects(this);
            }
            _commit.Clear();
            if (_dirty.Count > 0 && HasExtraBuilds()) ProcessDirty();
            else if (_commit.Count == 0) return;
        }
    }

    bool HasExtraBuilds()
    {
        foreach (var c in _dirty)
            if (c._extraBuildAllowed) return true;
        return false;
    }

    void FinishDeferred()
    {
        if (_deferred.Count == 0)
        {
            _reentrantFrames = 0;
            return;
        }
        if (++_reentrantFrames >= MaxReentrantFrames)
        {
            _reentrantFrames = 0;
            foreach (var c in _deferred)
            {
                c._queued = false;
                c._forceRebuild = false;
                if (c._observer is not null) c._observer._status = CellStatus.Clean;
                ReportDiagnostic(new UIDiagnostic("AW1005", DiagnosticSeverity.Error,
                    "组件 " + c.GetType().Name + " 连续 " + MaxReentrantFrames + " 帧在 Build 阶段被再次弄脏（循环重建），已停止重建它。检查 Build() 是否写入了 State。", c));
                ReportError(new InvalidOperationException("AW1005: 组件 " + c.GetType().Name + " 循环重建。"), c, ErrorPhase.Build);
            }
            _deferred.Clear();
            return;
        }
        foreach (var c in _deferred) _dirty.Add(c);
        _deferred.Clear();
    }

    internal void EnqueueCommit(Component c) => _commit.Add(c);

    /// <summary>把组件排进脏集合并请求一帧。</summary>
    internal void ScheduleRebuild(Component c)
    {
        if (!c._mounted || c._unmounting) return;
        if (!c._queued)
        {
            c._queued = true;
            c._queueSeq = ++_queueSeq;
            _dirty.Add(c);
        }
        RequestFrame();
    }

    void RequestFrame()
    {
        if (_inFrame || _inPipeline || _frameRequested) return;
        RaiseFrameRequested();
    }

    void RaiseFrameRequested()
    {
        _frameRequested = true;
        FrameRequested?.Invoke(this);
    }

    internal void OnElementDirty()
    {
        if (Pipeline is not null) RequestFrame();
    }

    void VerifyAccess() => _dispatcher?.VerifyAccess();

    // ------------------------------------------------------------ 错误与诊断

    internal void ReportDiagnostic(UIDiagnostic diagnostic) => Diagnostic?.Invoke(diagnostic);

    /// <summary>把异常交给最近的错误边界；没有则进入 <see cref="UnhandledError"/>。</summary>
    internal void ReportError(Exception ex, Component? source, ErrorPhase phase)
    {
        Diagnostic?.Invoke(new UIDiagnostic("AW0001", DiagnosticSeverity.Error,
            phase + " 阶段的异常：" + ex.GetType().Name + ": " + ex.Message, source));
        // 边界自身（含后备界面）出错交给更外层
        var start = source is ErrorBoundary ? source._parent : source;
        for (var c = start; c is not null; c = c._parent)
        {
            if (c is ErrorBoundary b && b._mounted && !b._unmounting && b.Capture(ex, source)) return;
        }
        var info = new UIError(ex, source, phase);
        if (UnhandledError is not null) UnhandledError(info);
        else _unhandled.Add(info);
    }

    void ThrowUnhandled()
    {
        if (_unhandled.Count == 0 || _inFrame || _dispatchDepth > 0) return;
        var errors = _unhandled.ToArray();
        _unhandled.Clear();
        if (errors.Length == 1) ExceptionDispatchInfo.Capture(errors[0].Exception).Throw();
        var list = new Exception[errors.Length];
        for (int i = 0; i < errors.Length; i++) list[i] = errors[i].Exception;
        throw new AggregateException("界面中发生了多个未处理的异常。", list);
    }

    /// <summary>事件派发的外层包装：派发结束后重新抛出未处理的异常。</summary>
    internal void BeginDispatch()
    {
        VerifyAccess();
        _dispatchDepth++;
    }

    internal void EndDispatch()
    {
        _dispatchDepth--;
        ThrowUnhandled();
    }

    // ------------------------------------------------------------ 组件样式

    readonly HashSet<Type> _styledTypes = new();
    readonly HashSet<string> _styleTexts = new(StringComparer.Ordinal);
    readonly List<ComponentStyleSheet> _componentStyles = new();

    /// <summary>已登记的组件样式表，按首次挂载的顺序（在根的生命期内保留）。</summary>
    public IReadOnlyList<ComponentStyleSheet> ComponentStyleSheets => _componentStyles;

    /// <summary>组件实例首次挂载：每个组件类型只读取一次样式钩子；相同的文本（例如派生类型继承了基类的样式）只登记一次。</summary>
    internal void RegisterComponentStyles(Component c)
    {
        var type = c.GetType();
        if (!_styledTypes.Add(type)) return;
        string? scoped, global;
        try
        {
            scoped = c.ScopedStyleSheet;
            global = c.GlobalStyleSheet;
        }
        catch (Exception ex)
        {
            ReportError(ex, c, ErrorPhase.Build);
            return;
        }
        if (global is { Length: > 0 }) AddComponentStyle(type, global, isGlobal: true);
        if (scoped is { Length: > 0 }) AddComponentStyle(type, scoped, isGlobal: false);
    }

    void AddComponentStyle(Type type, string text, bool isGlobal)
    {
        if (!_styleTexts.Add(text)) return;
        var sheet = new ComponentStyleSheet(type, text, isGlobal);
        _componentStyles.Add(sheet);
        Pipeline?.OnComponentStyleSheet(this, sheet);
    }

    // ------------------------------------------------------------ 观察者

    public void AddObserver(ElementTreeObserver observer)
    {
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        _observers.Add(observer);
    }

    public void RemoveObserver(ElementTreeObserver observer) => _observers.Remove(observer);

    internal void OnSubtreeRemoved(Element parent, Element removed)
    {
        FocusManager.OnSubtreeRemoved(parent, removed);
        Input.OnSubtreeRemoved(parent, removed);
    }

    internal void NotifyChildInserted(Element parent, Element child)
    {
        foreach (var o in _observers) o.OnChildInserted(parent, child);
    }

    internal void NotifyChildRemoved(Element parent, Element child)
    {
        foreach (var o in _observers) o.OnChildRemoved(parent, child);
    }

    internal void NotifyChildMoved(Element parent, Element child, Element? before)
    {
        foreach (var o in _observers) o.OnChildMoved(parent, child, before);
    }

    internal void NotifyIdChanged(Element e, string? oldId)
    {
        foreach (var o in _observers) o.OnIdChanged(e, oldId);
    }

    internal void NotifyClassesChanged(Element e)
    {
        foreach (var o in _observers) o.OnClassesChanged(e);
    }

    internal void NotifyAttributeChanged(Element e, string name, string? oldValue)
    {
        foreach (var o in _observers) o.OnAttributeChanged(e, name, oldValue);
    }

    internal void NotifyInlineStyleChanged(Element e)
    {
        foreach (var o in _observers) o.OnInlineStyleChanged(e);
    }

    internal void NotifyPseudoStatesChanged(Element e, PseudoStates old)
    {
        foreach (var o in _observers) o.OnPseudoStatesChanged(e, old);
    }

    internal void NotifyTextChanged(Element e)
    {
        foreach (var o in _observers) o.OnTextChanged(e);
    }

    internal void NotifyPropertyChanged(Element e, string name)
    {
        foreach (var o in _observers) o.OnPropertyChanged(e, name);
    }
}
