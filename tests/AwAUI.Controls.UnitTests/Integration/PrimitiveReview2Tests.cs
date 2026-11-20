using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>第九批第二次复查：右键外部、变换后的锚点、滚动锁定保持偏移、嵌套模态对话框。</summary>
public class PrimitiveReview2Tests
{
    const string Css = """
        root { margin: 0; }
        button { width: 80px; height: 30px; padding: 0; margin: 0; border-width: 0; display: block; }
        .at { position: absolute; }
        .pop { width: 160px; height: 100px; background-color: #eeeeee; z-index: 50; }
        .overlay { position: fixed; left: 0; top: 0; right: 0; bottom: 0; background-color: rgba(0, 0, 0, 0.5); }
        .dialog { position: fixed; left: 200px; top: 150px; width: 400px; height: 250px; background-color: white; z-index: 50; }
        .dialog2 { position: fixed; left: 250px; top: 200px; width: 200px; height: 120px; background-color: white; z-index: 50; }
        .spacer { height: 3000px; }
        """;

    static AttributeDictionary IdAttr(string id) => new() { ["id"] = id };

    static bool Has(UIHarness h, string cls) => h.FindAll(e => e.HasClass(cls)).Count > 0;

    static void PointerClick(UIHarness h, Point p, PointerButtons button, KeyModifiers mods = KeyModifiers.None)
    {
        var t = TimeSpan.FromSeconds(100);
        h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p, PointerButtons.None, PointerButtons.None, mods, t));
        h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, button, button, mods, t));
        h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, button, PointerButtons.None, mods, t));
        h.Frame();
    }

    static Node SimpleDialog(NodeList? inner = null) => new Dialog.Root
    {
        Children =
        [
            new Div { Class = "at", Style = "left: 20px; top: 20px", Children = [new Dialog.Trigger { Attributes = IdAttr("dtrigger"), Children = ["Edit"] }] },
            new Dialog.Portal
            {
                Children =
                [
                    new Dialog.Overlay { Class = "overlay" },
                    new Dialog.Content { Class = "dialog", Children = inner ?? [new UIButton { Id = "a", Children = { "A" } }] },
                ],
            },
        ],
    };

    [Fact]
    public void Right_click_outside_modal_dialog_does_not_close_it()
    {
        // Radix DialogContentModal：右键（或 Ctrl+左键）点击外部相当于右键点击遮罩，不关闭
        var h = new UIHarness(() => new Div { Children = [SimpleDialog()] }, Css);
        h.Click("dtrigger");
        Assert.True(Has(h, "dialog"));
        PointerClick(h, new Point(60, 500), PointerButtons.Right);
        Assert.True(Has(h, "dialog"));
        PointerClick(h, new Point(60, 500), PointerButtons.Left, KeyModifiers.Control);
        Assert.True(Has(h, "dialog"));
        PointerClick(h, new Point(60, 500), PointerButtons.Left);
        Assert.False(Has(h, "dialog"));
    }

    [Fact]
    public void Right_click_outside_modal_popover_closes_without_focusing_trigger()
    {
        // Radix PopoverContentModal：右键外部仍然关闭，但不把焦点还给触发器
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                new Popover.Root
                {
                    Modal = true,
                    Children =
                    [
                        new Div { Class = "at", Style = "left: 100px; top: 100px", Children = [new Popover.Trigger { Attributes = IdAttr("t"), Children = ["Open"] }] },
                        new Popover.Portal { Children = [new Popover.Content { Class = "pop", Children = [new UIButton { Id = "in" }] }] },
                    ],
                },
            ],
        }, Css);
        h.Click("t");
        Assert.True(Has(h, "pop"));
        Assert.Same(h.ById("in"), h.Focused);
        PointerClick(h, new Point(600, 500), PointerButtons.Right);
        Assert.False(Has(h, "pop"));
        Assert.NotSame(h.ById("t"), h.Focused);
        h.Click("t");
        Assert.True(Has(h, "pop"));
        PointerClick(h, new Point(600, 500), PointerButtons.Left);
        Assert.False(Has(h, "pop"));
        Assert.Same(h.ById("t"), h.Focused);
    }

    [Fact]
    public void Popover_is_positioned_against_the_transformed_anchor()
    {
        var h = new UIHarness(() => new Popover.Root
        {
            Children =
            [
                new Div
                {
                    Class = "at",
                    Style = "left: 100px; top: 100px; transform: translate(200px, 50px)",
                    Children = [new Popover.Trigger { Attributes = IdAttr("t"), Children = ["Open"] }],
                },
                new Popover.Portal { Children = [new Popover.Content { Class = "pop", Align = PopperAlign.Start }] },
            ],
        }, Css);
        var t = h.ById("t");
        Assert.Equal(new Rect(300, 150, 80, 30), t.GetBoundingClientRect());
        Assert.Equal(new Rect(100, 100, 80, 30), t.BoundingRect);
        h.Click("t");
        var pop = h.FindAll(e => e.HasClass("pop")).Single();
        Assert.Equal(new Rect(300, 180, 160, 100), pop.GetBoundingClientRect());
    }

    [Fact]
    public void Popover_follows_a_transformed_scroll_anchor_in_the_same_painted_frame()
    {
        var h = new UIHarness(() => new Popover.Root
        {
            Children =
            [
                new Div
                {
                    Id = "scroll", Style = "position: absolute; left: 20px; top: 40px; width: 200px; height: 180px; overflow: auto; transform: translateX(240px)",
                    Children =
                    [
                        new Div { Style = "height: 80px" },
                        new Popover.Trigger { Attributes = IdAttr("t"), Children = ["Open"] },
                        new Div { Style = "height: 300px" },
                    ],
                },
                new Popover.Portal { Children = [new Popover.Content { Class = "pop", Align = PopperAlign.Start, HideWhenDetached = true }] },
            ],
        }, Css);
        var anchor = h.ById("t");
        Assert.Equal(new Rect(260, 40, 200, 180), anchor.GetClippingRect());
        h.Click(anchor);
        var popup = h.FindAll(e => e.HasClass("pop")).Single();
        Assert.Equal(150, popup.GetBoundingClientRect().Top);
        h.ById("scroll").ScrollController.JumpTo(new Point(0, 30));
        h.Root.RunFrame();
        Assert.Equal(120, popup.GetBoundingClientRect().Top);
        using var pixels = h.Render();
        Assert.Equal(Color.Parse("#eeeeee"), pixels.GetPixel(400, 125));
    }

    [Fact]
    public void Scroll_lock_keeps_the_viewport_scroll_position()
    {
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                new Div { Class = "spacer" },
                new Dialog.Root
                {
                    Children =
                    [
                        new Div { Style = "position: fixed; left: 20px; top: 20px", Children = [new Dialog.Trigger { Attributes = IdAttr("dtrigger"), Children = ["Edit"] }] },
                        new Dialog.Portal { Children = [new Dialog.Overlay { Class = "overlay" }, new Dialog.Content { Class = "dialog", Children = [new UIButton { Id = "a" }] }] },
                    ],
                },
            ],
        }, Css);
        h.Wheel(new Point(400, 300), -150);
        var root = h.Root.RootElement.ScrollController;
        float y = root.Offset.Y;
        Assert.True(y > 0);
        h.Click("dtrigger");
        Assert.True(Has(h, "dialog"));
        Assert.True(h.Root.IsScrollLockActive);
        // 浏览器中 overflow: hidden 保留 scrollTop：锁定不改变视口的滚动位置
        Assert.Equal(y, root.Offset.Y);
        h.Escape();
        Assert.False(Has(h, "dialog"));
        Assert.Equal(y, root.Offset.Y);
        h.Wheel(new Point(400, 300), -50);
        Assert.True(root.Offset.Y > y);
    }

    [Fact]
    public void Nested_modal_dialogs_close_innermost_first_and_restore_blocking()
    {
        var h = new UIHarness(() => new Div
        {
            Children =
            [
                SimpleDialog(
                [
                    new UIButton { Id = "a", Children = { "A" } },
                    new Dialog.Root
                    {
                        Children =
                        [
                            new Dialog.Trigger { Attributes = IdAttr("t2"), Children = ["More"] },
                            new Dialog.Portal { Children = [new Dialog.Content { Class = "dialog2", Children = [new UIButton { Id = "x", Children = { "X" } }] }] },
                        ],
                    },
                ]),
            ],
        }, Css);
        h.Click("dtrigger");
        h.Click("t2");
        Assert.True(Has(h, "dialog2"));
        Assert.Same(h.ById("x"), h.Focused);
        // 内层模态时外层内容被屏蔽：点击外层的按钮只关闭内层，焦点回到内层的触发器
        h.ClickAt(h.Box("a").Center);
        Assert.False(Has(h, "dialog2"));
        Assert.True(Has(h, "dialog"));
        Assert.Same(h.ById("t2"), h.Focused);
        // 外层仍是模态
        Assert.True(h.Root.IsPointerBlockingActive);
        Assert.True(h.Root.IsScrollLockActive);
        h.Click("a");
        Assert.Same(h.ById("a"), h.Focused);
        h.Click("t2");
        h.Escape();
        Assert.True(Has(h, "dialog"));
        Assert.Same(h.ById("t2"), h.Focused);
        h.Escape();
        Assert.False(Has(h, "dialog"));
        Assert.Same(h.ById("dtrigger"), h.Focused);
        Assert.False(h.Root.IsPointerBlockingActive);
        Assert.False(h.Root.IsScrollLockActive);
    }

    [Fact]
    public void Popper_follows_a_scrolling_anchor_in_the_same_frame()
    {
        const string css = Css + ".scroller { position: absolute; left: 300px; top: 100px; width: 300px; height: 300px; overflow: auto; } .tall { height: 2000px; }";
        var h = new UIHarness(() => new Div
        {
            Class = "scroller",
            Id = "scroller",
            Children =
            [
                new Popover.Root
                {
                    Children =
                    [
                        new Div { Class = "at", Style = "left: 20px; top: 100px", Children = [new Popover.Trigger { Attributes = IdAttr("t"), Children = ["Open"] }] },
                        new Popover.Portal { Children = [new Popover.Content { Class = "pop", SideOffset = 4, Style = "background-color: #ff0000" }] },
                    ],
                },
                new Div { Class = "tall" },
            ],
        }, css);
        h.Click("t");
        var pop = h.FindAll(e => e.HasClass("pop")).Single();
        for (int step = 1; step <= 4; step++)
        {
            h.ById("scroller").ScrollController.JumpTo(new Point(0, 10 * step));
            h.Root.RunFrame(); // 恰好一帧
            var anchor = h.ById("t").GetBoundingClientRect();
            var r = pop.GetBoundingClientRect();
            Assert.Equal(anchor.Bottom + 4, r.Y);
            // 本帧绘制的也是新位置（不是上一帧的）
            using var bmp = h.Render();
            Assert.Equal(Color.Parse("#ff0000"), bmp.GetPixel((int)(r.X + 5), (int)(r.Y + 2)));
            Assert.NotEqual(Color.Parse("#ff0000"), bmp.GetPixel((int)(r.X + 5), (int)(r.Bottom + 3)));
        }
    }
}
