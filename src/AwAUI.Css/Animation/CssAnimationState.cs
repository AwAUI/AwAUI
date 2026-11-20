using System;
using System.Collections.Generic;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.Animation;

/// <summary>动画与过渡事件的种类（CSS Animations 1 §4.2、CSS Transitions 1 §6 / 2 §4）。</summary>
public enum CssAnimationEventType : byte
{
    AnimationStart,
    AnimationIteration,
    AnimationEnd,
    AnimationCancel,
    TransitionRun,
    TransitionStart,
    TransitionEnd,
    TransitionCancel,
}

/// <summary>动画引擎产生的事件：由宿主（AwAUI.UI）派发给元素。</summary>
public readonly struct CssAnimationEvent
{
    public CssAnimationEvent(CssAnimationEventType type, string name, double elapsedTime, PseudoElementKind pseudoElement)
    {
        Type = type;
        Name = name;
        ElapsedTime = elapsedTime;
        PseudoElement = pseudoElement;
    }

    public CssAnimationEventType Type { get; }

    /// <summary>动画事件为 <c>animation-name</c>；过渡事件为属性名（如 <c>opacity</c>、<c>--x</c>）。</summary>
    public string Name { get; }

    /// <summary>事件对应的动画时间（秒，不含延迟，CSS Animations 1 §4.2 / CSS Transitions 1 §6.1 的 <c>elapsedTime</c>）。</summary>
    public double ElapsedTime { get; }

    public PseudoElementKind PseudoElement { get; }

    /// <summary>是否为动画（而不是过渡）事件。</summary>
    public bool IsAnimationEvent => Type <= CssAnimationEventType.AnimationCancel;

    public override string ToString() => Type + "(" + Name + ", " + ElapsedTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s)";
}

/// <summary>A pending CSS animation or transition. Equality identifies the same runtime instance across style updates.</summary>
public readonly struct CssPendingMotion : IEquatable<CssPendingMotion>
{
    readonly object _identity;

    internal CssPendingMotion(object identity, double endTime)
    {
        _identity = identity;
        EndTime = endTime;
    }

    /// <summary>Expected end time in seconds on the animation clock, including delay and iterations.</summary>
    public double EndTime { get; }

    public bool Equals(CssPendingMotion other) => ReferenceEquals(_identity, other._identity);

    public override bool Equals(object? obj) => obj is CssPendingMotion other && Equals(other);

    public override int GetHashCode() => _identity?.GetHashCode() ?? 0;
}

/// <summary>动画或过渡所处的阶段（Web Animations §4.5.? 的 before / active / after；Idle 表示尚未报告过事件）。</summary>
internal enum AnimationPhase : byte { Idle, Before, Active, After }

/// <summary>
/// 一个元素（或伪元素）上的 CSS 动画与过渡状态，由 <see cref="CssAnimator"/> 维护。宿主按元素保存它，并在每次重算样式或时间前进时交回动画引擎。
/// 实例只能在 UI 线程上使用。
/// </summary>
public sealed class CssAnimationState
{
    internal readonly List<RunningAnimation> Animations = new();
    internal readonly List<RunningTransition> Transitions = new();
    internal readonly HashSet<CssPropertyId> AboveAnimation = new();
    internal readonly HashSet<CssPropertyId> AboveTransition = new();
    internal readonly HashSet<CssPropertyId> AnimatedProperties = new();
    internal readonly HashSet<string> AnimatedCustom = new(StringComparer.Ordinal);

    /// <summary>不含动画的级联结果（最近一次重算样式）。</summary>
    internal ComputedStyle? Base;

    /// <summary>最近一次的最终样式（含动画与过渡）。</summary>
    internal ComputedStyle? Final;

    internal ComputedStyle? Parent;
    internal StyleElement? Element;
    internal PseudoElementKind Pseudo;
    internal int KeyframesGeneration = -1;

    /// <summary>不含动画与过渡的级联结果（最近一次重算样式时）。</summary>
    public ComputedStyle? BaseStyle => Base;

    /// <summary>最近一次计算得到的最终样式。</summary>
    public ComputedStyle? CurrentStyle => Final;

    /// <summary>CSS 动画的个数（含已结束但仍在 <c>animation-name</c> 中的）。</summary>
    public int AnimationCount => Animations.Count;

    /// <summary>进行中（含延迟阶段）的过渡个数。</summary>
    public int TransitionCount => Transitions.Count;

    /// <summary>正在过渡的属性名。</summary>
    public IEnumerable<string> TransitioningProperties
    {
        get
        {
            foreach (var t in Transitions) yield return t.Name;
        }
    }

    /// <summary>Finite motion that is active or delayed. Paused and finished animations are excluded.</summary>
    public IEnumerable<CssPendingMotion> PendingFiniteMotion
    {
        get
        {
            foreach (var a in Animations)
            {
                if (a.LastPhase is not (AnimationPhase.Before or AnimationPhase.Active) || !double.IsNaN(a.HoldTime)
                    || double.IsInfinity(a.Iterations)) continue;
                double end = a.StartTime + Math.Max(0, a.Delay + a.Duration * a.Iterations);
                if (end < double.PositiveInfinity) yield return new CssPendingMotion(a, end);
            }
            foreach (var t in Transitions)
            {
                double end = t.StartTime + t.Delay + t.Duration;
                if (end < double.PositiveInfinity) yield return new CssPendingMotion(t, end);
            }
        }
    }

    /// <summary>没有任何动画与过渡记录（宿主可以丢弃本状态）。</summary>
    public bool IsEmpty => Animations.Count == 0 && Transitions.Count == 0;
}

/// <summary>一个 CSS 动画（<c>animation-name</c> 中的一项）。</summary>
internal sealed class RunningAnimation
{
    public RunningAnimation(string name, double startTime)
    {
        Name = name;
        StartTime = startTime;
    }

    public string Name { get; }
    public CssKeyframes? Keyframes;
    public double StartTime;

    /// <summary>暂停时保持的本地时间；运行中为 NaN。</summary>
    public double HoldTime = double.NaN;

    public double Delay, Duration, Iterations = 1;
    public CssAnimationDirection Direction;
    public CssAnimationFillMode Fill;
    public CssEasingFunction Easing = CssEasingFunction.Ease;
    public CssCompositeOperation Composite;

    public AnimationPhase LastPhase;
    public double LastIteration;

    // 关键帧在元素上的计算值（按基础样式缓存）
    public ComputedStyle? CacheBase;
    public ComputedStyle?[]? FrameStyles;
}

/// <summary>一个进行中的过渡（CSS Transitions 1 §3）。</summary>
internal sealed class RunningTransition
{
    public RunningTransition(CssPropertyId id, string? custom)
    {
        Id = id;
        Custom = custom;
    }

    public CssPropertyId Id { get; }

    /// <summary>自定义属性名（已注册的带类型属性）；长属性为 null。</summary>
    public string? Custom { get; }

    public string Name => Custom ?? CssProperties.Get(Id).Name;

    public object Start = null!, End = null!, ReversingAdjustedStart = null!;
    public double ShorteningFactor = 1;
    public double StartTime, Delay, Duration;
    public CssEasingFunction Easing = CssEasingFunction.Ease;

    /// <summary>按离散方式过渡（<c>transition-behavior: allow-discrete</c> 下的不可插值值）。</summary>
    public bool Discrete;

    public AnimationPhase LastPhase;
}
