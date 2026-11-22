using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>Density of an <see cref="Attachment"/>.</summary>
public enum AttachmentSize { Default, Sm, Xs }

/// <summary>Direction in which an <see cref="Attachment"/> lays out its media and content.</summary>
public enum AttachmentOrientation { Horizontal, Vertical }

/// <summary>Lifecycle of an attachment.</summary>
public enum AttachmentState { Idle, Uploading, Processing, Error, Done }

/// <summary>Presentation of an <see cref="AttachmentMedia"/> slot.</summary>
public enum AttachmentMediaVariant { Icon, Image }

/// <summary>A file or resource chip with media, title, description and actions. Use inside an <see cref="AttachmentGroup"/>.</summary>
public sealed partial class Attachment : Component
{
    static readonly Cva<AttachmentSize, AttachmentOrientation> Variants = new(
        "group/attachment relative flex w-fit max-w-full min-w-0 shrink-0 flex-wrap rounded-xl border bg-card text-card-foreground transition-colors " +
        "focus-within:ring-1 focus-within:ring-ring/50 has-[>a,>button]:hover:bg-muted/50 " +
        "data-[state=error]:border-destructive/30 data-[state=idle]:border-dashed",
        new Dictionary<AttachmentSize, string>
        {
            [AttachmentSize.Default] =
                "gap-2 text-sm has-data-[slot=attachment-content]:px-2.5 has-data-[slot=attachment-content]:py-2 has-data-[slot=attachment-media]:p-2",
            [AttachmentSize.Sm] =
                "gap-2.5 text-xs has-data-[slot=attachment-content]:px-2 has-data-[slot=attachment-content]:py-1.5 has-data-[slot=attachment-media]:p-1.5",
            [AttachmentSize.Xs] =
                "gap-1.5 rounded-lg text-xs has-data-[slot=attachment-content]:px-1.5 has-data-[slot=attachment-content]:py-1 has-data-[slot=attachment-media]:p-1",
        },
        new Dictionary<AttachmentOrientation, string>
        {
            [AttachmentOrientation.Horizontal] = "min-w-40 items-center",
            [AttachmentOrientation.Vertical] = "w-24 flex-col has-data-[slot=attachment-content]:w-30",
        });

    public AttachmentState State { get; set; } = AttachmentState.Done;
    public AttachmentSize Size { get; set; }
    public AttachmentOrientation Orientation { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var attachment = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "attachment",
            Variants.Of(Size, Orientation), Class, Rest);
        attachment.Attributes["data-state"] ??= StateName(State);
        attachment.Attributes["data-size"] ??= SizeName(Size);
        attachment.Attributes["data-orientation"] ??= OrientationName(Orientation);
        return attachment;
    }

    static string StateName(AttachmentState state) => state switch
    {
        AttachmentState.Idle => "idle",
        AttachmentState.Uploading => "uploading",
        AttachmentState.Processing => "processing",
        AttachmentState.Error => "error",
        _ => "done",
    };

    static string SizeName(AttachmentSize size) => size switch
    {
        AttachmentSize.Sm => "sm",
        AttachmentSize.Xs => "xs",
        _ => "default",
    };

    static string OrientationName(AttachmentOrientation orientation) =>
        orientation == AttachmentOrientation.Vertical ? "vertical" : "horizontal";
}

/// <summary>Scrollable row of attachments.</summary>
public sealed partial class AttachmentGroup : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "attachment-group",
        "flex min-w-0 scroll-fade-x snap-x snap-mandatory scroll-px-1 scrollbar-none gap-3 overflow-x-auto overscroll-x-contain py-1 " +
        "*:data-[slot=attachment]:flex-none *:data-[slot=attachment]:snap-start", Class, Rest);
}

