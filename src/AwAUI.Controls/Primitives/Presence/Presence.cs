using System;

namespace AwAUI.Controls.Primitives;

/// <summary>Presence 状态机的状态（Radix <c>usePresence</c>）。</summary>
public enum PresenceState : byte
{
    /// <summary>不渲染。</summary>
    Unmounted,

    /// <summary>已渲染且应显示（<c>data-state="open"</c>）。</summary>
    Mounted,

    /// <summary>已请求移除，但退出动画 / 过渡仍在进行：继续渲染（<c>data-state="closed"</c>），等结束事件后卸载。</summary>
    UnmountSuspended,
}

/// <summary>
/// 内容在 <c>data-state</c> 切换为 closed 之后的计算样式摘要：由宿主从 CSS 动画引擎读取后交给 <see cref="Presence"/>。
/// </summary>
public readonly struct PresenceStyle
{
    /// <summary>当前的 <c>animation-name</c>（逗号分隔的列表；没有动画为 <c>none</c> 或空）。</summary>
    public string? AnimationName { get; init; }

    /// <summary>动画的最长总时长（delay + duration × 迭代次数）；未知时为零（使用 <see cref="Presence.FallbackTimeout"/>）。</summary>
    public TimeSpan AnimationDuration { get; init; }

    /// <summary>动画为无限迭代：永远等不到结束事件，直接卸载。</summary>
    public bool InfiniteAnimation { get; init; }

    /// <summary>因这次状态切换而开始的过渡数（<c>transitionrun</c>）；每个都要等到 <c>transitionend</c> / <c>transitioncancel</c>。</summary>
    public int RunningTransitions { get; init; }

    /// <summary>过渡的最长总时长（delay + duration）；未知时为零。</summary>
    public TimeSpan TransitionDuration { get; init; }

    /// <summary><c>display: none</c>：不会播放动画，直接卸载。</summary>
    public bool DisplayNone { get; init; }

    internal bool HasAnimation => !string.IsNullOrEmpty(AnimationName) && AnimationName != "none";
}

/// <summary>
/// 进出场协调（Radix <c>Presence</c>）：<c>present</c> 变为 false 时，若内容因此开始了退出动画或过渡，先进入
/// <see cref="PresenceState.UnmountSuspended"/>，等对应的结束事件后才卸载；否则立即卸载。
/// <para>状态机：Mounted --UNMOUNT--&gt; Unmounted；Mounted --ANIMATION_OUT--&gt; UnmountSuspended --ANIMATION_END--&gt; Unmounted；
/// UnmountSuspended / Unmounted --MOUNT--&gt; Mounted。</para>
/// <para>
/// 与 Radix 的差别：同时支持 CSS 过渡（Radix 只看动画）；无限动画直接卸载；每次挂起都有截止时间（动画 / 过渡总时长 + <see cref="EndEventGrace"/>，
/// 时长未知时为 <see cref="FallbackTimeout"/>），到期由 <see cref="Tick"/> 卸载——结束事件丢失（元素被隐藏、动画被别的样式替换）不会让内容永远留下。
/// </para>
/// <para>时间由调用方传入（帧时钟），便于测试。</para>
/// </summary>
public sealed class Presence
{
    string _prevAnimationName = "none";
    string? _currentAnimationName;
    int _pendingTransitions;
    bool _waitingAnimation;
    TimeSpan _deadline;

    public Presence(bool present) => State = present ? PresenceState.Mounted : PresenceState.Unmounted;

    public PresenceState State { get; private set; }

    /// <summary>是否应渲染内容（Mounted 或 UnmountSuspended）。</summary>
    public bool IsPresent => State != PresenceState.Unmounted;

    /// <summary>状态变化。</summary>
    public event Action<PresenceState>? StateChanged;

