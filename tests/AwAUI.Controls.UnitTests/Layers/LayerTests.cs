using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.Layers;

/// <summary>测试用的节点：按名字的前缀判断包含关系（"dialog/button" 在 "dialog" 之内）。</summary>
sealed class Node
{
    public Node(string path) => Path = path;

    public string Path { get; }

    public bool IsWithin(string root) => Path == root || Path.StartsWith(root + "/", StringComparison.Ordinal);

    public override string ToString() => Path;
}

public class DismissableLayerTests
{
    static DismissableLayer<Node> Layer(string root, List<string> log, bool modal = false) => new(n => n.IsWithin(root))
    {
        Name = root,
        DisableOutsidePointerEvents = modal,
        OnDismiss = r => log.Add(root + ":" + r),
    };

    [Fact]
    public void Escape_only_dismisses_top_layer()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        var dialog = Layer("dialog", log, modal: true);
        var menu = Layer("menu", log);
        stack.Push(dialog);
        stack.Push(menu);
        var handled = stack.DispatchEscape(out bool dismissed);
        Assert.Same(menu, handled);
        Assert.True(dismissed);
        Assert.Equal(new[] { "menu:EscapeKey" }, log);
    }

    [Fact]
    public void Escape_can_be_prevented()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        var layer = Layer("popover", log);
        layer.OnEscapeKeyDown = e => e.PreventDefault();
        stack.Push(layer);
        stack.DispatchEscape(out bool dismissed);
        Assert.False(dismissed);
        Assert.Empty(log);
        Assert.Null(new DismissableLayerStack<Node>().DispatchEscape(out _));
    }

    [Fact]
    public void Pointer_outside_dismisses_all_non_modal_layers_outside()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        stack.Push(Layer("popover", log));
        var nested = Layer("nested", log);
        stack.Push(nested);
        stack.DispatchPointerDown(new Node("body/button"));
        Assert.Equal(new[] { "nested:PointerDownOutside", "popover:PointerDownOutside" }, log);
    }

    [Fact]
    public void Logical_children_count_as_inside()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        var popover = Layer("popover", log);
        var nested = Layer("portal/nested", log);
        nested.Parent = popover;
        stack.Push(popover);
        stack.Push(nested);
        // 点击嵌套层（经 Portal 挂在别处）的内容：对父层不算外部
        stack.DispatchPointerDown(new Node("portal/nested/item"));
        Assert.Empty(log);
        // 点击父层内容：嵌套层关闭，父层保留
        stack.DispatchPointerDown(new Node("popover/text"));
        Assert.Equal(new[] { "portal/nested:PointerDownOutside" }, log);
    }

    [Fact]
    public void Modal_layer_blocks_outside_pointer_for_layers_below()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        var below = Layer("menu", log);
        var dialog = Layer("dialog", log, modal: true);
        var tooltip = Layer("tooltip", log);
        stack.Push(below);
        stack.Push(dialog);
        stack.Push(tooltip);
        Assert.True(stack.IsOutsidePointerEventsDisabled);
        Assert.False(stack.IsPointerEventsEnabled(below));
        Assert.True(stack.IsPointerEventsEnabled(dialog));
        Assert.True(stack.IsPointerEventsEnabled(tooltip));
        stack.DispatchPointerDown(new Node("body"));
        Assert.Equal(new[] { "tooltip:PointerDownOutside", "dialog:PointerDownOutside" }, log);
    }

    [Fact]
    public void Prevented_pointer_down_outside_keeps_layer_and_interact_outside_runs()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        var dialog = Layer("dialog", log, modal: true);
        var calls = new List<string>();
        dialog.OnPointerDownOutside = e => { calls.Add("pointer " + e.Target); e.PreventDefault(); };
        dialog.OnInteractOutside = e => calls.Add("interact " + e.DefaultPrevented);
        stack.Push(dialog);
        var dismissed = stack.DispatchPointerDown(new Node("overlay"));
        Assert.Empty(dismissed);
        Assert.Empty(log);
        Assert.Equal(new[] { "pointer overlay", "interact True" }, calls);
    }

    [Fact]
    public void Focus_outside_ignores_pointer_blocking_and_branches_are_inside()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        var below = Layer("menu", log);
        stack.Push(below);
        stack.Push(Layer("dialog", log, modal: true));
        using (stack.AddBranch(n => n.IsWithin("toasts")))
        {
            stack.DispatchPointerDown(new Node("toasts/close"));
            stack.DispatchFocusIn(new Node("toasts/action"));
            Assert.Empty(log);
        }
        stack.DispatchFocusIn(new Node("toasts/action"));
        Assert.Equal(new[] { "dialog:FocusOutside", "menu:FocusOutside" }, log);
    }

    [Fact]
    public void Dispatch_uses_snapshot_when_callbacks_remove_layers()
    {
        var stack = new DismissableLayerStack<Node>();
        var log = new List<string>();
        var a = Layer("a", log);
        var b = Layer("b", log);
        a.OnDismiss = r => { log.Add("a"); stack.Remove(a); };
        b.OnDismiss = r => { log.Add("b"); stack.Remove(b); stack.Remove(a); };
        stack.Push(a);
        stack.Push(b);
        var dismissed = stack.DispatchPointerDown(new Node("x"));
        Assert.Equal(new[] { "b", "a" }, log);
        Assert.Equal(2, dismissed.Count);
        Assert.Empty(stack.Layers);
    }

    [Fact]
    public void Cyclic_parents_do_not_hang()
    {
        var log = new List<string>();
        var stack = new DismissableLayerStack<Node>();
        var a = Layer("a", log);
        var b = Layer("b", log);
        a.Parent = b;
        b.Parent = a;
        stack.Push(a);
        stack.Push(b);
        stack.DispatchPointerDown(new Node("a/x"));
        Assert.Empty(log);   // 互为子层：对双方都在内部
    }
}