/// <summary>Leading thumbnail or icon of an attachment.</summary>
public sealed partial class AttachmentMedia : Component
{
    static readonly Cva<AttachmentMediaVariant> Variants = new(
        "relative flex aspect-square w-10 shrink-0 items-center justify-center overflow-hidden rounded-lg bg-muted text-foreground " +
        "group-data-[orientation=vertical]/attachment:w-full group-data-[size=sm]/attachment:w-8 " +
        "group-data-[size=xs]/attachment:w-7 group-data-[size=xs]/attachment:rounded-md " +
        "group-data-[state=error]/attachment:bg-destructive/10 group-data-[state=error]/attachment:text-destructive " +
        "group-data-[orientation=vertical]/attachment:*:data-[slot=spinner]:size-6! " +
        "[&_svg]:pointer-events-none [&_svg:not([class*='size-'])]:size-4 " +
        "group-data-[orientation=vertical]/attachment:[&_svg:not([class*='size-'])]:size-6 " +
        "group-data-[size=xs]/attachment:[&_svg:not([class*='size-'])]:size-3.5",
        new Dictionary<AttachmentMediaVariant, string>
        {
            [AttachmentMediaVariant.Icon] = "",
            [AttachmentMediaVariant.Image] =
                "opacity-60 group-data-[state=done]/attachment:opacity-100 group-data-[state=idle]/attachment:opacity-100 " +
                "*:[img]:aspect-square *:[img]:w-full *:[img]:object-cover",
        });

    public AttachmentMediaVariant Variant { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var media = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "attachment-media",
            Variants.Of(Variant), Class, Rest);
        media.Attributes["data-variant"] ??= Variant == AttachmentMediaVariant.Image ? "image" : "icon";
        return media;
    }
}

/// <summary>The text column of an attachment.</summary>
public sealed partial class AttachmentContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "attachment-content",
        "max-w-full min-w-0 flex-1 leading-tight group-data-[orientation=vertical]/attachment:px-1", Class, Rest);
}

/// <summary>The attachment's file name or heading.</summary>
public sealed partial class AttachmentTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Span { Children = Children ?? new NodeList() }, "attachment-title",
        "block max-w-full min-w-0 truncate font-medium " +
        "group-data-[state=processing]/attachment:shimmer group-data-[state=uploading]/attachment:shimmer", Class, Rest);
}

/// <summary>The attachment's supporting text, such as size or progress.</summary>
public sealed partial class AttachmentDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Span { Children = Children ?? new NodeList() }, "attachment-description",
        "mt-0.5 block min-w-0 max-w-full truncate text-xs text-muted-foreground " +
        "group-data-[state=error]/attachment:text-destructive/80", Class, Rest);
}

/// <summary>Trailing action row of an attachment.</summary>
public sealed partial class AttachmentActions : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "attachment-actions",
        "relative z-20 flex shrink-0 items-center " +
        "group-data-[orientation=vertical]/attachment:absolute group-data-[orientation=vertical]/attachment:top-3 " +
        "group-data-[orientation=vertical]/attachment:right-3 group-data-[orientation=vertical]/attachment:gap-1", Class, Rest);
}

/// <summary>
/// An icon button in an attachment's action row. It reuses the shadcn <see cref="Button"/> through its <c>AsChild</c> slot
/// so the rendered native button keeps the <c>attachment-action</c> slot. Ghost and icon-xs by default, like the registry.
/// </summary>
public sealed partial class AttachmentAction : Component
{
    public ButtonVariant Variant { get; set; } = ButtonVariant.Ghost;
    public ButtonSize Size { get; set; } = ButtonSize.IconXs;
    public bool Disabled { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var button = new UIButton
        {
            Disabled = Disabled,
            OnClick = OnClick,
            Children = Children ?? new NodeList(),
        };
        button.Attributes["data-slot"] = "attachment-action";

        // The inner button owns the slot; the shadcn Button only supplies variants, classes and remaining attributes.
        return new Button
        {
            Variant = Variant,
            Size = Size,
            AsChild = true,
            Class = Class,
            Rest = Rest,
            Children = [button],
        };
    }
}

/// <summary>An invisible full-surface button that makes the whole attachment clickable.</summary>
public sealed partial class AttachmentTrigger : Component
{
    public bool AsChild { get; set; }
    public bool Disabled { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var trigger = StaticParts.Style(new UIButton
        {
            Disabled = Disabled,
            OnClick = OnClick,
            Children = Children ?? new NodeList(),
        }, "attachment-trigger", "absolute inset-0 z-10 outline-none", Class, Rest);
        // Matches the registry: an asChild trigger renders the caller's element and must not force button semantics.
        if (AsChild) trigger.Attributes["type"] = null;
        else trigger.Attributes["type"] ??= "button";
        var node = AsChild ? Slot.Merge(trigger, Slot.SingleChild(Children ?? new NodeList())) : trigger;
        if (node is ElementNode element) element.Class = TailwindMerge.Cn(element.Class.Text);
        return node;
    }
}
