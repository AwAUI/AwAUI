using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// CSS 过渡与 <c>@keyframes</c> 动画在帧管线中的接入：<see cref="ManualUIClock"/> 驱动的确定性取值与像素、帧请求、
/// 事件派发与顺序、失效范围（只重绘 / 重新布局）、没有时钟时的瞬时语义、移除与 display:none。
/// </summary>
public class AnimationPipelineTests
{
    const string Css = """
        .box { width: 100px; height: 50px; background-color: rgb(255, 0, 0); opacity: 1; transition: opacity 1s linear; }
        .box.off { opacity: 0; }
        @keyframes grow { from { width: 0px } to { width: 100px } }
        @keyframes fade { from { opacity: 0 } to { opacity: 1 } }
        .grow { height: 20px; background-color: rgb(0, 0, 255); animation: grow 1s linear; }
        .fade { width: 40px; height: 40px; background-color: rgb(0, 0, 0); animation: fade 1s linear 0.5s backwards; }
        .gone { display: none; }
        """;

    sealed class Scene
    {
        public readonly ManualUIClock Clock = new();
        public readonly PipelineHarness H;
        public readonly State<string> Classes;
        public readonly List<string> Log = new();

        public Scene(string classes, string css = Css, bool clock = true, string? parentClass = null)
        {
            Classes = new State<string>(classes);
            H = new PipelineHarness(new Lambda
            {
                Render = () => new Div
                {
                    Id = "parent",
                    Class = parentClass ?? "",
                    OnAnimationStart = e => Log.Add("parent:" + e.Type),
                    Children =
                    {
                        new Div
                        {
                            Id = "t",
                            Class = Classes.Value,
                            OnTransitionRun = e => Log.Add($"{e.Type}:{e.PropertyName}@{e.ElapsedTime:0.###}"),
                            OnTransitionStart = e => Log.Add($"{e.Type}:{e.PropertyName}@{e.ElapsedTime:0.###}"),
                            OnTransitionEnd = e => Log.Add($"{e.Type}:{e.PropertyName}@{e.ElapsedTime:0.###}"),
                            OnTransitionCancel = e => Log.Add($"{e.Type}:{e.PropertyName}@{e.ElapsedTime:0.###}"),
                            OnAnimationStart = e => Log.Add($"{e.Type}:{e.AnimationName}@{e.ElapsedTime:0.###}"),
                            OnAnimationIteration = e => Log.Add($"{e.Type}:{e.AnimationName}@{e.ElapsedTime:0.###}"),
                            OnAnimationEnd = e => Log.Add($"{e.Type}:{e.AnimationName}@{e.ElapsedTime:0.###}"),
                            OnAnimationCancel = e => Log.Add($"{e.Type}:{e.AnimationName}@{e.ElapsedTime:0.###}"),
                        },
                    },
                },
            }, css, deferMount: true);
            if (clock) H.Root.Clock = Clock;
            H.MountNow();
            H.Frame();
        }

        public Element T => H.El("t");

        public ComputedStyle Style => T.ComputedStyle!;

