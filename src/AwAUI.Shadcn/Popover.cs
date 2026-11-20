using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;

namespace AwAUI.Shadcn;

/// <summary>An anchored popup with controlled or uncontrolled open state.</summary>
public sealed partial class Popover : Component
{
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public bool Modal { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => new PopoverPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        Modal = Modal,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    };
}

public sealed partial class PopoverTrigger : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new PopoverPrimitive.Trigger
    {
        AsChild = AsChild,
        Children = Children,
        Attributes = DialogAttributes.ForPart("popover-trigger", Rest, Class),
    };
}

/// <summary>Positions the popover relative to this part instead of its trigger.</summary>
public sealed partial class PopoverAnchor : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new PopoverPrimitive.Anchor
    {
        AsChild = AsChild,
        Children = Children,
        Attributes = DialogAttributes.ForPart("popover-anchor", Rest, Class),
    };
}

public sealed partial class PopoverClose : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new PopoverPrimitive.Close
    {
        AsChild = AsChild,
        Children = Children ?? (AsChild ? null : new NodeList { "Close" }),
        Attributes = DialogAttributes.ForPart("popover-close", Rest, Class),
    };
}

public sealed partial class PopoverContent : Component
{
    // Popper publishes AwAUI's transform origin on its positioning wrapper.
    const string ContentClass = "z-50 w-72 rounded-md border bg-popover p-4 text-popover-foreground shadow-md outline-hidden " + PopupMotion.Classes;

    public Side Side { get; set; } = Side.Bottom;
    public float SideOffset { get; set; } = 4;
    public PopperAlign Align { get; set; } = PopperAlign.Center;
    public float AlignOffset { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public float CollisionPadding { get; set; }
    public PopperSticky Sticky { get; set; } = PopperSticky.Partial;
    public bool HideWhenDetached { get; set; }
    public int ZOrder { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnOpenAutoFocus { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnCloseAutoFocus { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnInteractOutside { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new PopoverPrimitive.Portal
    {
        ZOrder = ZOrder,
        Children =
        [
            new PopoverPrimitive.Content
            {
                Side = Side,
                SideOffset = SideOffset,
                Align = Align,
                AlignOffset = AlignOffset,
                AvoidCollisions = AvoidCollisions,
                CollisionPadding = CollisionPadding,
                Sticky = Sticky,
                HideWhenDetached = HideWhenDetached,
                OnOpenAutoFocus = OnOpenAutoFocus,
                OnCloseAutoFocus = OnCloseAutoFocus,
                OnEscapeKeyDown = OnEscapeKeyDown,
                OnPointerDownOutside = OnPointerDownOutside,
                OnFocusOutside = OnFocusOutside,
                OnInteractOutside = OnInteractOutside,
                Attributes = DialogAttributes.ForPart("popover-content", Rest, Class, ContentClass),
                Children = Children,
            },
        ],
    };
}

public sealed partial class PopoverHeader : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("popover-header", Rest, Class, "flex flex-col gap-1 text-sm"));
        return node;
    }
}

public sealed partial class PopoverTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new UI.Text { Role = Role.Heading, Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("popover-title", Rest, Class, "font-medium"));
        return node;
    }
}

public sealed partial class PopoverDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new UI.Text { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("popover-description", Rest, Class, "text-muted-foreground"));
        return node;
    }
}
