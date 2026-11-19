using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

public class EventTests
{
    static UIRoot MountStatic(Node node)
    {
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => node });
        return root;
    }

    [Fact]
    public void Dispatch_runs_capture_target_bubble_in_order()
    {
        var log = new List<string>();
        Action<PointerEvent> H(string name) => e => log.Add(name + ":" + e.Phase + ":" + e.CurrentTarget!.Id);
        var root = MountStatic(new Div
        {
            Id = "outer", OnClickCapture = H("outer-capture"), OnClick = H("outer"),
            Children = [new Div
            {
                Id = "middle", OnClickCapture = H("middle-capture"), OnClick = H("middle"),
                Children = [new Div { Id = "inner", OnClickCapture = H("inner-capture"), OnClick = H("inner") }],
            }],
        });
        root.Input.Click(root.RootElement.ById("inner"));
        Assert.Equal(new[]
        {
            "outer-capture:Capturing:outer", "middle-capture:Capturing:middle",
            "inner-capture:AtTarget:inner", "inner:AtTarget:inner",
            "middle:Bubbling:middle", "outer:Bubbling:outer",
        }, log.ToArray());
    }

    [Fact]
    public void Stop_propagation_and_stop_immediate()
    {
        var log = new List<string>();
        var root = MountStatic(new Div
        {
            Id = "outer", OnClick = _ => log.Add("outer"),
            Children =
            [
                new Div { Id = "a", OnClick = e => { log.Add("a"); e.StopPropagation(); } },
                new Div { Id = "b", OnClickCapture = e => { log.Add("b-capture"); e.StopImmediatePropagation(); }, OnClick = _ => log.Add("b") },
            ],
        });
        root.Input.Click(root.RootElement.ById("a"));
        root.Input.Click(root.RootElement.ById("b"));
        Assert.Equal(new[] { "a", "b-capture" }, log.ToArray());

        log.Clear();
        var r2 = MountStatic(new Div { OnClickCapture = e => { log.Add("outer-capture"); e.StopPropagation(); }, Children = [new Div { Id = "t", OnClick = _ => log.Add("target") }] });
        r2.Input.Click(r2.RootElement.ById("t"));
        Assert.Equal(new[] { "outer-capture" }, log.ToArray());
    }

    [Fact]
    public void Prevent_default_on_pointer_down_keeps_focus()
    {
        var root = MountStatic(new Div
        {
            Children =
            [
                new Button { Id = "a" },
                new Button { Id = "b", OnPointerDown = e => e.PreventDefault() },
                new Div { Id = "plain" },
            ],
        });
        var a = root.RootElement.ById("a");
        root.Input.Click(a);
        Assert.Same(a, root.FocusManager.FocusedElement);
        Assert.False(a.States.HasFlag(PseudoStates.FocusVisible)); // 指针聚焦不显示焦点环
        root.Input.Click(root.RootElement.ById("b"));
        Assert.Same(a, root.FocusManager.FocusedElement);
        root.Input.Click(root.RootElement.ById("plain")); // 点到不可聚焦处：失焦
        Assert.Null(root.FocusManager.FocusedElement);
        Assert.False(a.States.HasFlag(PseudoStates.Focus));
    }

    [Fact]
    public void Prevent_default_on_tab_keydown_keeps_focus()
    {
        var root = MountStatic(new Div
        {
            OnKeyDown = e => { if (e.Key == "Tab") e.PreventDefault(); },
            Children = [new Button { Id = "a" }, new Button { Id = "b" }],
        });
        root.RootElement.ById("a").Focus();
        root.Press(PhysicalKey.Tab);
        Assert.Equal("a", root.FocusManager.FocusedElement!.Id);
    }

    [Fact]
    public void Tab_order_positive_tabindex_first_then_tree_order_and_wraps()
    {
        var root = MountStatic(new Div
        {
            Children =
            [
                new Button { Id = "b1" },
                new Div { Id = "skip" },
                new Div { Id = "t2", TabIndex = 2 },
                new Div { Id = "neg", TabIndex = -1 },
                new Div { Children = [new Input { Id = "in" }, new Button { Id = "disabled", Disabled = true }] },
                new Div { Id = "t1", TabIndex = 1 },
                new Div { Id = "z", TabIndex = 0 },
            ],
        });
        Assert.Equal(new[] { "t1", "t2", "b1", "in", "z" }, root.FocusManager.GetTabOrder().Select(e => e.Id).ToArray());
        var visited = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            root.Press(PhysicalKey.Tab);
            visited.Add(root.FocusManager.FocusedElement!.Id!);
        }
        Assert.Equal(new[] { "t1", "t2", "b1", "in", "z", "t1" }, visited.ToArray());
        root.Press(PhysicalKey.Tab, KeyModifiers.Shift);
        Assert.Equal("z", root.FocusManager.FocusedElement!.Id);

        // TabIndex = -1 只能程序聚焦，Tab 从它在树中的位置继续
        Assert.True(root.RootElement.ById("neg").Focus());
        root.Press(PhysicalKey.Tab);
        Assert.Equal("in", root.FocusManager.FocusedElement!.Id);
        Assert.False(root.RootElement.ById("disabled").Focus());
        Assert.False(root.RootElement.ById("skip").Focus());
    }

    [Fact]
    public void Focus_events_order_and_focus_within()
    {
        var log = new List<string>();
        Action<FocusEvent> H(string n) => e => log.Add(n + ":" + e.Type + ":" + e.Target!.Id);
        var root = MountStatic(new Div
        {
            Id = "group", OnFocusIn = H("group"), OnFocusOut = H("group"),
            Children =
            [
                new Button { Id = "a", OnFocus = H("a"), OnBlur = H("a") },
                new Button { Id = "b", OnFocus = H("b"), OnBlur = H("b") },
            ],
        });
        var group = root.RootElement.ById("group");
        var a = root.RootElement.ById("a");
        var b = root.RootElement.ById("b");
        a.Focus();
        Assert.True(group.States.HasFlag(PseudoStates.FocusWithin));
        Assert.True(root.RootElement.States.HasFlag(PseudoStates.FocusWithin));
        log.Clear();
        var obs = new RecordingObserver();
        root.AddObserver(obs);
        b.Focus();
        Assert.Equal(new[] { "a:blur:a", "group:focusout:a", "b:focus:b", "group:focusin:b" }, log.ToArray());
        // 公共祖先的 focus-within 不翻转
        Assert.DoesNotContain("state group", obs.Events);
        Assert.True(group.States.HasFlag(PseudoStates.FocusWithin));
        Assert.False(a.States.HasFlag(PseudoStates.FocusWithin));
        Assert.True(b.States.HasFlag(PseudoStates.Focus));
        b.Blur();
        Assert.False(group.States.HasFlag(PseudoStates.FocusWithin));
        Assert.Null(root.FocusManager.FocusedElement);
    }

    [Fact]
    public void Focus_visible_follows_input_modality()
    {
        var root = MountStatic(new Div { Children = [new Button { Id = "a" }, new Input { Id = "in" }] });
        var a = root.RootElement.ById("a");
        var input = root.RootElement.ById("in");
        root.Input.Click(a);
        Assert.False(a.States.HasFlag(PseudoStates.FocusVisible));
        root.Input.Click(input);
        Assert.True(input.States.HasFlag(PseudoStates.FocusVisible)); // 文本输入总是显示
        a.Focus(); // 程序聚焦：最近一次是指针交互
        Assert.False(a.States.HasFlag(PseudoStates.FocusVisible));
        root.Input.DispatchKey(TestUtil.KeyDown(PhysicalKey.ShiftLeft)); // 修饰键不算键盘交互
        a.Blur();
        a.Focus();
        Assert.False(a.States.HasFlag(PseudoStates.FocusVisible));
        root.Press(PhysicalKey.ArrowDown);
        a.Blur();
        a.Focus();
        Assert.True(a.States.HasFlag(PseudoStates.FocusVisible));
    }

    [Fact]
    public void Removing_focused_element_clears_focus_states()
    {
        var show = new State<bool>(true);
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => new Div { Id = "group", Children = [show.Value ? new Button { Id = "a" } : null] } });
        var group = root.RootElement.ById("group");
        root.RootElement.ById("a").Focus();
        Assert.True(group.States.HasFlag(PseudoStates.FocusWithin));
        show.Value = false;
        root.RunFrame();
        Assert.Null(root.FocusManager.FocusedElement);
        Assert.False(group.States.HasFlag(PseudoStates.FocusWithin));
    }

    [Fact]
    public void Hover_tracking_sets_states_and_fires_enter_leave()
    {
        var log = new List<string>();
        Action<PointerEvent> H(string n) => e => log.Add(e.Type + ":" + n);
        var root = MountStatic(new Div
        {
            Id = "outer", OnPointerEnter = H("outer"), OnPointerLeave = H("outer"),
            Children =
            [
                new Div { Id = "a", OnPointerEnter = H("a"), OnPointerLeave = H("a"), Children = [new Span { Id = "a1", OnPointerEnter = H("a1"), OnPointerLeave = H("a1") }] },
                new Div { Id = "b", OnPointerEnter = H("b"), OnPointerLeave = H("b") },
            ],
        });
        var e = root.RootElement;
        var move = TestUtil.Pointer(RawPointerEventType.Move);
        root.Input.DispatchPointer(move, e.ById("a1"));
        Assert.Equal(new[] { "pointerenter:outer", "pointerenter:a", "pointerenter:a1" }, log.ToArray());
        Assert.True(e.ById("outer").States.HasFlag(PseudoStates.Hover));
        Assert.True(e.ById("a1").States.HasFlag(PseudoStates.Hover));
        log.Clear();
        root.Input.DispatchPointer(move, e.ById("b"));
        Assert.Equal(new[] { "pointerleave:a1", "pointerleave:a", "pointerenter:b" }, log.ToArray());
        Assert.False(e.ById("a").States.HasFlag(PseudoStates.Hover));
        Assert.True(e.ById("outer").States.HasFlag(PseudoStates.Hover));
        log.Clear();
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Leave), null);
        Assert.Equal(new[] { "pointerleave:b", "pointerleave:outer" }, log.ToArray());
        Assert.Equal(PseudoStates.None, e.ById("outer").States & PseudoStates.Hover);
    }

    [Fact]
    public void Active_state_and_click_synthesis_use_common_ancestor()
    {
        var clicks = new List<string>();
        var root = MountStatic(new Div
        {
            Id = "outer", OnClick = ev => clicks.Add("outer<-" + ev.Target!.Id),
            Children = [new Div { Id = "a" }, new Div { Id = "b" }],
        });
        var a = root.RootElement.ById("a");
        var b = root.RootElement.ById("b");
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Down, changed: PointerButtons.Left, buttons: PointerButtons.Left), a);
        Assert.True(a.States.HasFlag(PseudoStates.Active));
        Assert.True(root.RootElement.ById("outer").States.HasFlag(PseudoStates.Active));
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Up, changed: PointerButtons.Left), b);
        Assert.False(a.States.HasFlag(PseudoStates.Active));
        Assert.Equal(new[] { "outer<-outer" }, clicks.ToArray());

        // 右键不产生 click
        clicks.Clear();
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Down, changed: PointerButtons.Right, buttons: PointerButtons.Right), a);
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Up, changed: PointerButtons.Right), a);
        Assert.Empty(clicks);
    }

    [Fact]
    public void Pointer_capture_redirects_events()
    {
        var moves = new List<string>();
        var root = MountStatic(new Div
        {
            Children =
            [
                new Div { Id = "slider", OnPointerDown = e => ((Element)e.CurrentTarget!).SetPointerCapture(e.PointerId), OnPointerMove = e => moves.Add("slider") },
                new Div { Id = "other", OnPointerMove = e => moves.Add("other") },
            ],
        });
        var slider = root.RootElement.ById("slider");
        var other = root.RootElement.ById("other");
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Down, changed: PointerButtons.Left, buttons: PointerButtons.Left), slider);
        Assert.True(slider.HasPointerCapture(1));
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Move, buttons: PointerButtons.Left), other);
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Up, changed: PointerButtons.Left), other);
        Assert.False(slider.HasPointerCapture(1));
        root.Input.DispatchPointer(TestUtil.Pointer(RawPointerEventType.Move), other);
        Assert.Equal(new[] { "slider", "other" }, moves.ToArray());
    }

    [Fact]
    public void Disabled_button_is_not_clickable_or_focusable()
    {
        int clicks = 0;
        var root = MountStatic(new Button { Id = "b", Disabled = true, OnClick = _ => clicks++ });
        var b = root.RootElement.ById("b");
        Assert.True(b.States.HasFlag(PseudoStates.Disabled));
        Assert.Equal("", b.GetAttribute("disabled"));
        root.Input.Click(b);
        Assert.False(root.Input.Activate(b));
        Assert.Equal(0, clicks);
        Assert.Null(root.FocusManager.FocusedElement);
    }

    [Fact]
    public void Key_events_bubble_from_focused_element()
    {
        var log = new List<string>();
        var root = MountStatic(new Div
        {
            OnKeyDown = e => log.Add("div:" + e.Key + ":" + e.PhysicalKey),
            Children = [new Button { Id = "b", OnKeyDown = e => log.Add("button:" + e.Key) }],
        });
        root.RootElement.ById("b").Focus();
        root.Input.DispatchKey(TestUtil.KeyDown(PhysicalKey.KeyA), "a");
        root.Input.DispatchKey(TestUtil.KeyDown(PhysicalKey.Escape));
        Assert.Equal(new[] { "button:a", "div:a:KeyA", "button:Escape", "div:Escape:Escape" }, log.ToArray());
    }

    [Fact]
    public void Input_change_fires_on_blur_after_edit()
    {
        var log = new List<string>();
        var root = MountStatic(new Div
        {
            Children =
            [
                new Input { Id = "in", OnInput = e => log.Add("input:" + e.Value), OnChange = e => log.Add("change:" + e.Value) },
                new Button { Id = "b" },
            ],
        });
        root.RootElement.ById("in").Focus();
        root.Input.DispatchTextInput("ab");
        root.Press(PhysicalKey.Backspace);
        root.Press(PhysicalKey.Tab);
        Assert.Equal(new[] { "input:ab", "input:a", "change:a" }, log.ToArray());
    }

    [Fact]
    public void Shortcut_normalization_maps_ctrl_and_cmd()
    {
        try
        {
            Shortcuts.UseMacConventions = true;
            Assert.Equal(KeyModifiers.Meta, Shortcuts.PrimaryModifier);
            Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, Shortcuts.Normalize(KeyModifiers.Meta | KeyModifiers.Shift));
            Assert.Equal(KeyModifiers.Meta, Shortcuts.Normalize(KeyModifiers.Control));
            var save = KeyGesture.Parse("Mod+S");
            Assert.True(save.Matches(PhysicalKey.KeyS, KeyModifiers.Meta));
            Assert.False(save.Matches(PhysicalKey.KeyS, KeyModifiers.Control));

            Shortcuts.UseMacConventions = false;
            Assert.True(save.Matches(PhysicalKey.KeyS, KeyModifiers.Control));
            Assert.Equal(KeyModifiers.Control, Shortcuts.Normalize(KeyModifiers.Control));
            Assert.True(KeyGesture.Parse("Ctrl+Shift+Z").Matches(PhysicalKey.KeyZ, KeyModifiers.Control | KeyModifiers.Shift));
            Assert.True(KeyGesture.TryParse("Alt+F4", out var g) && g.Key == PhysicalKey.F4);
            Assert.False(KeyGesture.TryParse("Ctrl+", out _));
            Assert.False(KeyGesture.TryParse("Foo+S", out _));
            Assert.False(KeyGesture.TryParse("Ctrl+12", out _));

            var root = MountStatic(new Button { Id = "b" });
            KeyEvent? seen = null;
            root.RootElement.ById("b").Focus();
            var r2 = MountStatic(new Div { TabIndex = 0, Id = "d", OnKeyDown = e => seen = e });
            r2.RootElement.ById("d").Focus();
            r2.Input.DispatchKey(TestUtil.KeyDown(PhysicalKey.KeyS, KeyModifiers.Control));
            Assert.True(seen!.HasPrimaryModifier);
            Assert.True(seen.Matches(save));
        }
        finally
        {
            Shortcuts.ResetPlatformConventions();
        }
    }
}
