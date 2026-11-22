using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Presentation of a <see cref="Marker"/> divider.</summary>
public enum MarkerVariant { Default, Separator, Border }

/// <summary>A quiet inline note, date stamp, or separators such as "Today" in a transcript.</summary>
public sealed partial class Marker : Component
{
    static readonly Cva<MarkerVariant> Variants = new(
        "group/marker relative flex min-h-4 w-full items-center gap-2 text-left text-sm text-muted-foreground " +
        "[&_svg:not([class*='size-'])]:size-4 [a]:underline [a]:underline-offset-3 [a]:hover:text-foreground",
        new Dictionary<MarkerVariant, string>
        {
            [MarkerVariant.Default] = "",
            [MarkerVariant.Separator] =
                "before:mr-1 before:h-px before:min-w-0 before:flex-1 before:bg-border " +
                "after:ml-1 after:h-px after:min-w-0 after:flex-1 after:bg-border",
            [MarkerVariant.Border] = "border-b border-border pb-2",
        });

    public MarkerVariant Variant { get; set; }
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var marker = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "marker",
            Variants.Of(Variant), Class, Rest);
        marker.Attributes["data-variant"] ??= Variant.ToString().ToLowerInvariant();
        if (!AsChild) return marker;

        var node = Slot.Merge(marker, Slot.SingleChild(Children));
        if (node is ElementNode element) element.Class = TailwindMerge.Cn(element.Class.Text);
        return node;
    }
}

/// <summary>Decorative glyph in a marker (hidden from assistive technology).</summary>
public sealed partial class MarkerIcon : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var icon = StaticParts.Style(new Span { Children = Children ?? new NodeList() }, "marker-icon",
            "size-4 shrink-0 [&_svg:not([class*='size-'])]:size-4", Class, Rest);
        icon.Attributes["aria-hidden"] ??= "true";
        return icon;
    }
}

/// <summary>The marker's text or rich content.</summary>
public sealed partial class MarkerContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Span { Children = Children ?? new NodeList() }, "marker-content",
        "min-w-0 wrap-break-word group-data-[variant=separator]/marker:flex-none " +
        "group-data-[variant=separator]/marker:text-center *:[a]:underline *:[a]:underline-offset-3 *:[a]:hover:text-foreground",
        Class, Rest);
}
