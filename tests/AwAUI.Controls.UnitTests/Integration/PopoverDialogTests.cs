using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Popover 与 Dialog 原语的无头端到端测试：定位（含翻转、平移、随滚动更新）、外部点击、Escape、焦点陷阱与恢复、模态指针屏蔽。</summary>
public class PopoverDialogTests
{
    const string Css = """
        root { margin: 0; }
        button { width: 80px; height: 30px; padding: 0; margin: 0; border-width: 0; display: block; }
        .at { position: absolute; }
        .pop { width: 160px; height: 100px; background-color: #eeeeee; z-index: 50; }
        .overlay { position: fixed; left: 0; top: 0; right: 0; bottom: 0; background-color: rgba(0, 0, 0, 0.5); }
        .dialog { position: fixed; left: 200px; top: 150px; width: 400px; height: 250px; background-color: white; z-index: 50; }
        .box { position: absolute; left: 600px; top: 20px; width: 100px; height: 60px; }
        .scroller { position: absolute; left: 300px; top: 100px; width: 300px; height: 300px; overflow: auto; }
        .spacer { height: 2000px; }
        """;

    static AttributeDictionary IdAttr(string id) => new() { ["id"] = id };

    static Node PopoverAt(float left, float top, Action<Popover.Root>? configure = null, NodeList? content = null)
    {
        var root = new Popover.Root
        {
            Children =
            [
                new Div { Class = "at", Style = $"left: {left}px; top: {top}px", Children = [new Popover.Trigger { Attributes = IdAttr("trigger"), Children = ["Open"] }] },
                new Popover.Portal
                {
                    Children =
                    [
                        new Popover.Content
                        {
                            SideOffset = 4,
                            Class = "pop",
                            Children = content ?? [new UIButton { Id = "inner", Children = { "OK" } }, new Popover.Close { Attributes = IdAttr("close"), Children = ["x"] }],
                        },
                    ],
                },
            ],
        };
        configure?.Invoke(root);
        return root;
    }

    static Element Pop(UIHarness h) => h.FindAll(e => e.HasClass("pop")).Single();

    static bool HasPop(UIHarness h) => h.FindAll(e => e.HasClass("pop")).Count > 0;

    [Fact]
    public void Popover_opens_in_overlay_below_trigger_and_focuses_first_tabbable()
    {
        var h = new UIHarness(() => new Div { Children = [PopoverAt(100, 100), new Div { Class = "box", Id = "outside" }] }, Css);
        Assert.False(HasPop(h));
        Assert.False(h.Root.Overlay.IsAttached);
        var trigger = h.ById("trigger");
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Equal("dialog", trigger.GetAttribute("aria-haspopup"));

        h.Click(trigger);
        Assert.True(HasPop(h));
        var pop = Pop(h);
        Assert.True(h.Root.Overlay.IsAttached);
        Assert.True(h.Root.Overlay.Element.IsInclusiveAncestorOf(pop));
        Assert.Same(h.Root.Overlay.Element, h.Root.RootElement.Children[h.Root.RootElement.Children.Count - 1]);
        // 树序：覆盖层中的内容在触发器之后
        Assert.Equal(-1, Element.CompareTreeOrder(trigger, pop));

        Assert.Equal("true", trigger.GetAttribute("aria-expanded"));
        Assert.Equal("open", trigger.GetAttribute("data-state"));
        Assert.Equal(pop.Id, trigger.GetAttribute("aria-controls"));
        Assert.Equal("dialog", pop.GetAttribute("role"));
        Assert.Equal("open", pop.GetAttribute("data-state"));
        Assert.Equal("bottom", pop.GetAttribute("data-side"));
        Assert.Equal("center", pop.GetAttribute("data-align"));

        // 同一帧内定位：触发器 (100,100,80,30)，内容居中、下方偏移 4
        Assert.Equal(new Rect(100, 100, 80, 30), trigger.GetBoundingClientRect());
        Assert.Equal(new Rect(60, 134, 160, 100), pop.GetBoundingClientRect());
        var wrapper = pop.Parent!;
        Assert.Equal("80px", wrapper.GetStyleOverride("--aw-popper-anchor-width"));
        Assert.Equal("30px", wrapper.GetStyleOverride("--aw-popper-anchor-height"));

        Assert.Same(h.ById("inner"), h.Focused);
    }

    [Fact]
    public void Popover_near_bottom_edge_flips_to_top_and_near_right_edge_shifts()
    {
        var h = new UIHarness(() => PopoverAt(100, 540), Css);
        h.Click("trigger");
        var pop = Pop(h);
        Assert.Equal("top", pop.GetAttribute("data-side"));
        Assert.Equal(new Rect(60, 540 - 4 - 100, 160, 100), pop.GetBoundingClientRect());

        var h2 = new UIHarness(() => PopoverAt(760, 100), Css);
        h2.Click("trigger");
        var pop2 = Pop(h2);
        Assert.Equal("bottom", pop2.GetAttribute("data-side"));
        // 居中时 x = 720，右侧溢出 80：平移到 800 - 160 = 640（limitShift 允许，锚点仍与浮层相邻）
        Assert.Equal(640, pop2.GetBoundingClientRect().X);
    }

