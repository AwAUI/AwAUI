using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

/// <summary>过渡、动画、交互与滚动属性（第九批）。</summary>
public class AnimationAndBehaviorPropertyTests
{
    [Theory]
    [InlineData("transition-property", "all", "all")]
    [InlineData("transition-property", "none", "none")]
    [InlineData("transition-property", "color, Background-Color, --tw-x", "color, background-color, --tw-x")]
    [InlineData("transition-duration", "150ms, 1s", "0.15s, 1s")]
    [InlineData("transition-timing-function", "cubic-bezier(0.4, 0, 0.2, 1)", "cubic-bezier(0.4, 0, 0.2, 1)")]
    [InlineData("transition-timing-function", "ease-in, steps(4, jump-start), step-end, linear", "ease-in, steps(4, jump-start), step-end, linear")]
    [InlineData("transition-timing-function", "linear(0, 0.25 75%, 1)", "linear(0 0%, 0.25 75%, 1 100%)")]
    [InlineData("transition-delay", "-1s", "-1s")]
    [InlineData("transition-behavior", "allow-discrete", "allow-discrete")]
    [InlineData("animation-name", "spin, none, \"x y\"", "spin, none, x y")]
    [InlineData("animation-duration", "auto, 2s", "auto, 2s")]
    [InlineData("animation-iteration-count", "infinite, 2.5", "infinite, 2.5")]
    [InlineData("animation-direction", "alternate-reverse", "alternate-reverse")]
    [InlineData("animation-fill-mode", "both, forwards", "both, forwards")]
    [InlineData("animation-play-state", "paused", "paused")]
    [InlineData("animation-composition", "accumulate", "accumulate")]
    public void Longhand_lists(string property, string value, string expected) => Assert.Equal(expected, Text(property, value));

    [Theory]
    [InlineData("transition-property", "all, none")]
    [InlineData("transition-duration", "-1s")]
    [InlineData("transition-duration", "1")]
    [InlineData("transition-timing-function", "cubic-bezier(2, 0, 0, 1)")]
    [InlineData("transition-timing-function", "steps(0)")]
    [InlineData("transition-timing-function", "steps(1, jump-none)")]
    [InlineData("animation-name", "initial, x")]
    [InlineData("animation-iteration-count", "-1")]
    public void Invalid_lists(string property, string value) => Assert.False(IsValid(property, value), property + ": " + value);

    [Fact]
    public void Transition_shorthand()
    {
        var s = Compute("transition", "opacity 200ms ease-out 50ms, transform 1s allow-discrete");
        Assert.Equal(new[] { "opacity", "transform" }, s.TransitionProperty);
        Assert.Equal(new[] { 200f, 1000f }, s.TransitionDuration);
        Assert.Equal("ease-out", s.TransitionTimingFunction[0].ToString());
        Assert.Equal("ease", s.TransitionTimingFunction[1].ToString());
        Assert.Equal(new[] { 50f, 0f }, s.TransitionDelay);
        Assert.Equal(CssTransitionBehavior.AllowDiscrete, s.TransitionBehavior[1]);
        Assert.Empty(Compute("transition", "none").TransitionProperty);
        Assert.False(IsValid("transition", "none, opacity 1s"));
        Assert.False(IsValid("transition", "1s 1s 1s"));
    }

    [Fact]
    public void Tailwind_transition_utilities_compute()
    {
        // transition-colors：属性列表 + var() 回退链
        const string css = "--default-transition-duration: 150ms; --default-transition-timing-function: cubic-bezier(0.4, 0, 0.2, 1);"
            + " transition-property: color, background-color, border-color, outline-color, text-decoration-color, fill, stroke;"
            + " transition-timing-function: var(--tw-ease, var(--default-transition-timing-function));"
            + " transition-duration: var(--tw-duration, var(--default-transition-duration));";
        var s = Compute("color", "red", extra: css);
        Assert.Equal(7, s.TransitionProperty.Count);
        Assert.Equal(150f, s.TransitionDuration[0]);
        Assert.Equal(CssEasingKind.CubicBezier, s.TransitionTimingFunction[0].Kind);
    }