public class FocusScopeTests
{
    static readonly Node Container = new("scope");
    static readonly Node First = new("scope/first");
    static readonly Node Middle = new("scope/middle");
    static readonly Node Last = new("scope/last");
    static readonly Node Outside = new("outside");
    static readonly Node[] Tabbables = { First, Middle, Last };

    [Fact]
    public void Loop_wraps_tab_and_shift_tab()
    {
        var s = new FocusScopeModel<Node>(Container) { Loop = true };
        var r = s.HandleTab(Last, shift: false, Tabbables);
        Assert.True(r.PreventDefault);
        Assert.Same(First, r.Target);
        r = s.HandleTab(First, shift: true, Tabbables);
        Assert.Same(Last, r.Target);
        r = s.HandleTab(Middle, shift: false, Tabbables);
        Assert.False(r.PreventDefault);
    }

    [Fact]
    public void Trapped_without_loop_stops_at_edges()
    {
        var s = new FocusScopeModel<Node>(Container) { Trapped = true };
        var r = s.HandleTab(Last, false, Tabbables);
        Assert.True(r.PreventDefault);
        Assert.False(r.HasTarget);
        Assert.False(new FocusScopeModel<Node>(Container).HandleTab(Last, false, Tabbables).PreventDefault);
    }

    [Fact]
    public void Empty_scope_keeps_focus_on_container()
    {
        var s = new FocusScopeModel<Node>(Container) { Trapped = true };
        Assert.True(s.HandleTab(Container, false, Array.Empty<Node>()).PreventDefault);
        Assert.True(s.HandleTab(Container, true, Tabbables).PreventDefault);
    }

    [Fact]
    public void Mount_auto_focuses_first_visible_non_link_and_unmount_restores()
    {
        var s = new FocusScopeModel<Node>(Container);
        var target = s.Mount(Outside, currentFocusIsInside: false, new[]
        {
            new FocusCandidate<Node>(new Node("scope/hidden"), isVisible: false),
            new FocusCandidate<Node>(new Node("scope/link"), isLink: true),
            new FocusCandidate<Node>(Middle),
        });
        Assert.Same(Middle, target);
        Assert.Same(Outside, s.Unmount());
        Assert.Null(s.Unmount(preventRestore: true));
        var empty = new FocusScopeModel<Node>(Container);
        Assert.Same(Container, empty.Mount(Outside, false, Array.Empty<FocusCandidate<Node>>()));
        Assert.Null(new FocusScopeModel<Node>(Container).Mount(Outside, false, Array.Empty<FocusCandidate<Node>>(), preventAutoFocus: true));
        Assert.Null(new FocusScopeModel<Node>(Container).Mount(First, true, Array.Empty<FocusCandidate<Node>>()));
    }

    [Fact]
    public void Trapped_pulls_focus_back_and_paused_scope_does_not()
    {
        var s = new FocusScopeModel<Node>(Container) { Trapped = true };
        s.Mount(Outside, false, new[] { new FocusCandidate<Node>(First) });
        Assert.Null(s.OnFocusIn(Middle, isInside: true));
        Assert.Same(Middle, s.OnFocusIn(Outside, isInside: false));
        Assert.Same(Container, s.OnFocusedElementRemoved());

        var stack = new FocusScopeStack<Node>();
        var inner = new FocusScopeModel<Node>(new Node("inner")) { Trapped = true };
        stack.Add(s);
        stack.Add(inner);
        Assert.True(s.IsPaused);
        Assert.Same(inner, stack.Active);
        Assert.Null(s.OnFocusIn(Outside, false));
        Assert.False(s.HandleTab(Last, false, Tabbables).PreventDefault);
        stack.Remove(inner);
        Assert.False(s.IsPaused);
        Assert.Same(s, stack.Active);
        // 重新激活已在栈中的作用域：移到栈顶
        stack.Add(inner);
        stack.Add(s);
        Assert.Same(s, stack.Active);
        Assert.True(inner.IsPaused);
    }
}

