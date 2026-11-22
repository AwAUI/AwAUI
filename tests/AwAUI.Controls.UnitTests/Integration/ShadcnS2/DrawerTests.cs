using System;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;
using PrimSide = AwAUI.Controls.Primitives.Side;

namespace AwAUI.Shadcn.Integration;

/// <summary>Drawer: dialog semantics, side, focus trap, Escape, overlay click and drag-to-dismiss.</summary>
public class DrawerTests
{
    static DrawerTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element? Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault();

    static Element SlotOrThrow(UIHarness h, string slot) =>
        Slot(h, slot) ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static Element? Popup(UIHarness h, Role role) =>
        h.FindAll(e => e.Role == role && e.IsVisible).FirstOrDefault();

    static bool IsInside(Element root, Element? element)
    {
        for (var e = element; e is not null; e = e.Parent)
            if (ReferenceEquals(e, root)) return true;
        return false;
    }

    static readonly AttributeDictionary Sized = new()
    {
        ["style"] = "width: 320px; height: 200px",
    };

    static Node DrawerBody(PrimSide side = PrimSide.Bottom, bool withClose = false) => new Drawer
    {
        Children =
        [
            new DrawerTrigger { Children = [new UIText("Open drawer")] },
            new DrawerContent
            {
                Side = side,
                ShowCloseButton = withClose,
                Rest = Sized,
                Children =
                [
                    new DrawerHeader
                    {
                        Children =
                        [
                            new DrawerTitle { Children = [new UIText("Filters")] },
                            new DrawerDescription { Children = [new UIText("Narrow the result list.")] },
                        ],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Drawer_opens_from_its_trigger_and_escape_restores_focus()
    {
        var h = Harness(() => DrawerBody());
        var trigger = SlotOrThrow(h, "drawer-trigger");
        Assert.Equal("false", A(trigger, "aria-expanded"));

        h.Click(trigger);
        var dialog = Popup(h, Role.Dialog);
        Assert.NotNull(dialog);
        Assert.Equal("true", A(trigger, "aria-expanded"));
        Assert.Equal("true", A(dialog!, "aria-modal"));
        Assert.True(IsInside(dialog!, h.Focused));

        var content = SlotOrThrow(h, "drawer-content");
        Assert.Equal("bottom", A(content, "data-vaul-drawer-direction"));
        Assert.Equal("bottom", A(content, "data-side"));
        Assert.Equal("open", A(content, "data-state"));
        Assert.NotNull(Slot(h, "drawer-handle"));
        Assert.NotNull(Slot(h, "drawer-title"));
        Assert.NotNull(Slot(h, "drawer-description"));

        h.Escape();
        Assert.Null(Popup(h, Role.Dialog));
        Assert.True(trigger.IsFocused);
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Theory]
    [InlineData(PrimSide.Bottom, true)]
    [InlineData(PrimSide.Top, false)]
    [InlineData(PrimSide.Left, false)]
    [InlineData(PrimSide.Right, false)]
    public void Drawer_handle_is_rendered_for_every_side_and_visible_only_at_the_bottom(PrimSide side, bool visible)
    {
        var h = Harness(() => DrawerBody(side));
        h.Click(SlotOrThrow(h, "drawer-trigger"));

        var handle = SlotOrThrow(h, "drawer-handle");
        Assert.True(handle.HasClass("hidden"));
        Assert.True(handle.HasClass("group-data-[vaul-drawer-direction=bottom]/drawer-content:block"));
        Assert.Equal(visible, handle.IsVisible);
    }

    [Fact]
    public void Clicking_the_overlay_closes_the_drawer()
    {
        var h = Harness(() => DrawerBody());
        h.Click(SlotOrThrow(h, "drawer-trigger"));
        Assert.NotNull(Popup(h, Role.Dialog));

        h.ClickAt(new Point(4, 4));

        Assert.Null(Popup(h, Role.Dialog));
    }

    [Fact]
    public void Dragging_past_the_threshold_dismisses_the_drawer()
    {
        var h = Harness(() => DrawerBody());
        h.Click(SlotOrThrow(h, "drawer-trigger"));
        var content = SlotOrThrow(h, "drawer-content");
        var start = content.GetBoundingClientRect().Center;

        h.PointerDown(start);
        Assert.Equal("true", A(content, "data-dragging"));

        h.PointerMove(new Point(start.X, start.Y + 300));
        h.PointerUp(new Point(start.X, start.Y + 300));
        h.Frame();

        Assert.Null(Popup(h, Role.Dialog));
    }

    [Fact]
    public void Releasing_below_the_threshold_snaps_back()
    {
        var h = Harness(() => DrawerBody());
        h.Click(SlotOrThrow(h, "drawer-trigger"));
        var content = SlotOrThrow(h, "drawer-content");
        var start = content.GetBoundingClientRect().Center;

        h.PointerDown(start);
        h.PointerMove(new Point(start.X, start.Y + 10));
        h.PointerUp(new Point(start.X, start.Y + 10));
        h.Frame();

        Assert.NotNull(Popup(h, Role.Dialog));
        Assert.Equal("open", A(content, "data-state"));
        Assert.Null(A(content, "data-dragging"));
    }

    [Fact]
    public void A_left_drawer_drags_along_the_horizontal_axis()
    {
        var h = Harness(() => DrawerBody(PrimSide.Left));
        h.Click(SlotOrThrow(h, "drawer-trigger"));
        var content = SlotOrThrow(h, "drawer-content");
        Assert.Equal("left", A(content, "data-vaul-drawer-direction"));

        var start = content.GetBoundingClientRect().Center;
        h.PointerDown(start);
        h.PointerMove(new Point(start.X - 400, start.Y));
        h.PointerUp(new Point(start.X - 400, start.Y));
        h.Frame();

        Assert.Null(Popup(h, Role.Dialog));
    }

    [Fact]
    public void The_close_button_closes_the_drawer()
    {
        var h = Harness(() => DrawerBody(PrimSide.Bottom, withClose: true));
        h.Click(SlotOrThrow(h, "drawer-trigger"));
        var close = SlotOrThrow(h, "drawer-close");
        // The registry fixes the close affordance to the physical top right edge.
        Assert.Contains("right-4", A(close, "class"));
        Assert.DoesNotContain("end-4", A(close, "class"));

        h.Click(close);

        Assert.Null(Popup(h, Role.Dialog));
    }

    [Fact]
    public void A_bare_drawer_close_renders_no_default_label()
    {
        var h = Harness(() => new Drawer
        {
            DefaultOpen = true,
            Children =
            [
                new DrawerTrigger { Children = [new UIText("Open")] },
                new DrawerContent
                {
                    Children = [new DrawerClose { Rest = new AttributeDictionary { ["id"] = "bare-drawer-close" } }],
                },
            ],
        });

        Assert.Equal("", h.ById("bare-drawer-close").TextContent.Trim());
    }
}
