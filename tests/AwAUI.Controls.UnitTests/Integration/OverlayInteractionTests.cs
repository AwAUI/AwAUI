using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>
/// Focus, dismissal and timing coverage for the menu / overlay, tooltip and repeating button surfaces
/// called out by the 9c handoff: outside dismissal, disabled items, delayed tooltips and hold-to-repeat.
/// </summary>
public class OverlayInteractionTests
{
    static List<Element> Popups(UIHarness h) =>
        h.FindAll(e => e.Role == Role.Menu && e.IsVisible);

    [Fact]
    public void MenuFlyout_closes_on_escape_and_outside_pointer()
    {
        var h = new UIHarness(() => new MenuFlyout
        {
            Trigger = "Actions",
            Children =
            [
                new MenuFlyoutItem { Children = [new UIText("Rename")] },
                new MenuFlyoutItem { Children = [new UIText("Archive")] },
            ],
        });

        var trigger = h.FindAll(e => e.GetAttribute("part") == "trigger").Single();
        h.Click(trigger);
        Assert.Single(Popups(h));

        h.Escape();
        Assert.Empty(Popups(h));
        Assert.True(trigger.IsFocused);

        h.Click(trigger);
        Assert.Single(Popups(h));
        h.ClickAt(new Point(5, 595));
        Assert.Empty(Popups(h));
    }

    [Fact]
    public void MenuFlyout_keyboard_skips_disabled_items()
    {
        var h = new UIHarness(() => new MenuFlyout
        {
            Trigger = "Actions",
            Children =
            [
                new MenuFlyoutItem { Id = "disabled", IsEnabled = false, Children = [new UIText("Archive")] },
                new MenuFlyoutItem { Id = "enabled", Children = [new UIText("Rename")] },
            ],
        });

        var trigger = h.FindAll(e => e.GetAttribute("part") == "trigger").Single();
        trigger.Focus();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");

        Assert.Single(Popups(h));
        Assert.Equal("enabled", h.Focused?.Id);
    }

    [Fact]
    public void ToolTip_waits_for_the_show_delay_and_closes_on_escape()
    {
        var clock = new ManualUIClock();
        var h = new UIHarness(() => new ToolTip
        {
            Content = "More information",
            Children = [new UIButton { Id = "tip", Children = [new UIText("Hover")] }],
        });
        h.Root.Clock = clock;

        h.PointerMove(h.ById("tip").GetBoundingClientRect().Center, PointerButtons.None);
        Assert.Empty(h.FindAll(e => e.Role == Role.Tooltip && e.IsVisible));

        clock.Advance(TimeSpan.FromMilliseconds(520));
        h.Frame();
        Assert.Single(h.FindAll(e => e.Role == Role.Tooltip && e.IsVisible));

        h.Escape();
        Assert.Empty(h.FindAll(e => e.Role == Role.Tooltip && e.IsVisible));
    }

    [Fact]
    public void RepeatButton_repeats_while_held_and_stops_on_release()
    {
        int clicks = 0;
        var clock = new ManualUIClock();
        var h = new UIHarness(() => new RepeatButton
        {
            Delay = 200,
            Interval = 100,
            OnClick = _ => clicks++,
            Children = [new UIText("Hold")],
        });
        h.Root.Clock = clock;

        var button = h.FindAll(e => e.GetAttribute("part") == "button").Single();
        var center = button.GetBoundingClientRect().Center;
        h.PointerDown(center);
        Assert.Equal(0, clicks);

        clock.Advance(TimeSpan.FromMilliseconds(210));
        h.Frame();
        Assert.Equal(1, clicks);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        h.Frame();
        Assert.Equal(3, clicks);

        h.PointerUp(center);
        int settled = clicks;
        clock.Advance(TimeSpan.FromMilliseconds(500));
        h.Frame();
        Assert.Equal(settled, clicks);
    }

    [Fact]
    public void SplitButton_dropdown_activates_a_menu_item_with_the_keyboard()
    {
        int selected = 0;
        var h = new UIHarness(() => new SplitButton
        {
            Children = [new UIText("Save")],
            Items = [new MenuItem { Header = "Save as", OnClick = _ => selected++ }],
        });

        h.Click(h.FindAll(e => e.GetAttribute("part") == "toggle").Single());
        Assert.Single(Popups(h));

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal(1, selected);
        Assert.Empty(Popups(h));
    }
}