        public void Advance(double seconds)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            H.Root.RunFrame();
        }
    }

    [Fact]
    public void Transition_values_and_pixels_at_fixed_times()
    {
        var s = new Scene("box");
        Assert.False(s.H.Root.NeedsFrame);
        s.Classes.Value = "box off";
        s.H.Root.RunFrame();
        Assert.Equal(1f, s.Style.Opacity);
        Assert.True(s.H.Root.NeedsFrame);
        s.Advance(0.25);
        Assert.Equal(0.75f, s.Style.Opacity, 4);
        s.Advance(0.25);
        Assert.Equal(0.5f, s.Style.Opacity, 4);
        using (var bmp = s.H.Render())
        {
            // 红色 50% 不透明度叠在白色上
            ColorAssert.Near(new Color(1, 0.5f, 0.5f), bmp.GetPixel(10, 10), 0.01f);
        }
        s.Advance(0.5);
        Assert.Equal(0f, s.Style.Opacity);
        Assert.False(s.H.Root.NeedsFrame);
        Assert.Equal(0, s.H.Pipeline.AnimatedElementCount);
        Assert.Equal(new[] { "transitionrun:opacity@0", "transitionstart:opacity@0", "transitionend:opacity@1" }, s.Log);
    }

    [Fact]
    public void Opacity_animation_repaints_without_relayout()
    {
        var s = new Scene("box");
        s.Classes.Value = "box off";
        s.H.Root.RunFrame();
        s.Advance(0.1); // 不透明度第一次低于 1，建立层叠上下文。
        Assert.Contains(s.T, s.H.Pipeline.LastRecordedElements);
        s.Advance(0.1);
        Assert.True(s.H.Pipeline.LastTimings.Repainted);
        Assert.False(s.H.Pipeline.LastTimings.LayoutRan);
        Assert.Equal(0, s.H.Pipeline.LastTimings.StyledElements); // 只推进时间：不重新级联
        Assert.DoesNotContain(s.T, s.H.Pipeline.LastRecordedElements); // 只重新合成，复用内容列表。
    }

    [Fact]
    public void Width_animation_relayouts_each_frame()
    {
        var s = new Scene("grow");
        Assert.Equal(0, s.T.BoundingRect.Width);
        s.Advance(0.5);
        Assert.True(s.H.Pipeline.LastTimings.LayoutRan);
        Assert.Equal(50, s.T.BoundingRect.Width, 3);
        s.Advance(0.5);
        Assert.Equal("animationstart:grow@0 animationend:grow@1", string.Join(" ", s.Log.Where(l => !l.StartsWith("parent"))));
        // 动画事件冒泡
        Assert.Contains("parent:animationstart", s.Log);
    }

    [Fact]
    public void Delay_phase_uses_a_clock_timer_instead_of_frames()
    {
        var s = new Scene("fade");
        // backwards 填充：延迟期间显示第一帧
        Assert.Equal(0f, s.Style.Opacity);
        Assert.False(s.H.Root.NeedsFrame);
        Assert.Equal(1, s.Clock.PendingCount);
        Assert.Equal(TimeSpan.FromSeconds(0.5), s.Clock.NextDue);
        int requests = s.H.FrameRequests;
        s.Clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.True(s.H.FrameRequests > requests); // 定时器唤醒请求了一帧
        s.H.Root.RunFrame();
        Assert.Contains("animationstart:fade@0", s.Log);
        s.Advance(0.5);
        Assert.Equal(0.5f, s.Style.Opacity, 4);
    }

    [Fact]
    public void Reversal_cancels_and_shortens()
    {
        var s = new Scene("box");
        s.Classes.Value = "box off";
        s.H.Root.RunFrame();
        s.Advance(0.25);
        s.Classes.Value = "box";
        s.H.Root.RunFrame();
        s.Advance(0.125);
        Assert.Equal(0.875f, s.Style.Opacity, 4);
        s.Advance(0.125);
        Assert.Equal(1f, s.Style.Opacity);
        Assert.Equal(new[]
        {
            "transitionrun:opacity@0", "transitionstart:opacity@0", "transitioncancel:opacity@0.25",
            "transitionrun:opacity@0", "transitionstart:opacity@0", "transitionend:opacity@0.25",
        }, s.Log);
    }

    [Fact]
    public void Ancestor_display_none_cancels_running_animations()
    {
        var s = new Scene("grow");
        s.Advance(0.25);
        s.H.El("parent").SetStyleOverride("display", "none");
        s.H.Root.RunFrame();
        // 子元素在祖先隐藏的同一帧取消，不需要额外的时钟帧。
        Assert.Contains("animationcancel:grow@0.25", s.Log);
        Assert.Equal(0, s.H.Pipeline.AnimatedElementCount);
        Assert.False(s.H.Root.NeedsFrame);
        s.H.El("parent").SetStyleOverride("display", null);
        s.H.Root.RunFrame();
        Assert.Equal(0, s.T.BoundingRect.Width);
        Assert.Equal(2, s.Log.Count(e => e == "animationstart:grow@0"));
    }

    [Fact]
    public void Removed_elements_drop_their_animations()
    {
        var s = new Scene("grow");
        s.Advance(0.25);
        Assert.Equal(1, s.H.Pipeline.AnimatedElementCount);
        s.Classes.Value = "grow gone";
        s.H.Root.RunFrame();
        Assert.Equal(0, s.H.Pipeline.AnimatedElementCount);
        Assert.Contains("animationcancel:grow@0.25", s.Log);
    }

    [Fact]
    public void Without_a_clock_finite_animations_finish_in_the_same_frame()
    {
        var s = new Scene("grow", clock: false);
        Assert.Equal(new[] { "animationstart:grow@0", "parent:animationstart", "animationend:grow@1" }, s.Log);
        Assert.Equal(400, s.T.BoundingRect.Width, 3); // 结束且不填充：基础值（auto，撑满视口）
        Assert.False(s.H.Root.NeedsFrame);

        var t = new Scene("box", clock: false);
        t.Classes.Value = "box off";
        t.H.Frame();
        Assert.Equal(0f, t.Style.Opacity); // 过渡不运行
        Assert.Empty(t.Log);
    }

    [Fact]
    public void Exit_animation_then_unmount_on_animation_end()
    {
        // Presence 的用法：data-state=closed 时播放退出动画，animationend 后才卸载
        const string css = """
            @keyframes out { from { opacity: 1 } to { opacity: 0 } }
            .panel { width: 50px; height: 50px; background-color: rgb(0, 128, 0); }
            .panel[data-state=closed] { animation: out 0.2s linear forwards; }
            """;
        var clock = new ManualUIClock();
        var open = new State<bool>(true);
        var mounted = new State<bool>(true);
        var log = new List<string>();
        var h = new PipelineHarness(new Lambda
        {
            Render = () => mounted.Value
                ? new Div
                {
                    Id = "p",
                    Class = "panel",
                    Attributes = { ["data-state"] = open.Value ? "open" : "closed" },
                    OnAnimationEnd = e =>
                    {
                        log.Add("end:" + e.AnimationName);
                        mounted.Value = false;
                    },
                }
                : new Fragment(),
        }, css, deferMount: true);
        h.Root.Clock = clock;
        h.MountNow();
        h.Frame();
        open.Value = false;
        h.Root.RunFrame();
        Assert.Equal(1f, h.El("p").ComputedStyle!.Opacity);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        h.Root.RunFrame();
        Assert.Equal(0.5f, h.El("p").ComputedStyle!.Opacity, 4);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        h.Root.RunFrame();
        Assert.Equal(new[] { "end:out" }, log);
        h.Root.RunFrame();
        Assert.Throws<InvalidOperationException>(() => h.El("p"));
        Assert.Equal(0, h.Pipeline.AnimatedElementCount);
    }

    [Fact]
    public void Starting_style_transition_on_mount()
    {
        const string css = """
            .pop { width: 30px; height: 30px; background-color: rgb(0, 0, 0); opacity: 1; transition: opacity 1s linear; }
            @starting-style { .pop { opacity: 0; } }
            """;
        var s = new Scene("pop", css);
        Assert.Equal(0f, s.Style.Opacity);
        s.Advance(0.5);
        Assert.Equal(0.5f, s.Style.Opacity, 4);
    }

    [Fact]
    public void Inherited_property_animation_reaches_text_children()
    {
        const string css = """
            @keyframes c { from { color: rgb(0, 0, 0) } to { color: rgb(0, 0, 255) } }
            .ink { animation: c 1s linear; }
            """;
        var clock = new ManualUIClock();
        var h = new PipelineHarness(new Lambda { Render = () => new Div { Id = "d", Class = "ink", Children = { new Text("hello") { Id = "txt" } } } }, css, deferMount: true);
        h.Root.Clock = clock;
        h.MountNow();
        h.Frame();
        clock.Advance(TimeSpan.FromSeconds(1.0 / 3));
        h.Root.RunFrame();
        var parent = h.El("d").ComputedStyle!.Color.Value;
        var child = h.El("txt").ComputedStyle!.Color.Value;
        Assert.Equal(parent, child);
        Assert.True(parent.B > 0.1f && parent.B < 0.9f);
    }

    [Fact]
    public void Reduced_motion_media_query_switches_animations_off()
    {
        const string css = """
            @keyframes grow { from { width: 0px } to { width: 100px } }
            .g { height: 10px; width: 100px; animation: grow 1s linear; }
            @media (prefers-reduced-motion: reduce) { .g { animation: none; } }
            """;
        var s = new Scene("g", css);
        Assert.Equal(1, s.H.Pipeline.AnimatedElementCount);
        var media = s.H.Pipeline.StyleEngine.Environment.Media;
        media.PrefersReducedMotion = true;
        s.H.Pipeline.StyleEngine.EnvironmentChanged();
        s.H.Pipeline.InvalidateAll();
        s.H.Root.RunFrame();
        Assert.Equal(0, s.H.Pipeline.AnimatedElementCount);
        Assert.Equal(100, s.T.BoundingRect.Width, 3);
        Assert.Contains("animationcancel:grow@0", s.Log);
    }

    [Fact]
    public void Animation_values_are_reflected_in_computed_style_text()
    {
        var s = new Scene("grow");
        s.Advance(0.3);
        Assert.Equal("30px", s.Style.GetPropertyValueText("width"));
        Assert.NotNull(s.H.Pipeline.GetAnimationState(s.T));
    }
}