    /// <summary>挂起超过动画总时长后再等结束事件的宽限。</summary>
    public TimeSpan EndEventGrace { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>动画 / 过渡时长未知时挂起的上限。</summary>
    public TimeSpan FallbackTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>挂起的截止时间（仅在 <see cref="PresenceState.UnmountSuspended"/> 时有意义）。</summary>
    public TimeSpan Deadline => _deadline;

    /// <summary>
    /// 内容渲染后（或 <c>animationstart</c> 时）记录当前的 <c>animation-name</c>：退出时据此判断动画名是否变化（Radix 的 <c>prevAnimationNameRef</c>）。
    /// </summary>
    public void RecordAnimationName(string? animationName) => _prevAnimationName = Normalize(animationName);

    /// <summary>
    /// <c>present</c> 属性变化。<paramref name="style"/> 是 <c>data-state</c> 已切换后的样式（变为 true 时只取其动画名，作为之后判断退出动画的基准）。
    /// </summary>
    public void SetPresent(bool present, in PresenceStyle style, TimeSpan now)
    {
        if (present)
        {
            // 与 Radix 相同：打开后的动画名作为「之前的动画名」，关闭时动画名变化才算有退出动画
            _prevAnimationName = Normalize(style.AnimationName);
            _pendingTransitions = 0;
            _waitingAnimation = false;
            Transition(PresenceState.Mounted);
            return;
        }
        if (State != PresenceState.Mounted) return;
        string current = Normalize(style.AnimationName);
        bool animating = style.HasAnimation && !style.InfiniteAnimation && current != _prevAnimationName;
        bool transitioning = style.RunningTransitions > 0;
        if (style.DisplayNone || (!animating && !transitioning))
        {
            Transition(PresenceState.Unmounted);
            return;
        }
        _currentAnimationName = current;
        _waitingAnimation = animating;
        _pendingTransitions = transitioning ? style.RunningTransitions : 0;
        TimeSpan longest = TimeSpan.Zero;
        if (animating && style.AnimationDuration > longest) longest = style.AnimationDuration;
        if (transitioning && style.TransitionDuration > longest) longest = style.TransitionDuration;
        // 时长来自样式（可能极大）：饱和相加，不溢出
        _deadline = longest > TimeSpan.Zero ? SaturatingAdd(SaturatingAdd(now, longest), EndEventGrace) : SaturatingAdd(now, FallbackTimeout);
        Transition(PresenceState.UnmountSuspended);
    }

    /// <summary><c>animationend</c> / <c>animationcancel</c>（目标是内容根元素时）。</summary>
    public void OnAnimationEnd(string? animationName)
    {
        if (State != PresenceState.UnmountSuspended || !_waitingAnimation) return;
        var name = animationName ?? "";
        if (!ContainsName(_currentAnimationName, name)) return;
        _waitingAnimation = false;
        if (_pendingTransitions == 0) Transition(PresenceState.Unmounted);
    }

    /// <summary><c>transitionend</c> / <c>transitioncancel</c>（目标是内容根元素时）。</summary>
    public void OnTransitionEnd()
    {
        if (State != PresenceState.UnmountSuspended || _pendingTransitions == 0) return;
        _pendingTransitions--;
        if (_pendingTransitions == 0 && !_waitingAnimation) Transition(PresenceState.Unmounted);
    }

    /// <summary>推进时间：挂起超过截止时间时卸载。返回是否因此卸载。</summary>
    public bool Tick(TimeSpan now)
    {
        if (State != PresenceState.UnmountSuspended || now < _deadline) return false;
        _pendingTransitions = 0;
        _waitingAnimation = false;
        Transition(PresenceState.Unmounted);
        return true;
    }

    void Transition(PresenceState next)
    {
        if (State == next) return;
        State = next;
        if (next == PresenceState.Unmounted) _prevAnimationName = "none";
        StateChanged?.Invoke(next);
    }

    static TimeSpan SaturatingAdd(TimeSpan a, TimeSpan b)
    {
        long x = a.Ticks, y = b.Ticks;
        long r = unchecked(x + y);
        // 同号相加而结果变号即溢出
        if (((x ^ r) & (y ^ r)) < 0) return x > 0 ? TimeSpan.MaxValue : TimeSpan.MinValue;
        return new TimeSpan(r);
    }

    static string Normalize(string? name) => string.IsNullOrEmpty(name) ? "none" : name!.Trim();

    static bool ContainsName(string? list, string name)
    {
        if (list is null || name.Length == 0) return false;
        foreach (var part in list.Split(','))
            if (part.Trim() == name) return true;
        return false;
    }
}
