using System;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// HoverCard: delayed hover/focus opening, configurable delays through <see cref="UIRoot.Clock"/>, staying open while
/// the card itself is hovered, and closing on Escape or an outside interaction.
/// </summary>
public class HoverCardTests
{
    static HoverCardTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slots(h, slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static bool IsVisible(UIHarness h, string slot) => Slots(h, slot).Any(e => e.IsVisible);

    static UIHarness CardHarness(int openDelay = 400, int closeDelay = 200) =>
        Harness(() => new HoverCard
        {
            OpenDelay = openDelay,
            CloseDelay = closeDelay,
            Children =
            [
                new HoverCardTrigger { Children = [new UIText("Ada")] },
                new HoverCardContent
                {
                    Children = [new Div { Children = [new UIText("Engineer")] }],
                },
            ],
        });

    [Fact]
    public void HoverCard_waits_for_the_open_delay_and_stays_open_while_the_panel_is_hovered()
    {
        var clock = new ManualUIClock();
        var h = CardHarness();
        h.Root.Clock = clock;

        var trigger = SlotOrThrow(h, "hover-card-trigger");
        Assert.Equal("dialog", A(trigger, "aria-haspopup"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.False(IsVisible(h, "hover-card-content"));

        h.PointerMove(trigger.GetBoundingClientRect().Center, PointerButtons.None);
        Assert.False(IsVisible(h, "hover-card-content"));

        clock.Advance(TimeSpan.FromMilliseconds(399));
        h.Frame();
        Assert.False(IsVisible(h, "hover-card-content"));

        clock.Advance(TimeSpan.FromMilliseconds(2));
        h.Frame();

        Assert.True(IsVisible(h, "hover-card-content"));
        Assert.Equal("true", A(trigger, "aria-expanded"));
        var content = SlotOrThrow(h, "hover-card-content");
        Assert.Equal("open", A(content, "data-state"));
        Assert.Equal(trigger.GetAttribute("aria-controls"), content.Id);
        Assert.Contains("Engineer", content.TextContent);

        // Moving from the trigger into the card keeps it open past both delays.
        h.PointerMove(content.GetBoundingClientRect().Center, PointerButtons.None);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        h.Frame();
        Assert.True(content.IsVisible);

        h.PointerMove(new Point(620, 460), PointerButtons.None);
        clock.Advance(TimeSpan.FromMilliseconds(2000));
        h.Frame();
        // Let the exit transition finish before the content is unmounted.
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.Frame();

        Assert.False(IsVisible(h, "hover-card-content"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void HoverCard_opens_immediately_on_keyboard_focus_and_escape_closes_it()
    {
        var clock = new ManualUIClock();
        var h = CardHarness(openDelay: 700, closeDelay: 300);
        h.Root.Clock = clock;

        var trigger = SlotOrThrow(h, "hover-card-trigger");
        h.Tab();

        Assert.True(trigger.IsFocused);
        Assert.True(IsVisible(h, "hover-card-content"));
        Assert.Equal("true", A(trigger, "aria-expanded"));

        h.Escape();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.Frame();

        Assert.False(IsVisible(h, "hover-card-content"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
        Assert.True(trigger.IsFocused);
    }

    [Fact]
    public void HoverCard_outside_click_dismisses_the_card()
    {
        var h = CardHarness();
        var clock = new ManualUIClock();
        h.Root.Clock = clock;
        var trigger = SlotOrThrow(h, "hover-card-trigger");
        h.Tab();
        Assert.True(IsVisible(h, "hover-card-content"));

        h.ClickAt(new Point(620, 460));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.Frame();

        Assert.False(IsVisible(h, "hover-card-content"));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }
}
