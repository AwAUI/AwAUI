using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Bubble：分组布局、变体 / 对齐的 data 属性与 surface 类、内容槽位，以及带 side / align 特性的反应徽标。
/// </summary>
public class BubbleTests
{
    static BubbleTests() => ShadcnTheme.Register();

    static UIHarness Harness(System.Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    [Fact]
    public void Bubble_group_renders_a_vertical_column()
    {
        var h = Harness(() => new BubbleGroup
        {
            Children =
            [
                new Bubble { Children = [new BubbleContent { Children = [new UIText("Hi")] }] },
                new Bubble { Children = [new BubbleContent { Children = [new UIText("Hello")] }] },
            ],
        });

        var group = SlotOrThrow(h, "bubble-group");
        Assert.Contains("flex-col", A(group, "class")!);
        Assert.Equal(2, h.FindAll(e => A(e, "data-slot") == "bubble").Count);
    }

    [Fact]
    public void Bubble_end_alignment_sets_data_align_and_self_end_class()
    {
        var h = Harness(() => new Bubble
        {
            Variant = BubbleVariant.Outline,
            Align = BubbleAlign.End,
            Children = [new BubbleContent { Children = [new UIText("Right aligned")] }],
        });

        var bubble = SlotOrThrow(h, "bubble");
        Assert.Equal("outline", A(bubble, "data-variant"));
        Assert.Equal("end", A(bubble, "data-align"));
        Assert.Contains("data-[align=end]:self-end", A(bubble, "class")!);
        Assert.Contains("group/bubble", A(bubble, "class")!);

        var content = SlotOrThrow(h, "bubble-content");
        Assert.Equal("Right aligned", content.TextContent.Trim());
        Assert.Contains("rounded-xl", A(content, "class")!);
        Assert.Contains("group-data-[align=end]/bubble:self-end", A(content, "class")!);
    }

    [Fact]
    public void Bubble_variants_paint_the_content_surface()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Bubble { Variant = BubbleVariant.Default, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Secondary, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Muted, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Tinted, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Outline, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Ghost, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Destructive, Children = [new BubbleContent()] },
            ],
        });

        var bubbles = h.FindAll(e => A(e, "data-slot") == "bubble");
        Assert.Equal(7, bubbles.Count);
        Assert.Equal(
            new[] { "default", "secondary", "muted", "tinted", "outline", "ghost", "destructive" },
            bubbles.Select(e => A(e, "data-variant")).ToArray());
        Assert.Contains("bg-primary", A(bubbles[0], "class")!);
        Assert.Contains("bg-secondary", A(bubbles[1], "class")!);
        Assert.Contains("bg-destructive/10", A(bubbles[6], "class")!);
    }

    [Fact]
    public void Bubble_secondary_muted_and_tinted_use_the_registry_hover_mixes()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Bubble { Variant = BubbleVariant.Secondary, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Muted, Children = [new BubbleContent()] },
                new Bubble { Variant = BubbleVariant.Tinted, Children = [new BubbleContent()] },
            ],
        });

        var bubbles = h.FindAll(e => A(e, "data-slot") == "bubble");
        Assert.Contains("bg-[color-mix(in_oklch,var(--secondary),var(--foreground)_5%)]", A(bubbles[0], "class")!);
        Assert.Contains("bg-[color-mix(in_oklch,var(--muted),var(--foreground)_5%)]", A(bubbles[1], "class")!);
        Assert.Contains("bg-[oklch(from_var(--primary)_0.93_calc(c*0.4)_h)]", A(bubbles[2], "class")!);
        Assert.Contains("bg-[oklch(from_var(--primary)_0.3_calc(c*0.4)_h)]", A(bubbles[2], "class")!);

        // The arbitrary color values must actually compile, not just survive on the class list.
        var surfaces = h.FindAll(e => A(e, "data-slot") == "bubble-content");
        Assert.Equal(3, surfaces.Count);
        Assert.All(surfaces, surface => Assert.True(
            surface.ComputedStyle is { } style && style.BackgroundColor.Value.A > 0.5f,
            "each variant must resolve an opaque surface background"));
    }

    [Fact]
    public void Bubble_reactions_expose_side_and_align()
    {
        var h = Harness(() => new Bubble
        {
            Align = BubbleAlign.Start,
            Children =
            [
                new BubbleContent { Children = [new UIText("Nice")] },
                new BubbleReactions
                {
                    Side = BubbleSide.Top,
                    Align = BubbleAlign.Start,
                    Children = [new UIText("+1")],
                },
            ],
        });

        var reactions = SlotOrThrow(h, "bubble-reactions");
        Assert.Equal("top", A(reactions, "data-side"));
        Assert.Equal("start", A(reactions, "data-align"));
        Assert.Contains("top-0", A(reactions, "class")!);
        Assert.Contains("left-3", A(reactions, "class")!);
        Assert.Contains("rounded-full", A(reactions, "class")!);
    }
}
