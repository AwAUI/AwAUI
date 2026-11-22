using System;
using System.Collections.Generic;
using AwAUI.Icons.Lucide;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Page navigation. Compose a <see cref="PaginationContent"/> with items, links and ellipses.</summary>
public sealed partial class Pagination : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var nav = StaticParts.Style(new StaticParts.TagElement("nav"), "pagination",
            "mx-auto flex w-full justify-center", Class, Rest);
        if (nav.Role == Role.None) nav.Role = Role.Navigation;
        nav.Attributes["aria-label"] ??= "pagination";
        nav.Children = Children ?? new NodeList();
        return nav;
    }
}

/// <summary>The row of page items.</summary>
public sealed partial class PaginationContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var list = StaticParts.Style(new StaticParts.TagElement("ul"), "pagination-content",
            "flex flex-row items-center gap-1", Class, Rest);
        if (list.Role == Role.None) list.Role = Role.List;
        list.Children = Children ?? new NodeList();
        return list;
    }
}

/// <summary>A single page entry.</summary>
public sealed partial class PaginationItem : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var item = StaticParts.Style(new StaticParts.TagElement("li"), "pagination-item", null, Class, Rest);
        if (item.Role == Role.None) item.Role = Role.ListItem;
        item.Children = Children ?? new NodeList();
        return item;
    }
}

/// <summary>A focusable page link. The active page is exposed with <c>aria-current="page"</c>.</summary>
public sealed partial class PaginationLink : Component
{
    const string ButtonBase =
        "inline-flex shrink-0 items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap transition-all outline-none select-none " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 active:not-aria-[haspopup]:translate-y-px " +
        "disabled:pointer-events-none disabled:opacity-50 " +
        "aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4";

    static readonly Cva<ButtonVariant, ButtonSize> Variants = new(
        ButtonBase,
        new Dictionary<ButtonVariant, string>
        {
            [ButtonVariant.Default] = "bg-primary text-primary-foreground hover:bg-primary/90",
            [ButtonVariant.Destructive] = "bg-destructive text-white hover:bg-destructive/90 focus-visible:ring-destructive/20 dark:bg-destructive/60 dark:focus-visible:ring-destructive/40",
            [ButtonVariant.Outline] = "border bg-background shadow-xs hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30 dark:hover:bg-input/50",
            [ButtonVariant.Secondary] = "bg-secondary text-secondary-foreground hover:bg-secondary/80",
            [ButtonVariant.Ghost] = "hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50",
            [ButtonVariant.Link] = "text-primary underline-offset-4 hover:underline",
        },
        new Dictionary<ButtonSize, string>
        {
            [ButtonSize.Default] = "h-9 px-4 py-2 has-[>svg]:px-3",
            [ButtonSize.Xs] = "h-6 gap-1 rounded-md px-2 text-xs has-[>svg]:px-1.5 [&_svg:not([class*='size-'])]:size-3",
            [ButtonSize.Sm] = "h-8 gap-1.5 rounded-md px-3 has-[>svg]:px-2.5",
            [ButtonSize.Lg] = "h-10 rounded-md px-6 has-[>svg]:px-4",
            [ButtonSize.Icon] = "size-9",
            [ButtonSize.IconXs] = "size-6 rounded-md [&_svg:not([class*='size-'])]:size-3",
            [ButtonSize.IconSm] = "size-8",
            [ButtonSize.IconLg] = "size-10",
        });

    public bool IsActive { get; set; }
    public ButtonSize Size { get; set; } = ButtonSize.Icon;
    public string? Href { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var variant = IsActive ? ButtonVariant.Outline : ButtonVariant.Ghost;
        var link = StaticParts.Link("pagination-link", Variants.Of(variant, Size), Href, Disabled,
            Class, Rest, Children, OnClick, Ref);
        link.Attributes["data-active"] ??= IsActive ? "true" : "false";
        link.Attributes["data-size"] ??= SizeName(Size);
        if (IsActive) link.Attributes["aria-current"] ??= "page";
        return link;
    }

    static string SizeName(ButtonSize size) => size switch
    {
        ButtonSize.IconXs => "icon-xs",
        ButtonSize.IconSm => "icon-sm",
        ButtonSize.IconLg => "icon-lg",
        _ => size.ToString().ToLowerInvariant(),
    };

    /// <summary>Copies spread attributes and supplies a default accessible name.</summary>
    internal static AttributeDictionary Label(AttributeDictionary? rest, string label)
    {
        var attributes = rest is null ? new AttributeDictionary() : new AttributeDictionary(rest);
        attributes["aria-label"] ??= label;
        return attributes;
    }
}

/// <summary>Link to the previous page.</summary>
public sealed partial class PaginationPrevious : Component
{
    public bool IsActive { get; set; }
    public string? Href { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new PaginationLink
    {
        IsActive = IsActive,
        Size = ButtonSize.Default,
        Href = Href,
        Disabled = Disabled,
        Class = TailwindMerge.Cn("gap-1 px-2.5 sm:pl-2.5", Class),
        Ref = Ref,
        OnClick = OnClick,
        Rest = PaginationLink.Label(Rest, "Go to previous page"),
        Children = Children is { Count: > 0 }
            ? Children
            : new NodeList
            {
                StaticParts.Glyph(LucideIcons.ChevronLeft),
                new Span("Previous") { Class = "hidden sm:block" },
            },
    };
}

/// <summary>Link to the next page.</summary>
public sealed partial class PaginationNext : Component
{
    public bool IsActive { get; set; }
    public string? Href { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new PaginationLink
    {
        IsActive = IsActive,
        Size = ButtonSize.Default,
        Href = Href,
        Disabled = Disabled,
        Class = TailwindMerge.Cn("gap-1 px-2.5 sm:pr-2.5", Class),
        Ref = Ref,
        OnClick = OnClick,
        Rest = PaginationLink.Label(Rest, "Go to next page"),
        Children = Children is { Count: > 0 }
            ? Children
            : new NodeList
            {
                new Span("Next") { Class = "hidden sm:block" },
                StaticParts.Glyph(LucideIcons.ChevronRight),
            },
    };
}

/// <summary>Collapsed range of pages.</summary>
public sealed partial class PaginationEllipsis : Component
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
                new Span("More pages") { Class = "sr-only" },
            ],
        }, "pagination-ellipsis", "flex size-9 items-center justify-center", Class, Rest);
        if (ellipsis.Role == Role.None) ellipsis.Role = Role.Presentation;
        ellipsis.Attributes["aria-hidden"] ??= "true";
        return ellipsis;
    }
}
