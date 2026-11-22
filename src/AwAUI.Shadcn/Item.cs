using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Visual treatment of an <see cref="Item"/>.</summary>
public enum ItemVariant { Default, Outline, Muted }

/// <summary>Density of an <see cref="Item"/>.</summary>
public enum ItemSize { Default, Sm }

/// <summary>Presentation of an <see cref="ItemMedia"/> slot.</summary>
public enum ItemMediaVariant { Default, Icon, Image }

/// <summary>A row with media, content and actions. Use inside an <see cref="ItemGroup"/>.</summary>
public sealed partial class Item : Component
{
    static readonly Cva<ItemVariant, ItemSize> Variants = new(
        "group/item flex flex-wrap items-center rounded-md border border-transparent text-sm transition-colors duration-100 outline-none " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 [a]:transition-colors [a]:hover:bg-accent/50",
        new Dictionary<ItemVariant, string>
        {
            [ItemVariant.Default] = "bg-transparent",
            [ItemVariant.Outline] = "border-border",
            [ItemVariant.Muted] = "bg-muted/50",
        },
        new Dictionary<ItemSize, string>
        {
            [ItemSize.Default] = "gap-4 p-4",
            [ItemSize.Sm] = "gap-2.5 px-4 py-3",
        });

    public ItemVariant Variant { get; set; }
    public ItemSize Size { get; set; }
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var item = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item",
            Variants.Of(Variant, Size), Class, Rest);
        item.Attributes["data-variant"] ??= VariantName(Variant);
        item.Attributes["data-size"] ??= SizeName(Size);
        if (!AsChild) return item;

        Node node = Slot.Merge(item, Slot.SingleChild(Children));
        if (node is ElementNode element) element.Class = TailwindMerge.Cn(element.Class.Text);
        return node;
    }

    static string VariantName(ItemVariant variant) => variant switch
    {
        ItemVariant.Outline => "outline",
        ItemVariant.Muted => "muted",
        _ => "default",
    };

    static string SizeName(ItemSize size) => size == ItemSize.Sm ? "sm" : "default";
}

/// <summary>Lists related items.</summary>
public sealed partial class ItemGroup : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item-group",
            "group/item-group flex flex-col", Class, Rest);
        if (group.Role == Role.None) group.Role = Role.List;
        return group;
    }
}

/// <summary>Divides adjacent items.</summary>
public sealed partial class ItemSeparator : Component
{
    public SeparatorOrientation Orientation { get; set; } = SeparatorOrientation.Horizontal;
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool vertical = Orientation == SeparatorOrientation.Vertical;
        var separator = StaticParts.Style(new Div(), "item-separator",
            "shrink-0 bg-border data-[orientation=horizontal]:h-px data-[orientation=horizontal]:w-full " +
            "data-[orientation=vertical]:h-full data-[orientation=vertical]:w-px my-0", Class, Rest);
        if (separator.Role == Role.None) separator.Role = Role.Presentation;
        separator.Attributes["aria-hidden"] ??= "true";
        separator.Attributes["data-orientation"] ??= vertical ? "vertical" : "horizontal";
        return separator;
    }
}

/// <summary>Leading icon or image of an item.</summary>
public sealed partial class ItemMedia : Component
{
    static readonly Cva<ItemMediaVariant> Variants = new(
        "flex shrink-0 items-center justify-center gap-2 group-has-[[data-slot=item-description]]/item:translate-y-0.5 " +
        "group-has-[[data-slot=item-description]]/item:self-start [&_svg]:pointer-events-none",
        new Dictionary<ItemMediaVariant, string>
        {
            [ItemMediaVariant.Default] = "bg-transparent",
            [ItemMediaVariant.Icon] = "size-8 rounded-sm border bg-muted [&_svg:not([class*='size-'])]:size-4",
            [ItemMediaVariant.Image] = "size-10 overflow-hidden rounded-sm [&_img]:size-full [&_img]:object-cover",
        });

    public ItemMediaVariant Variant { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var media = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item-media",
            Variants.Of(Variant), Class, Rest);
        media.Attributes["data-variant"] ??= (Variant switch
        {
            ItemMediaVariant.Icon => "icon",
            ItemMediaVariant.Image => "image",
            _ => "default",
        });
        return media;
    }
}

/// <summary>The main column of an item.</summary>
public sealed partial class ItemContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item-content",
        "flex flex-1 flex-col gap-1 [&+[data-slot=item-content]]:flex-none", Class, Rest);
}

/// <summary>The item's title.</summary>
public sealed partial class ItemTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item-title",
        "flex w-fit items-center gap-2 text-sm leading-snug font-medium", Class, Rest);
}

/// <summary>The item's supporting text.</summary>
public sealed partial class ItemDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new StaticParts.TagElement("p") { Children = Children ?? new NodeList() },
        "item-description",
        "line-clamp-2 text-sm leading-normal font-normal text-balance text-muted-foreground " +
        "[&>a]:underline [&>a]:underline-offset-4 [&>a:hover]:text-primary", Class, Rest);
}

/// <summary>Trailing actions of an item.</summary>
public sealed partial class ItemActions : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item-actions",
        "flex items-center gap-2", Class, Rest);
}

/// <summary>A full-width header row above the item's content.</summary>
public sealed partial class ItemHeader : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item-header",
        "flex basis-full items-center justify-between gap-2", Class, Rest);
}

/// <summary>A full-width footer row below the item's content.</summary>
public sealed partial class ItemFooter : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "item-footer",
        "flex basis-full items-center justify-between gap-2", Class, Rest);
}
