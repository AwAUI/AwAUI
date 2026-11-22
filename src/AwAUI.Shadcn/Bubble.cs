using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Visual treatment of a <see cref="Bubble"/>.</summary>
public enum BubbleVariant { Default, Secondary, Muted, Tinted, Outline, Ghost, Destructive }

/// <summary>Side of the conversation a bubble sits on.</summary>
public enum BubbleAlign { Start, End }

/// <summary>Vertical side of a bubble's reaction badge.</summary>
public enum BubbleSide { Top, Bottom }

/// <summary>A column of conversational bubbles.</summary>
public sealed partial class BubbleGroup : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "bubble-group",
        "flex min-w-0 flex-col gap-2", Class, Rest);
}

/// <summary>A message bubble. Add a <see cref="BubbleContent"/> for the surface and <see cref="BubbleReactions"/> for reactions.</summary>
public sealed partial class Bubble : Component
{
    static readonly Cva<BubbleVariant> Variants = new(
        "group/bubble relative flex w-fit max-w-[80%] min-w-0 flex-col gap-1 " +
        "group-data-[align=end]/message:self-end data-[align=end]:self-end data-[variant=ghost]:max-w-full",
        new Dictionary<BubbleVariant, string>
        {
            [BubbleVariant.Default] =
                "*:data-[slot=bubble-content]:bg-primary *:data-[slot=bubble-content]:text-primary-foreground " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:bg-primary/80",
            [BubbleVariant.Secondary] =
                "*:data-[slot=bubble-content]:bg-secondary *:data-[slot=bubble-content]:text-secondary-foreground " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:bg-[color-mix(in_oklch,var(--secondary),var(--foreground)_5%)]",
            [BubbleVariant.Muted] =
                "*:data-[slot=bubble-content]:bg-muted " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:bg-[color-mix(in_oklch,var(--muted),var(--foreground)_5%)]",
            [BubbleVariant.Tinted] =
                "*:data-[slot=bubble-content]:bg-[oklch(from_var(--primary)_0.93_calc(c*0.4)_h)] " +
                "*:data-[slot=bubble-content]:text-foreground " +
                "dark:*:data-[slot=bubble-content]:bg-[oklch(from_var(--primary)_0.3_calc(c*0.4)_h)] " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:bg-[oklch(from_var(--primary)_0.88_calc(c*0.5)_h)] " +
                "dark:[&>[data-slot=bubble-content]:is(button,a):hover]:bg-[oklch(from_var(--primary)_0.35_calc(c*0.5)_h)]",
            [BubbleVariant.Outline] =
                "*:data-[slot=bubble-content]:border-border *:data-[slot=bubble-content]:bg-background " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:bg-muted " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:text-foreground " +
                "dark:[&>[data-slot=bubble-content]:is(button,a):hover]:bg-input/30",
            [BubbleVariant.Ghost] =
                "border-none *:data-[slot=bubble-content]:rounded-none *:data-[slot=bubble-content]:bg-transparent " +
                "*:data-[slot=bubble-content]:p-0 " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:bg-muted " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:text-foreground " +
                "dark:[&>[data-slot=bubble-content]:is(button,a):hover]:bg-muted/50",
            [BubbleVariant.Destructive] =
                "*:data-[slot=bubble-content]:bg-destructive/10 *:data-[slot=bubble-content]:text-destructive " +
                "dark:*:data-[slot=bubble-content]:bg-destructive/20 " +
                "[&>[data-slot=bubble-content]:is(button,a):hover]:bg-destructive/20 " +
                "dark:[&>[data-slot=bubble-content]:is(button,a):hover]:bg-destructive/30",
        });

    public BubbleVariant Variant { get; set; }
    public BubbleAlign Align { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var bubble = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "bubble",
            Variants.Of(Variant), Class, Rest);
        bubble.Attributes["data-variant"] ??= Variant.ToString().ToLowerInvariant();
        bubble.Attributes["data-align"] ??= Align == BubbleAlign.End ? "end" : "start";
        return bubble;
    }
}

/// <summary>The surface of a bubble. Set <see cref="AsChild"/> to render an interactive button or link instead.</summary>
public sealed partial class BubbleContent : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var content = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "bubble-content",
            "w-fit max-w-full min-w-0 overflow-hidden rounded-xl border border-transparent px-3 py-2 text-sm leading-relaxed wrap-break-word " +
            "group-data-[align=end]/bubble:self-end [button]:text-left [button,a]:transition-colors " +
            "[button,a]:outline-none [button,a]:focus-visible:border-ring [button,a]:focus-visible:ring-3 " +
            "[button,a]:focus-visible:ring-ring/50", Class, Rest);
        if (!AsChild) return content;

        var node = Slot.Merge(content, Slot.SingleChild(Children));
        if (node is ElementNode element) element.Class = TailwindMerge.Cn(element.Class.Text);
        return node;
    }
}

/// <summary>A floating reaction badge anchored to a bubble's corner.</summary>
public sealed partial class BubbleReactions : Component
{
    static readonly Cva<BubbleSide, BubbleAlign> Variants = new(
        "absolute z-10 flex w-fit shrink-0 items-center justify-center gap-1 rounded-full bg-muted px-1.5 py-0.5 text-sm " +
        "ring-3 ring-card has-[button]:p-0",
        new Dictionary<BubbleSide, string>
        {
            [BubbleSide.Top] = "top-0 -translate-y-3/4",
            [BubbleSide.Bottom] = "bottom-0 translate-y-3/4",
        },
        new Dictionary<BubbleAlign, string>
        {
            [BubbleAlign.Start] = "left-3",
            [BubbleAlign.End] = "right-3",
        });

    public BubbleSide Side { get; set; } = BubbleSide.Bottom;
    public BubbleAlign Align { get; set; } = BubbleAlign.End;
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var reactions = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "bubble-reactions",
            Variants.Of(Side, Align), Class, Rest);
        reactions.Attributes["data-side"] ??= Side == BubbleSide.Top ? "top" : "bottom";
        reactions.Attributes["data-align"] ??= Align == BubbleAlign.End ? "end" : "start";
        return reactions;
    }
}
