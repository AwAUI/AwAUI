using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>
/// <see cref="TransitioningContentControl"/>：键变化时旧内容继续挂载（退出动画层）直到 <c>TransitionDuration</c>，
/// 之后移除；没有时钟、关闭过渡或时长为零时立即移除。
/// </summary>
public class TransitioningContentTests
{
    static UIHarness Harness(State<int> key, bool enabled = true, TimeSpan? duration = null) => new(() =>
    {
        var control = new TransitioningContentControl
        {
            ContentKey = key.Value,
            Content = new Div { Id = "page-" + key.Value },
            IsTransitionEnabled = enabled,
        };
        if (duration is { } d) control.TransitionDuration = d;
        return control;
    });

    [Fact]
    public void Key_change_keeps_the_outgoing_content_until_the_duration_elapses()
    {
        var key = new State<int>(0);
        var h = Harness(key);
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        Assert.NotNull(h.TryById("page-0"));

        key.Value = 1;
        h.Frame();

        var outgoing = h.ById("page-0").Parent!;
        Assert.NotNull(outgoing.GetAttribute("data-exiting"));
        Assert.Equal("true", outgoing.GetAttribute("aria-hidden"));
        Assert.NotNull(h.ById("page-1").Parent!.GetAttribute("data-entering"));
        Assert.Equal(2, h.FindAll(e => e.GetAttribute("part") == "content").Count);

        clock.Advance(TimeSpan.FromMilliseconds(149));
        h.Frame();
        Assert.NotNull(h.TryById("page-0"));

        clock.Advance(TimeSpan.FromMilliseconds(2));
        h.Frame();
        Assert.Null(h.TryById("page-0"));
        Assert.NotNull(h.TryById("page-1"));
        Assert.Single(h.FindAll(e => e.GetAttribute("part") == "content"));
    }

    [Fact]
    public void Outgoing_content_is_inert_and_not_tabbable()
    {
        var key = new State<int>(0);
        var h = new UIHarness(() => new TransitioningContentControl
        {
            ContentKey = key.Value,
            Content = key.Value,
            ContentTemplate = v => new Div
            {
                Id = "page-" + v,
                Children = [new Button { Id = "button-" + v, Children = [new UIText("Go")] }],
            },
        });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;

        var oldButton = h.ById("button-0");
        Assert.True(oldButton.IsFocusable);
        Assert.True(oldButton.IsTabStop);

        key.Value = 1;
        h.Frame();

        var outgoing = h.FindAll(e => e.GetAttribute("data-exiting") is not null).Single();
        Assert.True(outgoing.IsInclusiveAncestorOf(oldButton));
        Assert.NotNull(outgoing.GetAttribute("inert"));
        Assert.False(oldButton.IsFocusable);
        Assert.False(oldButton.IsTabStop);
        Assert.True(h.ById("button-1").IsFocusable);

        clock.Advance(TimeSpan.FromMilliseconds(151));
        h.Frame();
        Assert.Null(h.TryById("button-0"));
    }

    [Fact]
    public void Without_a_clock_the_outgoing_content_is_removed_immediately()
    {
        var key = new State<int>(0);
        var h = Harness(key);
        Assert.NotNull(h.TryById("page-0"));

        key.Value = 1;
        h.Frame();

        Assert.Null(h.TryById("page-0"));
        Assert.NotNull(h.TryById("page-1"));
    }

    [Fact]
    public void A_zero_duration_or_disabled_transition_removes_the_outgoing_content_immediately()
    {
        var key = new State<int>(0);
        var h = Harness(key, duration: TimeSpan.Zero);
        h.Root.Clock = new ManualUIClock();
        key.Value = 1;
        h.Frame();
        Assert.Null(h.TryById("page-0"));

        key.Value = 2;
        var disabled = Harness(key, enabled: false);
        disabled.Root.Clock = new ManualUIClock();
        key.Value = 3;
        disabled.Frame();
        Assert.Null(disabled.TryById("page-2"));
        Assert.NotNull(disabled.TryById("page-3"));
    }

    [Fact]
    public void A_second_key_change_replaces_the_pending_outgoing_content()
    {
        var key = new State<int>(0);
        var h = Harness(key);
        var clock = new ManualUIClock();
        h.Root.Clock = clock;

        key.Value = 1;
        h.Frame();
        Assert.NotNull(h.TryById("page-0"));

        key.Value = 2;
        h.Frame();
        // 只保留最近一次退出层：page-0 已完成退出，page-1 现在是退出层
        Assert.Null(h.TryById("page-0"));
        Assert.NotNull(h.ById("page-1").Parent!.GetAttribute("data-exiting"));
        Assert.NotNull(h.TryById("page-2"));
        Assert.Equal(2, h.FindAll(e => e.GetAttribute("part") == "content").Count);

        clock.Advance(TimeSpan.FromMilliseconds(151));
        h.Frame();
        Assert.Null(h.TryById("page-1"));
        Assert.NotNull(h.TryById("page-2"));
        Assert.Single(h.FindAll(e => e.GetAttribute("part") == "content"));
    }
}