    [Fact]
    public void Tailwind_animate_in_shorthand_with_adjacent_var_functions()
    {
        // tw-animate-css：var() 之间没有空白，替换按记号进行，不会粘成一个记号
        const string css = "--tw-duration: 200ms; animation: enter var(--tw-animation-duration,var(--tw-duration,.15s))var(--tw-ease,ease)"
            + "var(--tw-animation-delay,0s)var(--tw-animation-iteration-count,1)var(--tw-animation-direction,normal)var(--tw-animation-fill-mode,none);";
        var s = Compute("color", "red", extra: css);
        Assert.Equal(new[] { "enter" }, s.AnimationName);
        Assert.Equal(new[] { 200f }, s.AnimationDuration);
        Assert.Equal("ease", s.AnimationTimingFunction[0].ToString());
        Assert.Equal(new[] { 0f }, s.AnimationDelay);
        Assert.Equal(new[] { 1f }, s.AnimationIterationCount);
        Assert.Equal(CssAnimationFillMode.None, s.AnimationFillMode[0]);
    }

    [Fact]
    public void Animation_shorthand_keyword_precedence()
    {
        var s = Compute("animation", "1s infinite alternate both paused spin 2s linear");
        Assert.Equal("spin", s.AnimationName[0]);
        Assert.Equal(1000f, s.AnimationDuration[0]);
        Assert.Equal(2000f, s.AnimationDelay[0]);
        Assert.True(float.IsPositiveInfinity(s.AnimationIterationCount[0]));
        Assert.Equal(CssAnimationDirection.Alternate, s.AnimationDirection[0]);
        Assert.Equal(CssAnimationFillMode.Both, s.AnimationFillMode[0]);
        Assert.Equal(CssAnimationPlayState.Paused, s.AnimationPlayState[0]);
        Assert.Equal(CssEasingKind.Linear, s.AnimationTimingFunction[0].Kind);
        var none = Compute("animation", "none");
        Assert.Equal("", none.AnimationName[0]);
        Assert.Equal("none", none.GetPropertyValueText("animation-name"));
        // ease 是缓动函数而不是动画名
        Assert.Equal("", Compute("animation", "ease 1s").AnimationName[0]);
        Assert.Equal(2, Compute("animation", "a 1s, b 2s").AnimationName.Count);
    }

    [Fact]
    public void Easing_functions_evaluate()
    {
        Assert.Equal(0.5, CssEasingFunction.Linear.Evaluate(0.5), 6);
        var ease = CssEasingFunction.Ease;
        Assert.Equal(0, ease.Evaluate(0), 6);
        Assert.Equal(1, ease.Evaluate(1), 6);
        Assert.Equal(0.8024, ease.Evaluate(0.5), 3); // Chrome 的 ease(0.5)
        Assert.True(CssEasingFunction.EaseIn.Evaluate(0.25) < 0.25);
        var steps = Compute("transition-timing-function", "steps(4, jump-end)").TransitionTimingFunction[0];
        Assert.Equal(0.25, steps.Evaluate(0.3), 6);
        Assert.Equal(0, CssEasingFunction.StepEnd.Evaluate(0.99), 6);
        Assert.Equal(1, CssEasingFunction.StepStart.Evaluate(0.01), 6);
        var lin = Compute("transition-timing-function", "linear(0, 1 50%, 0)").TransitionTimingFunction[0];
        Assert.Equal(1, lin.Evaluate(0.5), 6);
        Assert.Equal(0.5, lin.Evaluate(0.75), 6);
    }

    [Theory]
    [InlineData("user-select", "none", "none")]
    [InlineData("-webkit-user-select", "text", null)]
    [InlineData("touch-action", "pan-x pinch-zoom", "pan-x pinch-zoom")]
    [InlineData("touch-action", "manipulation", "manipulation")]
    [InlineData("touch-action", "none", "none")]
    [InlineData("will-change", "auto", "auto")]
    [InlineData("scroll-behavior", "smooth", "smooth")]
    [InlineData("overscroll-behavior-y", "contain", "contain")]
    [InlineData("scroll-snap-type", "x mandatory", "x mandatory")]
    [InlineData("scroll-snap-type", "both", "both")]
    [InlineData("scroll-snap-align", "start end", "start end")]
    [InlineData("scroll-snap-align", "center", "center")]
    [InlineData("scroll-snap-stop", "always", "always")]
    [InlineData("scroll-margin-top", "1em", "16px")]
    [InlineData("scroll-margin-left", "-4px", "-4px")]
    [InlineData("scroll-padding-top", "10%", "10%")]
    [InlineData("scroll-padding-top", "auto", "auto")]
    [InlineData("scrollbar-width", "thin", "thin")]
    [InlineData("scrollbar-gutter", "stable both-edges", "stable both-edges")]
    [InlineData("container-type", "inline-size", "inline-size")]
    [InlineData("container-name", "card header", "card header")]
    public void Behavior_values(string property, string value, string? expected)
    {
        Assert.True(IsValid(property, value), property + ": " + value);
        if (expected is not null) Assert.Equal(expected, Text(property, value));
    }

