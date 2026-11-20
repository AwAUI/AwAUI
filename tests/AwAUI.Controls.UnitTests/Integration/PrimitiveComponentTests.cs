using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>行为原语组件（Slot、DismissableLayer、FocusScope、RovingFocusGroup、PresenceGate、ScrollLock、Popper 箭头与 hide）的端到端测试。</summary>
public class PrimitiveComponentTests
{
    const string Css = """
        button { width: 80px; height: 30px; padding: 0; margin: 0; border-width: 0; display: block; }
        .at { position: absolute; }
        .pop { width: 100px; height: 60px; }
        .clip { position: absolute; left: 0; top: 0; width: 300px; height: 200px; overflow: auto; }
        .tall { height: 2000px; }
        """;

    // ------------------------------------------------------------ Slot

    [Fact]
    public void Slot_merges_class_style_attributes_and_handlers_without_mutating_inputs()
    {
        var log = new List<string>();
        var slotProps = new Div { Class = "slot", Style = "color: red; width: 5px", Role = Role.Button, TabIndex = 0, OnClick = _ => log.Add("slot") };
        slotProps.Attributes["data-state"] = "open";
        slotProps.Attributes["aria-label"] = "slot label";
        var child = new UIButton { Class = "child", Style = "width: 10px", Id = "c", Disabled = false, OnClick = _ => log.Add("child") };
        child.Attributes["data-state"] = "child-wins";
        child.Children.Add("label");

        var merged = (UIButton)Slot.Merge(slotProps, child);
        Assert.NotSame(child, merged);
        Assert.Equal("slot child", merged.Class.Text);
        Assert.Equal("color: red; width: 5px; width: 10px", merged.Style.Text);
        Assert.Equal("c", merged.Id);
        Assert.Equal(Role.Button, merged.Role);
        Assert.Equal(0, merged.TabIndex);
        Assert.Equal("child-wins", merged.Attributes["data-state"]);
        Assert.Equal("slot label", merged.Attributes["aria-label"]);
        Assert.Same(child.Children, merged.Children);
        merged.OnClick!(new PointerEvent("click"));
        Assert.Equal(new[] { "child", "slot" }, log);
        // 输入不变
        Assert.Equal("child", child.Class.Text);
        Assert.Equal("child-wins", child.Attributes["data-state"]);
        Assert.Equal("slot", slotProps.Class.Text);

        // 不能合并的子节点（文本）：渲染槽位载体并包住它
        var wrapped = Assert.IsType<Div>(Slot.Merge(slotProps, new TextNode("x")));
        Assert.Equal("slot", wrapped.Class.Text);
        Assert.Single(wrapped.Children);
    }

    [Fact]
    public void Compose_runs_internal_handler_only_when_not_prevented()
    {
        var log = new List<string>();
        var composed = EventComposition.Compose<PointerEvent>(e => { log.Add("user"); e.PreventDefault(); }, _ => log.Add("internal"))!;
        composed(new PointerEvent("click"));
        Assert.Equal(new[] { "user" }, log);
    }

    [Fact]
    public void As_child_trigger_renders_the_child_element_with_trigger_props()
    {
        int userClicks = 0;
        var h = new UIHarness(() => new Popover.Root
        {
            Children =
            [
                new Popover.Trigger { AsChild = true, Children = [new Div { Id = "custom", Class = "mine", TabIndex = 0, Style = "width: 50px; height: 20px", OnClick = _ => userClicks++ }] },
                new Popover.Portal { Children = [new Popover.Content { Class = "pop" }] },
            ],
        }, Css);
        var trigger = h.ById("custom");
        Assert.Equal("div", trigger.Tag);
        Assert.True(trigger.HasClass("mine"));
        Assert.Equal("dialog", trigger.GetAttribute("aria-haspopup"));
        h.Click(trigger);
        Assert.Equal(1, userClicks);
        Assert.Equal("true", trigger.GetAttribute("aria-expanded"));
        Assert.Single(h.FindAll(e => e.HasClass("pop")));
    }

    // ------------------------------------------------------------ DismissableLayer（不经 Portal 的嵌套）

