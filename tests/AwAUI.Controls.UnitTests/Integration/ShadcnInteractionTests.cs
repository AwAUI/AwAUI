using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// Shadcn S1 组件的键盘与指针验收（design/22「质量门槛」第 1 条）：每个用例通过无头端到端装配
/// 触碰真实组件的角色、ARIA 状态与交互处理器。组件样式来自 <see cref="ShadcnTheme.Register"/>，
/// 因此 Tailwind 工具类与 Tailwind 工具类带来的布局在测试中同样生效。
/// </summary>
public class ShadcnInteractionTests
{
    static ShadcnInteractionTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element? Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault();

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slot(h, slot) ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    // ------------------------------------------------------------ 按钮

    [Fact]
    public void Button_exposes_variant_state_and_activates_with_keyboard()
    {
        int clicks = 0;
        var h = Harness(() => new Button
        {
            Variant = ButtonVariant.Outline,
            Size = ButtonSize.Sm,
            OnClick = _ => clicks++,
            Children = [new UIText("Save")],
        });

        var button = SlotOrThrow(h, "button");
        Assert.IsType<ButtonElement>(button);
        Assert.Equal("outline", A(button, "data-variant"));
        Assert.Equal("sm", A(button, "data-size"));

        h.Tab();
        Assert.True(button.IsFocused);
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, clicks);
        h.Key(PhysicalKey.Space, " ");
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void Button_disabled_ignores_pointer_and_keyboard()
    {
        int clicks = 0;
        var h = Harness(() => new Button
        {
            Disabled = true,
            OnClick = _ => clicks++,
            Children = [new UIText("Save")],
        });

        var button = SlotOrThrow(h, "button");
        h.Click(button);
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void Button_presses_down_one_pixel_like_the_web()
    {
        var h = Harness(() => new Button { OnClick = _ => { }, Children = [new UIText("Save")] });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        h.Frame();

        var button = SlotOrThrow(h, "button");
        var rest = button.GetBoundingClientRect();
        var center = rest.Center;

        h.PointerMove(center, PointerButtons.None);
        Advance(h, clock, 200);
        var hover = button.GetBoundingClientRect();
        Assert.Equal(rest.Top, hover.Top, 2);

        h.PointerDown(center);
        Advance(h, clock, 200);
        Assert.Equal(hover.Top + 1, button.GetBoundingClientRect().Top, 2);

        h.PointerUp(center);
        Advance(h, clock, 200);
        Assert.Equal(hover.Top, button.GetBoundingClientRect().Top, 2);
    }

    [Fact]
    public void Button_with_a_popup_does_not_press_down()
    {
        var h = Harness(() => new Button
        {
            Rest = new AttributeDictionary { ["aria-haspopup"] = "menu" },
            Children = [new UIText("Menu")],
        });
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        h.Frame();

        var button = SlotOrThrow(h, "button");
        var top = button.GetBoundingClientRect().Top;
        h.PointerDown(button.GetBoundingClientRect().Center);
        Advance(h, clock, 200);
        Assert.Equal(top, button.GetBoundingClientRect().Top, 2);
    }

    static void Advance(UIHarness h, ManualUIClock clock, int milliseconds)
    {
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        h.Root.RunFrame();
    }

    [Fact]
    public void Touch_tap_activates_buttons_and_checkboxes()
    {
        int clicks = 0;
        var h = Harness(() => new Div
        {
            Children =
            [
                new Button { OnClick = _ => clicks++, Children = [new UIText("Save")] },
                new Checkbox { Id = "c" },
            ],
        });

        h.Tap(SlotOrThrow(h, "button"));
        Assert.Equal(1, clicks);

        h.Tap(h.ById("c"));
        Assert.Equal("true", A(h.ById("c"), "aria-checked"));
    }

    // ------------------------------------------------------------ 复选框 / 开关

    [Fact]
    public void Checkbox_toggles_with_pointer_and_keyboard()
    {
        var h = Harness(() => new Checkbox { Id = "c" });
        var box = h.ById("c");

        Assert.Equal(Role.CheckBox, box.Role);
        Assert.Equal("false", A(box, "aria-checked"));
        Assert.Equal("unchecked", A(box, "data-state"));

        h.Click(box);
        Assert.Equal("true", A(box, "aria-checked"));
        Assert.Equal("checked", A(box, "data-state"));
        Assert.NotNull(Slot(h, "checkbox-indicator"));

        h.Tab();
        Assert.True(box.IsFocused);
        h.Key(PhysicalKey.Space, " ");
        Assert.Equal("false", A(box, "aria-checked"));
        Assert.Null(Slot(h, "checkbox-indicator"));
    }

    [Fact]
    public void Checkbox_controlled_state_reports_changes_without_switching()
    {
        bool? reported = null;
        var h = Harness(() => new Checkbox
        {
            Id = "c",
            Checked = true,
            CheckedChanged = v => reported = v,
        });
        var box = h.ById("c");

        Assert.Equal("true", A(box, "aria-checked"));
        h.Click(box);
        Assert.False(reported);
        // 受控：状态来自属性，点击后仍保持勾选
        Assert.Equal("true", A(box, "aria-checked"));
    }

    [Fact]
    public void Checkbox_reports_a_mixed_state()
    {
        bool? mixedCleared = null;
        var h = Harness(() => new Checkbox
        {
            Id = "c",
            Indeterminate = true,
            IndeterminateChanged = v => mixedCleared = v,
        });
        var box = h.ById("c");

        Assert.Equal("mixed", A(box, "aria-checked"));
        Assert.Equal("indeterminate", A(box, "data-state"));

        h.Click(box);
        // 混合状态由调用方通过 Indeterminate 属性清除；激活只请求清除并记录勾选
        Assert.False(mixedCleared);
        Assert.Equal("mixed", A(box, "aria-checked"));
    }

    [Fact]
    public void Switch_toggles_and_respects_disabled()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Switch { Id = "s" },
                new Switch { Id = "off", Disabled = true },
            ],
        });

        var toggle = h.ById("s");
        Assert.Equal(Role.Switch, toggle.Role);
        Assert.Equal("false", A(toggle, "aria-checked"));
        h.Click(toggle);
        Assert.Equal("true", A(toggle, "aria-checked"));

        var disabled = h.ById("off");
        h.Click(disabled);
        Assert.Equal("false", A(disabled, "aria-checked"));
    }

    [Fact]
    public void Label_click_activates_the_associated_checkbox()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Checkbox { Id = "c" },
                new Label { For = "c", Children = [new UIText("Enabled")] },
            ],
        });

        var box = h.ById("c");
        h.Click(h.FindAll(e => A(e, "data-slot") == "label").Single());
        Assert.Equal("true", A(box, "aria-checked"));
    }

    // ------------------------------------------------------------ 标签页

    [Fact]
    public void Tabs_arrow_keys_move_and_activate_in_automatic_mode()
    {
        var h = Harness(() => new Tabs
        {
            DefaultValue = "one",
            Children =
            [
                new TabsList
                {
                    Children =
                    [
                        new TabsTrigger { Value = "one", Children = [new UIText("One")] },
                        new TabsTrigger { Value = "two", Children = [new UIText("Two")] },
                    ],
                },
                new TabsContent { Value = "one", Children = [new UIText("First")] },
                new TabsContent { Value = "two", Children = [new UIText("Second")] },
            ],
        });

        var triggers = h.FindAll(e => e.Role == Role.Tab);
        Assert.Equal(2, triggers.Count);
        Assert.Equal("true", A(triggers[0], "aria-selected"));
        Assert.Equal("false", A(triggers[1], "aria-selected"));
        Assert.Single(h.FindAll(e => e.Role == Role.TabPanel), p => p.IsVisible);

        h.Tab();
        Assert.True(triggers[0].IsFocused);
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(triggers[1].IsFocused);
        Assert.Equal("true", A(triggers[1], "aria-selected"));

        // 只有选中的面板可见
        var visible = Assert.Single(h.FindAll(e => e.Role == Role.TabPanel), p => p.IsVisible);
        Assert.Equal("active", A(visible, "data-state"));
    }

    [Fact]
    public void Tabs_manual_mode_requires_activation()
    {
        var h = Harness(() => new Tabs
        {
            DefaultValue = "one",
            ActivationMode = TabsActivationMode.Manual,
            Children =
            [
                new TabsList
                {
                    Children =
                    [
                        new TabsTrigger { Value = "one", Children = [new UIText("One")] },
                        new TabsTrigger { Value = "two", Children = [new UIText("Two")] },
                    ],
                },
                new TabsContent { Value = "one", Children = [new UIText("First")] },
                new TabsContent { Value = "two", Children = [new UIText("Second")] },
            ],
        });

        var triggers = h.FindAll(e => e.Role == Role.Tab);
        h.Tab();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(triggers[1].IsFocused);
        Assert.Equal("false", A(triggers[1], "aria-selected"));

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("true", A(triggers[1], "aria-selected"));
    }

    // ------------------------------------------------------------ 单选组 / 切换组

    [Fact]
    public void RadioGroup_arrow_keys_move_focus_and_selection()
    {
        var h = Harness(() => new RadioGroup
        {
            DefaultValue = "a",
            Children =
            [
                new RadioGroupItem { Id = "ra", Value = "a" },
                new RadioGroupItem { Id = "rb", Value = "b" },
            ],
        });

        var first = h.ById("ra");
        var second = h.ById("rb");
        Assert.Equal(Role.Radio, first.Role);
        Assert.Equal("true", A(first, "aria-checked"));
        Assert.Equal("false", A(second, "aria-checked"));

        h.Tab();
        Assert.True(first.IsFocused);
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.True(second.IsFocused);
        Assert.Equal("true", A(second, "aria-checked"));
        Assert.Equal("false", A(first, "aria-checked"));
    }

    [Fact]
    public void ToggleGroup_click_toggles_and_arrows_only_move_focus()
    {
        var h = Harness(() => new ToggleGroup
        {
            DefaultValue = "",
            Children =
            [
                new ToggleGroupItem { Value = "list", Children = [new UIText("List")] },
                new ToggleGroupItem { Value = "grid", Children = [new UIText("Grid")] },
            ],
        });

        var items = h.FindAll(e => e.Role == Role.Button);
        Assert.Equal(2, items.Count);

        h.Click(items[0]);
        Assert.Equal("on", A(items[0], "data-state"));
        Assert.Equal("true", A(items[0], "aria-pressed"));

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(items[1].IsFocused);
        Assert.Equal("off", A(items[1], "data-state"));
    }

    // ------------------------------------------------------------ 滑块

    [Fact]
    public void Slider_keyboard_updates_value_and_aria()
    {
        double value = 50;
        var h = Harness(() => new Slider
        {
            Id = "s",
            Min = 0,
            Max = 100,
            Step = 10,
            DefaultValue = 50,
            ValueChanged = v => value = v,
            ValueText = "Volume",
        });

        var slider = h.ById("s");
        Assert.Equal(Role.Slider, slider.Role);
        Assert.Equal("50", A(slider, "aria-valuenow"));
        Assert.Equal("Volume", A(slider, "aria-valuetext"));

        h.Tab();
        Assert.True(slider.IsFocused);
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(60, value);
        Assert.Equal("60", A(slider, "aria-valuenow"));
        h.Key(PhysicalKey.Home, "Home");
        Assert.Equal(0, value);
        h.Key(PhysicalKey.End, "End");
        Assert.Equal(100, value);
    }

    [Fact]
    public void Slider_disabled_ignores_keys()
    {
        double value = 50;
        var h = Harness(() => new Slider
        {
            Id = "s",
            Disabled = true,
            DefaultValue = 50,
            ValueChanged = v => value = v,
        });

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(50, value);
    }

    // ------------------------------------------------------------ 手风琴 / 折叠

    [Fact]
    public void Accordion_trigger_toggles_its_panel()
    {
        var h = Harness(() => new Accordion
        {
            DefaultValue = "project",
            Collapsible = true,
            Children =
            [
                new AccordionItem
                {
                    Value = "project",
                    Children =
                    [
                        new AccordionTrigger { Children = [new UIText("Project")] },
                        new AccordionContent { Children = [new UIText("Details")] },
                    ],
                },
                new AccordionItem
                {
                    Value = "activity",
                    Children =
                    [
                        new AccordionTrigger { Children = [new UIText("Activity")] },
                        new AccordionContent { Children = [new UIText("Recent changes")] },
                    ],
                },
            ],
        });

        var triggers = h.FindAll(e => A(e, "data-slot") == "accordion-trigger");
        Assert.Equal(2, triggers.Count);
        Assert.Equal("true", A(triggers[0], "aria-expanded"));
        Assert.Equal("false", A(triggers[1], "aria-expanded"));
        Assert.Single(h.FindAll(e => e.Role == Role.Region));

        h.Click(triggers[1]);
        Assert.Equal("true", A(triggers[1], "aria-expanded"));
        Assert.Equal("false", A(triggers[0], "aria-expanded"));

        // 可折叠：再次激活已打开的项目会关闭它
        h.Click(triggers[1]);
        Assert.Equal("false", A(triggers[1], "aria-expanded"));
        Assert.Empty(h.FindAll(e => e.Role == Role.Region));
    }

    [Fact]
    public void Collapsible_trigger_toggles_content_presence()
    {
        var h = Harness(() => new Collapsible
        {
            Children =
            [
                new CollapsibleTrigger { Children = [new UIText("Show details")] },
                new CollapsibleContent { Children = [new UIText("Additional settings")] },
            ],
        });

        var trigger = SlotOrThrow(h, "collapsible-trigger");
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.Null(Slot(h, "collapsible-content"));

        h.Click(trigger);
        Assert.Equal("true", A(trigger, "aria-expanded"));
        Assert.NotNull(Slot(h, "collapsible-content"));
    }

    // ------------------------------------------------------------ 开关按钮

    [Fact]
    public void Toggle_tracks_pressed_state_and_reports_changes()
    {
        bool? reported = null;
        var h = Harness(() => new Toggle
        {
            Id = "t",
            DefaultPressed = false,
            PressedChanged = v => reported = v,
            Children = [new UIText("Bold")],
        });

        var toggle = h.ById("t");
        Assert.Equal("false", A(toggle, "aria-pressed"));
        Assert.Equal("off", A(toggle, "data-state"));

        h.Click(toggle);
        Assert.True(reported);
        Assert.Equal("true", A(toggle, "aria-pressed"));
        Assert.Equal("on", A(toggle, "data-state"));

        h.Key(PhysicalKey.Space, " ");
        Assert.False(reported);
        Assert.Equal("false", A(toggle, "aria-pressed"));
    }
}
