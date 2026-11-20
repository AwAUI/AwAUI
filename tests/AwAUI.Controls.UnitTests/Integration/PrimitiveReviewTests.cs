using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>第九批复查：与 Radix 语义对照的边界情况（Slot 中阻止默认动作、对话框中的浮层、受控关闭后的焦点恢复）。</summary>
public class PrimitiveReviewTests
{
    const string Css = """
        root { margin: 0; }
        button { width: 80px; height: 30px; padding: 0; margin: 0; border-width: 0; display: block; }
        .at { position: absolute; }
        .pop { width: 160px; height: 100px; background-color: #eeeeee; z-index: 50; }
        .dialog { position: fixed; left: 200px; top: 150px; width: 400px; height: 250px; background-color: white; z-index: 50; }
        .box { position: absolute; left: 600px; top: 20px; width: 100px; height: 60px; }
        """;

    static AttributeDictionary IdAttr(string id) => new() { ["id"] = id };

    static bool Has(UIHarness h, string cls) => h.FindAll(e => e.HasClass(cls)).Count > 0;

    [Fact]
    public void As_child_trigger_child_handler_can_prevent_the_toggle()
    {
        // Radix：Trigger 的 onClick 是 composeEventHandlers(props.onClick, onOpenToggle)，Slot 先调用子元素的处理器；
        // 子元素 preventDefault 后内部的切换不执行
        bool prevent = true;
        int childClicks = 0;
        var h = new UIHarness(() => new Popover.Root
        {
            Children =
            [
                new Popover.Trigger
                {
                    AsChild = true,
                    Children =
                    [
                        new UIButton
                        {
                            Id = "t",
                            OnClick = e =>
                            {
                                childClicks++;
                                if (prevent) e.PreventDefault();
                            },
                        },
                    ],
                },
                new Popover.Portal { Children = [new Popover.Content { Class = "pop", Children = [new Popover.Close { AsChild = true, Children = [new UIButton { Id = "c", OnClick = e => { if (prevent) e.PreventDefault(); } }] }] }] },
            ],
        }, Css);
        h.Click("t");
        Assert.Equal(1, childClicks);
        Assert.False(Has(h, "pop"));
        prevent = false;
        h.Click("t");
        Assert.True(Has(h, "pop"));
        // Close 同样：子元素阻止默认动作时不关闭
        prevent = true;
        h.Click("c");
        Assert.True(Has(h, "pop"));
        prevent = false;
        h.Click("c");
        Assert.False(Has(h, "pop"));
    }

    [Fact]
    public void Trigger_handler_from_spread_attributes_can_prevent_the_toggle()
    {
        var attrs = new AttributeDictionary { ["id"] = "t", ["onclick"] = (Action<PointerEvent>)(e => e.PreventDefault()) };
        var h = new UIHarness(() => new Dialog.Root
        {
            Children =
            [
                new Dialog.Trigger { Attributes = attrs, Children = ["Open"] },
                new Dialog.Portal { Children = [new Dialog.Content { Class = "dialog" }] },
            ],
        }, Css);
        h.Click("t");
        Assert.False(Has(h, "dialog"));
    }

    [Fact]
    public void Pointer_down_in_dialog_outside_nested_popover_closes_only_the_popover()
    {
        var h = new UIHarness(() => new Dialog.Root
        {
            DefaultOpen = true,
            Children =
            [
                new Dialog.Trigger { Attributes = IdAttr("dtrigger"), Children = ["Edit"] },
                new Dialog.Portal
                {
                    Children =
                    [
                        new Dialog.Content
                        {
                            Class = "dialog",
                            Children =
                            [
                                new UIButton { Id = "a", Children = { "A" } },
                                new Popover.Root
                                {
                                    Children =
                                    [
                                        new Popover.Trigger { Attributes = IdAttr("ptrigger"), Children = ["More"] },
                                        new Popover.Portal { Children = [new Popover.Content { Class = "pop", Side = Side.Right, Children = [new UIButton { Id = "pinner" }] }] },
                                    ],
                                },
                                new UIButton { Id = "b", Style = "margin-top: 100px", Children = { "B" } },
                            ],
                        },
                    ],
                },
            ],
        }, Css);
        Assert.True(Has(h, "dialog"));
        h.Click("ptrigger");
        Assert.True(Has(h, "pop"));
        h.Click("b");
        Assert.False(Has(h, "pop"));
        Assert.True(Has(h, "dialog"));
        // 焦点仍在对话框中（指针在外部交互过：浮层不把焦点交回触发器，焦点落在点击的按钮上）
        Assert.Same(h.ById("b"), h.Focused);
    }

