using System;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>焦点作用域内的一个候选：可按 Tab 到达的元素（按 Tab 顺序给出）。</summary>
public readonly struct FocusCandidate<T>
{
    public FocusCandidate(T target, bool isVisible = true, bool isLink = false)
    {
        Target = target;
        IsVisible = isVisible;
        IsLink = isLink;
    }

    public T Target { get; }

    /// <summary>是否可见（不可见的候选不能获得焦点，跳过）。</summary>
    public bool IsVisible { get; }

    /// <summary>是否为链接：自动聚焦时跳过链接（Radix 的 <c>removeLinks</c>）。</summary>
    public bool IsLink { get; }
}

/// <summary>Tab 键在焦点作用域中的处理结果。</summary>
public readonly struct FocusTabResult<T>
{
    internal FocusTabResult(bool preventDefault, bool hasTarget, T target)
    {
        PreventDefault = preventDefault;
        HasTarget = hasTarget;
        Target = target;
    }

    /// <summary>宿主应阻止默认的 Tab 移动。</summary>
    public bool PreventDefault { get; }

    /// <summary>是否需要把焦点移到 <see cref="Target"/>（回绕）。</summary>
    public bool HasTarget { get; }

    public T Target { get; }

    /// <summary>不处理：按默认的 Tab 顺序移动。</summary>
    public static FocusTabResult<T> Default => default;
}

/// <summary>
/// 焦点作用域（Radix <c>FocusScope</c>）的逻辑：
/// <list type="bullet">
/// <item><see cref="Loop"/>：在最后一个可 Tab 项上按 Tab 回到第一个，在第一个上按 Shift+Tab 到最后一个。</item>
/// <item><see cref="Trapped"/>：焦点不能离开作用域——焦点移到外部时拉回最后一次在内部的焦点；Tab 在两端被阻止（不回绕时停住）。</item>
/// <item>挂载时自动聚焦第一个可见的非链接候选，没有则聚焦容器；卸载时把焦点还给挂载前的焦点元素。两者都可由回调阻止。</item>
/// </list>
/// 元素以泛型 <typeparamref name="T"/> 表示（UI 接入后为 <c>Element</c>），Tab 顺序与可见性由宿主给出，因此逻辑可脱离渲染测试。
/// 嵌套作用域由 <see cref="FocusScopeStack{T}"/> 管理：只有最上面的作用域生效，其余暂停。
/// </summary>
public sealed class FocusScopeModel<T> where T : class
{
    public FocusScopeModel(T container) => Container = container ?? throw new ArgumentNullException(nameof(container));

    /// <summary>作用域的容器元素（没有可聚焦的子项时它自己获得焦点）。</summary>
    public T Container { get; }

    public bool Loop { get; set; }

    public bool Trapped { get; set; }

    /// <summary>被更上面的作用域暂停（不处理 Tab 与焦点拉回）。</summary>
    public bool IsPaused { get; internal set; }

    /// <summary>挂载前的焦点元素（卸载时恢复）。</summary>
    public T? PreviouslyFocused { get; private set; }

    /// <summary>最后一次位于作用域内的焦点元素。</summary>
    public T? LastFocusedInside { get; private set; }

    /// <summary>
    /// 挂载：记下 <paramref name="currentFocus"/>；若它已在作用域内则不移动焦点（返回 null），
    /// 否则返回应聚焦的元素（第一个可见的非链接候选，没有则为容器）。<paramref name="preventAutoFocus"/> 对应 <c>onMountAutoFocus</c> 中的 <c>preventDefault</c>。
    /// </summary>
    public T? Mount(T? currentFocus, bool currentFocusIsInside, IReadOnlyList<FocusCandidate<T>> candidates, bool preventAutoFocus = false)
    {
        PreviouslyFocused = currentFocus;
        if (currentFocusIsInside)
        {
            LastFocusedInside = currentFocus;
            return null;
        }
        if (preventAutoFocus) return null;
        T? target = null;
        if (candidates is not null)
            foreach (var c in candidates)
                if (c.IsVisible && !c.IsLink && c.Target is not null) { target = c.Target; break; }
        target ??= Container;
        LastFocusedInside = target;
        return target;
    }

