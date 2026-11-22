using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.UI.Accessibility;

namespace AwAUI.UI.UnitTests.Accessibility;

/// <summary>design/10「平台桥」：无头桥接收快照，并把动作回传给元素的既有行为。</summary>
public class AccessibilityBridgeTests
{
    [Fact]
    public void Headless_bridge_invokes_focus_set_value_and_increment_through_element_behavior()
    {
        int clicks = 0, ups = 0, downs = 0;
        var root = new UIRoot();
        root.Mount(new Lambda
        {
            Render = () => new Div
            {
                Children =
                {
                    new Button { Id = "btn", OnClick = _ => clicks++, Children = { "保存" } },
                    new Input { Id = "field" },
                    new Div
                    {
                        Id = "slider",
                        Role = Role.Slider,
                        TabIndex = 0,
                        Attributes = { ["aria-valuenow"] = "5", ["aria-valuemin"] = "0", ["aria-valuemax"] = "10" },
                        OnKeyDown = e =>
                        {
                            if (e.Key == "ArrowUp") ups++;
                            else if (e.Key == "ArrowDown") downs++;
                        },
                    },
                },
            },
        });

        var tree = root.Accessibility;
        var bridge = new HeadlessAccessibilityBridge();
        using var connection = tree.ConnectBridge(bridge);

        Assert.Equal(1, bridge.UpdateCount);
        Assert.NotNull(bridge.LastSnapshot);
        Assert.Equal("保存", bridge.Find("btn")!.Name);

        Assert.True(bridge.Perform(AccessibilityActionKind.Invoke, "btn"));
        Assert.Equal(1, clicks);
        Assert.Single(bridge.Requests);

        Assert.True(bridge.Perform(AccessibilityActionKind.Focus, "btn"));
        Assert.Same(root.RootElement.ById("btn"), root.FocusManager.FocusedElement);

        Assert.True(bridge.Perform(AccessibilityActionKind.SetValue, "field", "hello"));
        Assert.Equal("hello", ((InputElement)root.RootElement.ById("field")).Value);

        var slider = tree.FindById("slider")!;
        Assert.True(slider.Actions.HasFlag(AccessibilityAction.Increment));
        Assert.True(slider.Actions.HasFlag(AccessibilityAction.Decrement));
        Assert.True(bridge.Perform(AccessibilityActionKind.Increment, "slider"));
        Assert.True(bridge.Perform(AccessibilityActionKind.Decrement, "slider"));
        Assert.Equal(1, ups);
        Assert.Equal(1, downs);
    }

    [Fact]
    public void Actions_are_rejected_when_not_available_or_when_disabled()
    {
        bool clicked = false;
        var root = new UIRoot();
        root.Mount(new Lambda
        {
            Render = () => new Div
            {
                Children =
                {
                    new Button
                    {
                        Id = "disabled",
                        Disabled = true,
                        OnClick = _ => clicked = true,
                        Children = { "不可用" },
                    },
                    new Div { Id = "plain" },
                },
            },
        });
        var tree = root.Accessibility;
        var bridge = new HeadlessAccessibilityBridge();
        using var connection = tree.ConnectBridge(bridge);

        var disabled = tree.FindById("disabled")!;
        Assert.True(disabled.State.HasFlag(AccessibilityState.Disabled));
        Assert.False(bridge.Perform(AccessibilityActionKind.Invoke, "disabled"));
        Assert.False(clicked);

        var plain = tree.FindById("plain");
        Assert.Null(plain); // 无可访问名称、角色、焦点或动作：装饰性盒子不产生节点
        Assert.False(bridge.Perform(AccessibilityActionKind.Focus, "plain"));
    }

    [Fact]
    public void Scroll_into_view_action_scrolls_the_container_and_bounds_follow_the_scroll()
    {
        var (host, _, clock) = ScrollSupport.Create(new ScrollSupport.ListApp { Rows = 40 });
        var root = host.Root;
        var tree = root.Accessibility;
        var bridge = new HeadlessAccessibilityBridge();
        using var connection = tree.ConnectBridge(bridge);

        var scroller = root.RootElement.ById("scroller");
        var viewport = scroller.GetBoundingClientRect();
        var before = tree.FindById("row-20")!.Bounds;
        Assert.True(before.Top >= viewport.Bottom - 0.5f, "row should start below the scrollport");

        Assert.True(bridge.Perform(AccessibilityActionKind.ScrollIntoView, "row-20"));
        ScrollSupport.Settle(host, clock);

        Assert.True(scroller.ScrollController.OffsetY > 0, "the action should scroll the nearest container");
        var after = tree.FindById("row-20")!.Bounds;
        Assert.True(after.Top < before.Top);
        Assert.InRange(after.Top, viewport.Top - 1, viewport.Bottom + 1);
        Assert.InRange(after.Bottom, viewport.Top - 1, viewport.Bottom + 1);
        Assert.Equal(tree.Version, bridge.LastSnapshot!.Version);
    }
}
