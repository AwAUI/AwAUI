using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 MessageScroller acceptance: part slots, auto-follow when content is appended while at the end, and the jump
/// buttons appearing only when there is somewhere to jump to.
/// </summary>
public class MessageScrollerTests
{
    static MessageScrollerTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Node View(IReadOnlyList<string> items, bool includeStart = false)
    {
        var content = new NodeList();
        foreach (var item in items)
        {
            content.Add(new MessageScrollerItem
            {
                ScrollAnchor = true,
                Children = [new Div { Style = "height: 40px", Children = [new UIText(item)] }],
            });
        }

        var viewport = new MessageScrollerViewport
        {
            Children = [new MessageScrollerContent { Children = content }],
        };

        var scrollerChildren = new NodeList { viewport };
        if (includeStart)
            scrollerChildren.Add(new MessageScrollerButton { Direction = MessageScrollerDirection.Start });
        scrollerChildren.Add(new MessageScrollerButton { Direction = MessageScrollerDirection.End });

        return new Div
        {
            Style = "height: 140px; width: 320px",
            Children =
            [
                new MessageScrollerProvider
                {
                    AutoFollow = true,
                    Children = [new MessageScroller { Children = scrollerChildren }],
                },
            ],
        };
    }

    [Fact]
    public void MessageScroller_renders_slots_and_stays_at_the_end_when_content_is_appended()
    {
        var items = new List<string> { "0", "1", "2", "3", "4" };
        var version = new State<int>(0);
        var h = Harness(() =>
        {
            _ = version.Value;
            return View(items);
        });

        Assert.Single(Slots(h, "message-scroller"));
        Assert.Single(Slots(h, "message-scroller-viewport"));
        Assert.Single(Slots(h, "message-scroller-content"));
        Assert.Equal(5, Slots(h, "message-scroller-item").Count);
        Assert.Single(Slots(h, "message-scroller-button"));

        var viewport = Slots(h, "message-scroller-viewport").Single();
        var controller = viewport.ScrollController!;
        Assert.True(controller.MaxOffset.Y > 0, "the content must overflow the viewport");
        Assert.True(Math.Abs(controller.Offset.Y - controller.MaxOffset.Y) <= 0.5,
            "auto-follow starts pinned to the end");

        double before = controller.Offset.Y;
        items.Add("5");
        items.Add("6");
        version.Value = 1;
        h.Frame();

        Assert.True(controller.MaxOffset.Y > before, "appended content grows the scroll range");
        Assert.True(Math.Abs(controller.Offset.Y - controller.MaxOffset.Y) <= 0.5,
            "appending while at the end keeps the view pinned to the end");
    }

    [Fact]
    public void MessageScroller_parts_use_the_registry_transition_and_viewport_classes()
    {
        var h = Harness(() => View(new List<string> { "0", "1", "2", "3", "4" }));

        var viewport = Slots(h, "message-scroller-viewport").Single();
        var viewportClass = A(viewport, "class")!;
        Assert.Contains("data-autoscrolling:scrollbar-none", viewportClass);
        Assert.Contains("data-pending-scroll:invisible", viewportClass);
        Assert.DoesNotContain("focus-visible:ring", viewportClass);

        var end = Slots(h, "message-scroller-button").Single(e => A(e, "data-direction") == "end");
        var endClass = A(end, "class")!;
        Assert.Contains("data-[active=false]:duration-400", endClass);
        Assert.Contains("data-[active=false]:ease-[cubic-bezier(0.7,0,0.84,0)]", endClass);
        Assert.Contains("data-[active=true]:ease-[cubic-bezier(0.23,1,0.32,1)]", endClass);
        Assert.Contains("data-[direction=end]:data-[active=false]:translate-y-full", endClass);
    }

    [Fact]
    public void MessageScroller_jump_buttons_appear_when_not_at_the_edge_and_scroll()
    {
        var h = Harness(() => View(new List<string> { "0", "1", "2", "3", "4" }, includeStart: true));

        var viewport = Slots(h, "message-scroller-viewport").Single();
        var controller = viewport.ScrollController!;
        Element End() => Slots(h, "message-scroller-button").Single(e => A(e, "data-direction") == "end");
        Element Start() => Slots(h, "message-scroller-button").Single(e => A(e, "data-direction") == "start");

        Assert.Equal("false", A(End(), "data-active"));
        Assert.Equal("true", A(Start(), "data-active"));

        controller.JumpTo(0, 0);
        h.Frame();

        Assert.Equal("true", A(End(), "data-active"));
        Assert.Equal("false", A(Start(), "data-active"));

        h.Click(End());
        Assert.True(Math.Abs(controller.Offset.Y - controller.MaxOffset.Y) <= 0.5);
        Assert.Equal("false", A(End(), "data-active"));

        controller.JumpTo(0, controller.MaxOffset.Y);
        h.Frame();
        Assert.Equal("true", A(Start(), "data-active"));

        h.Click(Start());
        Assert.True(controller.Offset.Y <= 0.5);
    }
}
