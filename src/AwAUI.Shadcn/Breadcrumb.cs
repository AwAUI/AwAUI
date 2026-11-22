using System;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Navigation trail showing the path to the current page. Compose a <see cref="BreadcrumbList"/> with items.</summary>
public sealed partial class Breadcrumb : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var nav = StaticParts.Style(new StaticParts.TagElement("nav"), "breadcrumb", null, Class, Rest);
        if (nav.Role == Role.None) nav.Role = Role.Navigation;
        nav.Attributes["aria-label"] ??= "breadcrumb";
        nav.Children = Children ?? new NodeList();
        return nav;
    }
}

/// <summary>The ordered trail of breadcrumb items.</summary>
public sealed partial class BreadcrumbList : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var list = StaticParts.Style(new StaticParts.TagElement("ol"), "breadcrumb-list",
            "flex flex-wrap items-center gap-1.5 text-sm break-words text-muted-foreground sm:gap-2.5", Class, Rest);
        if (list.Role == Role.None) list.Role = Role.List;
        list.Children = Children ?? new NodeList();
        return list;
    }
}

/// <summary>A single step in the trail.</summary>
public sealed partial class BreadcrumbItem : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var item = StaticParts.Style(new StaticParts.TagElement("li"), "breadcrumb-item",
            "inline-flex items-center gap-1.5", Class, Rest);
        if (item.Role == Role.None) item.Role = Role.ListItem;
        item.Children = Children ?? new NodeList();
        return item;
    }
}

/// <summary>A link to an ancestor page. Focusable; activates with Enter, never with Space.</summary>
public sealed partial class BreadcrumbLink : Component
{
    public bool AsChild { get; set; }
    public string? Href { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var link = StaticParts.Link("breadcrumb-link", "transition-colors hover:text-foreground", Href, Disabled,
            Class, Rest, Children, OnClick, Ref);
        Node node = AsChild ? Slot.Merge(link, Slot.SingleChild(Children)) : link;
        if (node is ElementNode element) element.Class = AwAUI.Tailwind.TailwindMerge.Cn(element.Class.Text);
        return node;
    }
}

/// <summary>The current page: exposed as the disabled current link in the trail.</summary>
public sealed partial class BreadcrumbPage : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var page = StaticParts.Style(new Span { Children = Children ?? new NodeList() }, "breadcrumb-page",
            "font-normal text-foreground", Class, Rest);
        if (page.Role == Role.None) page.Role = Role.Link;
        page.Attributes["aria-disabled"] ??= "true";
        page.Attributes["aria-current"] ??= "page";
        return page;
    }
}

/// <summary>Separates two trail items; shows a chevron unless children are supplied.</summary>
public sealed partial class BreadcrumbSeparator : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var separator = StaticParts.Style(new StaticParts.TagElement("li"), "breadcrumb-separator",
            "[&>svg]:size-3.5", Class, Rest);
        if (separator.Role == Role.None) separator.Role = Role.Presentation;
        separator.Attributes["aria-hidden"] ??= "true";
        separator.Children = Children is { Count: > 0 }
            ? Children
            : new NodeList { StaticParts.Glyph(LucideIcons.ChevronRight) };
        return separator;
    }
}

/// <summary>A collapsed portion of the trail.</summary>
public sealed partial class BreadcrumbEllipsis : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        // A block container: the glyph and the visually hidden label must get their own boxes. Children of an
        // inline paragraph are inlined, which would paint the sr-only label.
        var ellipsis = StaticParts.Style(new Div
        {
            Children =
            [
                StaticParts.Glyph(LucideIcons.MoreHorizontal, "size-4"),
                new Span("More") { Class = "sr-only" },
            ],
        }, "breadcrumb-ellipsis", "flex size-9 items-center justify-center", Class, Rest);
        if (ellipsis.Role == Role.None) ellipsis.Role = Role.Presentation;
        ellipsis.Attributes["aria-hidden"] ??= "true";
        return ellipsis;
    }
}
