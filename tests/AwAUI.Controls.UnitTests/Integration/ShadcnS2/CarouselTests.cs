using System;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>Carousel 的页面切换、按钮停用、键盘、循环、圆点与 ARIA。</summary>
public class CarouselTests
{
    static CarouselTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).First();

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Element Inner(UIHarness h) => Slot(h, "carousel-content").Children[0];

    static Node BuildCarousel(Action<int>? changed = null, bool loop = false, int? index = null) =>
        // The shadcn dots/arrows sit outside the slide area (-left-12 / -right-12). Padding keeps them
        // inside the viewport so pointer clicks in the harness hit them.
        new Div
        {
            Style = "padding-left:48px;padding-right:48px",
            Children =
            [
                new Carousel
                {
                    Loop = loop,
                    Index = index,
                    IndexChanged = changed,
                    Children =
                    [
                        new CarouselContent
                        {
                            Children =
                            [
                                new CarouselItem { Children = [new UIText("One")] },
                                new CarouselItem { Children = [new UIText("Two")] },
                                new CarouselItem { Children = [new UIText("Three")] },
                            ],
                        },
                        new CarouselPrevious(),
                        new CarouselNext(),
                        new CarouselDots(),
                    ],
                },
            ],
        };

    [Fact]
    public void Carousel_exposes_roles_slides_and_end_disabled_buttons()
    {
        var h = Harness(() => BuildCarousel());

        var root = Slot(h, "carousel");
        Assert.Equal(Role.Region, root.Role);
        Assert.Equal("carousel", A(root, "aria-roledescription"));

        var items = Slots(h, "carousel-item");
        Assert.Equal(3, items.Count);
        foreach (var item in items)
        {
            Assert.Equal(Role.Group, item.Role);
            Assert.Equal("slide", A(item, "aria-roledescription"));
        }
        Assert.Equal("1 of 3", A(items[0], "aria-label"));
        Assert.Equal("3 of 3", A(items[2], "aria-label"));

        var previous = Slot(h, "carousel-previous");
        var next = Slot(h, "carousel-next");
        Assert.True(previous.IsDisabled);
        Assert.False(next.IsDisabled);

        var dots = Slots(h, "carousel-dot");
        Assert.Equal(3, dots.Count);
        Assert.Equal("true", A(dots[0], "aria-current"));
        Assert.Null(A(dots[1], "aria-current"));

        Assert.Contains("translateX(-0%)", Inner(h).InlineStyle);
    }

    [Fact]
    public void Carousel_next_and_dots_page_and_update_disabled_state()
    {
        int? changed = null;
        var h = Harness(() => BuildCarousel(value => changed = value));

        h.Click(Slot(h, "carousel-next"));
        Assert.Equal(1, changed);
        Assert.False(Slot(h, "carousel-previous").IsDisabled);
        Assert.Contains("translateX(-100%)", Inner(h).InlineStyle);

        var dots = Slots(h, "carousel-dot");
        h.Click(dots[2]);
        Assert.Equal(2, changed);
        Assert.Equal("true", A(Slots(h, "carousel-dot")[2], "aria-current"));
        Assert.True(Slot(h, "carousel-next").IsDisabled);
        Assert.Contains("translateX(-200%)", Inner(h).InlineStyle);
    }

    [Fact]
    public void Carousel_arrow_keys_move_between_slides()
    {
        int? changed = null;
        var h = Harness(() => BuildCarousel(value => changed = value));

        h.Tab();
        var next = Slot(h, "carousel-next");
        Assert.True(next.IsFocused);

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal(1, changed);

        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.Equal(0, changed);

        // Focus stays on the control that received the key.
        Assert.True(Slot(h, "carousel-next").IsFocused);
    }

    [Fact]
    public void Carousel_loop_wraps_in_both_directions()
    {
        int? changed = null;
        var h = Harness(() => BuildCarousel(value => changed = value, loop: true));

        Assert.False(Slot(h, "carousel-previous").IsDisabled);
        Assert.False(Slot(h, "carousel-next").IsDisabled);

        h.Click(Slot(h, "carousel-previous"));
        Assert.Equal(2, changed);
        Assert.Equal("true", A(Slots(h, "carousel-dot")[2], "aria-current"));

        h.Click(Slot(h, "carousel-next"));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Carousel_controlled_index_selects_the_requested_slide()
    {
        var h = Harness(() => BuildCarousel(index: 2));

        var dots = Slots(h, "carousel-dot");
        Assert.Equal("true", A(dots[2], "aria-current"));
        Assert.True(Slot(h, "carousel-next").IsDisabled);
        Assert.Contains("translateX(-200%)", Inner(h).InlineStyle);
    }

    [Fact]
    public void Carousel_default_index_seeds_the_uncontrolled_slide()
    {
        int? changed = null;
        var h = Harness(() => new Carousel
        {
            DefaultIndex = 2,
            IndexChanged = value => changed = value,
            Children =
            [
                new CarouselContent
                {
                    Children =
                    [
                        new CarouselItem { Children = [new UIText("One")] },
                        new CarouselItem { Children = [new UIText("Two")] },
                        new CarouselItem { Children = [new UIText("Three")] },
                    ],
                },
                new CarouselPrevious(),
                new CarouselNext(),
                new CarouselDots(),
            ],
        });

        var dots = Slots(h, "carousel-dot");
        Assert.Equal("true", A(dots[2], "aria-current"));
        Assert.False(Slot(h, "carousel-previous").IsDisabled);
        Assert.True(Slot(h, "carousel-next").IsDisabled);
        Assert.Contains("translateX(-200%)", Inner(h).InlineStyle);

        // The seeded index is the live uncontrolled state: going back reports from it.
        h.Click(Slot(h, "carousel-previous"));
        Assert.Equal(1, changed);
        Assert.Contains("translateX(-100%)", Inner(h).InlineStyle);
    }

    [Fact]
    public void Carousel_rtl_reverses_the_arrow_keys()
    {
        int? changed = null;
        var h = Harness(() => new Carousel
        {
            Dir = Direction.Rtl,
            DefaultIndex = 1,
            IndexChanged = value => changed = value,
            Children =
            [
                new CarouselContent
                {
                    Children =
                    [
                        new CarouselItem { Children = [new UIText("One")] },
                        new CarouselItem { Children = [new UIText("Two")] },
                        new CarouselItem { Children = [new UIText("Three")] },
                    ],
                },
                new CarouselPrevious(),
                new CarouselNext(),
                new CarouselDots(),
            ],
        });

        Assert.Equal("rtl", A(Slot(h, "carousel"), "dir"));

        h.Tab();
        Assert.True(Slot(h, "carousel-previous").IsFocused);
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");

        // In RTL the left arrow advances to the next slide (index 1 -> 2), the reverse of LTR.
        Assert.Equal(2, changed);
        Assert.Contains("translateX(-200%)", Inner(h).InlineStyle);
    }
}