    [Fact]
    public void Controlled_close_from_outside_restores_focus_to_trigger()
    {
        var open = new State<bool>(false);
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                new Dialog.Root
                {
                    Open = open.Value,
                    OnOpenChange = v => open.Value = v,
                    Children =
                    [
                        new Dialog.Trigger { Attributes = IdAttr("dtrigger"), Children = ["Edit"] },
                        new Dialog.Portal { Children = [new Dialog.Content { Class = "dialog", Children = [new UIButton { Id = "a" }] }] },
                    ],
                },
            ],
        }, Css);
        h.Click("dtrigger");
        Assert.True(open.Peek());
        Assert.Same(h.ById("a"), h.Focused);
        open.Value = false;
        h.Frame();
        Assert.False(Has(h, "dialog"));
        Assert.Same(h.ById("dtrigger"), h.Focused);
        Assert.False(h.Root.IsPointerBlockingActive);
        Assert.False(h.Root.IsScrollLockActive);
    }

    [Fact]
    public void Opening_a_second_popover_closes_the_first()
    {
        static Node Pop(string name, float left) => new Div
        {
            Class = "at",
            Style = $"left: {left}px; top: 20px",
            Children =
            [
                new Popover.Root
                {
                    Children =
                    [
                        new Popover.Trigger { Attributes = IdAttr("t" + name), Children = [name] },
                        new Popover.Portal { Children = [new Popover.Content { Class = "pop p" + name }] },
                    ],
                },
            ],
        };
        var h = new UIHarness(() => new Div { Children = [Pop("1", 20), Pop("2", 400)] }, Css);
        h.Click("t1");
        Assert.True(Has(h, "p1"));
        h.Click("t2");
        Assert.False(Has(h, "p1"));
        Assert.True(Has(h, "p2"));
        Assert.Single(h.Root.Overlay.Layers);
    }

    [Fact]
    public void Dialog_without_tabbables_focuses_content_and_tab_keeps_focus_there()
    {
        var h = new UIHarness(() => new Dialog.Root
        {
            Children =
            [
                new Dialog.Trigger { Attributes = IdAttr("dtrigger"), Children = ["Edit"] },
                new Dialog.Portal { Children = [new Dialog.Content { Class = "dialog", Children = [new Div { Children = { "text only" } }] }] },
            ],
        }, Css);
        h.Click("dtrigger");
        var content = h.FindAll(e => e.HasClass("dialog")).Single();
        Assert.Same(content, h.Focused);
        h.Tab();
        Assert.Same(content, h.Focused);
        h.Tab(shift: true);
        Assert.Same(content, h.Focused);
        h.Escape();
        Assert.Same(h.ById("dtrigger"), h.Focused);
    }

    [Fact]
    public void Modal_popover_trigger_click_closes_without_reopening()
    {
        var h = new UIHarness(() => new Div
        {
            Class = "at",
            Style = "left: 100px; top: 100px",
            Children =
            [
                new Popover.Root
                {
                    Modal = true,
                    Children =
                    [
                        new Popover.Trigger { Attributes = IdAttr("t"), Children = ["Open"] },
                        new Popover.Portal { Children = [new Popover.Content { Class = "pop", Children = [new UIButton { Id = "in" }] }] },
                    ],
                },
            ],
        }, Css);
        h.Click("t");
        Assert.True(Has(h, "pop"));
        Assert.True(h.Root.IsPointerBlockingActive);
        // 模态时触发器也在外部（被屏蔽）：按下关闭浮层，点击不会落到触发器上再次打开
        h.Click("t");
        Assert.False(Has(h, "pop"));
        Assert.Same(h.ById("t"), h.Focused);
        Assert.False(h.Root.IsPointerBlockingActive);
    }
}
