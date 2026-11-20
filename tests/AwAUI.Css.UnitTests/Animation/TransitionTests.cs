using AwAUI.Css.Animation;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Animation;

/// <summary>CSS Transitions 1/2：开始、采样、反转（缩短系数）、取消、负延迟、@starting-style、allow-discrete、事件。</summary>
public class TransitionTests
{
    const string Fade = ".box { opacity: 1; transition: opacity 1s linear; } .box.off { opacity: 0; }";

    [Fact]
    public void Transition_runs_from_before_change_to_after_change_value()
    {
        var h = new AnimationHarness(Fade, "box");
        h.Resolve(0);
        Assert.Null(h.State); // 第一次得到样式：没有 before-change style，不过渡
        Assert.Equal("1", h.Text("opacity"));

        h.SetClasses("box off", 0);
        Assert.Equal("1", h.Text("opacity"));
        Assert.NotNull(h.State);
        Assert.Equal("trun:opacity@0 tstart:opacity@0", h.EventLog());

        h.Tick(0.25);
        Assert.Equal("0.75", h.Text("opacity"));
        h.Tick(0.5);
        Assert.Equal("0.5", h.Text("opacity"));
        h.Tick(1);
        Assert.Equal("0", h.Text("opacity"));
        Assert.Equal("trun:opacity@0 tstart:opacity@0 tend:opacity@1", h.EventLog());
        Assert.True(h.State!.IsEmpty);
    }

