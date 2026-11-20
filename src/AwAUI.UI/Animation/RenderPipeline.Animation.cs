using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Animation;
using AwAUI.Css.Selectors;

namespace AwAUI.UI;

/// <summary>
/// CSS 过渡与 <c>@keyframes</c> 动画在帧管线中的接入（design/05「动画」、design/08「级联」的动画 / 过渡槽）：
/// <list type="bullet">
/// <item><b>时间</b>：每帧开始时读取一次 <see cref="UIRoot.Clock"/>（测试用 <see cref="ManualUIClock"/>，确定性）。没有时钟时（无窗口、没有调度器的根）
/// 动画引擎按「瞬时」模式工作：过渡不运行（直接取终值），有限的动画在开始的同一帧结束（仍派发 start / end 事件，供等待退出动画的组件使用），
/// 无限的动画停在 0 时刻 —— 与动画图片「没有时钟时停在第一帧」一致：时间不前进，也不因此请求帧。</item>
/// <item><b>重算</b>：样式阶段对需要重算的元素经 <see cref="CssAnimator"/> 叠加动画与过渡（开始、反转、取消过渡，增删动画）；
/// 只有时间前进的元素不重新级联，而是在样式阶段之前从缓存的基础样式重新采样（<see cref="AnimationTickPass"/>）。</item>
/// <item><b>失效</b>：新旧样式照常经 <see cref="ComputedStyle.Diff"/> 转成脏标记 —— 只改变 <c>transform</c> / <c>opacity</c> / 颜色等绘制类属性的帧
/// 只重绘（局部损伤），改变尺寸类属性（width、height、padding …）的帧重新布局；改变继承属性（color、font-size …）的元素按完整的样式重算处理，
/// 以便子元素与文本节点继承新值。</item>
/// <item><b>帧请求</b>：只在有进行中的动画 / 过渡时请求下一帧；只处于延迟阶段时用时钟定时器在开始时刻唤醒；全部结束、暂停或保持填充时不请求帧。</item>
/// <item><b>事件</b>：<c>animation*</c> / <c>transition*</c> 事件在帧末（绘制之后）按发生顺序派发给元素（冒泡）。被移除的元素上的动画静默丢弃。</item>
/// </list>
/// 伪元素（<c>::before</c> 等）上的动画与过渡尚未接入。
/// </summary>
public sealed partial class RenderPipeline
{
    sealed class AnimationEntry
    {
        public AnimationEntry(CssAnimationState state) => State = state;

        public CssAnimationState State;

        /// <summary>下一次需要重新采样的时间（秒）；+∞ 表示静止。</summary>
        public double WakeAt;
    }

    CssAnimator? _animator;
    readonly Dictionary<Element, AnimationEntry> _cssAnimations = new();
    readonly List<CssAnimationEvent> _animationEventScratch = new();
    readonly List<KeyValuePair<Element, CssAnimationEvent>> _pendingAnimationEvents = new();
    readonly List<Element> _animationScratch = new();
    double _animationTime;
    IDisposable? _animationWake;
    double _animationWakeAt = double.PositiveInfinity;
    bool _hasRunningAnimations;

    /// <summary>CSS 动画引擎（过渡与 <c>@keyframes</c> 动画）。</summary>
    public CssAnimator Animator => _animator ??= new CssAnimator(_engine);

    /// <summary>有动画或过渡记录的元素数（含已结束但保持填充的动画）。</summary>
    public int AnimatedElementCount => _cssAnimations.Count;

    /// <summary>是否有需要在下一帧推进的动画或过渡（不含只在延迟阶段、由定时器唤醒的）。</summary>
    public bool HasRunningAnimations => _hasRunningAnimations;

    /// <summary>元素上的动画状态（测试与 DevTools 使用）；没有动画与过渡时为 null。</summary>
    public CssAnimationState? GetAnimationState(Element element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        return _cssAnimations.TryGetValue(element, out var e) ? e.State : null;
    }

    /// <summary>帧开始（样式阶段之前）：读取时钟，推进只有时间变化的动画。</summary>
    void BeginAnimationFrame(UIRoot root)
    {
        var clock = root.Clock;
        var animator = Animator;
        animator.Instant = clock is null;
        _animationTime = clock is null ? 0 : clock.Now.TotalSeconds;
        if (_cssAnimations.Count > 0) AnimationTickPass();
    }

