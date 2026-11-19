using System;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 组件：<c>UI = f(state)</c>（design/05、12）。组件类既是描述（父组件 <c>new TodoRow { Todo = t }</c>）也是存活实例（持有 State）：
/// 首次出现的对象成为存活实例；之后同类型同 Key 的新对象只是 Props 载体，由生成的 <see cref="AdoptProps"/> 拷贝 Props 后丢弃，
/// <see cref="PropsEqual"/> 为真且自身无脏依赖时整棵子树跳过。
/// <para>
/// Props = 组件上声明的公共可写实例属性。把组件类声明为 <c>partial</c>，源生成器（AwAUI.Generators）即生成
/// <see cref="PropsEqual"/>、<see cref="AdoptProps"/>、<see cref="CanAdoptProps"/>；<c>.awaui</c> 编译器用同一套逻辑直接产出它们。
/// </para>
/// <para>
/// 生命周期顺序：<c>Build()</c> 在帧的 Build 阶段执行；<see cref="OnMount"/> 与 <see cref="UseEffect"/> 的副作用在本帧全部元素变更完成后
/// （提交阶段）执行，子组件先于父组件，同一组件先 <see cref="OnMount"/> 后副作用（按声明顺序）。卸载时同样子组件先于父组件：
/// 每个组件先按声明顺序执行副作用的清理函数，再调用 <see cref="OnUnmount"/>；整个被移除子树的回调都执行完之后，
/// 元素才从树上摘离、<see cref="Ref{T}"/> 才被清空，因此 <see cref="OnUnmount"/> 中仍可访问自己的元素。
/// 已卸载的实例可以再次挂载（被缓存的实例、<see cref="ErrorBoundary.Reset"/> 后重新渲染的子内容）：它保留自己的 State，
/// 并重新收到 <see cref="OnMount"/> 与副作用，与之前的 <see cref="OnUnmount"/> 成对。同一实例不能同时挂载在两处。
/// </para>
/// </summary>
public abstract class Component : Node
{
    internal UIRoot? _root;
    internal Fiber? _fiber;
    internal Component? _parent;
    internal int _depth;
    internal ComponentObserver? _observer;
    internal bool _mounted;
    internal bool _unmounting;
    internal bool _mountCalled;
    internal bool _queued;
    internal long _queueSeq;
    internal bool _forceRebuild;
    internal bool _extraBuildAllowed;
    internal long _builtFrame = -1;
    bool _buildWriteReported;

    List<EffectSlot>? _effects;
    int _effectCursor;
    bool _inBuild;

    /// <summary>产出界面描述。只读取 State 与 Props，不写 State（AW1005）。</summary>
    protected abstract Node Build();

    /// <summary>首次 Build 成功且元素已挂到树上之后调用（提交阶段）。</summary>
    protected virtual void OnMount() { }

    /// <summary>从树上移除前调用（副作用清理之后）。</summary>
    protected virtual void OnUnmount() { }

    /// <summary>
    /// 判断载体 <paramref name="other"/> 的 Props 是否与本实例相同。由源生成器为 partial 组件实现：
    /// 逐个 Prop 用 <see cref="EqualityComparer{T}.Default"/> 比较（委托因此按「同一方法 + 同一目标」比较）。
    /// 默认（没有 Props 的组件）为 true。
    /// </summary>
    protected virtual bool PropsEqual(Component other) => true;

    /// <summary>把载体 <paramref name="other"/> 的 Props 拷贝到本实例。由源生成器实现。</summary>
    protected virtual void AdoptProps(Component other) { }

    /// <summary>
    /// 能否通过 <see cref="AdoptProps"/> 接收载体的 Props。只读（<c>init</c>）Props 无法在实例上更新：
    /// 生成的实现在它们变化时返回 false，协调器随之重新挂载该组件（State 重置）。默认 true。
    /// </summary>
    protected virtual bool CanAdoptProps(Component other) => true;

    /// <summary>
    /// 组件的作用域样式表文本（<c>.awaui</c> 的 <c>&lt;style&gt;</c>，选择器已由编译器附加作用域属性选择器）；没有为 null。
    /// 某个组件类型的实例第一次挂载到一个 <see cref="UIRoot"/> 时读取一次，作为 Author 来源的样式表加入该根的帧管线
    /// （按首次挂载的顺序，在根的生命期内保留）。<c>.awaui</c> 编译器生成的组件覆盖它；手写组件也可以覆盖（返回常量文本）。
    /// </summary>
    protected internal virtual string? ScopedStyleSheet => null;