    [Theory]
    [InlineData("touch-action", "pan-x pan-left")]
    [InlineData("touch-action", "none pan-x")]
    [InlineData("will-change", "auto, transform")]
    [InlineData("will-change", "will-change")]
    [InlineData("scroll-snap-type", "none mandatory")]
    [InlineData("scroll-margin-top", "10%")]
    [InlineData("scroll-padding-top", "-1px")]
    [InlineData("scrollbar-gutter", "both-edges")]
    [InlineData("container-name", "none card")]
    [InlineData("container-name", "and")]
    public void Invalid_behavior_values(string property, string value) => Assert.False(IsValid(property, value), property + ": " + value);

    [Fact]
    public void Scroll_shorthands_and_logical_aliases()
    {
        var s = Compute("scroll-margin", "1px 2px 3px 4px");
        Assert.Equal(1f, s.ScrollMarginTop);
        Assert.Equal(2f, s.ScrollMarginRight);
        Assert.Equal(3f, s.ScrollMarginBottom);
        Assert.Equal(4f, s.ScrollMarginLeft);
        var b = Compute("scroll-margin-block", "4px");
        Assert.Equal(4f, b.ScrollMarginTop);
        Assert.Equal(4f, b.ScrollMarginBottom);
        Assert.Equal(0f, b.ScrollMarginLeft);
        var p = Compute("scroll-padding-inline", "1px 2px");
        Assert.Equal(1f, p.ScrollPaddingLeft.Value.Length);
        Assert.Equal(2f, p.ScrollPaddingRight.Value.Length);
        var o = Compute("overscroll-behavior", "none auto");
        Assert.Equal(CssOverscrollBehavior.None, o.OverscrollBehaviorX);
        Assert.Equal(CssOverscrollBehavior.Auto, o.OverscrollBehaviorY);
        Assert.Equal(CssUserSelect.None, Compute("-webkit-user-select", "none").UserSelect);
        Assert.Equal(CssTouchAction.PanX | CssTouchAction.PinchZoom, Compute("touch-action", "pinch-zoom pan-x").TouchAction);
    }

    [Fact]
    public void Container_shorthand()
    {
        var s = Compute("container", "card-header / inline-size");
        Assert.Equal(new[] { "card-header" }, s.ContainerName);
        Assert.Equal(CssContainerType.InlineSize, s.ContainerType);
        var n = Compute("container", "a b");
        Assert.Equal(2, n.ContainerName.Count);
        Assert.Equal(CssContainerType.Normal, n.ContainerType);
        Assert.False(IsValid("container", "a / foo"));
        Assert.False(IsValid("container", "/ size"));
    }

    [Fact]
    public void Logical_sizing_and_radius_aliases()
    {
        Assert.Equal("10px", Compute("inline-size", "10px").GetPropertyValueText("width"));
        Assert.Equal("10px", Compute("max-block-size", "10px").GetPropertyValueText("max-height"));
        Assert.Equal("3px", Compute("border-end-start-radius", "3px").GetPropertyValueText("border-bottom-left-radius"));
    }

    [Fact]
    public void Group_sharing_for_new_groups()
    {
        // 未覆盖的新分组与初始样式共享实例（不增加每元素的分配）
        var s = Compute("color", "red");
        Assert.Same(ComputedStyle.Initial.TransformGroup, s.TransformGroup);
        Assert.Same(ComputedStyle.Initial.AnimationGroup, s.AnimationGroup);
        Assert.Same(ComputedStyle.Initial.BehaviorGroup, s.BehaviorGroup);
        var t = Compute("rotate", "10deg");
        Assert.NotSame(ComputedStyle.Initial.TransformGroup, t.TransformGroup);
        Assert.Equal(StyleDifference.Composite, ComputedStyle.Diff(s, Compute("color", "red", extra: "rotate: 1deg;")));
    }
}
