using AwAUI.Css.Animation;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Animation;

/// <summary>CSS Animations 1：关键帧选择、迭代、方向、填充、播放状态、合成、事件、级联位置。</summary>
public class KeyframeAnimationTests
{
    const string FadeKeyframes = "@keyframes fade { from { opacity: 0 } to { opacity: 1 } }";

    [Fact]
    public void Animation_samples_keyframes_and_reports_start_and_end()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear; }", "a");
        h.Resolve(0);
        Assert.Equal("0", h.Text("opacity"));
        Assert.Equal("start:fade@0", h.EventLog());
        h.Tick(0.4);
        Assert.Equal("0.4", h.Text("opacity"));
        h.Tick(1);
        Assert.Equal("1", h.Text("opacity")); // 结束且不填充：回到基础值（1）
        Assert.Equal("start:fade@0 end:fade@1", h.EventLog());
        Assert.Equal(double.PositiveInfinity, h.Animator.NextUpdateTime(h.State!, 1));
    }

    [Fact]
    public void Iterations_and_alternate_direction()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear 3 alternate; }", "a");
        h.Resolve(0);
        h.Tick(1.25);
        Assert.Equal("0.75", h.Text("opacity")); // 第 2 次迭代反向
        h.Tick(2.25);
        Assert.Equal("0.25", h.Text("opacity"));
        h.Tick(3);
        Assert.Equal("start:fade@0 iteration:fade@1 iteration:fade@2 end:fade@3", h.EventLog());
    }

    [Fact]
    public void Reverse_and_alternate_reverse()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear reverse; }", "a");
        h.Resolve(0);
        h.Tick(0.25);
        Assert.Equal("0.75", h.Text("opacity"));
        var h2 = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear 2 alternate-reverse; }", "a");
        h2.Resolve(0);
        h2.Tick(0.25);
        Assert.Equal("0.75", h2.Text("opacity"));
        h2.Tick(1.25);
        Assert.Equal("0.25", h2.Text("opacity"));
    }

    [Theory]
    [InlineData("none", 0.0, "0.7")]
    [InlineData("backwards", 0.0, "0")]
    [InlineData("both", 0.0, "0")]
    [InlineData("forwards", 0.0, "0.7")]
    [InlineData("none", 3.0, "0.7")]
    [InlineData("forwards", 3.0, "1")]
    [InlineData("both", 3.0, "1")]
    [InlineData("backwards", 3.0, "0.7")]
    public void Fill_modes(string fill, double t, string expected)
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { opacity: 0.7; animation: fade 1s linear 0.5s " + fill + "; }", "a");
        h.Resolve(0);
        h.Tick(t);
        Assert.Equal(expected, h.Text("opacity"));
    }

    [Fact]
    public void Fractional_iteration_count_fills_at_the_fraction()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear 1.5 forwards; }", "a");
        h.Resolve(0);
        h.Tick(5);
        Assert.Equal("0.5", h.Text("opacity"));
    }

    [Fact]
    public void Negative_delay_starts_part_way()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear -0.25s; }", "a");
        h.Resolve(0);
        Assert.Equal("0.25", h.Text("opacity"));
        Assert.Equal("start:fade@0.25", h.EventLog());
    }

    [Fact]
    public void Missing_end_keyframe_uses_the_underlying_value()
    {
        var h = new AnimationHarness("@keyframes k { from { opacity: 0.2 } } .a { opacity: 0.8; animation: k 1s linear; }", "a");
        h.Resolve(0);
        h.Tick(0.5);
        Assert.Equal("0.5", h.Text("opacity"));
    }

    [Fact]
    public void Intermediate_keyframes_and_per_keyframe_easing()
    {
        const string css = "@keyframes k { 0% { opacity: 0; animation-timing-function: steps(2, jump-end) } 50% { opacity: 0.5 } 100% { opacity: 1 } }"
                           + " .a { animation: k 1s linear; }";
        var h = new AnimationHarness(css, "a");
        h.Resolve(0);
        h.Tick(0.2);
        Assert.Equal("0", h.Text("opacity")); // 0–50% 按 steps(2)
        h.Tick(0.3);
        Assert.Equal("0.25", h.Text("opacity"));
        h.Tick(0.75);
        Assert.Equal("0.75", h.Text("opacity")); // 50–100% 按 animation-timing-function: linear
    }

    [Fact]
    public void Keyframes_with_same_offset_merge_and_later_declarations_win()
    {
        const string css = "@keyframes k { from { opacity: 0; width: 0px } from { opacity: 0.5 } to { opacity: 1; width: 100px } }"
                           + " .a { animation: k 1s linear; }";
        var h = new AnimationHarness(css, "a");
        h.Resolve(0);
        Assert.Equal("0.5", h.Text("opacity"));
        Assert.Equal("0px", h.Text("width"));
    }

    [Fact]
    public void Important_in_keyframes_is_ignored_and_important_declarations_beat_animations()
    {
        var h = new AnimationHarness("@keyframes k { from { opacity: 0 !important; width: 10px } to { width: 20px } } .a { animation: k 1s linear; }", "a");
        h.Resolve(0);
        Assert.Equal("1", h.Text("opacity"));
        Assert.Equal("10px", h.Text("width"));

        var h2 = new AnimationHarness(FadeKeyframes + " .a { opacity: 0.3 !important; animation: fade 1s linear; }", "a");
        h2.Resolve(0);
        h2.Tick(0.5);
        Assert.Equal("0.3", h2.Text("opacity"));

        // 普通内联声明低于动画
        var h3 = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear; }", "a");
        h3.Target.Style("opacity: 0.3");
        h3.Resolve(0);
        h3.Tick(0.5);
        Assert.Equal("0.5", h3.Text("opacity"));
    }

    [Fact]
    public void Composition_add_and_accumulate()
    {
        const string kf = "@keyframes m { from { margin-left: 0px } to { margin-left: 100px } }";
        var h = new AnimationHarness(kf + " .a { margin-left: 10px; animation: m 1s linear; animation-composition: add; }", "a");
        h.Resolve(0);
        h.Tick(0.5);
        Assert.Equal("60px", h.Text("margin-left"));

        // 后面的动画叠加在前面的结果上
        var h2 = new AnimationHarness(kf + " @keyframes n { to { margin-left: 10px } } .a { animation: m 1s linear, n 1s linear; animation-composition: replace, add; }", "a");
        h2.Resolve(0);
        h2.Tick(0.5);
        // n：from = 底层值（m 的结果 50px），to = 50px + 10px
        Assert.Equal("55px", h2.Text("margin-left"));
    }

    [Fact]
    public void Later_animations_override_earlier_ones()
    {
        var h = new AnimationHarness(FadeKeyframes + " @keyframes half { from, to { opacity: 0.5 } } .a { animation: fade 1s linear, half 1s linear; }", "a");
        h.Resolve(0);
        h.Tick(0.25);
        Assert.Equal("0.5", h.Text("opacity"));
    }

    [Fact]
    public void Paused_animations_hold_their_time()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear; } .a.p { animation-play-state: paused; }", "a");
        h.Resolve(0);
        h.Tick(0.25);
        h.SetClasses("a p", 0.25);
        h.Tick(0.75);
        Assert.Equal("0.25", h.Text("opacity"));
        Assert.Equal(double.PositiveInfinity, h.Animator.NextUpdateTime(h.State!, 0.75));
        h.SetClasses("a", 0.75);
        h.Tick(1.0);
        Assert.Equal("0.5", h.Text("opacity"));
    }

    [Fact]
    public void Changing_timing_keeps_the_start_time_and_removing_cancels()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear; } .a.slow { animation-duration: 4s; } .a.off { animation: none; }", "a");
        h.Resolve(0);
        h.Tick(0.5);
        h.SetClasses("a slow", 0.5);
        Assert.Equal("0.125", h.Text("opacity")); // 同一个动画，开始时间不变，新时长
        h.SetClasses("a off", 1);
        Assert.Equal("1", h.Text("opacity"));
        Assert.Equal("start:fade@0 cancel:fade@1", h.EventLog());
        Assert.Null(h.State);
    }

    [Fact]
    public void Unknown_keyframes_do_not_run()
    {
        var h = new AnimationHarness(".a { animation: nope 1s; }", "a");
        h.Resolve(0);
        Assert.Null(h.State);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void Keyframes_resolution_prefers_later_rules_and_respects_media()
    {
        const string css = "@keyframes k { to { opacity: 0 } } @keyframes k { to { opacity: 0.5 } } @media (min-width: 5000px) { @keyframes k { to { opacity: 0.9 } } }"
                           + " .a { animation: k 1s linear forwards; }";
        var h = new AnimationHarness(css, "a");
        h.Resolve(0);
        h.Tick(2);
        Assert.Equal("0.5", h.Text("opacity"));
    }

    [Fact]
    public void Keyframe_values_use_the_element_variables_and_font_size()
    {
        const string css = "@keyframes k { from { width: var(--w); padding-left: 2em } to { width: 0px; padding-left: 0px } }"
                           + " .a { --w: 120px; font-size: 10px; animation: k 1s linear; }";
        var h = new AnimationHarness(css, "a");
        h.Resolve(0);
        Assert.Equal("120px", h.Text("width"));
        Assert.Equal("20px", h.Text("padding-left"));
        h.Tick(0.5);
        Assert.Equal("60px", h.Text("width"));
    }

    [Fact]
    public void Font_size_animation_updates_em_values()
    {
        var h = new AnimationHarness("@keyframes g { from { font-size: 10px } to { font-size: 20px } } .a { padding-left: 1em; animation: g 1s linear; }", "a");
        h.Resolve(0);
        h.Tick(0.5);
        Assert.Equal("15px", h.Text("font-size"));
        Assert.Equal("15px", h.Text("padding-left"));
    }

    [Fact]
    public void Registered_custom_properties_animate()
    {
        const string css = "@property --x { syntax: '<length>'; inherits: false; initial-value: 0px; }"
                           + " @keyframes cx { from { --x: 0px } to { --x: 100px } } .a { margin-left: var(--x); animation: cx 1s linear; }";
        var h = new AnimationHarness(css, "a");
        h.Resolve(0);
        h.Tick(0.5);
        Assert.Equal("50px", h.Text("margin-left"));

        // 未注册的自定义属性离散切换
        var h2 = new AnimationHarness("@keyframes cy { from { --y: 0px } to { --y: 100px } } .a { margin-left: var(--y, 7px); animation: cy 1s linear; }", "a");
        h2.Resolve(0);
        h2.Tick(0.25);
        Assert.Equal("0px", h2.Text("margin-left"));
        h2.Tick(0.75);
        Assert.Equal("100px", h2.Text("margin-left"));
    }

    [Fact]
    public void Discrete_properties_in_keyframes_flip_at_half()
    {
        var h = new AnimationHarness("@keyframes d { from { visibility: hidden; position: static } to { visibility: hidden; position: absolute } } .a { animation: d 1s linear; }", "a");
        h.Resolve(0);
        h.Tick(0.4);
        Assert.Equal("static", h.Text("position"));
        h.Tick(0.6);
        Assert.Equal("absolute", h.Text("position"));
        Assert.Equal("hidden", h.Text("visibility"));
    }

    [Fact]
    public void Zero_duration_animation_starts_and_ends_immediately()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 0s forwards; }", "a");
        h.Resolve(0);
        Assert.Equal("1", h.Text("opacity"));
        Assert.Equal("start:fade@0 end:fade@0", h.EventLog());
    }

    [Fact]
    public void Instant_mode_finishes_finite_animations_and_freezes_infinite_ones()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { opacity: 0.7; animation: fade 1s linear 0.5s; }", "a");
        h.Animator.Instant = true;
        h.Resolve(0);
        Assert.Equal("0.7", h.Text("opacity"));
        Assert.Equal("start:fade@0 end:fade@1", h.EventLog());

        var h2 = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear infinite; }", "a");
        h2.Animator.Instant = true;
        h2.Resolve(0);
        Assert.Equal("0", h2.Text("opacity"));
        Assert.Equal("start:fade@0", h2.EventLog());
        Assert.Equal(double.PositiveInfinity, h2.Animator.NextUpdateTime(h2.State!, 0));
    }

    [Fact]
    public void Display_none_cancels_animations()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear; } .a.gone { display: none; }", "a");
        h.Resolve(0);
        h.Tick(0.3);
        h.SetClasses("a gone", 0.3);
        Assert.Equal("start:fade@0 cancel:fade@0.3", h.EventLog());
        Assert.Null(h.State);
        // 再次显示：重新开始
        h.SetClasses("a", 1);
        Assert.Equal("0", h.Text("opacity"));
        Assert.EndsWith("start:fade@0", h.EventLog());
    }

    [Fact]
    public void Transitions_do_not_start_for_properties_driven_by_animations()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { transition: opacity 1s; animation: fade 1s linear infinite; }", "a");
        h.Resolve(0);
        h.Tick(0.5);
        h.SetClasses("a", 0.5);
        Assert.Equal(0, h.State!.TransitionCount);
        Assert.Equal("0.5", h.Text("opacity"));
    }

    [Fact]
    public void Next_update_time_for_delayed_animations()
    {
        var h = new AnimationHarness(FadeKeyframes + " .a { animation: fade 1s linear 2s; }", "a");
        h.Resolve(10);
        Assert.Equal(12, h.Animator.NextUpdateTime(h.State!, 10), 9);
        h.Tick(12.5);
        Assert.Equal(12.5, h.Animator.NextUpdateTime(h.State!, 12.5), 9);
    }
}