    /// <summary>组件的全局样式表文本（<c>.awaui</c> 的 <c>&lt;style global&gt;</c>，不限定作用域）；没有为 null。注册方式同 <see cref="ScopedStyleSheet"/>。</summary>
    protected internal virtual string? GlobalStyleSheet => null;

    // ------------------------------------------------------------ 公共信息

    /// <summary>是否已挂载（存活实例）。</summary>
    public bool IsMounted => _mounted;

    /// <summary>所属 UI 根；未挂载时为 null。</summary>
    public UIRoot? Root => _root;

    /// <summary>组件树中最近的祖先组件（DevTools、上下文与错误边界沿它查找）。</summary>
    public Component? ParentComponent => _parent;

    /// <summary>组件产出的顶层元素（按树序；片段与子组件展开）。</summary>
    public IReadOnlyList<Element> GetRootElements()
    {
        var list = new List<Element>();
        if (_fiber is not null) Reconciler.CollectHosts(_fiber, list);
        return list;
    }

    // ------------------------------------------------------------ 组件内工具

    /// <summary>
    /// 声明副作用：在 <c>Build()</c> 中调用，按调用顺序识别。首次提交、或 <paramref name="deps"/> 与上一次不同（逐项 <see cref="object.Equals(object, object)"/>）时，
    /// 在提交阶段先执行上一次返回的清理函数再执行 <paramref name="effect"/>；卸载时执行最后一次的清理函数。
    /// 不传 deps 表示只在挂载时执行一次。副作用函数不追踪 State 依赖（由 deps 驱动）。
    /// </summary>
    protected void UseEffect(Func<Action?> effect, params object?[] deps)
    {
        if (effect is null) throw new ArgumentNullException(nameof(effect));
        if (!_inBuild) throw new InvalidOperationException("UseEffect 只能在 Build() 中调用。需要一次性的初始化请重写 OnMount。");
        deps ??= Array.Empty<object?>();
        _effects ??= new List<EffectSlot>();
        EffectSlot slot;
        if (_effectCursor < _effects.Count)
        {
            slot = _effects[_effectCursor];
        }
        else
        {
            slot = new EffectSlot();
            _effects.Add(slot);
        }
        _effectCursor++;
        slot.Remove = false;
        bool changed = !slot.HasRun || !DepsEqual(slot.Deps, deps);
        slot.Pending = changed ? effect : null;
        slot.PendingDeps = changed ? deps : null;
    }

    /// <summary>读取最近的祖先 <see cref="Provide{T}"/> 提供的值；在 <c>Build()</c> 中读取会登记依赖，值变化时本组件重建。找不到时抛出异常。</summary>
    protected T Use<T>()
    {
        if (TryUse<T>(out var value)) return value;
        throw new InvalidOperationException("没有祖先组件提供类型为 " + typeof(T).Name + " 的上下文（用 Provide<T>(value, child) 提供）。");
    }