    [Fact]
    public void Outside_click_closes_and_trigger_click_toggles_without_reopening()
    {
        var h = new UIHarness(() => new Div { Children = [PopoverAt(100, 100), new Div { Class = "box", Id = "outside" }] }, Css);
        h.Click("trigger");
        Assert.True(HasPop(h));
        h.Click("outside");
        Assert.False(HasPop(h));
        Assert.False(h.Root.Overlay.IsAttached);
        // 在外部交互过：焦点不回到触发器（与 Radix 非模态相同）
        Assert.NotSame(h.ById("trigger"), h.Focused);

        h.Click("trigger");
        Assert.True(HasPop(h));
        h.Click("trigger"); // 在触发器上按下不算外部：触发器自己切换为关闭，不会先关再开
        Assert.False(HasPop(h));
    }

    [Fact]
    public void Click_inside_content_keeps_it_open_and_close_button_closes_and_restores_focus()
    {
        var h = new UIHarness(() => PopoverAt(100, 100), Css);
        h.Click("trigger");
        h.ClickAt(new Point(200, 220)); // 内容中的空白处
        Assert.True(HasPop(h));
        h.Click("close");
        Assert.False(HasPop(h));
        Assert.Same(h.ById("trigger"), h.Focused);
    }

    [Fact]
    public void Escape_closes_popover_and_returns_focus_to_trigger()
    {
        var h = new UIHarness(() => PopoverAt(100, 100), Css);
        h.Click("trigger");
        Assert.Same(h.ById("inner"), h.Focused);
        h.Escape();
        Assert.False(HasPop(h));
        Assert.Same(h.ById("trigger"), h.Focused);
    }

    [Fact]
    public void Controlled_popover_only_reports_open_change()
    {
        var calls = new List<bool>();
        var h = new UIHarness(() => PopoverAt(100, 100, r =>
        {
            r.Open = false;
            r.OnOpenChange = calls.Add;
        }), Css);
        h.Click("trigger");
        Assert.Equal(new[] { true }, calls);
        Assert.False(HasPop(h));
    }

    [Fact]
    public void Popper_follows_anchor_when_its_scroll_container_scrolls()
    {
        var h = new UIHarness(() => new Div
        {
            Class = "scroller",
            Id = "scroller",
            Children = [PopoverAt(20, 100), new Div { Class = "spacer" }],
        }, Css);
        h.Click("trigger");
        var pop = Pop(h);
        var before = pop.GetBoundingClientRect();
        Assert.Equal(new Rect(300 + 20 + 40 - 80, 100 + 100 + 30 + 4, 160, 100), before);
        h.ById("scroller").ScrollController.JumpTo(new Point(0, 50));
        h.Frame();
        var after = pop.GetBoundingClientRect();
        Assert.Equal(before.Y - 50, after.Y);
        Assert.Equal(before.X, after.X);
        // 内容仍在覆盖层中（没有被滚动容器裁剪）
        Assert.True(h.Root.Overlay.Element.IsInclusiveAncestorOf(pop));
    }

