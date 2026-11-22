using System.Collections.Generic;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Presentation variant of <see cref="EmptyMedia"/>.</summary>
public enum EmptyMediaVariant { Default, Icon }

/// <summary>Placeholder shown when a collection has no content.</summary>
public sealed partial class Empty : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "empty",
        "flex min-w-0 flex-1 flex-col items-center justify-center gap-6 rounded-lg border-dashed p-6 text-center text-balance md:p-12",
        Class, Rest);
}

/// <summary>Centered media, title and description.</summary>
public sealed partial class EmptyHeader : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "empty-header",
        "flex max-w-sm flex-col items-center gap-2 text-center", Class, Rest);
}

/// <summary>Illustration or icon at the top of the empty state.</summary>
public sealed partial class EmptyMedia : Component
{
    static readonly Cva<EmptyMediaVariant> Variants = new(
        "mb-2 flex shrink-0 items-center justify-center [&_svg]:pointer-events-none [&_svg]:shrink-0",
        new Dictionary<EmptyMediaVariant, string>
        {
            [EmptyMediaVariant.Default] = "bg-transparent",
            [EmptyMediaVariant.Icon] = "flex size-10 shrink-0 items-center justify-center rounded-lg bg-muted text-foreground [&_svg:not([class*='size-'])]:size-6",
        });

    public EmptyMediaVariant Variant { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var media = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "empty-icon",
            Variants.Of(Variant), Class, Rest);
        media.Attributes["data-variant"] ??= Variant == EmptyMediaVariant.Icon ? "icon" : "default";
        return media;
    }
}

/// <summary>The headline of the empty state.</summary>
public sealed partial class EmptyTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "empty-title",
        "text-lg font-medium tracking-tight", Class, Rest);
}

/// <summary>Supporting text for the empty state.</summary>
public sealed partial class EmptyDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "empty-description",
        "text-sm/relaxed text-muted-foreground [&>a]:underline [&>a]:underline-offset-4 [&>a:hover]:text-primary", Class, Rest);
}

/// <summary>Actions shown below the empty state.</summary>
public sealed partial class EmptyContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "empty-content",
        "flex w-full max-w-sm min-w-0 flex-col items-center gap-4 text-sm text-balance", Class, Rest);
}