    /// <summary>样式阶段为一个元素重算样式：级联，然后（有动画声明或状态时）叠加动画与过渡。</summary>
    ComputedStyle ResolveStyle(Element e, RenderBox box, ComputedStyle? parent)
    {
        var animator = Animator;
        var view = box.GetView(this);
        var baseStyle = animator.ResolveBase(view, parent);
        AnimationEntry? entry = null;
        if (_cssAnimations.Count > 0) _cssAnimations.TryGetValue(e, out entry);
        if (entry is null && !CssAnimator.HasAnimationDeclarations(baseStyle)) return baseStyle;
        var state = entry?.State;
        _animationEventScratch.Clear();
        var result = animator.Animate(view, baseStyle, parent, PseudoElementKind.None, box.Style, IsParentRendered(e), ref state, _animationTime,
            _animationEventScratch);
        if (state is null)
        {
            if (entry is not null) _cssAnimations.Remove(e);
        }
        else if (entry is null) _cssAnimations[e] = new AnimationEntry(state) { WakeAt = _animationTime };
        else
        {
            entry.State = state;
            entry.WakeAt = _animationTime;
        }
        QueueAnimationEvents(e);
        return result;
    }

    /// <summary>祖先中没有 <c>display: none</c>（按祖先当前的样式，样式阶段自上而下，祖先已是本帧的结果）。</summary>
    static bool IsParentRendered(Element e)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
            if (p.Render?.Style is { Display: Css.Values.CssDisplay.None }) return false;
        return true;
    }

    void QueueAnimationEvents(Element e)
    {
        foreach (var ev in _animationEventScratch) _pendingAnimationEvents.Add(new KeyValuePair<Element, CssAnimationEvent>(e, ev));
        _animationEventScratch.Clear();
    }

    readonly List<(Element Element, RenderBox Box, ComputedStyle Old, ComputedStyle Next, AnimationEntry Entry)> _ticked = new();

    /// <summary>
    /// 只有时间前进的元素：从缓存的基础样式重新采样，按与样式阶段相同的规则把差异转成脏标记。本帧本来就要重算样式的元素跳过（由样式阶段处理）；
    /// 继承属性或 <c>display</c> 变化时改为标记重算样式（子元素与文本节点需要继承新值）。
    /// <para>
    /// 分两步：先采样全部元素并决定哪些改走完整的样式重算，再应用其余的结果 —— 祖先本帧要重算样式时后代也走完整路径，
    /// 使同一帧里祖先与后代的变化按样式阶段的顺序进入绘制（损伤按祖先的旧墨迹计算）。
    /// </para>
    /// </summary>
    void AnimationTickPass()
    {
        var animator = Animator;
        _animationScratch.Clear();
        _ticked.Clear();
        foreach (var kv in _cssAnimations) _animationScratch.Add(kv.Key);
        foreach (var e in _animationScratch)
        {
            if (!_cssAnimations.TryGetValue(e, out var entry)) continue;
            if (!e.IsAttached || !ReferenceEquals(e.Root, _root))
            {
                animator.Cancel(entry.State, _animationTime, null);
                _cssAnimations.Remove(e);
                continue;
            }
            if (entry.WakeAt > _animationTime || _fullRestyle) continue;
            if (e.Render is not { Style: { } old } box) continue;
            if (AncestorOrSelfNeedsRestyle(e) || !IsParentRendered(e))
            {
                // 由样式阶段按本帧的祖先样式采样或取消，避免先派发基于旧样式的事件。
                e.MarkDirty(ElementDirtyFlags.Style);
                continue;
            }
            _animationEventScratch.Clear();
            var next = animator.Tick(entry.State, _animationTime, _animationEventScratch);
            QueueAnimationEvents(e);
            if (ReferenceEquals(next, old))
            {
                if (entry.State.IsEmpty) _cssAnimations.Remove(e);
                continue;
            }
            if (InheritedChanged(old, next) || next.Display != old.Display || DecorationChanged(old, next))
            {
                e.MarkDirty(ElementDirtyFlags.Style);
                continue;
            }
            _ticked.Add((e, box, old, next, entry));
        }
        foreach (var (e, box, old, next, entry) in _ticked)
        {
            if (AncestorOrSelfNeedsRestyle(e))
            {
                // 样式阶段会以同样的时间重新计算它（事件已在上面产生，不会重复）
                e.MarkDirty(ElementDirtyFlags.Style);
                continue;
            }
            box.Style = next;
            ComputedStyleChanged?.Invoke(e);
            ApplyStyleDifference(e, box, ComputedStyle.Diff(old, next), old, next);
            if (AffectsLayering(old, next)) LayeringChanged();
            UpdatePseudoElements(e, box);
            if (entry.State.IsEmpty) _cssAnimations.Remove(e);
        }
        _ticked.Clear();
    }

    static bool AncestorOrSelfNeedsRestyle(Element e)
    {
        for (Element? x = e; x is not null; x = x.Parent)
        {
            if ((x.DirtyFlags & ElementDirtyFlags.Style) != 0) return true;
            if (x.Render is { } b && (b.RestyleSubtree || b.RestyleChildren)) return true;
        }
        return false;
    }

    /// <summary>帧末（绘制之后）：派发事件，并为下一次需要采样的时刻请求帧或安排定时器。</summary>
    void EndAnimationFrame(UIRoot root)
    {
        if (_pendingAnimationEvents.Count > 0) DispatchAnimationEvents(root);
        _hasRunningAnimations = false;
        if (_cssAnimations.Count == 0)
        {
            CancelAnimationWake();
            return;
        }
        var animator = Animator;
        double next = double.PositiveInfinity;
        _animationScratch.Clear();
        foreach (var kv in _cssAnimations)
        {
            var e = kv.Key;
            if (!e.IsAttached || !ReferenceEquals(e.Root, root))
            {
                _animationScratch.Add(e);
                continue;
            }
            var entry = kv.Value;
            entry.WakeAt = animator.NextUpdateTime(entry.State, _animationTime);
            if (entry.WakeAt <= _animationTime) _hasRunningAnimations = true;
            else if (entry.WakeAt < next) next = entry.WakeAt;
        }
        foreach (var e in _animationScratch)
        {
            if (_cssAnimations.TryGetValue(e, out var entry)) animator.Cancel(entry.State, _animationTime, null);
            _cssAnimations.Remove(e);
        }
        if (double.IsPositiveInfinity(next) || root.Clock is not { } clock)
        {
            CancelAnimationWake();
            return;
        }
        if (_animationWake is not null && _animationWakeAt == next) return;
        CancelAnimationWake();
        _animationWakeAt = next;
        _animationWake = clock.Schedule(TimeSpan.FromSeconds(Math.Max(0, next - clock.Now.TotalSeconds)), OnAnimationWake);
    }

    void CancelAnimationWake()
    {
        _animationWake?.Dispose();
        _animationWake = null;
        _animationWakeAt = double.PositiveInfinity;
    }

    /// <summary>延迟阶段结束：请求一帧以采样到期的动画，是否重绘由采样后的样式差异决定。</summary>
    void OnAnimationWake()
    {
        _animationWake = null;
        _animationWakeAt = double.PositiveInfinity;
        if (_root is not { Clock: { } clock } root) return;
        double now = clock.Now.TotalSeconds;
        foreach (var kv in _cssAnimations)
        {
            if (kv.Value.WakeAt > now || !ReferenceEquals(kv.Key.Root, root)) continue;
            _hasRunningAnimations = true;
            root.OnElementDirty();
            break;
        }
    }

    void DispatchAnimationEvents(UIRoot root)
    {
        // 处理器可能再次触发样式变化（下一帧生效）；先取出本帧的事件
        var list = _pendingAnimationEvents.ToArray();
        _pendingAnimationEvents.Clear();
        foreach (var kv in list)
        {
            var e = kv.Key;
            if (!e.IsAttached || !ReferenceEquals(e.Root, root)) continue;
            var ev = kv.Value;
            string pseudo = ev.PseudoElement == PseudoElementKind.None ? string.Empty : "::" + ev.PseudoElement.ToString().ToLowerInvariant();
            switch (ev.Type)
            {
                case CssAnimationEventType.AnimationStart:
                    root.Input.Dispatch(e, new AnimationEvent("animationstart", ev.Name, ev.ElapsedTime, pseudo), EventKind.AnimationStart, null);
                    break;
                case CssAnimationEventType.AnimationIteration:
                    root.Input.Dispatch(e, new AnimationEvent("animationiteration", ev.Name, ev.ElapsedTime, pseudo), EventKind.AnimationIteration, null);
                    break;
                case CssAnimationEventType.AnimationEnd:
                    root.Input.Dispatch(e, new AnimationEvent("animationend", ev.Name, ev.ElapsedTime, pseudo), EventKind.AnimationEnd, null);
                    break;
                case CssAnimationEventType.AnimationCancel:
                    root.Input.Dispatch(e, new AnimationEvent("animationcancel", ev.Name, ev.ElapsedTime, pseudo), EventKind.AnimationCancel, null);
                    break;
                case CssAnimationEventType.TransitionRun:
                    root.Input.Dispatch(e, new TransitionEvent("transitionrun", ev.Name, ev.ElapsedTime, pseudo), EventKind.TransitionRun, null);
                    break;
                case CssAnimationEventType.TransitionStart:
                    root.Input.Dispatch(e, new TransitionEvent("transitionstart", ev.Name, ev.ElapsedTime, pseudo), EventKind.TransitionStart, null);
                    break;
                case CssAnimationEventType.TransitionEnd:
                    root.Input.Dispatch(e, new TransitionEvent("transitionend", ev.Name, ev.ElapsedTime, pseudo), EventKind.TransitionEnd, null);
                    break;
                case CssAnimationEventType.TransitionCancel:
                    root.Input.Dispatch(e, new TransitionEvent("transitioncancel", ev.Name, ev.ElapsedTime, pseudo), EventKind.TransitionCancel, null);
                    break;
            }
        }
    }
}
