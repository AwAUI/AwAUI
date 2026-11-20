using System;
using System.Collections.Generic;
using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.Animation;

/// <summary>
/// CSS 动画引擎（CSS Transitions 1/2、CSS Animations 1、Web Animations 1 的时间模型）：在级联结果上叠加动画与过渡的值，维护每个元素的
/// <see cref="CssAnimationState"/>，并产生 <c>animation*</c> / <c>transition*</c> 事件。
/// <list type="bullet">
/// <item><b>级联位置</b>（CSS Cascade 5）：动画值位于普通作者 / 内联声明之上、<c>!important</c> 之下；过渡值位于全部普通与 <c>!important</c>
/// 声明之上，只低于 DevTools 覆盖。<c>font-size</c>、<c>line-height</c> 与自定义属性的动画值在级联计算中途替换，使 em、lh、<c>var()</c> 随之变化；
/// 其余属性直接覆盖计算值（只复制被改动的分组）。</item>
/// <item><b>过渡</b>：比较 before-change style（上一帧的结果，进行中的过渡按当前时间取值）与 after-change style（基础样式 + CSS 动画），按
/// CSS Transitions 1 §3 开始、保留、取消或反转（缩短系数）过渡；元素刚开始被渲染（首次得到样式或从 <c>display: none</c> 变为显示）时以
/// <c>@starting-style</c> 的结果作为起点，没有时不过渡。<c>transition-behavior: allow-discrete</c> 时离散属性也过渡（<c>display</c> 在两端之一为
/// <c>none</c> 时整个过渡期间取另一端）。</item>
/// <item><b>动画</b>：<c>animation-name</c> 按名称与已有动画对应（同名按出现次序），已有的动画保留开始时间、实时采用新的时间参数；关键帧在元素上
/// 求值（<c>var()</c>、em 按元素），缺少 0% / 100% 时用底层值；每个关键帧区间按该帧的 <c>animation-timing-function</c> 缓动；后出现的动画在
/// 合成顺序上更高，<c>add</c> / <c>accumulate</c> 叠加在底层值上。</item>
/// <item><b>时间</b>：<c>now</c> 由宿主给出（秒，单调）；<see cref="Instant"/> 为 true（宿主没有时钟）时过渡不运行，有限的动画立即结束
/// （仍产生 start / end 事件），无限的动画停在 0 时刻。</item>
/// </list>
/// 实例不是线程安全的；与 <see cref="StyleEngine"/> 一样每个 UI 线程一个。
/// </summary>
public sealed class CssAnimator
{
    readonly StyleEngine _engine;
    readonly CascadeLevelSink _levels = new();
    readonly List<KeyValuePair<CssPropertyId, object>> _values = new();
    readonly Dictionary<CssPropertyId, object> _current = new();
    readonly Dictionary<string, CustomPropertyValue> _currentCustom = new(StringComparer.Ordinal);
    readonly Dictionary<CssList<string>, TransitionPlan> _plans = new();
    readonly List<CssPropertyId> _candidates = new();
    readonly HashSet<CssPropertyId> _candidateSet = new();
    const int MaxCachedPlans = 256;

    public CssAnimator(StyleEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public StyleEngine Engine => _engine;

    /// <summary>没有时间来源（宿主没有时钟）：过渡不运行，有限的动画立即结束，无限的动画停在 0 时刻。</summary>
    public bool Instant { get; set; }

    /// <summary>样式中是否声明了会运行的动画或过渡（没有时宿主不必为元素保存状态）。</summary>
    public static bool HasAnimationDeclarations(ComputedStyle style)
    {
        if (style is null) throw new ArgumentNullException(nameof(style));
        if (ReferenceEquals(style.AnimationGroup, ComputedStyle.Initial.AnimationGroup)) return false;
        foreach (var n in style.AnimationName)
            if (n.Length > 0) return true;
        var props = style.TransitionProperty;
        if (props.Count == 0) return false;
        var durations = style.TransitionDuration;
        var delays = style.TransitionDelay;
        for (int i = 0; i < props.Count; i++)
        {
            double d = durations.Count == 0 ? 0 : durations.GetRepeated(i);
            double delay = delays.Count == 0 ? 0 : delays.GetRepeated(i);
            if (Math.Max(d, 0) + delay > 0) return true;
        }
        return false;
    }

    // ================================================================ 公开入口

    /// <summary>
    /// 重算元素的样式（级联 + 动画 + 过渡）：等同于 <see cref="ResolveBase"/> 之后调用 <see cref="Animate"/>。
    /// </summary>
    public ComputedStyle Resolve(StyleElement element, ComputedStyle? parentStyle, PseudoElementKind pseudoElement, ComputedStyle? previousStyle,
        bool parentRendered, ref CssAnimationState? state, double now, List<CssAnimationEvent>? events)
    {
        var baseStyle = ResolveBase(element, parentStyle, pseudoElement);
        if (state is null && !HasAnimationDeclarations(baseStyle)) return baseStyle;
        return Animate(element, baseStyle, parentStyle, pseudoElement, previousStyle, parentRendered, ref state, now, events);
    }

    bool _baseValid;

    /// <summary>
    /// 级联得到不含动画的基础样式，并记录胜出层级高于动画 / 过渡槽的属性（供紧接着的 <see cref="Animate"/> 使用）。
    /// 结果与 <see cref="StyleEngine.Resolve(StyleElement, ComputedStyle?)"/> 相同。
    /// </summary>
    public ComputedStyle ResolveBase(StyleElement element, ComputedStyle? parentStyle, PseudoElementKind pseudoElement = PseudoElementKind.None)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        _baseValid = false;
        var s = _engine.ResolveForAnimation(element, parentStyle, pseudoElement, false, _levels, null);
        _baseValid = true;
        return s;
    }

