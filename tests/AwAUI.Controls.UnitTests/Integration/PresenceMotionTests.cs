using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls.UnitTests.Integration;

public class PresenceMotionTests
{
    const string Keyframes = """
        @keyframes fade { from { opacity: 1 } to { opacity: 0 } }
        @keyframes shrink { from { transform: scale(1) } to { transform: scale(.8) } }
        """;

    static void Advance(UIHarness h, ManualUIClock clock, int milliseconds)
    {
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        h.Root.RunFrame();
    }

    [Theory]
    [InlineData(100)]
    [InlineData(230)] // Jump past the fallback deadline; the runtime can still deliver the last end event.
    public void Gate_waits_for_all_close_animations_on_its_root(int finalStep)
    {
        var present = new State<bool>(false);
        var endedOn = new List<string?>();
        var h = new UIHarness(() => new PresenceGate
        {
            Present = present.Value,
            Children = [new Div
            {
                Id = "gate",
                Attributes = { ["data-state"] = present.Value ? "open" : "closed" },
                OnAnimationEnd = e => endedOn.Add(e.Target?.Id),
                Children = [new Div { Id = "descendant" }],
            }],
        }, Keyframes + """
            #gate[data-state=closed] { animation: fade 100ms linear both, shrink 150ms linear 50ms both; }
            #gate[data-state=closed] #descendant { animation: fade 20ms linear; }
            """);
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        present.Value = true;
        h.Root.RunFrame();
        var target = h.ById("gate");
        var gate = Assert.IsType<PresenceGate>(target.Owner);
        present.Value = false;
        h.Root.RunFrame();

        Assert.Equal(PresenceState.UnmountSuspended, gate.State);
        Assert.Equal("closed", target.GetAttribute("data-state"));
        Advance(h, clock, 50);
        Assert.Equal(new[] { "descendant" }, endedOn);
        Assert.Equal(0.5f, target.ComputedStyle!.Opacity, 4);
        Advance(h, clock, 60);
        Assert.Equal(new[] { "descendant", "gate" }, endedOn);
        Assert.Same(target, h.ById("gate"));
        Assert.Equal(PresenceState.UnmountSuspended, gate.State);
        Advance(h, clock, finalStep);
        h.Root.RunFrame();
        Assert.Equal(new[] { "descendant", "gate", "gate" }, endedOn);
        Assert.Null(h.TryById("gate"));
        Assert.Equal(PresenceState.Unmounted, gate.State);
        Assert.Equal(0, clock.PendingCount);
    }

    [Theory]
    [InlineData(false, "#gate.closed { animation: fade 1s linear; }")]
    [InlineData(true, "#gate { transition: opacity 1s; }")]
    [InlineData(true, "#gate.closed { animation: missing 1s linear; }")]
    [InlineData(true, "#gate.closed { animation: fade 1s linear paused; }")]
    [InlineData(true, "#gate.closed { animation: fade 1s linear infinite; }")]
    [InlineData(true, "#gate.closed { animation: fade 0s linear; }")]
    [InlineData(true, "#gate { animation: fade 1s linear; }")]
    [InlineData(true, "#gate.closed #descendant { animation: fade 1s linear; }")]
    [InlineData(true, "#gate.closed { display: none; animation: fade 1s linear; }")]
    public void Gate_skips_wait_when_no_finite_exit_motion_starts_on_its_root(bool withClock, string css)
    {
        var present = new State<bool>(false);
        var h = new UIHarness(() => new PresenceGate
        {
            Present = present.Value,
            Children = [new Div { Id = "gate", Class = present.Value ? "open" : "closed", Children = [new Div { Id = "descendant" }] }],
        }, Keyframes + css);
        var clock = withClock ? new ManualUIClock() : null;
        h.Root.Clock = clock;
        present.Value = true;
        h.Root.RunFrame();
        var gate = Assert.IsType<PresenceGate>(h.ById("gate").Owner);

        present.Value = false;
        h.Root.RunFrame();
        Assert.Equal(PresenceState.Unmounted, gate.State);
        if (withClock) h.Root.RunFrame();
        Assert.Null(h.TryById("gate"));
        Assert.False(h.Root.NeedsFrame);
        Assert.Equal(0, clock?.PendingCount ?? 0);
    }