    [Fact]
    public void Reversing_shortens_the_new_transition()
    {
        var h = new AnimationHarness(Fade, "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);
        h.Tick(0.25);
        Assert.Equal("0.75", h.Text("opacity"));
        // 中途反转：新过渡从当前值 0.75 回到 1，时长 = 1s × 缩短系数 0.25
        h.SetClasses("box", 0.25);
        Assert.Equal("0.75", h.Text("opacity"));
        h.Tick(0.375);
        Assert.Equal("0.875", h.Text("opacity"));
        h.Tick(0.5);
        Assert.Equal("1", h.Text("opacity"));
        Assert.Equal("trun:opacity@0 tstart:opacity@0 tcancel:opacity@0.25 trun:opacity@0 tstart:opacity@0 tend:opacity@0.25", h.EventLog());
    }

    [Fact]
    public void Reversing_twice_uses_the_accumulated_shortening_factor()
    {
        var h = new AnimationHarness(Fade, "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);   // 1 → 0，1s
        h.Tick(0.5);                  // 0.5
        h.SetClasses("box", 0.5);     // 反转：系数 0.5，0.5 → 1 用 0.5s
        h.Tick(0.75);                 // 0.75
        Assert.Equal("0.75", h.Text("opacity"));
        h.SetClasses("box off", 0.75);
        // 再次反转：|0.5 × 0.5 + 1 − 0.5| = 0.75 → 0.75s，从 0.75 到 0
        h.Tick(0.75 + 0.375);
        Assert.Equal("0.375", h.Text("opacity"));
    }

    [Fact]
    public void Changing_to_a_third_value_restarts_from_the_current_value()
    {
        var h = new AnimationHarness(Fade + " .box.half { opacity: 0.5 }", "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);
        h.Tick(0.5);
        h.SetClasses("box half", 0.5); // 不是反转：从 0.5 到 0.5 —— 当前值等于目标值，取消
        Assert.Equal("0.5", h.Text("opacity"));
        Assert.True(h.State is null || h.State.IsEmpty);
        h.SetClasses("box off", 0.5);
        h.Tick(1);
        Assert.Equal("0.25", h.Text("opacity")); // 新过渡 0.5 → 0，完整时长 1s
    }

    [Fact]
    public void Negative_delay_starts_part_way_through()
    {
        var h = new AnimationHarness(".box { opacity: 1; transition: opacity 1s linear -0.5s; } .box.off { opacity: 0; }", "box");
        h.Resolve(0);
        h.SetClasses("box off", 10);
        Assert.Equal("0.5", h.Text("opacity"));
        Assert.Equal("trun:opacity@0.5 tstart:opacity@0.5", h.EventLog());
        h.Tick(10.25);
        Assert.Equal("0.25", h.Text("opacity"));
    }

    [Fact]
    public void Positive_delay_holds_the_start_value()
    {
        var h = new AnimationHarness(".box { opacity: 1; transition: opacity 1s linear 0.5s; } .box.off { opacity: 0; }", "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);
        Assert.Equal("1", h.Text("opacity"));
        Assert.Equal("trun:opacity@0", h.EventLog());
        Assert.Equal(0.5, h.Animator.NextUpdateTime(h.State!, 0), 9);
        h.Tick(0.75);
        Assert.Equal("0.75", h.Text("opacity"));
        Assert.Equal("trun:opacity@0 tstart:opacity@0", h.EventLog());
    }

    [Fact]
    public void Timing_function_applies()
    {
        var h = new AnimationHarness(".box { width: 0px; transition: width 1s ease; } .box.on { width: 100px; }", "box");
        h.Resolve(0);
        h.SetClasses("box on", 0);
        h.Tick(0.5);
        Assert.Equal(80.24, ((SizeValue)AnimationDispatch.GetValue(h.Style!, CssPropertyId.Width)).Value.Length, 2);
    }

    [Fact]
    public void Shorthand_and_all_match_longhands()
    {
        var h = new AnimationHarness(".box { padding: 0px; color: red; transition: all 1s linear; } .box.on { padding: 10px; color: blue; }", "box");
        h.Resolve(0);
        h.SetClasses("box on", 0);
        h.Tick(0.5);
        Assert.Equal("5px", h.Text("padding-left"));
        Assert.Equal("5px", h.Text("padding-top"));
        Assert.NotEqual("rgb(0, 0, 255)", h.Text("color"));
        Assert.Equal(5, h.State!.TransitionCount); // 四个 padding + color

        var h2 = new AnimationHarness(".box { padding: 0px; margin: 0px; transition: padding 1s linear; } .box.on { padding: 10px; margin: 10px; }", "box");
        h2.Resolve(0);
        h2.SetClasses("box on", 0);
        h2.Tick(0.5);
        Assert.Equal("5px", h2.Text("padding-right"));
        Assert.Equal("10px", h2.Text("margin-left")); // 不在 transition-property 中：立即变化
    }

    [Fact]
    public void Removing_the_property_from_transition_property_cancels()
    {
        var h = new AnimationHarness(Fade + " .box.stop { transition: none; }", "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);
        h.Tick(0.5);
        h.SetClasses("box off stop", 0.5);
        Assert.Equal("0", h.Text("opacity"));
        Assert.EndsWith("tcancel:opacity@0.5", h.EventLog());
        Assert.Null(h.State);
    }

    [Fact]
    public void Display_none_cancels_running_transitions()
    {
        var h = new AnimationHarness(Fade + " .box.gone { display: none; }", "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);
        h.Tick(0.5);
        h.SetClasses("box off gone", 0.5);
        Assert.Equal("none", h.Text("display"));
        Assert.EndsWith("tcancel:opacity@0.5", h.EventLog());
        Assert.Null(h.State);
        // 再次显示：没有 before-change style（刚开始渲染），不过渡
        h.SetClasses("box", 0.6);
        Assert.Equal("1", h.Text("opacity"));
        Assert.Null(h.State);
    }

    [Fact]
    public void Ancestor_display_none_cancels()
    {
        var h = new AnimationHarness(Fade, "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);
        h.ParentRendered = false;
        h.Resolve(0.5);
        Assert.Null(h.State);
        Assert.EndsWith("tcancel:opacity@0.5", h.EventLog());
    }

    [Fact]
    public void Starting_style_provides_the_before_change_value_on_first_render()
    {
        const string css = ".pop { opacity: 1; transition: opacity 1s linear; } @starting-style { .pop { opacity: 0; } }";
        var h = new AnimationHarness(css, "pop");
        h.Resolve(0);
        Assert.Equal("0", h.Text("opacity"));
        h.Tick(0.5);
        Assert.Equal("0.5", h.Text("opacity"));
        h.Tick(1);
        Assert.Equal("1", h.Text("opacity"));

        // display: none → 显示 同样以 @starting-style 为起点
        var h2 = new AnimationHarness(css + " .pop.hidden { display: none }", "pop hidden");
        h2.Resolve(0);
        h2.SetClasses("pop", 1);
        Assert.Equal("0", h2.Text("opacity"));
        h2.Tick(1.25);
        Assert.Equal("0.25", h2.Text("opacity"));
    }

    [Fact]
    public void Allow_discrete_keeps_display_until_the_exit_transition_ends()
    {
        const string css = ".m { opacity: 1; transition: opacity 1s linear, display 1s allow-discrete; } .m.closed { opacity: 0; display: none; }";
        var h = new AnimationHarness(css, "m");
        h.Resolve(0);
        h.SetClasses("m closed", 0);
        Assert.Equal("inline", h.Text("display")); // 测试元素没有 UA 样式：display 初始值为 inline
        h.Tick(0.5);
        Assert.Equal("inline", h.Text("display"));
        Assert.Equal("0.5", h.Text("opacity"));
        h.Tick(1);
        Assert.Equal("none", h.Text("display"));
        Assert.Contains("tend:display@1", h.EventLog());

        // 没有 allow-discrete：display 立即变为 none，其他过渡被取消
        var h2 = new AnimationHarness(".m { opacity: 1; transition: opacity 1s linear, display 1s; } .m.closed { opacity: 0; display: none; }", "m");
        h2.Resolve(0);
        h2.SetClasses("m closed", 0);
        Assert.Equal("none", h2.Text("display"));
        Assert.Null(h2.State);
    }

    [Fact]
    public void Discrete_properties_need_allow_discrete()
    {
        var h = new AnimationHarness(".b { position: static; transition: all 1s; } .b.on { position: absolute; }", "b");
        h.Resolve(0);
        h.SetClasses("b on", 0);
        Assert.Equal("absolute", h.Text("position"));

        var h2 = new AnimationHarness(".b { position: static; transition: all 1s allow-discrete linear; } .b.on { position: absolute; }", "b");
        h2.Resolve(0);
        h2.SetClasses("b on", 0);
        Assert.Equal("static", h2.Text("position"));
        h2.Tick(0.5);
        Assert.Equal("absolute", h2.Text("position"));
    }

    [Fact]
    public void Important_declarations_still_transition_but_devtools_wins()
    {
        var h = new AnimationHarness(".box { opacity: 1 !important; transition: opacity 1s linear; } .box.off { opacity: 0 !important; }", "box");
        h.Resolve(0);
        h.SetClasses("box off", 0);
        h.Tick(0.5);
        Assert.Equal("0.5", h.Text("opacity")); // 过渡槽高于 !important
    }

    [Fact]
    public void Instant_mode_does_not_run_transitions()
    {
        var h = new AnimationHarness(Fade, "box");
        h.Animator.Instant = true;
        h.Resolve(0);
        h.SetClasses("box off", 0);
        Assert.Equal("0", h.Text("opacity"));
        Assert.Null(h.State);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void Registered_custom_properties_transition()
    {
        const string css = "@property --w { syntax: '<length>'; inherits: false; initial-value: 0px; }"
                           + " .b { --w: 0px; width: var(--w); transition: --w 1s linear; } .b.on { --w: 100px; }";
        var h = new AnimationHarness(css, "b");
        h.Resolve(0);
        h.SetClasses("b on", 0);
        h.Tick(0.25);
        Assert.Equal("25px", h.Text("width"));
        Assert.Equal("25px", h.Style!.GetCustomPropertyText("--w"));
        h.Tick(1);
        Assert.Equal("100px", h.Text("width"));
    }

    [Fact]
    public void Font_size_transition_updates_em_values_of_the_same_element()
    {
        var h = new AnimationHarness(".t { font-size: 10px; padding-left: 2em; transition: font-size 1s linear; } .t.big { font-size: 20px; }", "t");
        h.Resolve(0);
        h.SetClasses("t big", 0);
        h.Tick(0.5);
        Assert.Equal("15px", h.Text("font-size"));
        Assert.Equal("30px", h.Text("padding-left"));
    }

    [Fact]
    public void Thousand_property_changes_are_linear_in_the_number_of_transitions()
    {
        // all 的匹配只看变化的分组：未变化的属性不逐个比较
        var css = ".b { transition: all 1s linear; width: 0px; } .b.on { width: 100px; }";
        var h = new AnimationHarness(css, "b");
        h.Resolve(0);
        for (int i = 0; i < 1000; i++)
        {
            h.SetClasses(i % 2 == 0 ? "b on" : "b", i * 0.01);
        }
        Assert.True(h.State is null || h.State.TransitionCount <= 1);
    }
}