    /// <summary>
    /// 在 <paramref name="baseStyle"/>（必须是紧挨着的 <see cref="ResolveBase"/> 对同一元素的结果）上叠加动画与过渡。<paramref name="previousStyle"/>
    /// 为上一帧的最终样式（首次为 null）；<paramref name="parentRendered"/> 为 false 表示祖先 <c>display: none</c>（此时动画与过渡全部取消）。
    /// <paramref name="state"/> 按需创建，没有任何动画与过渡时置为 null。事件追加到 <paramref name="events"/>。
    /// </summary>
    public ComputedStyle Animate(StyleElement element, ComputedStyle baseStyle, ComputedStyle? parentStyle, PseudoElementKind pseudoElement,
        ComputedStyle? previousStyle, bool parentRendered, ref CssAnimationState? state, double now, List<CssAnimationEvent>? events)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (baseStyle is null) throw new ArgumentNullException(nameof(baseStyle));
        if (!_baseValid) throw new InvalidOperationException("Animate 必须紧接在对同一元素的 ResolveBase 之后调用。");
        _baseValid = false;
        bool declared = HasAnimationDeclarations(baseStyle);
        if (state is null && !declared) return baseStyle;
        bool rendered = parentRendered && baseStyle.Display != CssDisplay.None;

        var st = state ?? new CssAnimationState();

        // Sample the previous cascade with its existing animations at this style change's time.
        // Reusing the last cascade's sampled value would restart a finished animation as a
        // transition on the next unrelated style change (for example, pointer hover).
        bool newlyRendered = previousStyle is null || previousStyle.Display == CssDisplay.None;
        ComputedStyle? before;
        if (newlyRendered)
            before = _engine.HasStartingStyle(element, pseudoElement) ? _engine.ResolveStartingStyle(element, parentStyle, pseudoElement) : null;
        else if (st.Base is { } oldBase)
            before = st.Animations.Count > 0 && baseStyle.TransitionProperty.Count > 0 && !Instant
                ? ComputeAnimated(st, oldBase, now, null, null, includeTransitions: false)
                : oldBase;
        else
            before = previousStyle;
        // Update the snapshots only after sampling the previous cascade, including its parent
        // and important declarations. The before sample neither advances events nor finishes transitions.
        st.Element = element;
        st.Pseudo = pseudoElement;
        st.Parent = parentStyle;
        st.AboveAnimation.Clear();
        foreach (var id in _levels.AboveAnimation) st.AboveAnimation.Add(id);
        st.AboveTransition.Clear();
        foreach (var id in _levels.AboveTransition) st.AboveTransition.Add(id);

        if (!parentRendered || (!rendered && st.Transitions.Count == 0 && !AllowsDiscreteDisplay(baseStyle)))
        {
            // 不被渲染（且没有可能保持显示的 display 过渡）：取消一切
            Cancel(st, now, events);
            state = null;
            return baseStyle;
        }

        st.Base = baseStyle;
        UpdateAnimations(st, baseStyle, now, events);
        var afterChange = ComputeAnimated(st, baseStyle, now, events, null, includeTransitions: false);
        UpdateTransitions(st, before, afterChange, baseStyle, now, events);
        var final = st.Transitions.Count == 0 ? afterChange : ComputeAnimated(st, baseStyle, now, null, events, includeTransitions: true);