public class PresenceTests
{
    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Unmounts_immediately_without_animation()
    {
        var p = new Presence(true);
        var states = new List<PresenceState>();
        p.StateChanged += states.Add;
        p.SetPresent(false, new PresenceStyle { AnimationName = "none" }, Ms(0));
        Assert.Equal(PresenceState.Unmounted, p.State);
        Assert.False(p.IsPresent);
        Assert.Equal(new[] { PresenceState.Unmounted }, states);
    }

    [Fact]
    public void Exit_animation_suspends_until_animation_end()
    {
        var p = new Presence(false);
        p.SetPresent(true, new PresenceStyle { AnimationName = "fade-in" }, Ms(0));
        Assert.Equal(PresenceState.Mounted, p.State);
        p.SetPresent(false, new PresenceStyle { AnimationName = "fade-out", AnimationDuration = Ms(200) }, Ms(1000));
        Assert.Equal(PresenceState.UnmountSuspended, p.State);
        Assert.True(p.IsPresent);
        p.OnAnimationEnd("fade-in");   // 不是当前动画
        Assert.Equal(PresenceState.UnmountSuspended, p.State);
        p.OnAnimationEnd("fade-out");
        Assert.Equal(PresenceState.Unmounted, p.State);
    }

    [Fact]
    public void Same_animation_name_means_no_exit_animation()
    {
        var p = new Presence(false);
        p.SetPresent(true, new PresenceStyle { AnimationName = "pulse" }, Ms(0));
        p.SetPresent(false, new PresenceStyle { AnimationName = "pulse" }, Ms(10));
        Assert.Equal(PresenceState.Unmounted, p.State);
    }

    [Fact]
    public void Reopening_during_exit_returns_to_mounted()
    {
        var p = new Presence(true);
        p.SetPresent(false, new PresenceStyle { AnimationName = "out", AnimationDuration = Ms(300) }, Ms(0));
        p.SetPresent(true, new PresenceStyle { AnimationName = "in" }, Ms(100));
        Assert.Equal(PresenceState.Mounted, p.State);
        p.OnAnimationEnd("out");   // 迟到的结束事件被忽略
        Assert.Equal(PresenceState.Mounted, p.State);
        Assert.False(p.Tick(Ms(10_000)));
    }

    [Fact]
    public void Transitions_wait_for_every_end_event()
    {
        var p = new Presence(true);
        p.SetPresent(false, new PresenceStyle { RunningTransitions = 2, TransitionDuration = Ms(150) }, Ms(0));
        Assert.Equal(PresenceState.UnmountSuspended, p.State);
        p.OnTransitionEnd();
        Assert.Equal(PresenceState.UnmountSuspended, p.State);
        p.OnTransitionEnd();
        Assert.Equal(PresenceState.Unmounted, p.State);
    }

    [Fact]
    public void Animation_and_transition_together()
    {
        var p = new Presence(true);
        p.SetPresent(false, new PresenceStyle { AnimationName = "out, glow", RunningTransitions = 1 }, Ms(0));
        p.OnAnimationEnd("glow");
        Assert.Equal(PresenceState.UnmountSuspended, p.State);
        p.OnTransitionEnd();
        Assert.Equal(PresenceState.Unmounted, p.State);
    }

    [Fact]
    public void Display_none_and_infinite_animation_unmount_immediately()
    {
        var p = new Presence(true);
        p.SetPresent(false, new PresenceStyle { AnimationName = "out", DisplayNone = true }, Ms(0));
        Assert.Equal(PresenceState.Unmounted, p.State);
        var q = new Presence(true);
        q.SetPresent(false, new PresenceStyle { AnimationName = "spin", InfiniteAnimation = true }, Ms(0));
        Assert.Equal(PresenceState.Unmounted, q.State);
    }

    [Fact]
    public void Lost_end_event_is_bounded_by_deadline()
    {
        var p = new Presence(true);
        p.SetPresent(false, new PresenceStyle { AnimationName = "out", AnimationDuration = Ms(200) }, Ms(1000));
        Assert.Equal(Ms(1300), p.Deadline);
        Assert.False(p.Tick(Ms(1299)));
        Assert.True(p.Tick(Ms(1300)));
        Assert.Equal(PresenceState.Unmounted, p.State);
        var unknown = new Presence(true);
        unknown.SetPresent(false, new PresenceStyle { AnimationName = "out" }, Ms(0));
        Assert.Equal(unknown.FallbackTimeout, unknown.Deadline);
    }
}
