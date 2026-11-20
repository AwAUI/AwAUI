namespace AwAUI.UI;

/// <summary>
/// CSS 动画事件（DOM <c>AnimationEvent</c>：<c>animationstart</c>、<c>animationiteration</c>、<c>animationend</c>、<c>animationcancel</c>），冒泡。
/// 由帧管线在一帧的样式、布局与绘制之后按发生顺序派发（元素的 <c>OnAnimationStart</c> 等处理器）。
/// </summary>
public sealed class AnimationEvent : Event
{
    public AnimationEvent(string type, string animationName, double elapsedTime, string pseudoElement = "") : base(type)
    {
        AnimationName = animationName ?? string.Empty;
        ElapsedTime = elapsedTime;
        PseudoElement = pseudoElement ?? string.Empty;
    }

    /// <summary><c>animation-name</c> 中的名称（<c>@keyframes</c> 的名称）。</summary>
    public string AnimationName { get; }

    /// <summary>
    /// 动画已运行的时间（秒，不含延迟）：<c>animationstart</c> 为 max(−delay, 0)，<c>animationiteration</c> 为已完成的迭代数 × 迭代时长，
    /// <c>animationend</c> 为活动时长，<c>animationcancel</c> 为取消时的活动时间。
    /// </summary>
    public double ElapsedTime { get; }

    /// <summary>伪元素（如 <c>::before</c>）；元素本身为空串。</summary>
    public string PseudoElement { get; }
}

/// <summary>
/// CSS 过渡事件（DOM <c>TransitionEvent</c>：<c>transitionrun</c>、<c>transitionstart</c>、<c>transitionend</c>、<c>transitioncancel</c>），冒泡。
/// <c>transitionrun</c> 在过渡创建时（含延迟阶段），<c>transitionstart</c> 在延迟结束时，<c>transitionend</c> 在完成时，<c>transitioncancel</c>
/// 在被取消（反转、值改变、元素变为 <c>display: none</c>、属性不再过渡）时。
/// </summary>
public sealed class TransitionEvent : Event
{
    public TransitionEvent(string type, string propertyName, double elapsedTime, string pseudoElement = "") : base(type)
    {
        PropertyName = propertyName ?? string.Empty;
        ElapsedTime = elapsedTime;
        PseudoElement = pseudoElement ?? string.Empty;
    }

    /// <summary>过渡的长属性名（如 <c>opacity</c>、<c>background-color</c>，或已注册的自定义属性 <c>--x</c>）。</summary>
    public string PropertyName { get; }

    /// <summary>过渡已运行的时间（秒，不含延迟）：开始类事件为 max(−delay, 0)，<c>transitionend</c> 为时长，<c>transitioncancel</c> 为取消时的活动时间。</summary>
    public double ElapsedTime { get; }

    /// <summary>伪元素（如 <c>::before</c>）；元素本身为空串。</summary>
    public string PseudoElement { get; }
}