    [Fact]
    public void Gate_waits_for_new_transitions_without_waiting_for_an_unrelated_running_transition()
    {
        var phase = new State<int>(0);
        var endedProperties = new List<string>();
        var h = new UIHarness(() => new PresenceGate
        {
            Present = phase.Value is 1 or 2,
            Children = [new Div
            {
                Id = "gate",
                Class = phase.Value switch { 2 => "wide", 3 => "wide closed", _ => "" },
                OnTransitionEnd = e => endedProperties.Add(e.PropertyName),
            }],
        }, """
            #gate { width: 50px; transition: width 1s linear, opacity 100ms linear, transform 200ms linear; }
            #gate.wide { width: 100px; }
            #gate.closed { opacity: 0; transform: scale(.8); }
            """);
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        phase.Value = 1;
        h.Root.RunFrame();
        var target = h.ById("gate");
        phase.Value = 2;
        h.Root.RunFrame();
        Advance(h, clock, 20);
        Assert.Equal(1, h.Pipeline.GetAnimationState(target)!.TransitionCount);

        phase.Value = 3;
        h.Root.RunFrame();
        Assert.Equal(3, h.Pipeline.GetAnimationState(target)!.TransitionCount);
        Advance(h, clock, 110);
        Assert.Equal(new[] { "opacity" }, endedProperties);
        Assert.True(target.IsAttached);
        Advance(h, clock, 110);
        h.Root.RunFrame();
        Assert.Equal(new[] { "opacity", "transform" }, endedProperties);
        Assert.False(target.IsAttached);
        Assert.Equal(0, clock.PendingCount);
    }