    /// <summary>
    /// 卸载：返回应恢复焦点的元素（挂载前的焦点；没有时为 null，宿主可聚焦根元素）。
    /// <paramref name="preventRestore"/> 对应 <c>onUnmountAutoFocus</c> 中的 <c>preventDefault</c>。
    /// </summary>
    public T? Unmount(bool preventRestore = false) => preventRestore ? null : PreviouslyFocused;

    /// <summary>
    /// 焦点进入 <paramref name="target"/>。在作用域内则记下它；在外部且作用域被困住、未暂停时返回应拉回的元素（否则 null）。
    /// </summary>
    public T? OnFocusIn(T target, bool isInside)
    {
        if (isInside)
        {
            LastFocusedInside = target;
            return null;
        }
        if (!Trapped || IsPaused) return null;
        return LastFocusedInside ?? Container;
    }

    /// <summary>
    /// 作用域内焦点所在的元素被移除（焦点落到了根上）：困住时把焦点交给容器（Radix 的 MutationObserver 处理）。
    /// </summary>
    public T? OnFocusedElementRemoved() => Trapped && !IsPaused ? Container : null;

    /// <summary>
    /// Tab / Shift+Tab（没有按 Alt / Ctrl / Meta）。<paramref name="tabbables"/> 是作用域内按 Tab 顺序排列的可见可 Tab 元素。
    /// </summary>
    public FocusTabResult<T> HandleTab(T? focused, bool shift, IReadOnlyList<T> tabbables)
    {
        if ((!Loop && !Trapped) || IsPaused || focused is null) return FocusTabResult<T>.Default;
        if (tabbables is null || tabbables.Count == 0)
        {
            // 没有可 Tab 的子项：焦点在容器上时阻止离开
            return ReferenceEquals(focused, Container) ? new FocusTabResult<T>(true, false, null!) : FocusTabResult<T>.Default;
        }
        var first = tabbables[0];
        var last = tabbables[tabbables.Count - 1];
        if (!shift && ReferenceEquals(focused, last))
            return new FocusTabResult<T>(true, Loop, Loop ? first : null!);
        if (shift && ReferenceEquals(focused, first))
            return new FocusTabResult<T>(true, Loop, Loop ? last : null!);
        // 焦点在容器自身上：Shift+Tab 会离开作用域
        if (shift && ReferenceEquals(focused, Container))
            return new FocusTabResult<T>(true, Loop, Loop ? last : null!);
        return FocusTabResult<T>.Default;
    }
}

/// <summary>嵌套焦点作用域的栈（Radix 的 <c>focusScopesStack</c>）：新作用域激活时暂停原来最上面的，移除时恢复下一个。</summary>
public sealed class FocusScopeStack<T> where T : class
{
    readonly List<FocusScopeModel<T>> _scopes = new();

    public IReadOnlyList<FocusScopeModel<T>> Scopes => _scopes;

    /// <summary>当前生效的作用域。</summary>
    public FocusScopeModel<T>? Active => _scopes.Count == 0 ? null : _scopes[_scopes.Count - 1];

    /// <summary>激活（已在栈中则移到栈顶）。</summary>
    public void Add(FocusScopeModel<T> scope)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        var active = Active;
        if (active is not null && !ReferenceEquals(active, scope)) active.IsPaused = true;
        _scopes.Remove(scope);
        _scopes.Add(scope);
        scope.IsPaused = false;
    }

    /// <summary>移除并恢复新的栈顶。</summary>
    public void Remove(FocusScopeModel<T> scope)
    {
        if (!_scopes.Remove(scope)) return;
        scope.IsPaused = false;
        var active = Active;
        if (active is not null) active.IsPaused = false;
    }
}
