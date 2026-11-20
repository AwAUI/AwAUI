using System;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.UI;
using DialogPrimitive = AwAUI.Controls.Primitives.Dialog;

namespace AwAUI.Shadcn;

/// <summary>A dialog that opens along an edge of the window.</summary>
public sealed partial class Sheet : Component
{
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public bool Modal { get; set; } = true;
    public Action<bool>? OnOpenChange { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => new DialogPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        Modal = Modal,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    };
}

public sealed partial class SheetTrigger : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Trigger
    {
        AsChild = AsChild,
        Children = Children,
        Attributes = DialogAttributes.ForPart("sheet-trigger", Rest, Class),
    };
}

public sealed partial class SheetClose : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Close
    {
        AsChild = AsChild,
        Children = Children ?? (AsChild ? null : new NodeList { "Close" }),
        Attributes = DialogAttributes.ForPart("sheet-close", Rest, Class),
    };
}

/// <summary>Places a custom sheet overlay and panel in the same portal.</summary>
public sealed partial class SheetPortal : Component
{
    public int ZOrder { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => new DialogPortal { ZOrder = ZOrder, Children = Children };
}

public sealed partial class SheetOverlay : Component
{
    internal const string OverlayClass = "fixed inset-0 z-50 bg-black/50 data-[state=open]:animate-aw-dialog-backdrop-in data-[state=closed]:animate-aw-dialog-backdrop-out motion-reduce:animate-none!";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Overlay
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("sheet-overlay", Rest, Class, OverlayClass),
    };
}

public sealed partial class SheetContent : Component
{
    const string ContentClass = "fixed z-50 flex flex-col gap-4 bg-background shadow-lg data-[state=closed]:animate-aw-sheet-out data-[state=open]:animate-aw-sheet-in motion-reduce:animate-none!";
    const string RightClass = "inset-y-0 right-0 h-full w-3/4 border-l sm:max-w-sm";
    const string LeftClass = "inset-y-0 left-0 h-full w-3/4 border-r sm:max-w-sm";
    const string TopClass = "inset-x-0 top-0 h-auto border-b";
    const string BottomClass = "inset-x-0 bottom-0 h-auto border-t";
    const string CloseClass = "absolute top-4 right-4 rounded-xs opacity-70 ring-offset-background transition-opacity hover:opacity-100 focus:ring-2 focus:ring-ring focus:ring-offset-2 focus:outline-hidden disabled:pointer-events-none data-[state=open]:bg-secondary";

    public Side Side { get; set; } = Side.Right;
    public bool ShowCloseButton { get; set; } = true;
    public string CloseLabel { get; set; } = "Close";
    public Action<FocusScopeAutoFocusEvent>? OnOpenAutoFocus { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnCloseAutoFocus { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnInteractOutside { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var sideClass = Side switch
        {
            Side.Top => TopClass,
            Side.Bottom => BottomClass,
            Side.Left => LeftClass,
            _ => RightClass,
        };
        var children = Children;
        if (ShowCloseButton)
        {
            var glyph = SelectParts.Glyph(LucideIcons.X, "sheet-close-icon", "pointer-events-none size-4");
            children = new NodeList(Children)
            {
                new SheetClose
                {
                    Class = CloseClass,
                    Rest = new AttributeDictionary { ["aria-label"] = CloseLabel },
                    Children = [glyph],
                },
            };
        }

        var attributes = DialogAttributes.ForPart("sheet-content", Rest, Class, ContentClass + " " + sideClass);
        attributes["data-side"] = Side switch
        {
            Side.Top => "top", Side.Left => "left", Side.Bottom => "bottom", _ => "right",
        };
        var panel = new DialogPrimitive.Content
        {
            Attributes = attributes,
            Children = children,
            OnOpenAutoFocus = OnOpenAutoFocus,
            OnCloseAutoFocus = OnCloseAutoFocus,
            OnEscapeKeyDown = OnEscapeKeyDown,
            OnPointerDownOutside = OnPointerDownOutside,
            OnFocusOutside = OnFocusOutside,
            OnInteractOutside = OnInteractOutside,
        };
        var context = Use<DialogPrimitive.Context>();
        if (TryUse<DialogPortal.Placement>(out var placement) && ReferenceEquals(placement.Owner, context.Shared))
            return panel;
        return new DialogPrimitive.Portal { Children = [new SheetOverlay(), panel] };
    }
}

public sealed partial class SheetHeader : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("sheet-header", Rest, Class, "flex flex-col gap-1.5 p-4"));
        return node;
    }
}

public sealed partial class SheetFooter : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("sheet-footer", Rest, Class, "mt-auto flex flex-col gap-2 p-4"));
        return node;
    }
}

public sealed partial class SheetTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Title
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("sheet-title", Rest, Class, "font-semibold text-foreground"),
    };
}

public sealed partial class SheetDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Description
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("sheet-description", Rest, Class, "text-sm text-muted-foreground"),
    };
}