    [Fact]
    public void Force_mount_keeps_closed_content_without_scheduling_a_presence_deadline()
    {
        var present = new State<bool>(true);
        var h = new UIHarness(() => new PresenceGate
        {
            Present = present.Value,
            ForceMount = true,
            Children = [new Div { Id = "gate", Class = present.Value ? "open" : "closed" }],
        }, Keyframes + "#gate.closed { animation: fade 100ms linear; }");
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        var target = h.ById("gate");
        present.Value = false;
        h.Root.RunFrame();
        Assert.Equal(0, clock.PendingCount);
        Advance(h, clock, 250);
        Assert.Same(target, h.ById("gate"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dialog_content_and_backdrop_exit_independently_in_the_same_layer(bool backdropFirst)
    {
        var open = new State<bool>(false);
        int focusRestorations = 0;
        int backdropDuration = backdropFirst ? 100 : 250;
        int contentDuration = backdropFirst ? 250 : 100;
        var h = new UIHarness(() => new Dialog.Root
        {
            Open = open.Value,
            Children = [
                new Dialog.Trigger { Attributes = new() { ["id"] = "trigger" } },
                new Dialog.Portal
                {
                    Children = [
                        new Dialog.Overlay { Class = "backdrop" },
                        new Dialog.Content
                        {
                            Class = "content",
                            OnCloseAutoFocus = _ => focusRestorations++,
                            Children = [new UIButton { Id = "inside" }],
                        },
                    ],
                },
            ],
        }, Keyframes + $$"""
            .backdrop[data-state=closed] { animation: fade {{backdropDuration}}ms linear; }
            .content[data-state=closed] { animation: fade {{contentDuration}}ms linear; }
            """);
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        open.Value = true;
        h.Root.RunFrame();
        var layer = Assert.Single(h.Root.Overlay.Layers);
        var content = Assert.Single(h.FindAll(e => e.HasClass("content")));
        var backdrop = Assert.Single(h.FindAll(e => e.HasClass("backdrop")));
        Assert.Same(h.ById("inside"), h.Focused);

        open.Value = false;
        h.Root.RunFrame();
        Assert.Equal("closed", content.GetAttribute("data-state"));
        Assert.Equal("closed", backdrop.GetAttribute("data-state"));
        Assert.True(h.Root.IsPointerBlockingActive);
        Assert.True(h.Root.IsScrollLockActive);
        Assert.Equal(0, focusRestorations);

        Advance(h, clock, 125);
        h.Root.RunFrame();
        Assert.Equal(!backdropFirst, backdrop.IsAttached);
        Assert.Equal(backdropFirst, content.IsAttached);
        Assert.Same(layer, Assert.Single(h.Root.Overlay.Layers));
        Assert.Equal(backdropFirst, h.Root.IsPointerBlockingActive);
        Assert.Equal(backdropFirst, h.Root.IsScrollLockActive);
        Assert.Equal(backdropFirst ? 0 : 1, focusRestorations);

        Advance(h, clock, 150);
        h.Root.RunFrame();
        Assert.False(content.IsAttached);
        Assert.False(backdrop.IsAttached);
        Assert.False(h.Root.Overlay.IsAttached);
        Assert.False(h.Root.IsPointerBlockingActive);
        Assert.False(h.Root.IsScrollLockActive);
        Assert.Same(h.ById("trigger"), h.Focused);
        Assert.Equal(1, focusRestorations);
        Assert.Equal(0, clock.PendingCount);
    }

    [Fact]
    public void Popover_reopens_in_place_and_unmounts_when_its_exit_transition_is_cancelled()
    {
        var open = new State<bool>(false);
        int requestsToClose = 0;
        int focusRestorations = 0;
        var h = new UIHarness(() => new Popover.Root
        {
            Open = open.Value,
            OnOpenChange = value => { if (!value) requestsToClose++; open.Value = value; },
            Children = [
                new Popover.Trigger { Attributes = new() { ["id"] = "trigger" } },
                new Popover.Portal { Children = [new Popover.Content
                {
                    Class = "content",
                    OnCloseAutoFocus = _ => focusRestorations++,
                    Children = [new UIButton { Id = "inside" }],
                }] },
            ],
        }, """
            .content { width: 80px; height: 30px; transition: opacity 400ms linear; }
            .content[data-side=bottom][data-state=closed] { opacity: 0; }
            """);
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        open.Value = true;
        h.Root.RunFrame();
        var content = Assert.Single(h.FindAll(e => e.HasClass("content")));
        var layer = Assert.Single(h.Root.Overlay.Layers);

        h.Escape();
        Assert.Equal(1, requestsToClose);
        Assert.Equal("closed", content.GetAttribute("data-state"));
        Advance(h, clock, 100);
        Assert.Equal(0.75f, content.ComputedStyle!.Opacity, 4);
        h.Escape();
        Assert.Equal(1, requestsToClose);
        open.Value = true;
        h.Root.RunFrame();
        Advance(h, clock, 550);
        Assert.True(content.IsAttached);
        Assert.Same(layer, Assert.Single(h.Root.Overlay.Layers));
        Assert.Equal("open", content.GetAttribute("data-state"));
        Assert.Equal(1f, content.ComputedStyle!.Opacity);
        Assert.Equal(0, focusRestorations);

        open.Value = false;
        h.Root.RunFrame();
        Advance(h, clock, 50);
        Assert.True(content.IsAttached);
        content.SetStyleOverride("transition-property", "none");
        h.Root.RunFrame();
        h.Root.RunFrame();
        Assert.False(content.IsAttached);
        Assert.False(h.Root.Overlay.IsAttached);
        Assert.Same(h.ById("trigger"), h.Focused);
        Assert.Equal(1, focusRestorations);
        Assert.Equal(0, clock.PendingCount);
    }
}