    [Fact]
    public void Nested_dismissable_layers_escape_top_first_and_inner_click_is_not_outside_of_parent()
    {
        var log = new List<string>();
        var showInner = new State<bool>(false);
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                new UIButton { Id = "out", Children = { "out" } },
                new DismissableLayer
                {
                    OnDismiss = r => log.Add("outer " + r),
                    Children =
                    [
                        new Div
                        {
                            Id = "outer",
                            Style = "height: 100px",
                            Children =
                            {
                                showInner.Value
                                    ? new DismissableLayer
                                    {
                                        OnDismiss = r => log.Add("inner " + r),
                                        Children = [new Div { Id = "inner", Style = "height: 40px" }],
                                    }
                                    : null,
                            },
                        },
                    ],
                },
            ],
        }, Css);
        showInner.Value = true;
        h.Frame();
        h.Escape();
        Assert.Equal(new[] { "inner EscapeKey" }, log);
        log.Clear();
        h.Click("inner");
        Assert.Empty(log);
        h.Click("outer");
        Assert.Equal(new[] { "inner PointerDownOutside" }, log);
        log.Clear();
        // 外部的按钮：先是指针按下在外部（两层都收到，自上而下），随后焦点移到它上面（焦点移出，同样两层都收到；
        // 这里的层不会因 OnDismiss 卸载，所以与 Radix 一样两次都报告）
        h.Click("out");
        Assert.Equal(new[] { "inner PointerDownOutside", "outer PointerDownOutside", "inner FocusOutside", "outer FocusOutside" }, log);
    }

    // ------------------------------------------------------------ FocusScope

    [Fact]
    public void Focus_scope_loops_without_trapping_and_restores_focus_on_unmount()
    {
        var show = new State<bool>(false);
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                new UIButton { Id = "before", Children = { "b" } },
                show.Value
                    ? new FocusScope
                    {
                        Loop = true,
                        Children = [new Div { Id = "scope", TabIndex = -1, Children = { new UIButton { Id = "s1" }, new UIButton { Id = "s2" } } }],
                    }
                    : null,
            ],
        }, Css);
        h.ById("before").Focus();
        show.Value = true;
        h.Frame();
        Assert.Same(h.ById("s1"), h.Focused);
        h.Tab();
        Assert.Same(h.ById("s2"), h.Focused);
        h.Tab();
        Assert.Same(h.ById("s1"), h.Focused);
        // 不困住：程序把焦点移到外面可以
        h.ById("before").Focus();
        h.Frame();
        Assert.Same(h.ById("before"), h.Focused);
        h.ById("s2").Focus();
        show.Value = false;
        h.Frame();
        Assert.Same(h.ById("before"), h.Focused);
    }

    [Fact]
    public void Trapped_scope_gives_focus_to_container_when_focused_element_is_removed()
    {
        var showButton = new State<bool>(true);
        var h = new UIHarness(() => new FocusScope
        {
            Trapped = true,
            Children = [new Div { Id = "scope", TabIndex = -1, Children = { showButton.Value ? new UIButton { Id = "only" } : null } }],
        }, Css);
        Assert.Same(h.ById("only"), h.Focused);
        showButton.Value = false;
        h.Frame();
        Assert.Same(h.ById("scope"), h.Focused);
    }

    // ------------------------------------------------------------ RovingFocusGroup

    [Fact]
    public void Roving_focus_group_has_one_tab_stop_and_arrow_keys_move_focus()
    {
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                new RovingFocusGroup
                {
                    Orientation = Orientation.Horizontal,
                    Loop = true,
                    Children =
                    [
                        new RovingFocusGroupItem { AsChild = true, Children = [new UIButton { Id = "r1" }] },
                        new RovingFocusGroupItem { AsChild = true, Focusable = false, Children = [new UIButton { Id = "r2" }] },
                        new RovingFocusGroupItem { AsChild = true, Children = [new UIButton { Id = "r3" }] },
                    ],
                },
                new UIButton { Id = "after" },
            ],
        }, Css);
        Assert.Equal(0, h.ById("r1").TabIndex);
        Assert.Equal(-1, h.ById("r2").TabIndex);
        Assert.Equal(-1, h.ById("r3").TabIndex);
        h.Tab();
        Assert.Same(h.ById("r1"), h.Focused);
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Same(h.ById("r3"), h.Focused); // 跳过不可聚焦的项
        Assert.Equal(0, h.ById("r3").TabIndex);
        Assert.Equal(-1, h.ById("r1").TabIndex);
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Same(h.ById("r1"), h.Focused); // 回绕
        h.Key(PhysicalKey.ArrowDown, "ArrowDown"); // 水平组不响应上下
        Assert.Same(h.ById("r1"), h.Focused);
        h.Tab();
        Assert.Same(h.ById("after"), h.Focused); // 组只占一个 Tab 位
        h.Tab(shift: true);
        Assert.Same(h.ById("r1"), h.Focused);
    }

    // ------------------------------------------------------------ PresenceGate、ScrollLock、Props 更新

    [Fact]
    public void Presence_gate_mounts_and_unmounts_and_props_update_after_mount()
    {
        var present = new State<bool>(true);
        var h = new UIHarness(() => new PresenceGate { Present = present.Value, Children = [new Div { Id = "content" }] }, Css);
        Assert.NotNull(h.TryById("content"));
        present.Value = false;
        h.Frame();
        Assert.Null(h.TryById("content"));
        present.Value = true;
        h.Frame();
        Assert.NotNull(h.TryById("content"));
    }

    [Fact]
    public void Scroll_lock_locks_viewport_while_enabled()
    {
        var enabled = new State<bool>(true);
        var h = new UIHarness(() => new ScrollLock { Enabled = enabled.Value, Children = [new Div { Id = "allowed" }] }, Css);
        Assert.True(h.Root.IsScrollLockActive);
        Assert.Equal("hidden", h.Root.RootElement.GetStyleOverride("overflow"));
        enabled.Value = false;
        h.Frame();
        Assert.False(h.Root.IsScrollLockActive);
        Assert.Null(h.Root.RootElement.GetStyleOverride("overflow"));
    }

    // ------------------------------------------------------------ Popper 箭头与 hideWhenDetached

    [Fact]
    public void Arrow_is_positioned_and_hide_when_detached_hides_content_scrolled_out_of_view()
    {
        var h = new UIHarness(() => new Div
        {
            Class = "clip",
            Id = "clip",
            Children =
            [
                new Popover.Root
                {
                    DefaultOpen = true,
                    Children =
                    [
                        new Div { Class = "at", Style = "left: 100px; top: 50px", Children = [new Popover.Trigger { Attributes = new() { ["id"] = "trigger" }, Children = ["t"] }] },
                        new Popover.Portal
                        {
                            Children =
                            [
                                new Popover.Content
                                {
                                    Class = "pop",
                                    SideOffset = 2,
                                    HideWhenDetached = true,
                                    Children = [new Popover.Arrow { Width = 10, Height = 5 }],
                                },
                            ],
                        },
                    ],
                },
                new Div { Class = "tall" },
            ],
        }, Css);
        var pop = h.FindAll(e => e.HasClass("pop")).Single();
        // 箭头高 5：内容在触发器下方 2 + 5
        Assert.Equal(new Rect(90, 50 + 30 + 2 + 5, 100, 60), pop.GetBoundingClientRect());
        var arrow = h.FindAll(e => e.GetAttribute("data-aw-popper-arrow") is not null).Single();
        Assert.Equal("bottom", arrow.GetAttribute("data-side"));
        var ar = arrow.GetBoundingClientRect();
        Assert.Equal(new Rect(135, 82, 10, 5), ar); // 水平居中对准锚点，贴在内容上边之外
        Assert.Null(pop.Parent!.GetStyleOverride("visibility"));
        // 把锚点滚出裁剪区域：浮层隐藏且不接收指针
        h.ById("clip").ScrollController.JumpTo(new Point(0, 300));
        h.Frame();
        Assert.Equal("hidden", pop.Parent!.GetStyleOverride("visibility"));
        Assert.Equal("none", pop.Parent!.GetStyleOverride("pointer-events"));
    }

    [Fact]
    public void Modal_popover_traps_focus_and_blocks_outside()
    {
        int clicks = 0;
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                new UIButton { Id = "main", Style = "position: absolute; left: 400px; top: 400px", OnClick = _ => clicks++ },
                new Popover.Root
                {
                    Modal = true,
                    Children =
                    [
                        new Div { Class = "at", Style = "left: 100px; top: 50px", Children = [new Popover.Trigger { Attributes = new() { ["id"] = "trigger" }, Children = ["t"] }] },
                        new Popover.Portal { Children = [new Popover.Content { Class = "pop", Children = [new UIButton { Id = "p1" }, new UIButton { Id = "p2" }] }] },
                    ],
                },
            ],
        }, Css);
        h.Click("trigger");
        Assert.True(h.Root.IsPointerBlockingActive);
        Assert.Same(h.ById("p1"), h.Focused);
        h.Tab();
        h.Tab();
        Assert.Same(h.ById("p1"), h.Focused);
        h.ClickAt(new Point(440, 415));
        Assert.Equal(0, clicks);
        Assert.Empty(h.FindAll(e => e.HasClass("pop")));
        Assert.Same(h.ById("trigger"), h.Focused);
    }
}
