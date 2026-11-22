using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Direction in which a <see cref="ButtonGroup"/> lays out its buttons.</summary>
public enum ButtonGroupOrientation { Horizontal, Vertical }

/// <summary>Joins adjacent buttons into a single segmented control.</summary>
public sealed partial class ButtonGroup : Component
{
    static readonly Cva<ButtonGroupOrientation> Variants = new(
        "flex w-fit items-stretch has-[>[data-slot=button-group]]:gap-2 [&>*]:focus-visible:relative [&>*]:focus-visible:z-10 " +
        "has-[select[aria-hidden=true]:last-child]:[&>[data-slot=select-trigger]:last-of-type]:rounded-r-md " +
        "[&>[data-slot=select-trigger]:not([class*='w-'])]:w-fit [&>input]:flex-1",
        new Dictionary<ButtonGroupOrientation, string>
        {
            [ButtonGroupOrientation.Horizontal] =
                "[&>*:not(:first-child)]:rounded-l-none [&>*:not(:first-child)]:border-l-0 [&>*:not(:last-child)]:rounded-r-none",
            [ButtonGroupOrientation.Vertical] =
                "flex-col [&>*:not(:first-child)]:rounded-t-none [&>*:not(:first-child)]:border-t-0 [&>*:not(:last-child)]:rounded-b-none",
        });

    public ButtonGroupOrientation Orientation { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "button-group",
            Variants.Of(Orientation), Class, Rest);
        if (group.Role == Role.None) group.Role = Role.Group;
        group.Attributes["data-orientation"] ??=
            Orientation == ButtonGroupOrientation.Vertical ? "vertical" : "horizontal";
        return group;
    }
}

/// <summary>Decorative divider between two buttons.</summary>
public sealed partial class ButtonGroupSeparator : Component
{
    const string SeparatorBase =
        "shrink-0 bg-border data-[orientation=horizontal]:h-px data-[orientation=horizontal]:w-full " +
        "data-[orientation=vertical]:h-full data-[orientation=vertical]:w-px";

    public SeparatorOrientation Orientation { get; set; } = SeparatorOrientation.Vertical;
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool vertical = Orientation == SeparatorOrientation.Vertical;
        var separator = StaticParts.Style(new Div(), "button-group-separator",
            TailwindMerge.Cn(SeparatorBase, "relative m-0! self-stretch bg-input data-[orientation=vertical]:h-auto"),
            Class, Rest);
        if (separator.Role == Role.None) separator.Role = Role.Presentation;
        separator.Attributes["aria-hidden"] ??= "true";
        separator.Attributes["data-orientation"] ??= vertical ? "vertical" : "horizontal";
        return separator;
    }
}

/// <summary>Non-interactive text or icon inside a button group.</summary>
public sealed partial class ButtonGroupText : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var text = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "button-group-text",
            "flex items-center gap-2 rounded-md border bg-muted px-4 text-sm font-medium shadow-xs " +
            "[&_svg]:pointer-events-none [&_svg:not([class*='size-'])]:size-4", Class, Rest);
        if (!AsChild) return text;

        Node node = Slot.Merge(text, Slot.SingleChild(Children));
        if (node is ElementNode element) element.Class = TailwindMerge.Cn(element.Class.Text);
        return node;
    }
}