    [Fact]
    public void Overlay_content_is_hit_above_main_tree_even_with_high_z_index()
    {
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                PopoverAt(100, 100),
                new Div { Id = "high", Style = "position: absolute; left: 0; top: 120px; width: 400px; height: 200px; z-index: 1000; background-color: red" },
            ],
        }, Css);
        h.Click("trigger");
        var pop = Pop(h);
        var hit = h.Pipeline.HitTest(new Point(100, 200));
        Assert.True(pop.IsInclusiveAncestorOf(hit));
        // 覆盖层根自身不挡住其他地方
        Assert.Same(h.ById("high"), h.Pipeline.HitTest(new Point(350, 300)));
    }

    // ------------------------------------------------------------ Dialog

    static Node DialogTree(Action? onMainClick = null, bool withPopover = false) => new Div
    {
        Children =
        [
            new UIButton { Id = "main", Style = "position: absolute; left: 20px; top: 300px", OnClick = _ => onMainClick?.Invoke(), Children = { "Main" } },
            new Dialog.Root
            {
                Children =
                [
                    new Div { Class = "at", Style = "left: 20px; top: 20px", Children = [new Dialog.Trigger { Attributes = IdAttr("dtrigger"), Children = ["Edit"] }] },
                    new Dialog.Portal
                    {
                        Children =
                        [
                            new Dialog.Overlay { Class = "overlay" },
                            new Dialog.Content
                            {
                                Class = "dialog",
                                Children =
                                [
                                    new Dialog.Title { Children = ["Edit profile"] },
                                    new Dialog.Description { Children = ["Make changes."] },
                                    new UIButton { Id = "a", Children = { "A" } },
                                    new UIButton { Id = "b", Children = { "B" } },
                                    withPopover
                                        ? new Popover.Root
                                        {
                                            Children =
                                            [
                                                new Popover.Trigger { Attributes = IdAttr("ptrigger"), Children = ["More"] },
                                                new Popover.Portal { Children = [new Popover.Content { Class = "pop", Children = [new UIButton { Id = "pinner", Children = { "P" } }] }] },
                                            ],
                                        }
                                        : null,
                                    new Dialog.Close { Attributes = IdAttr("dclose"), Children = ["Close"] },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    static Element DialogContent(UIHarness h) => h.FindAll(e => e.HasClass("dialog")).Single();

    static bool HasDialog(UIHarness h) => h.FindAll(e => e.HasClass("dialog")).Count > 0;

    [Fact]
    public void Dialog_has_aria_wiring_and_traps_focus_and_restores_it()
    {
        var h = new UIHarness(() => DialogTree(), Css);
        h.Click("dtrigger");
        var content = DialogContent(h);
        Assert.Equal("dialog", content.GetAttribute("role"));
        Assert.Equal("true", content.GetAttribute("aria-modal"));
        var title = h.FindAll(e => e.Role == Role.Heading).Single();
        Assert.Equal(title.Id, content.GetAttribute("aria-labelledby"));
        Assert.NotNull(content.GetAttribute("aria-describedby"));
        Assert.Equal(content.Id, h.ById("dtrigger").GetAttribute("aria-controls"));
        Assert.Equal("open", h.FindAll(e => e.HasClass("overlay")).Single().GetAttribute("data-state"));

        // 打开时聚焦第一个可 Tab 的元素；Tab 在内容中回绕
        Assert.Same(h.ById("a"), h.Focused);
        h.Tab();
        Assert.Same(h.ById("b"), h.Focused);
        h.Tab();
        Assert.Same(h.ById("dclose"), h.Focused);
        h.Tab();
        Assert.Same(h.ById("a"), h.Focused);
        h.Tab(shift: true);
        Assert.Same(h.ById("dclose"), h.Focused);

        // 程序把焦点移到外部：被拉回
        h.ById("main").Focus();
        h.Frame();
        Assert.True(content.IsInclusiveAncestorOf(h.Focused));

        h.Click("dclose");
        Assert.False(HasDialog(h));
        Assert.Same(h.ById("dtrigger"), h.Focused);
        Assert.False(h.Root.IsScrollLockActive);
        Assert.False(h.Root.IsPointerBlockingActive);
    }

    [Fact]
    public void Modal_dialog_blocks_pointer_below_and_closes_on_outside_click()
    {
        int mainClicks = 0;
        var h = new UIHarness(() => DialogTree(() => mainClicks++), Css);
        h.Click("main");
        Assert.Equal(1, mainClicks);
        h.Click("dtrigger");
        Assert.True(HasDialog(h));
        Assert.True(h.Root.IsScrollLockActive);
        Assert.True(h.Root.IsPointerBlockingActive);
        // 遮罩之下的按钮收不到点击；点击外部关闭对话框
        h.ClickAt(new Point(60, 315));
        Assert.Equal(1, mainClicks);
        Assert.False(HasDialog(h));
        Assert.Same(h.ById("dtrigger"), h.Focused);
        // 关闭之后恢复
        h.Click("main");
        Assert.Equal(2, mainClicks);
    }

    [Fact]
    public void Escape_closes_nested_layers_innermost_first()
    {
        var h = new UIHarness(() => DialogTree(withPopover: true), Css);
        h.Click("dtrigger");
        h.Click("ptrigger");
        Assert.True(HasPop(h));
        Assert.Same(h.ById("pinner"), h.Focused);
        // 浮层在对话框之上的一层：可以点击（不被模态屏蔽），点击它不关闭对话框
        Assert.Equal(2, h.Root.Overlay.Layers.Count);
        // 包装元素取内容的 z-index（50），与对话框内容同级时按层的打开顺序叠放
        Assert.Equal("50", Pop(h).Parent!.GetStyleOverride("z-index"));
        h.ClickAt(Pop(h).GetBoundingClientRect().Center);
        Assert.True(HasPop(h));
        Assert.True(HasDialog(h));

        h.Escape();
        Assert.False(HasPop(h));
        Assert.True(HasDialog(h));
        Assert.Same(h.ById("ptrigger"), h.Focused);

        h.Escape();
        Assert.False(HasDialog(h));
        Assert.Same(h.ById("dtrigger"), h.Focused);
        Assert.False(h.Root.Overlay.IsAttached);
    }

    [Fact]
    public void Scroll_lock_blocks_wheel_outside_dialog()
    {
        var h = new UIHarness(() => new Div
        {
            Children = [new Div { Class = "scroller", Id = "scroller", Children = [new Div { Class = "spacer" }] }, DialogTree()],
        }, Css);
        var scroller = h.ById("scroller").ScrollController;
        h.Wheel(new Point(450, 250), -100);
        Assert.True(scroller.Offset.Y > 0);
        float y = scroller.Offset.Y;
        h.Click("dtrigger");
        h.Wheel(new Point(310, 120), -100); // 遮罩下、对话框外
        Assert.Equal(y, scroller.Offset.Y);
        Assert.Equal("hidden", h.Root.RootElement.GetStyleOverride("overflow"));
        h.Escape();
        Assert.Null(h.Root.RootElement.GetStyleOverride("overflow"));
    }
}