        if (final.Display == CssDisplay.None)
        {
            Cancel(st, now, events);
            state = null;
            return baseStyle;
        }
        st.Final = final;
        state = st.IsEmpty ? null : st;
        return final;
    }

    /// <summary><c>display</c> 是否可以离散过渡（transition-property 匹配 display 且 allow-discrete）。</summary>
    bool AllowsDiscreteDisplay(ComputedStyle style)
    {
        var names = style.TransitionProperty;
        if (names.Count == 0) return false;
        int i = PlanFor(names).Index[(int)CssPropertyId.Display];
        return i >= 0 && Repeat(style.TransitionBehavior, i, CssTransitionBehavior.Normal) == CssTransitionBehavior.AllowDiscrete;
    }

    /// <summary>
    /// 时间前进而样式输入不变（元素没有被标记为需要重算样式）：按 <paramref name="now"/> 重新采样动画与过渡。返回新的最终样式（可能与上一帧相同的实例）。
    /// 宿主在元素的样式输入可能变化时必须改用 <see cref="Resolve"/>。
    /// </summary>
    public ComputedStyle Tick(CssAnimationState state, double now, List<CssAnimationEvent>? events)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (state.Base is not { } baseStyle) throw new InvalidOperationException("状态还没有经过 Resolve。");
        var final = ComputeAnimated(state, baseStyle, now, events, events, includeTransitions: true);
        state.Final = final;
        return final;
    }

    /// <summary>取消全部动画与过渡（元素被移除、变为 <c>display: none</c>）：处于 before / active 阶段的产生 cancel 事件。</summary>
    public void Cancel(CssAnimationState state, double now, List<CssAnimationEvent>? events)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        foreach (var a in state.Animations) CancelAnimation(state, a, now, events);
        state.Animations.Clear();
        foreach (var t in state.Transitions) CancelTransition(state, t, now, events);
        state.Transitions.Clear();
    }

    /// <summary>
    /// 下一次需要重新采样的时间（秒）：进行中的动画 / 过渡返回 <paramref name="now"/>（每帧推进）；只有处于延迟阶段的返回其开始时间（宿主可以用定时器唤醒）；
    /// 全部静止（已结束、暂停或填充）时返回 +∞。
    /// </summary>
    public double NextUpdateTime(CssAnimationState state, double now)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        double next = double.PositiveInfinity;
        if (Instant) return next;
        foreach (var t in state.Transitions)
        {
            double begin = t.StartTime + t.Delay;
            next = Math.Min(next, now < begin ? begin : now);
        }
        foreach (var a in state.Animations)
        {
            if (a.Keyframes is null || !double.IsNaN(a.HoldTime)) continue;
            var timing = Timing(a);
            double local = now - a.StartTime;
            var phase = PhaseAt(timing, local);
            if (phase == AnimationPhase.Active) return now;
            if (phase == AnimationPhase.Before) next = Math.Min(next, a.StartTime + timing.BeforeActive);
        }
        return next;
    }

    // ================================================================ 动画列表

    void UpdateAnimations(CssAnimationState st, ComputedStyle style, double now, List<CssAnimationEvent>? events)
    {
        var names = style.AnimationName;
        int gen = _engine.CompileGeneration;
        bool keyframesChanged = st.KeyframesGeneration != gen;
        st.KeyframesGeneration = gen;
        if (names.Count == 0 && st.Animations.Count == 0) return;

        var old = st.Animations;
        var used = old.Count == 0 ? null : new bool[old.Count];
        var list = new List<RunningAnimation>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (name.Length == 0) continue;
            var keyframes = _engine.FindKeyframes(name);
            if (keyframes is null) continue;
            RunningAnimation? anim = null;
            for (int k = 0; k < old.Count; k++)
            {
                if (used![k] || !string.Equals(old[k].Name, name, StringComparison.Ordinal)) continue;
                used[k] = true;
                anim = old[k];
                break;
            }
            bool paused = style.AnimationPlayState.Count > 0 && style.AnimationPlayState.GetRepeated(i) == CssAnimationPlayState.Paused;
            if (anim is null)
            {
                anim = new RunningAnimation(name, now);
                if (paused) anim.HoldTime = 0;
            }
            else if (paused && double.IsNaN(anim.HoldTime))
            {
                anim.HoldTime = Math.Max(0, now - anim.StartTime);
            }
            else if (!paused && !double.IsNaN(anim.HoldTime))
            {
                anim.StartTime = now - anim.HoldTime;
                anim.HoldTime = double.NaN;
            }
            if (!ReferenceEquals(anim.Keyframes, keyframes) || keyframesChanged)
            {
                anim.Keyframes = keyframes;
                anim.CacheBase = null;
                anim.FrameStyles = null;
            }
            float duration = Repeat(style.AnimationDuration, i, 0);
            anim.Duration = duration < 0 ? 0 : duration / 1000.0;
            anim.Delay = Repeat(style.AnimationDelay, i, 0) / 1000.0;
            anim.Iterations = Math.Max(0, Repeat(style.AnimationIterationCount, i, 1));
            anim.Direction = Repeat(style.AnimationDirection, i, CssAnimationDirection.Normal);
            anim.Fill = Repeat(style.AnimationFillMode, i, CssAnimationFillMode.None);
            anim.Easing = Repeat(style.AnimationTimingFunction, i, CssEasingFunction.Ease);
            anim.Composite = (CssCompositeOperation)(byte)Repeat(style.AnimationComposition, i, CssAnimationComposition.Replace);
            list.Add(anim);
        }
        if (used is not null)
        {
            for (int k = 0; k < old.Count; k++)
                if (!used[k]) CancelAnimation(st, old[k], now, events);
        }
        st.Animations.Clear();
        st.Animations.AddRange(list);
    }

    static T Repeat<T>(CssList<T> list, int i, T fallback) => list.Count == 0 ? fallback : list.GetRepeated(i);

    // ================================================================ 时间模型（Web Animations §4）

    struct TimingInfo
    {
        public double Delay, Duration, Iterations, ActiveDuration, EndTime, BeforeActive, ActiveAfter;
    }

    static TimingInfo Timing(RunningAnimation a)
    {
        var t = new TimingInfo { Delay = a.Delay, Duration = a.Duration, Iterations = a.Iterations };
        t.ActiveDuration = a.Duration == 0 || a.Iterations == 0 ? 0 : a.Duration * a.Iterations;
        t.EndTime = Math.Max(a.Delay + t.ActiveDuration, 0);
        t.BeforeActive = Math.Max(Math.Min(a.Delay, t.EndTime), 0);
        t.ActiveAfter = Math.Max(Math.Min(a.Delay + t.ActiveDuration, t.EndTime), 0);
        return t;
    }

    static AnimationPhase PhaseAt(in TimingInfo t, double local)
    {
        if (local < t.BeforeActive) return AnimationPhase.Before;
        if (local >= t.ActiveAfter) return AnimationPhase.After;
        return AnimationPhase.Active;
    }

    double LocalTime(RunningAnimation a, in TimingInfo t, double now)
    {
        if (Instant) return double.IsInfinity(t.ActiveDuration) ? 0 : t.EndTime;
        return double.IsNaN(a.HoldTime) ? now - a.StartTime : a.HoldTime;
    }

    /// <summary>采样结果：迭代进度（已按方向处理）、before 标志、当前迭代；没有效果时 <c>HasEffect</c> 为 false。</summary>
    struct Sample
    {
        public bool HasEffect;
        public double Progress;
        public bool BeforeFlag;
        public double Iteration;
        public AnimationPhase Phase;
    }

    static Sample SampleAt(RunningAnimation a, in TimingInfo t, double local)
    {
        var s = new Sample { Phase = PhaseAt(t, local) };
        double? activeTime = s.Phase switch
        {
            AnimationPhase.Before => a.Fill is CssAnimationFillMode.Backwards or CssAnimationFillMode.Both ? Math.Max(local - t.Delay, 0) : null,
            AnimationPhase.Active => local - t.Delay,
            _ => a.Fill is CssAnimationFillMode.Forwards or CssAnimationFillMode.Both ? Math.Max(Math.Min(local - t.Delay, t.ActiveDuration), 0) : null,
        };
        double overall;
        if (activeTime is { } at)
        {
            if (t.Duration == 0) overall = s.Phase == AnimationPhase.Before ? 0 : t.Iterations;
            else overall = at / t.Duration;
        }
        else
        {
            // 没有效果，但事件仍需要当前迭代
            overall = t.Duration == 0 ? 0 : Math.Max(0, local - t.Delay) / t.Duration;
            if (!double.IsInfinity(t.Iterations)) overall = Math.Min(overall, t.Iterations);
        }
        double simple = double.IsInfinity(overall) ? 0 : overall - Math.Floor(overall);
        if (simple == 0 && s.Phase != AnimationPhase.Before && activeTime is { } a2 && a2 == t.ActiveDuration && t.Iterations != 0) simple = 1;
        double iteration = s.Phase == AnimationPhase.After && double.IsInfinity(t.Iterations) ? double.PositiveInfinity
            : simple == 1 ? Math.Floor(overall) - 1 : Math.Floor(overall);
        bool forwards = a.Direction switch
        {
            CssAnimationDirection.Reverse => false,
            CssAnimationDirection.Alternate => double.IsInfinity(iteration) || Math.IEEERemainder(iteration, 2) == 0,
            CssAnimationDirection.AlternateReverse => !double.IsInfinity(iteration) && Math.IEEERemainder(iteration, 2) != 0,
            _ => true,
        };
        s.HasEffect = activeTime is not null;
        s.Progress = forwards ? simple : 1 - simple;
        s.BeforeFlag = (s.Phase == AnimationPhase.Before && forwards) || (s.Phase == AnimationPhase.After && !forwards);
        s.Iteration = iteration;
        return s;
    }

    void AnimationEvents(CssAnimationState st, RunningAnimation a, in TimingInfo t, in Sample s, List<CssAnimationEvent>? events)
    {
        var prev = a.LastPhase;
        var cur = s.Phase;
        a.LastPhase = cur;
        double lastIteration = a.LastIteration;
        a.LastIteration = s.Iteration;
        if (events is null) return;
        double intervalStart = Math.Max(Math.Min(-t.Delay, t.ActiveDuration), 0);
        double intervalEnd = Math.Max(Math.Min(t.EndTime - t.Delay, t.ActiveDuration), 0);
        switch (prev)
        {
            case AnimationPhase.Idle:
            case AnimationPhase.Before:
                if (cur == AnimationPhase.Active) Add(CssAnimationEventType.AnimationStart, intervalStart);
                else if (cur == AnimationPhase.After)
                {
                    Add(CssAnimationEventType.AnimationStart, intervalStart);
                    Add(CssAnimationEventType.AnimationEnd, intervalEnd);
                }
                break;
            case AnimationPhase.Active:
                if (cur == AnimationPhase.After) Add(CssAnimationEventType.AnimationEnd, intervalEnd);
                else if (cur == AnimationPhase.Active && s.Iteration != lastIteration && !double.IsInfinity(s.Iteration))
                    Add(CssAnimationEventType.AnimationIteration, s.Iteration * t.Duration);
                else if (cur == AnimationPhase.Before) Add(CssAnimationEventType.AnimationEnd, intervalStart);
                break;
        }

        void Add(CssAnimationEventType type, double elapsed) => events.Add(new CssAnimationEvent(type, a.Name, elapsed, st.Pseudo));
    }

    void CancelAnimation(CssAnimationState st, RunningAnimation a, double now, List<CssAnimationEvent>? events)
    {
        if (a.LastPhase is AnimationPhase.Before or AnimationPhase.Active && events is not null)
        {
            var t = Timing(a);
            double local = LocalTime(a, t, now);
            double active = Math.Max(0, Math.Min(local - t.Delay, double.IsInfinity(t.ActiveDuration) ? double.MaxValue : t.ActiveDuration));
            events.Add(new CssAnimationEvent(CssAnimationEventType.AnimationCancel, a.Name, active, st.Pseudo));
        }
        a.LastPhase = AnimationPhase.Idle;
    }

    // ================================================================ 合成

    /// <summary>按当前时间采样动画（与过渡），得到叠加后的样式。</summary>
    ComputedStyle ComputeAnimated(CssAnimationState st, ComputedStyle baseStyle, double now, List<CssAnimationEvent>? animationEvents,
        List<CssAnimationEvent>? transitionEvents, bool includeTransitions)
    {
        _current.Clear();
        _currentCustom.Clear();
        st.AnimatedProperties.Clear();
        st.AnimatedCustom.Clear();
        foreach (var a in st.Animations) ApplyAnimation(st, a, baseStyle, now, animationEvents);
        if (includeTransitions) ApplyTransitions(st, baseStyle, now, transitionEvents);
        if (_current.Count == 0 && _currentCustom.Count == 0) return baseStyle;

        // font-size、line-height 与自定义属性在级联中途替换（其他属性的计算依赖它们）
        EarlyAnimatedValues? early = null;
        _values.Clear();
        foreach (var kv in _current)
        {
            if (kv.Key == CssPropertyId.FontSize) (early ??= new EarlyAnimatedValues()).FontSize = kv.Value;
            else if (kv.Key == CssPropertyId.LineHeight) (early ??= new EarlyAnimatedValues()).LineHeight = kv.Value;
            else _values.Add(kv);
        }
        if (_currentCustom.Count > 0)
        {
            early ??= new EarlyAnimatedValues();
            early.Custom = new Dictionary<string, CustomPropertyValue>(_currentCustom, StringComparer.Ordinal);
        }
        var s0 = baseStyle;
        if (early is not null && st.Element is { } element)
            s0 = _engine.ResolveForAnimation(element, st.Parent, st.Pseudo, false, null, early);
        return _engine.ApplyComputedValues(s0, _values);
    }

    void ApplyAnimation(CssAnimationState st, RunningAnimation a, ComputedStyle baseStyle, double now, List<CssAnimationEvent>? events)
    {
        if (a.Keyframes is not { } kf) return;
        var t = Timing(a);
        double local = LocalTime(a, t, now);
        var s = SampleAt(a, t, local);
        // 事件只在一遍采样中产生（Resolve 的第一遍、Tick）
        if (events is not null) AnimationEvents(st, a, t, s, events);
        if (!s.HasEffect) return;
        EnsureFrameStyles(st, a, kf, baseStyle);
        var groups = kf.Groups;
        foreach (var id in kf.Properties)
        {
            if (st.AboveAnimation.Contains(id)) continue;
            st.AnimatedProperties.Add(id);
            var underlying = _current.TryGetValue(id, out var u) ? u : AnimationDispatch.GetValue(baseStyle, id);
            var value = SampleProperty(a, groups, id, underlying, s);
            if (value is not null) _current[id] = value;
        }
        foreach (var name in kf.CustomProperties)
        {
            st.AnimatedCustom.Add(name);
            _currentCustom.TryGetValue(name, out var u);
            u ??= baseStyle.CustomProperties.TryGetValue(name, out var bv) ? bv : null;
            var value = SampleCustom(a, groups, name, u, s);
            if (value is not null) _currentCustom[name] = value;
        }
    }

    void EnsureFrameStyles(CssAnimationState st, RunningAnimation a, CssKeyframes kf, ComputedStyle baseStyle)
    {
        if (ReferenceEquals(a.CacheBase, baseStyle) && a.FrameStyles is not null) return;
        var groups = kf.Groups;
        var styles = new ComputedStyle?[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Frozen.Length == 0 || st.Element is null) continue;
            styles[i] = _engine.ComputeDeclarations(st.Element, baseStyle, st.Parent, groups[i].Frozen);
        }
        a.FrameStyles = styles;
        a.CacheBase = baseStyle;
    }

    readonly List<Frame> _frames = new();

    /// <summary>一个属性在一个动画中的值（Web Animations 关键帧效果的计算；CSS 动画的效果缓动为线性，缓动作用在关键帧区间上）。</summary>
    object? SampleProperty(RunningAnimation a, IReadOnlyList<KeyframeGroup> groups, CssPropertyId id, object underlying, in Sample s)
    {
        var frames = _frames;
        frames.Clear();
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Declares(id)) frames.Add(new Frame(groups[i].Offset, i, groups[i].Easing, groups[i].Composite));
        if (frames.Count == 0) return null;
        // 缺少 0% / 100% 时用底层值补帧（CSS Animations 1 §3）
        if (frames[0].Offset != 0) frames.Insert(0, new Frame(0, -1, null, null));
        if (frames[frames.Count - 1].Offset != 1) frames.Add(new Frame(1, -1, null, null));
        // 区间：start 为位置 ≤ q 的最后一帧（但不是最后一帧），end 为其后一帧
        double q = s.Progress;
        int k = 0;
        while (k < frames.Count - 2 && q >= frames[k + 1].Offset) k++;
        var start = frames[k];
        var end = frames[k + 1];
        var sv = FrameValue(a, start, id, underlying);
        var ev = FrameValue(a, end, id, underlying);
        double span = end.Offset - start.Offset;
        double local = span <= 0 ? 1 : (q - start.Offset) / span;
        double eased = (start.Easing ?? a.Easing).Evaluate(local, s.BeforeFlag);
        if (eased == 0) return sv;
        if (eased == 1) return ev;
        CssInterpolation.Interpolate(id, sv, ev, eased, out var r);
        return r;
    }

    readonly struct Frame
    {
        public Frame(double offset, int group, CssEasingFunction? easing, CssCompositeOperation? composite)
        {
            Offset = offset;
            Group = group;
            Easing = easing;
            Composite = composite;
        }

        public readonly double Offset;

        /// <summary>关键帧组下标；-1 为补的底层值帧。</summary>
        public readonly int Group;
        public readonly CssEasingFunction? Easing;
        public readonly CssCompositeOperation? Composite;
    }

    static object FrameValue(RunningAnimation a, in Frame f, CssPropertyId id, object underlying)
    {
        if (f.Group < 0 || a.FrameStyles?[f.Group] is not { } style) return underlying;
        var v = AnimationDispatch.GetValue(style, id);
        var composite = f.Composite ?? a.Composite;
        if (composite != CssCompositeOperation.Replace && AnimationDispatch.TryAdd(id, underlying, v, composite == CssCompositeOperation.Accumulate, out var sum))
            return sum;
        return v;
    }

    CustomPropertyValue? SampleCustom(RunningAnimation a, IReadOnlyList<KeyframeGroup> groups, string name, CustomPropertyValue? underlying, in Sample s)
    {
        _engine.Registry.TryGet(name, out var reg);
        CustomPropertyValue? Value(int g)
        {
            if (g < 0 || a.FrameStyles?[g] is not { } style) return underlying;
            return style.CustomProperties.TryGetValue(name, out var v) ? v : null;
        }
        // 与长属性相同的区间选择（帧的值可能为 null：guaranteed-invalid）
        var offsets = new List<(double Offset, int Group, CssEasingFunction? Easing)>();
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Declares(name)) offsets.Add((groups[i].Offset, i, groups[i].Easing));
        if (offsets.Count == 0) return null;
        if (offsets[0].Offset != 0) offsets.Insert(0, (0, -1, null));
        if (offsets[offsets.Count - 1].Offset != 1) offsets.Add((1, -1, null));
        double q = s.Progress;
        int k = 0;
        while (k < offsets.Count - 2 && q >= offsets[k + 1].Offset) k++;
        var start = offsets[k];
        var end = offsets[k + 1];
        var sv = Value(start.Group);
        var ev = Value(end.Group);
        double span = end.Offset - start.Offset;
        double local = span <= 0 ? 1 : (q - start.Offset) / span;
        double eased = (start.Easing ?? a.Easing).Evaluate(local, s.BeforeFlag);
        if (sv is null || ev is null) return eased < 0.5 ? sv : ev;
        CustomPropertyInterpolation.TryInterpolate(reg, sv, ev, eased, out var r);
        return r;
    }

    // ================================================================ 过渡

    /// <summary><c>transition-property</c> 列表展开后的匹配表：长属性 → 列表下标（后出现的优先）。</summary>
    sealed class TransitionPlan
    {
        public readonly int[] Index = new int[CssProperties.LonghandCount];
        public readonly Dictionary<string, int> Custom = new(StringComparer.Ordinal);
        public readonly List<CssPropertyId> Listed = new();
        public bool All;

        public TransitionPlan(CssList<string> names)
        {
            for (int i = 0; i < Index.Length; i++) Index[i] = -1;
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[i];
                if (n.StartsWith("--", StringComparison.Ordinal))
                {
                    Custom[n] = i;
                    continue;
                }
                if (string.Equals(n, "all", StringComparison.OrdinalIgnoreCase))
                {
                    All = true;
                    for (int id = 0; id < Index.Length; id++)
                        if (CssProperties.Get((CssPropertyId)id).Animatable != CssAnimatable.No) Index[id] = i;
                    continue;
                }
                if (!CssProperties.TryGet(n, out var prop)) continue;
                if (prop.IsShorthand)
                {
                    foreach (var l in prop.LonghandIds)
                        if (CssProperties.Get(l).Animatable != CssAnimatable.No) Index[(int)l] = i;
                }
                else if (prop.Animatable != CssAnimatable.No) Index[(int)prop.Id] = i;
            }
            for (int id = 0; id < Index.Length; id++)
                if (Index[id] >= 0) Listed.Add((CssPropertyId)id);
        }
    }

    TransitionPlan PlanFor(CssList<string> names)
    {
        if (_plans.TryGetValue(names, out var plan)) return plan;
        if (_plans.Count >= MaxCachedPlans) _plans.Clear();
        plan = new TransitionPlan(names);
        _plans[names] = plan;
        return plan;
    }

    void UpdateTransitions(CssAnimationState st, ComputedStyle? before, ComputedStyle after, ComputedStyle style, double now, List<CssAnimationEvent>? events)
    {
        var names = style.TransitionProperty;
        var plan = names.Count == 0 ? null : PlanFor(names);

        // 不再匹配 transition-property 的进行中过渡：取消
        for (int i = st.Transitions.Count - 1; i >= 0; i--)
        {
            var t = st.Transitions[i];
            bool matched = plan is not null && (t.Custom is null ? plan.Index[(int)t.Id] >= 0 : plan.Custom.ContainsKey(t.Custom));
            if (matched) continue;
            CancelTransition(st, t, now, events);
            st.Transitions.RemoveAt(i);
        }
        if (plan is null) return;

        // 候选属性：列表中的属性；all 时只看 before / after 中不同的分组（加上进行中的过渡）
        _candidates.Clear();
        _candidateSet.Clear();
        if (plan.All && before is not null)
        {
            for (int id = 0; id < CssProperties.LonghandCount; id++)
            {
                if (plan.Index[id] < 0) continue;
                var g = CssProperties.Get((CssPropertyId)id).Group!.Value;
                if (ReferenceEquals(AnimationDispatch.GetGroup(before, g), AnimationDispatch.GetGroup(after, g))) continue;
                if (_candidateSet.Add((CssPropertyId)id)) _candidates.Add((CssPropertyId)id);
            }
        }
        else if (!plan.All)
        {
            foreach (var id in plan.Listed)
                if (_candidateSet.Add(id)) _candidates.Add(id);
        }
        foreach (var t in st.Transitions)
            if (t.Custom is null && _candidateSet.Add(t.Id)) _candidates.Add(t.Id);

        foreach (var id in _candidates)
        {
            int index = plan.Index[(int)id];
            if (index < 0) continue;
            var afterValue = AnimationDispatch.GetValue(after, id);
            var running = FindTransition(st, id, null);
            if (running is not null && Equals(running.End, afterValue)) continue;
            // 正被 CSS 动画驱动的属性：before / after 都是动画值，不开始过渡
            if (st.AnimatedProperties.Contains(id))
            {
                if (running is not null)
                {
                    CancelTransition(st, running, now, events);
                    st.Transitions.Remove(running);
                }
                continue;
            }
            object? beforeValue = running is not null ? TransitionValue(running, now) : before is null ? null : AnimationDispatch.GetValue(before, id);
            StartOrUpdate(st, id, null, index, style, running, beforeValue, afterValue, now, events);
        }

        foreach (var kv in plan.Custom)
        {
            var name = kv.Key;
            _engine.Registry.TryGet(name, out var reg);
            if (!CustomPropertyInterpolation.IsTyped(reg)) continue;
            after.CustomProperties.TryGetValue(name, out var av);
            var running = FindTransition(st, default, name);
            if (av is null)
            {
                if (running is not null)
                {
                    CancelTransition(st, running, now, events);
                    st.Transitions.Remove(running);
                }
                continue;
            }
            if (running is not null && Equals(running.End, av)) continue;
            object? bv = running is not null ? TransitionValue(running, now)
                : before is not null && before.CustomProperties.TryGetValue(name, out var b0) && b0 is not null ? b0 : null;
            StartOrUpdate(st, default, name, kv.Value, style, running, bv, av, now, events);
        }
    }

    static RunningTransition? FindTransition(CssAnimationState st, CssPropertyId id, string? custom)
    {
        foreach (var t in st.Transitions)
        {
            if (custom is null ? t.Custom is null && t.Id == id : t.Custom == custom) return t;
        }
        return null;
    }

    /// <summary>CSS Transitions 1 §3 第 1、4 步（2 步：已完成的过渡不保留，before-change style 已等于结束值）。</summary>
    void StartOrUpdate(CssAnimationState st, CssPropertyId id, string? custom, int index, ComputedStyle style, RunningTransition? running,
        object? before, object after, double now, List<CssAnimationEvent>? events)
    {
        double duration = Math.Max(0, Repeat(style.TransitionDuration, index, 0f)) / 1000.0;
        double delay = Repeat(style.TransitionDelay, index, 0f) / 1000.0;
        var easing = Repeat(style.TransitionTimingFunction, index, CssEasingFunction.Ease);
        bool allowDiscrete = Repeat(style.TransitionBehavior, index, CssTransitionBehavior.Normal) == CssTransitionBehavior.AllowDiscrete;
        double combined = duration + delay;

        if (running is null)
        {
            if (before is null || Equals(before, after) || combined <= 0 || Instant) return;
            if (!Transitionable(id, custom, before, after, allowDiscrete, out bool discrete)) return;
            Begin(st, id, custom, before, after, before, 1, now, delay, duration, easing, discrete);
            return;
        }

        // 进行中的过渡结束值不同（相同的情况已在调用方跳过）
        var current = before!;
        if (Equals(current, after) || !Transitionable(id, custom, current, after, allowDiscrete, out bool disc) || combined <= 0 || Instant)
        {
            CancelTransition(st, running, now, events);
            st.Transitions.Remove(running);
            return;
        }
        CancelTransition(st, running, now, events);
        st.Transitions.Remove(running);
        if (Equals(running.ReversingAdjustedStart, after))
        {
            // 反转：缩短时长（CSS Transitions 1 §3 第 4.3 步）
            double oldProgress = running.Duration <= 0 ? 1
                : running.Easing.Evaluate(Math.Max(0, Math.Min(1, (now - running.StartTime - running.Delay) / running.Duration)));
            double factor = Math.Abs(oldProgress * running.ShorteningFactor + 1 - running.ShorteningFactor);
            factor = Math.Max(0, Math.Min(1, factor));
            double d = delay < 0 ? delay * factor : delay;
            Begin(st, id, custom, current, after, running.End, factor, now, d, duration * factor, easing, disc);
            return;
        }
        Begin(st, id, custom, current, after, current, 1, now, delay, duration, easing, disc);
    }

    static void Begin(CssAnimationState st, CssPropertyId id, string? custom, object start, object end, object reversingAdjustedStart, double factor,
        double styleChangeTime, double delay, double duration, CssEasingFunction easing, bool discrete)
    {
        st.Transitions.Add(new RunningTransition(id, custom)
        {
            Start = start,
            End = end,
            ReversingAdjustedStart = reversingAdjustedStart,
            ShorteningFactor = factor,
            StartTime = styleChangeTime,
            Delay = delay,
            Duration = duration,
            Easing = easing,
            Discrete = discrete,
        });
    }

    bool Transitionable(CssPropertyId id, string? custom, object a, object b, bool allowDiscrete, out bool discrete)
    {
        discrete = false;
        if (custom is not null)
        {
            _engine.Registry.TryGet(custom, out var reg);
            if (CustomPropertyInterpolation.TryInterpolate(reg, (CustomPropertyValue)a, (CustomPropertyValue)b, 0.5, out _)) return true;
            discrete = true;
            return allowDiscrete;
        }
        if (AnimationDispatch.IsInterpolable(id) && AnimationDispatch.TryInterpolate(id, a, b, 0.5, out _)) return true;
        discrete = true;
        return allowDiscrete && CssProperties.Get(id).Animatable != CssAnimatable.No;
    }

    /// <summary>过渡在 <paramref name="now"/> 的值（延迟阶段取起点：过渡按 backwards 填充）。</summary>
    object TransitionValue(RunningTransition t, double now)
    {
        double active = now - t.StartTime - t.Delay;
        if (active <= 0) return t.Start;
        if (t.Duration <= 0 || active >= t.Duration) return t.End;
        double p = t.Easing.Evaluate(active / t.Duration);
        return Interpolate(t, p);
    }

    object Interpolate(RunningTransition t, double p)
    {
        if (t.Custom is not null)
        {
            _engine.Registry.TryGet(t.Custom, out var reg);
            if (t.Discrete) return p < 0.5 ? t.Start : t.End;
            CustomPropertyInterpolation.TryInterpolate(reg, (CustomPropertyValue)t.Start, (CustomPropertyValue)t.End, p, out var r);
            return r;
        }
        if (t.Discrete) return CssInterpolation.Discrete(t.Id, t.Start, t.End, p);
        CssInterpolation.Interpolate(t.Id, t.Start, t.End, p, out var v);
        return v;
    }

    void ApplyTransitions(CssAnimationState st, ComputedStyle baseStyle, double now, List<CssAnimationEvent>? events)
    {
        for (int i = 0; i < st.Transitions.Count; i++)
        {
            var t = st.Transitions[i];
            double active = now - t.StartTime - t.Delay;
            var phase = active < 0 ? AnimationPhase.Before : active >= t.Duration ? AnimationPhase.After : AnimationPhase.Active;
            TransitionEvents(st, t, phase, active, events);
            if (phase == AnimationPhase.After)
            {
                st.Transitions.RemoveAt(i--);
                continue;
            }
            object value = phase == AnimationPhase.Before ? t.Start : Interpolate(t, t.Easing.Evaluate(active / t.Duration));
            if (t.Custom is not null)
            {
                _currentCustom[t.Custom] = (CustomPropertyValue)value;
                continue;
            }
            if (st.AboveTransition.Contains(t.Id)) continue;
            _current[t.Id] = value;
        }
    }

    void TransitionEvents(CssAnimationState st, RunningTransition t, AnimationPhase cur, double active, List<CssAnimationEvent>? events)
    {
        var prev = t.LastPhase;
        t.LastPhase = cur;
        if (events is null || prev == cur) return;
        // 经过时间：开始为 max(min(−delay, 时长), 0)，结束为时长
        double start = Math.Max(Math.Min(-t.Delay, t.Duration), 0), end = t.Duration;
        if (prev == AnimationPhase.Idle) Add(CssAnimationEventType.TransitionRun, start);
        if (prev is AnimationPhase.Idle or AnimationPhase.Before && cur is AnimationPhase.Active or AnimationPhase.After)
            Add(CssAnimationEventType.TransitionStart, start);
        if (cur == AnimationPhase.After) Add(CssAnimationEventType.TransitionEnd, end);

        void Add(CssAnimationEventType type, double elapsed) => events.Add(new CssAnimationEvent(type, t.Name, elapsed, st.Pseudo));
    }

    void CancelTransition(CssAnimationState st, RunningTransition t, double now, List<CssAnimationEvent>? events)
    {
        if (t.LastPhase is AnimationPhase.Before or AnimationPhase.Active && events is not null)
        {
            double active = Math.Max(0, Math.Min(now - t.StartTime - t.Delay, t.Duration));
            events.Add(new CssAnimationEvent(CssAnimationEventType.TransitionCancel, t.Name, active, st.Pseudo));
        }
        t.LastPhase = AnimationPhase.Idle;
    }
}