    /// <summary>读取上下文；找不到时返回 false。</summary>
    protected bool TryUse<T>(out T value)
    {
        for (var c = _parent; c is not null; c = c._parent)
        {
            if (c is ContextProvider<T> p)
            {
                value = p.Read();
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>向 <paramref name="child"/> 子树提供上下文值（用 <see cref="Use{T}"/> 读取）。</summary>
    protected static Node Provide<T>(T value, Node child) => Context.Provide(value, child);

    /// <summary>条件类名：<c>Cx("tab", ("active", isActive))</c>。</summary>
    protected static ClassValue Cx(string? baseClasses, params (string Name, bool On)[] conditional) => ClassValue.Cx(baseClasses, conditional);

    /// <summary>条件类名：<c>Cx(("a", x), ("b", y))</c>。</summary>
    protected static ClassValue Cx(params (string Name, bool On)[] conditional) => ClassValue.Cx(null, conditional);

    /// <summary>强制在下一帧重建（通常不需要：读取的 State 变化会自动重建）。</summary>
    protected void Invalidate()
    {
        if (!_mounted) return;
        _forceRebuild = true;
        _root?.ScheduleRebuild(this);
    }

    // ------------------------------------------------------------ 运行时内部

    internal bool InvokePropsEqual(Component other) => PropsEqual(other);

    internal void InvokeAdoptProps(Component other) => AdoptProps(other);

    internal bool InvokeCanAdoptProps(Component other) => CanAdoptProps(other);

    internal void InvokeOnMount() => OnMount();

    internal void InvokeOnUnmount() => OnUnmount();

    internal Node InvokeBuild()
    {
        _inBuild = true;
        _effectCursor = 0;
        try
        {
            return Build();
        }
        finally
        {
            _inBuild = false;
        }
    }

    /// <summary>Build 结束：成功时把本次未调用到的副作用槽标记为待清理；失败时丢弃本次登记。</summary>
    internal void EndBuild(bool succeeded)
    {
        var effects = _effects;
        if (effects is null) return;
        if (!succeeded)
        {
            foreach (var s in effects)
            {
                s.Pending = null;
                s.PendingDeps = null;
            }
            return;
        }
        for (int i = _effectCursor; i < effects.Count; i++) effects[i].Remove = true;
    }

    /// <summary>提交阶段：执行待执行的副作用（先清理后执行）。</summary>
    internal void RunPendingEffects(UIRoot root)
    {
        var effects = _effects;
        if (effects is null) return;
        for (int i = 0; i < effects.Count; i++)
        {
            var s = effects[i];
            if (s.Remove)
            {
                RunCleanup(s, root);
                effects.RemoveAt(i--);
                continue;
            }
            var pending = s.Pending;
            if (pending is null) continue;
            s.Pending = null;
            RunCleanup(s, root);
            s.Deps = s.PendingDeps ?? Array.Empty<object?>();
            s.PendingDeps = null;
            s.HasRun = true;
            try
            {
                s.Cleanup = pending();
            }
            catch (Exception ex)
            {
                root.ReportError(ex, this, ErrorPhase.Effect);
            }
        }
    }

    /// <summary>卸载：按声明顺序执行全部清理函数。</summary>
    internal void RunAllCleanups(UIRoot root)
    {
        var effects = _effects;
        if (effects is null) return;
        foreach (var s in effects) RunCleanup(s, root);
        effects.Clear();
    }

    void RunCleanup(EffectSlot s, UIRoot root)
    {
        var c = s.Cleanup;
        if (c is null) return;
        s.Cleanup = null;
        try
        {
            c();
        }
        catch (Exception ex)
        {
            root.ReportError(ex, this, ErrorPhase.Effect);
        }
    }

    internal void ReportStateWriteDuringBuild()
    {
        if (_buildWriteReported) return;
        _buildWriteReported = true;
        _root?.ReportDiagnostic(new UIDiagnostic(
            "AW1005", DiagnosticSeverity.Warning,
            "组件 " + GetType().Name + " 在 Build() 中写入了 State，这会导致循环重建。把写入移到事件处理器、OnMount 或副作用中。",
            this));
    }

    static bool DepsEqual(object?[]? a, object?[] b)
    {
        if (a is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }

    sealed class EffectSlot
    {
        public object?[]? Deps;
        public object?[]? PendingDeps;
        public Func<Action?>? Pending;
        public Action? Cleanup;
        public bool HasRun;
        public bool Remove;
    }

    public override string ToString() => GetType().Name;
}

/// <summary>组件的依赖观察者：读过的 State/Computed 变化时把组件排进 UI 根的脏集合。</summary>
internal sealed class ComponentObserver : ReactiveCell
{
    public readonly Component Component;

    public ComponentObserver(Component component) => Component = component;

    internal override bool IsLinked => true;

    internal override void Schedule() => Component._root?.ScheduleRebuild(Component);
}

/// <summary>上下文提供与读取。</summary>
public static class Context
{
    /// <summary>向 <paramref name="child"/> 子树提供类型为 <typeparamref name="T"/> 的值；值变化（按 <see cref="EqualityComparer{T}.Default"/>）时只重建读取它的组件。</summary>
    public static Node Provide<T>(T value, Node child) => new ContextProvider<T>(value, child);
}

/// <summary>上下文提供者（<see cref="Context.Provide{T}"/> 产生）。值放在一个 State 中，读取者因此自动订阅。</summary>
internal sealed class ContextProvider<T> : Component
{
    readonly State<T> _cell;
    T _value;
    Node _child;

    public ContextProvider(T value, Node child)
    {
        _value = value;
        _child = child ?? throw new ArgumentNullException(nameof(child));
        _cell = new State<T>(value);
    }

    internal T Read() => _cell.Value;

    protected override Node Build() => _child;

    protected override bool PropsEqual(Component other)
    {
        var o = (ContextProvider<T>)other;
        return EqualityComparer<T>.Default.Equals(_value, o._value) && ReferenceEquals(_child, o._child);
    }

    protected override void AdoptProps(Component other)
    {
        var o = (ContextProvider<T>)other;
        _value = o._value;
        _child = o._child;
        _cell.Value = _value;
    }
}
